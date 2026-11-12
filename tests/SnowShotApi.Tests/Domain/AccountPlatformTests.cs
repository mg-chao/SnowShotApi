using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnowShot.Application;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Configuration;
using StbImageSharp;

namespace SnowShotApi.Tests.Domain;

public sealed class AccountPlatformTests
{
    [Fact]
    public void DedicatedAccountKeyRotationAndPrivacyKeyRotationPreserveEvidence()
    {
        var oldKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var identity = new IdentityOptions { HmacKeyBase64 = oldKey };
        var before = new AccountSecrets(identity, new() { KeyBase64 = oldKey });
        var encrypted = before.Protect("retained payment evidence");
        var after = new AccountSecrets(new() { HmacKeyBase64 = newKey }, new() { KeyBase64 = newKey, PreviousKeysBase64 = [oldKey] });
        Assert.Equal("retained payment evidence", after.Unprotect(encrypted));
        var legacy = new AccountSecrets(identity).Protect("pending bootstrap session");
        Assert.Equal("pending bootstrap session", new AccountSecrets(new() { HmacKeyBase64 = newKey, PreviousHmacKeyBase64 = oldKey }).Unprotect(legacy));
        Assert.Throws<CryptographicException>(() => new AccountSecrets(new() { HmacKeyBase64 = newKey }).Unprotect(legacy));
    }

    [Theory]
    [InlineData(0, "0d145cd845651404973c83c22f8a5a3b4dd5ebaf1b464fccdac2e583e65fcaf6", "a50c2545fb04c5103ab656ce0e3e8e888912dc1174aef112b96fd1361f7bb7c8")]
    [InlineData(1, "f929a9e0aa6d0cefd0b840ecaf75616a42100f550f1e1ced8d931b634df49b98", "0ebcf6f1b64e7dc3702b064c63ff53bae455ce283d44768ba511746080ab399c")]
    public void VirtualPaymentSignsExactDocumentedWireBytes(int environment, string paySig, string signature)
    {
        using var handler = new StubHandler();
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(environment), TimeProvider.System);
        var parameters = platform.Payment("1234567890abcdef1234567890abcdef", "openid", "session-key", "cny-9-9", 990);
        Assert.Equal("short_series_goods", parameters.Mode);
        Assert.Equal(paySig, parameters.PaySig);
        Assert.Equal(signature, parameters.Signature);
        using var sign = JsonDocument.Parse(parameters.SignData);
        Assert.Equal(1, sign.RootElement.GetProperty("buyQuantity").GetInt32());
        Assert.Equal(990, sign.RootElement.GetProperty("goodsPrice").GetInt32());
        Assert.Equal(environment, sign.RootElement.GetProperty("env").GetInt32());
    }

    [Theory]
    [InlineData(0, "7b5e6ec9f325a937b5ac7b2f629dccbd9470f14aea367d9c48b6dbdbad2b0d15")]
    [InlineData(1, "4a81fc2673a448bb1fd4d25273fc2cad69ce04f8ce0f1143bdc6a7947c2d8d3f")]
    public async Task QuerySignsExactlyTheTransmittedBodyWithTheDocumentedAlgorithm(int environment, string paySig)
    {
        using var handler = new StubHandler { EnvironmentType = environment + 1 };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(environment), TimeProvider.System);
        await platform.QueryAsync("test-order", "openid", TestContext.Current.CancellationToken);
        Assert.Equal("{\"openid\":\"openid\",\"env\":" + environment + ",\"order_id\":\"test-order\"}", handler.QueryBody);
        // Fixture calculated with the Python hmac.new example in person.md §5.5.
        Assert.Contains("pay_sig=" + paySig, handler.QueryUri, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveredCheckoutChangesOnlyTheCurrentUserSessionSignature()
    {
        using var handler = new StubHandler();
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        var original = platform.Payment("1234567890abcdef1234567890abcdef", "openid", "original-session", "cny-9-9", 990);
        var recovered = platform.RefreshPaymentAuthorization(original, "new-session");
        Assert.Equal(original.Mode, recovered.Mode);
        Assert.Equal(original.SignData, recovered.SignData);
        Assert.Equal(original.PaySig, recovered.PaySig);
        Assert.Equal(WechatAccountPlatform.Sign("new-session", original.SignData), recovered.Signature);
        Assert.NotEqual(original.Signature, recovered.Signature);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DeliveryNotificationUsesTheConfiguredPaymentEnvironment(int environment)
    {
        using var handler = new StubHandler();
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(environment), TimeProvider.System);
        await platform.NotifyDeliveryAsync("test-order", TestContext.Current.CancellationToken);
        Assert.Equal("{\"order_id\":\"test-order\",\"env\":" + environment + "}", handler.DeliveryBody);
    }

    [Theory]
    [InlineData(0, "7c6a1d12e6c56e0f8999f01ace34ada67320d0d5716138407f3d6829555ae761")]
    [InlineData(1, "ea5ccf78ef62d3f6d7e8d8560369ef00d529ab33167a615a8edf3ebb353820db")]
    public async Task DeliveryConfirmationSignsExactlyTheTransmittedBody(int environment, string paySig)
    {
        using var handler = new StubHandler();
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(environment), TimeProvider.System);
        await platform.NotifyDeliveryAsync("test-order", TestContext.Current.CancellationToken);
        Assert.Contains("pay_sig=" + paySig, handler.DeliveryUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonPostsDeclareUtf8ContentLengthBeforeSendingToWechat()
    {
        using var handler = new StubHandler { RequireContentLength = true };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        var token = TestContext.Current.CancellationToken;
        Assert.NotNull(await platform.QueryAsync("test-order", "openid", token));
        await platform.NotifyDeliveryAsync("test-order", token);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, await platform.CodeAsync(Guid.NewGuid(), token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JpegMiniProgramCodeHonorsClientSupport(bool acceptJpeg)
    {
        // 16×16 grayscale JPEG, black on the left and white on the right.
        using var handler = new StubHandler
        {
            CodeImage = Convert.FromBase64String(
                "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/" +
                "wAALCAAQABABAREA/8QAFAABAAAAAAAAAAAAAAAAAAAAC//EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AP/P8ABz/AA//2Q==")
        };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        var code = await platform.CodeAsync(Guid.NewGuid(), TestContext.Current.CancellationToken, acceptJpeg);
        if (acceptJpeg)
        {
            Assert.Equal(handler.CodeImage, code);
            return;
        }

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, code[..8]);
        var image = ImageResult.FromMemory(code, ColorComponents.Grey);
        Assert.Equal(16, image.Width);
        Assert.Equal(16, image.Height);
        Assert.Equal(0, image.Data[0]);
        Assert.Equal(255, image.Data[15]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedJpegMiniProgramCodeIsRejected(bool acceptJpeg)
    {
        using var handler = new StubHandler { CodeImage = [0xff, 0xd8, 0xff, 0xe0, 0, 2, 0xff, 0xd9] };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);

        var error = await Assert.ThrowsAsync<AccountException>(() =>
            platform.CodeAsync(Guid.NewGuid(), TestContext.Current.CancellationToken, acceptJpeg));
        Assert.Equal("payments_unavailable", error.Code);
    }

    [Theory]
    [InlineData(0, "release", true)]
    [InlineData(1, "release", true)]
    [InlineData(0, "develop", false)]
    [InlineData(1, "develop", false)]
    [InlineData(0, "trial", false)]
    [InlineData(1, "trial", false)]
    public async Task MiniProgramCodeVersionAndPathCheckAreIndependentOfThePaymentEnvironment(
        int environment, string version, bool checkPath)
    {
        using var handler = new StubHandler();
        using var platform = new WechatAccountPlatform(new Clients(handler),
            Options(environment, codeVersion: version, codeCheckPath: checkPath), TimeProvider.System);
        var requestId = Guid.NewGuid();
        await platform.CodeAsync(requestId, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(handler.CodeBody!);
        Assert.Equal(version, body.RootElement.GetProperty("env_version").GetString());
        Assert.Equal(checkPath, body.RootElement.GetProperty("check_path").GetBoolean());
        Assert.Equal("pages/purchase/purchase", body.RootElement.GetProperty("page").GetString());
        Assert.Equal(requestId.ToString("N"), body.RootElement.GetProperty("scene").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("<html>unavailable</html>")]
    [InlineData("{\"errcode\":null}")]
    [InlineData("{\"errcode\":\"0\"}")]
    [InlineData("{\"errcode\":0.5}")]
    [InlineData("{\"errcode\":40001}")]
    public async Task DeliveryRequiresAnExplicitSuccessfulPlatformAcknowledgement(string response)
    {
        using var handler = new StubHandler { DeliveryResponse = response };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<AccountException>(() =>
            platform.NotifyDeliveryAsync("test-order", TestContext.Current.CancellationToken));
        Assert.Equal("payments_unavailable", error.Code);
    }

    [Fact]
    public async Task DeliveryAcceptsAnExplicitSuccessfulPlatformAcknowledgement()
    {
        using var handler = new StubHandler { DeliveryResponse = "{\"errcode\":0,\"errmsg\":\"ok\"}" };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        await platform.NotifyDeliveryAsync("test-order", TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"0\"")]
    [InlineData("0.5")]
    [InlineData("40001")]
    public async Task OrderQueryRequiresAValidSuccessCodeBeforeTrustingPaymentEvidence(string? errorCode)
    {
        const string order = "\"order\":{\"order_id\":\"test-order\",\"env_type\":1,\"status\":3,\"order_type\":0,\"paid_fee\":990,\"order_fee\":990,\"wx_order_id\":\"wx-order\"}";
        var response = "{" + (errorCode is null ? "" : "\"errcode\":" + errorCode + ",") + order + "}";
        using var handler = new StubHandler { QueryResponse = response };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<AccountException>(() =>
            platform.QueryAsync("test-order", "openid", TestContext.Current.CancellationToken));
        Assert.Equal("payments_unavailable", error.Code);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(0, 4)]
    [InlineData(7, 2)]
    [InlineData(7, 3)]
    [InlineData(7, 4)]
    public async Task AndroidAndIosPaidOrdersRecoverDuringDelivery(int type, int status)
    {
        using var handler = new StubHandler { OrderType = type, Status = status };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        Assert.Equal("paid", (await platform.QueryAsync("test-order", "openid", TestContext.Current.CancellationToken))!.State);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("_12345678")]
    [InlineData("invalid/order")]
    [InlineData("123456789012345678901234567890123")]
    public void PaymentRejectsInvalidMerchantOrderIds(string id)
    {
        using var handler = new StubHandler();
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        Assert.Throws<AccountException>(() => platform.Payment(id, "openid", "session", "cny-9-9", 990));
    }

    [Theory]
    [InlineData(1, 5, "processing")]
    [InlineData(1, 7, "failed")]
    [InlineData(1, 8, "completed")]
    [InlineData(8, 5, "processing")]
    [InlineData(8, 8, "completed")]
    public async Task RefundQueriesRequireCustomerReceiptBeforeCompletion(int type, int status, string expected)
    {
        using var handler = new StubHandler { OrderType = type, Status = status };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(), TimeProvider.System);
        var query = await platform.QueryAsync("test-order", "openid", TestContext.Current.CancellationToken);
        Assert.NotNull(query); Assert.Equal(expected, Assert.Single(query.Refunds).State);
        Assert.Equal(990, query.Refunds[0].RefundFen);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    public async Task QueryRejectsConflictingMerchantOrderAndEnvironment(int environment, int conflictingEnvironmentType)
    {
        using var handler = new StubHandler { OrderId = "another-order" };
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(environment), TimeProvider.System);
        var error = await Assert.ThrowsAsync<AccountException>(() => platform.QueryAsync("test-order", "openid", TestContext.Current.CancellationToken));
        Assert.Equal("reconciliation_required", error.Code);
        handler.OrderId = "test-order"; handler.EnvironmentType = conflictingEnvironmentType;
        await Assert.ThrowsAsync<AccountException>(() => platform.QueryAsync("test-order", "openid", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void EncryptedCallbackAuthenticatesSignatureAndRecipient()
    {
        using var handler = new StubHandler(); var options = Options();
        using var platform = new WechatAccountPlatform(new Clients(handler), options, TimeProvider.System);
        var plain = "{\"Event\":\"xpay_goods_deliver_notify\",\"OutTradeNo\":\"test-order\"}";
        var message = Message(options, plain, options.AppId);
        Assert.Equal(plain, platform.VerifyMessage("", "123", "nonce", null, message.Body, message.Signature));
        Assert.Throws<AccountException>(() => platform.VerifyMessage("", "123", "tampered", null, message.Body, message.Signature));
        var wrongApp = Message(options, plain, "another-app");
        Assert.Throws<AccountException>(() => platform.VerifyMessage("", "123", "nonce", null, wrongApp.Body, wrongApp.Signature));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "Independent test producer implements WeChat's required SHA-1 message-push signature protocol.")]
    private static (string Body, string Signature) Message(WechatAccountOptions options, string message, string recipient)
    {
        var content = Encoding.UTF8.GetBytes(message); var app = Encoding.UTF8.GetBytes(recipient);
        var count = 20 + content.Length + app.Length; var padding = 32 - count % 32; var plain = new byte[count + padding];
        RandomNumberGenerator.Fill(plain.AsSpan(0, 16)); BinaryPrimitives.WriteInt32BigEndian(plain.AsSpan(16, 4), content.Length);
        content.CopyTo(plain, 20); app.CopyTo(plain, 20 + content.Length); plain.AsSpan(count).Fill((byte)padding);
        var key = Convert.FromBase64String(options.EncodingAesKey + "=");
        using var aes = Aes.Create(); aes.Key = key;
        var encrypted = Convert.ToBase64String(aes.EncryptCbc(plain, key.AsSpan(0, 16), PaddingMode.None));
        var pieces = new[] { options.MessageToken, "123", "nonce", encrypted }; Array.Sort(pieces, StringComparer.Ordinal);
        var signature = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(pieces))));
        return (JsonSerializer.Serialize(new { Encrypt = encrypted }), signature);
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void TestProductCanOnlyBeSignedDuringManualTesting(int environment, bool allowTesting)
    {
        using var handler = new StubHandler();
        using var platform = new WechatAccountPlatform(new Clients(handler), Options(environment, allowTesting), TimeProvider.System);
        if (!allowTesting)
        {
            var error = Assert.Throws<AccountException>(() => platform.Payment("test-order", "openid", "session-key", "cny-0-1", 10));
            Assert.Equal("invalid_payment_order", error.Code);
            return;
        }
        var parameters = platform.Payment("test-order", "openid", "session-key", "cny-0-1", 10);
        using var sign = JsonDocument.Parse(parameters.SignData);
        Assert.Equal("p0", sign.RootElement.GetProperty("productId").GetString());
        Assert.Equal(10, sign.RootElement.GetProperty("goodsPrice").GetInt32());
        Assert.Equal(environment, sign.RootElement.GetProperty("env").GetInt32());
        var wrongPrice = Assert.Throws<AccountException>(() => platform.Payment("test-order", "openid", "session-key", "cny-0-1", 1));
        Assert.Equal("invalid_payment_order", wrongPrice.Code);
    }

    private static WechatAccountOptions Options(int environment = 0, bool allowTesting = false,
        string codeVersion = "release", bool codeCheckPath = true) => new()
        {
            Environment = environment,
            MiniProgramCodeVersion = codeVersion,
            MiniProgramCodeCheckPath = codeCheckPath,
            AllowPaymentTesting = allowTesting || environment == 1,
            PaymentsEnabled = true,
            ReleasePrerequisitesConfirmed = true,
            AppId = "wx-app",
            AppSecret = "app-secret",
            OfferId = "offer-123",
            AppKey = "app-key",
            MessageToken = "message-token",
            EncodingAesKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()).TrimEnd('='),
            ProductIds = MembershipCatalog.For(true).ToDictionary(x => x.Id, x => x.Id == "cny-0-1" ? "p0" : x.Id == "cny-9-9" ? "prod-990" : x.Id, StringComparer.Ordinal),
        };
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://api.weixin.qq.com/") };
    }
    private sealed class StubHandler : HttpMessageHandler
    {
        public int OrderType { get; set; }
        public int Status { get; set; } = 2;
        public string OrderId { get; set; } = "test-order";
        public int EnvironmentType { get; set; } = 1;
        public bool RequireContentLength { get; set; }
        public string? DeliveryResponse { get; set; }
        public string? QueryResponse { get; set; }
        public byte[] CodeImage { get; set; } = [137, 80, 78, 71, 13, 10, 26, 10];
        public string? CodeBody { get; private set; }
        public string? QueryBody { get; private set; }
        public string? QueryUri { get; private set; }
        public string? DeliveryBody { get; private set; }
        public string? DeliveryUri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            // WeChat rejects HTTP/1.1 chunked JSON with 412 before returning a
            // platform errcode. Inspect the length before buffering the body.
            if (RequireContentLength && request.Content is { } content)
            {
                var declared = content.Headers.ContentLength;
                var bytes = await content.ReadAsByteArrayAsync(token);
                if (declared != bytes.LongLength)
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }
            if (request.RequestUri!.AbsolutePath == "/wxa/getwxacodeunlimit")
            {
                CodeBody = await request.Content!.ReadAsStringAsync(token);
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(CodeImage) };
            }
            if (request.RequestUri!.AbsolutePath == "/xpay/query_order")
            {
                QueryBody = await request.Content!.ReadAsStringAsync(token);
                QueryUri = request.RequestUri.ToString();
                if (QueryResponse is not null)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(QueryResponse, Encoding.UTF8, "application/json") };
            }
            if (request.RequestUri!.AbsolutePath == "/xpay/notify_provide_goods")
            {
                DeliveryBody = await request.Content!.ReadAsStringAsync(token);
                DeliveryUri = request.RequestUri.ToString();
                if (DeliveryResponse is not null)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(DeliveryResponse, Encoding.UTF8, "application/json") };
            }
            var json = request.RequestUri!.AbsolutePath == "/cgi-bin/stable_token" ? "{\"access_token\":\"test-token\",\"expires_in\":7200}" :
                JsonSerializer.Serialize(new { errcode = 0, order = new { order_id = OrderId, env_type = EnvironmentType, status = Status, order_type = OrderType, paid_fee = 990, refund_fee = 990, order_fee = 990, wx_order_id = "wx-order" } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
