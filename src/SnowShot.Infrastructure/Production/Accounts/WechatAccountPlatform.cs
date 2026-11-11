using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SnowShot.Application;
using StbImageSharp;

namespace SnowShot.Infrastructure.Accounts;

internal sealed partial class WechatAccountPlatform(IHttpClientFactory clients, WechatAccountOptions options,
    TimeProvider clock, ILogger<WechatAccountPlatform>? logger = null) : IWechatAccountPlatform, IDisposable
{
    private const int MaxCodeBytes = 2 * 1024 * 1024;
    private const int MaxCodeDimension = 1280;
    private static ReadOnlySpan<byte> JpegSignature => [0xff, 0xd8, 0xff];

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessExpires;
    public bool PaymentsAvailable => options.IsPaymentConfigured;
    public bool ReconciliationAvailable => options.IsReconciliationConfigured;
    public void Dispose() => _tokenGate.Dispose();

    public async Task<WechatLoginIdentity> LoginAsync(string code, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(options.AppId) || string.IsNullOrWhiteSpace(options.AppSecret))
            throw Unavailable();
        if (string.IsNullOrWhiteSpace(code) || code.Length > 512)
            throw new AccountException("invalid_credentials", "A valid WeChat sign-in code is required.", 401);
        var uri = "sns/jscode2session?appid=" + Uri.EscapeDataString(options.AppId) +
            "&secret=" + Uri.EscapeDataString(options.AppSecret) + "&js_code=" + Uri.EscapeDataString(code) + "&grant_type=authorization_code";
        using var result = await ReadAsync(await clients.CreateClient("wechat-account").GetAsync(uri, token), token);
        Check(result.RootElement, "code2Session");
        var openId = String(result.RootElement, "openid");
        var session = String(result.RootElement, "session_key");
        if (string.IsNullOrWhiteSpace(openId) || string.IsNullOrWhiteSpace(session)) throw Unavailable();
        return new(openId, session);
    }

    public async Task<byte[]> CodeAsync(Guid requestId, CancellationToken token)
    {
        RequirePayments();
        var access = await AccessTokenAsync(token);
        using var response = await PostJsonAsync(
            "wxa/getwxacodeunlimit?access_token=" + Uri.EscapeDataString(access),
            new
            {
                scene = requestId.ToString("N"),
                page = options.PurchasePage,
                check_path = options.MiniProgramCodeCheckPath,
                env_version = options.MiniProgramCodeVersion,
                width = 430
            }, token);
        if (!response.IsSuccessStatusCode)
        {
            ReportInvalidResponse(response, "http_status");
            throw Unavailable();
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length > MaxCodeBytes)
        {
            ReportInvalidResponse(response, "response_size");
            throw Unavailable();
        }
        if (bytes.AsSpan().StartsWith(PngImage.Signature)) return bytes;

        // Installed desktop builds support PNG without Qt's optional JPEG plugin.
        if (bytes.AsSpan().StartsWith(JpegSignature) &&
            ConvertCodeToPng(bytes) is { Length: <= MaxCodeBytes } png)
            return png;

        ReportInvalidResponse(response, "code_image");
        throw Unavailable();
    }

    private static byte[]? ConvertCodeToPng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        if (ImageInfo.FromStream(stream) is not
            { Width: > 0 and <= MaxCodeDimension, Height: > 0 and <= MaxCodeDimension })
            return null;

        ImageResult image;
        try { image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlue); }
        catch (InvalidOperationException) { return null; }
        return PngImage.EncodeRgb(image.Data, image.Width, image.Height);
    }

    public VirtualPaymentParameters Payment(string orderId, string openId, string sessionKey, string packageId, int priceFen)
    {
        RequirePayments();
        if (!options.ProductIds.TryGetValue(packageId, out var productId)) throw Unavailable();
        if (orderId.Length is < 8 or > 32 || orderId[0] == '_' ||
            orderId.Any(character => !char.IsAsciiLetterOrDigit(character) && !"_-|*@".Contains(character, StringComparison.Ordinal)) ||
            !MembershipCatalog.For(options.AllowPaymentTesting).Any(package => package.Id == packageId && package.Fen == priceFen) ||
            string.IsNullOrWhiteSpace(openId) || string.IsNullOrWhiteSpace(sessionKey))
            throw new AccountException("invalid_payment_order", "The payment order parameters are invalid.");
        var signData = JsonSerializer.Serialize(new
        {
            offerId = options.OfferId,
            buyQuantity = 1,
            env = options.Environment,
            currencyType = "CNY",
            productId,
            goodsPrice = priceFen,
            outTradeNo = orderId,
            attach = orderId,
        });
        return new("short_series_goods", signData, Sign(options.AppKey, "requestVirtualPayment&" + signData), Sign(sessionKey, signData));
    }

    public VirtualPaymentParameters RefreshPaymentAuthorization(VirtualPaymentParameters parameters, string sessionKey)
    {
        RequirePayments();
        if (string.IsNullOrWhiteSpace(sessionKey)) throw Unavailable();
        // Only the user's session credential changes. The merchant authorization
        // and exact purchase document remain the originally issued evidence.
        return parameters with { Signature = Sign(sessionKey, parameters.SignData) };
    }

    public async Task<VerifiedVirtualOrder?> QueryAsync(string orderId, string openId, CancellationToken token)
    {
        if (!ReconciliationAvailable) throw Unavailable();
        using var result = await ReadAsync(await PostPaymentAsync("xpay/query_order",
            new { openid = openId, env = options.Environment, order_id = orderId }, token), token);
        Check(result.RootElement, "query_order", requireSuccessCode: true);
        if (!result.RootElement.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Object) return null;
        if (String(order, "order_id") != orderId || Integer(order, "env_type") != (options.Environment == 0 ? 1 : 2))
            throw new AccountException("reconciliation_required", "The platform returned a conflicting order or environment.", 409);
        var status = Integer(order, "status");
        var type = Integer(order, "order_type");
        var returnedOpenId = String(order, "openid");
        if (returnedOpenId is not null && returnedOpenId != openId) throw new AccountException("reconciliation_required", "The platform returned a conflicting payer.", 409);
        if (type is 1 or 8)
        {
            var refundState = status switch { 8 => "completed", 7 => "failed", _ => "processing" };
            return new(orderId, openId, 0, "refund", String(order, "wx_order_id"),
                [new(orderId, Integer(order, "refund_fee"), refundState)]);
        }
        if (type is not (0 or 7)) throw new AccountException("reconciliation_required", "The platform returned an unsupported order type.", 409);
        var state = status switch { 2 or 3 or 4 => "paid", 5 or 7 or 8 when Integer(order, "paid_fee") > 0 => "paid", 6 => "closed", _ => "payment_pending" };
        // Status 5 is merchant-side refund processing, not proof of customer receipt.
        // Refund events and refund-order queries are reconciled separately.
        return new(orderId, openId, Integer(order, "paid_fee"), state, String(order, "wx_order_id"), [], Integer(order, "order_fee"), status == 4);
    }

    public async Task NotifyDeliveryAsync(string orderId, CancellationToken token)
    {
        if (!ReconciliationAvailable) throw Unavailable();
        using var result = await ReadAsync(await PostPaymentAsync("xpay/notify_provide_goods",
            new { order_id = orderId, env = options.Environment }, token), token);
        Check(result.RootElement, "notify_provide_goods", requireSuccessCode: true);
    }

    public string VerifyMessage(string signature, string timestamp, string nonce, string? echo,
        string? encryptedBody, string? messageSignature)
    {
        if (string.IsNullOrWhiteSpace(options.MessageToken) || timestamp.Length > 32 || nonce.Length > 256)
            throw new AccountException("invalid_platform_signature", "The platform message signature is invalid.", 401);
        if (encryptedBody is null)
        {
            VerifySha1(signature, [options.MessageToken, timestamp, nonce]);
            return echo ?? string.Empty;
        }
        string encrypted;
        try
        {
            if (encryptedBody.TrimStart().StartsWith('<'))
                encrypted = XElement.Parse(encryptedBody).Element("Encrypt")?.Value ?? string.Empty;
            else
            {
                using var json = JsonDocument.Parse(encryptedBody);
                encrypted = String(json.RootElement, "Encrypt") ?? string.Empty;
            }
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or JsonException)
        { throw new AccountException("invalid_platform_message", "The platform message is malformed."); }
        if (string.IsNullOrWhiteSpace(encrypted) || string.IsNullOrWhiteSpace(messageSignature))
            throw new AccountException("invalid_platform_signature", "Encrypted platform messages are required.", 401);
        VerifySha1(messageSignature, [options.MessageToken, timestamp, nonce, encrypted]);
        try
        {
            var key = Convert.FromBase64String(options.EncodingAesKey + "=");
            using var aes = Aes.Create(); aes.Key = key; aes.IV = key.AsSpan(0, 16).ToArray(); aes.Padding = PaddingMode.None;
            var plain = aes.DecryptCbc(Convert.FromBase64String(encrypted), aes.IV, PaddingMode.None);
            if (plain.Length < 32) throw new CryptographicException();
            var padding = plain[^1];
            if (padding is < 1 or > 32 || plain.Length < padding || plain.AsSpan(plain.Length - padding).IndexOfAnyExcept(padding) >= 0)
                throw new CryptographicException();
            var count = BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(16, 4));
            if (count < 0 || count > plain.Length - padding - 20 ||
                Encoding.UTF8.GetString(plain, 20 + count, plain.Length - padding - 20 - count) != options.AppId)
                throw new CryptographicException();
            return Encoding.UTF8.GetString(plain, 20, count);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        { throw new AccountException("invalid_platform_signature", "The platform message cannot be authenticated.", 401); }
    }

    private async Task<string> AccessTokenAsync(CancellationToken token)
    {
        await _tokenGate.WaitAsync(token);
        try
        {
            if (_accessToken is not null && clock.GetUtcNow() < _accessExpires) return _accessToken;
            using var result = await ReadAsync(await PostJsonAsync("cgi-bin/stable_token",
                new { grant_type = "client_credential", appid = options.AppId, secret = options.AppSecret, force_refresh = false }, token), token);
            Check(result.RootElement, "stable_token");
            _accessToken = String(result.RootElement, "access_token") ?? throw Unavailable();
            _accessExpires = clock.GetUtcNow().AddSeconds(Math.Max(30, Integer(result.RootElement, "expires_in") - 120));
            return _accessToken;
        }
        finally { _tokenGate.Release(); }
    }
    private async Task<HttpResponseMessage> PostJsonAsync(string uri, object payload, CancellationToken token)
    {
        // WeChat rejects HTTP/1.1 chunked JSON with 412. Buffer the UTF-8 JSON so
        // HttpClient supplies Content-Length for every platform JSON POST.
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await clients.CreateClient("wechat-account").PostAsync(uri, content, token);
    }
    private async Task<HttpResponseMessage> PostPaymentAsync(string path, object payload, CancellationToken token)
    {
        var body = JsonSerializer.Serialize(payload);
        var access = await AccessTokenAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            path + "?access_token=" + Uri.EscapeDataString(access) + "&pay_sig=" + Sign(options.AppKey, "/" + path + "&" + body))
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return await clients.CreateClient("wechat-account").SendAsync(request, token);
    }
    private void RequirePayments() { if (!PaymentsAvailable) throw Unavailable(); }
    private static AccountException Unavailable() => new("payments_unavailable", "WeChat account purchasing is temporarily unavailable.", 503);
    internal static string Sign(string key, string content) => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(content)));
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "The WeChat message-push authentication protocol requires SHA-1; payment signatures and storage use SHA-256.")]
    private static void VerifySha1(string signature, string[] pieces)
    {
        Array.Sort(pieces, StringComparer.Ordinal);
        var expected = SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(pieces)));
        byte[] actual;
        try { actual = Convert.FromHexString(signature); } catch (FormatException) { throw new AccountException("invalid_platform_signature", "The platform message signature is invalid.", 401); }
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new AccountException("invalid_platform_signature", "The platform message signature is invalid.", 401);
    }
    [LoggerMessage(Level = LogLevel.Warning, EventId = 5502, Message = "WeChat account platform rejected {Operation} with error code {ErrorCode}")]
    private static partial void PlatformRejected(ILogger logger, string operation, int errorCode);

    private void Check(JsonElement root, string operation, bool requireSuccessCode = false)
    {
        if (!root.TryGetProperty("errcode", out var error))
        {
            // Login and access-token responses can omit errcode on success.
            // Financial acknowledgements must explicitly establish success.
            if (requireSuccessCode) throw Unavailable();
            return;
        }
        if (error.ValueKind != JsonValueKind.Number || !error.TryGetInt32(out var value))
            throw Unavailable();
        if (value != 0)
        {
            if (logger is not null) PlatformRejected(logger, operation, value);
            throw Unavailable();
        }
    }
    [LoggerMessage(Level = LogLevel.Warning, EventId = 5503, Message = "WeChat account platform returned an invalid {Operation} response ({Failure}, HTTP {StatusCode}, content type {ContentType}, encoding {ContentEncoding})")]
    private static partial void InvalidResponse(ILogger logger, string operation, string failure, int statusCode, string? contentType, string contentEncoding);

    private void ReportInvalidResponse(HttpResponseMessage response, string failure)
    {
        if (logger is not null) InvalidResponse(logger, response.RequestMessage?.RequestUri?.AbsolutePath ?? "unknown", failure, (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType, string.Join(",", response.Content.Headers.ContentEncoding));
    }

    private async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode) { ReportInvalidResponse(response, "http_status"); throw Unavailable(); }
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            if (bytes.Length > 1024 * 1024) { ReportInvalidResponse(response, "response_size"); throw Unavailable(); }
            try
            {
                var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
                document.Dispose(); ReportInvalidResponse(response, "json_root"); throw Unavailable();
            }
            catch (JsonException) { ReportInvalidResponse(response, "json_format"); throw Unavailable(); }
        }
    }
    private static string? String(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int Integer(JsonElement root, string property) => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
}
