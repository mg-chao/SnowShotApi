using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;

namespace SnowShot.Infrastructure.Providers;

internal sealed class YoudaoBatchTranslationClient(
    IHttpClientFactory clients, YoudaoBatchTranslationOptions options, TimeProvider clock) : IFastTranslationProvider
{
    public const string ClientName = "youdao-batch";
    private static readonly Uri Endpoint = new("https://openapi.youdao.com/v2/api");
    private const int MaximumResponseBytes = 512 * 1024;

    // Response language codes from the Youdao batch API language table.
    private static readonly HashSet<string> ResponseLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "af", "am", "ar", "az", "be", "bg", "bn", "bs", "ca", "ceb", "co", "cs", "cy", "da", "de",
        "el", "en", "eo", "es", "et", "eu", "fa", "fi", "fj", "fr", "fy", "ga", "gd", "gl", "gu",
        "ha", "haw", "he", "hi", "hr", "ht", "hu", "hy", "id", "ig", "is", "it", "ja", "jw", "ka",
        "kk", "km", "kn", "ko", "ku", "ky", "la", "lb", "lo", "lt", "lv", "mg", "mi", "mk", "ml",
        "mn", "mr", "ms", "mt", "mww", "my", "ne", "nl", "no", "ny", "otq", "pa", "pl", "ps", "pt",
        "ro", "ru", "sd", "si", "sk", "sl", "sm", "sn", "so", "sq", "sr-Cyrl", "sr-Latn", "st", "su",
        "sv", "sw", "ta", "te", "tg", "th", "tl", "tlh", "to", "tr", "ty", "uk", "ur", "uz", "vi",
        "xh", "yi", "yo", "yua", "yue", "zh-CHS", "zh-CHT", "zu",
    };

    public bool Enabled => options.Enabled;

    public async Task<FastTranslationProviderResult> TranslateAsync(
        IReadOnlyList<string> content, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var token = timeout.Token;
        var salt = Guid.NewGuid().ToString("N");
        var time = clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var client = clients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(BuildForm(content, sourceLanguage, targetLanguage,
                options.AppKey, options.AppSecret, salt, time)),
        };
        int? status = null;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            status = (int)response.StatusCode;
            if (status == 429)
                return Failure(status, ApplicationErrorCode.RateLimited, "youdao_rate_limited", CostBasis.Exact,
                    response.Headers.RetryAfter?.Delta);
            if (!response.IsSuccessStatusCode)
                return Failure(status, ApplicationErrorCode.ProviderFailure, "youdao_http_error");

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = JsonDocument.Parse(await BoundedStreams.ReadAllAsync(stream, MaximumResponseBytes, token));
            return Parse(document.RootElement, content, sourceLanguage, targetLanguage, status);
        }
        catch (OperationCanceledException)
        {
            return Failure(status, ApplicationErrorCode.DeadlineExceeded, "youdao_timeout");
        }
        catch (HttpRequestException)
        {
            return Failure(status, ApplicationErrorCode.ProviderFailure, "youdao_network");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            return Failure(status, ApplicationErrorCode.ProviderFailure, "youdao_invalid_response");
        }
    }

    internal static IEnumerable<KeyValuePair<string, string>> BuildForm(IReadOnlyList<string> content,
        string from, string to, string appKey, string appSecret, string salt, string time)
    {
        var joined = string.Concat(content);
        var input = joined.Length <= 20 ? joined :
            joined[..10] + joined.Length.ToString(CultureInfo.InvariantCulture) + joined[^10..];
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            appKey + input + salt + time + appSecret)));
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("from", from), new("to", to), new("appKey", appKey), new("salt", salt),
            new("curtime", time), new("signType", "v3"), new("sign", signature),
        };
        if (from == "auto") parameters.Add(new("detectLevel", "1"));
        parameters.AddRange(content.Select(text => new KeyValuePair<string, string>("q", text)));
        return parameters;
    }

    private static FastTranslationProviderResult Parse(JsonElement root, IReadOnlyList<string> content,
        string from, string to, int? status)
    {
        FastTranslationProviderResult Invalid() =>
            Failure(status, ApplicationErrorCode.ProviderFailure, "youdao_invalid_response");

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errorCode", out var errorCode))
            return Invalid();
        var code = errorCode.ValueKind switch
        {
            JsonValueKind.String => errorCode.GetString(),
            JsonValueKind.Number => errorCode.GetRawText(),
            _ => null,
        };
        if (root.TryGetProperty("errorIndex", out var failed) &&
            (failed.ValueKind != JsonValueKind.Array || failed.GetArrayLength() != 0))
            return Failure(status, ApplicationErrorCode.ProviderFailure, "youdao_partial_result");
        if (code is "411" or "412")
            return Failure(status, ApplicationErrorCode.RateLimited, "youdao_rate_limited", CostBasis.Exact);
        if (code != "0")
            return Failure(status, ApplicationErrorCode.ProviderFailure, "youdao_rejected",
                code is "101" or "102" or "103" or "108" or "110" or "202" or "206" or "207" or "401"
                    ? CostBasis.Exact : CostBasis.Unknown);
        if (!root.TryGetProperty("translateResults", out var results) || results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() != content.Count) return Invalid();

        var items = new List<FastTranslationItem>(content.Count);
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "query") != content[items.Count] ||
                Text(item, "translation") is not { } translation || string.IsNullOrWhiteSpace(translation) ||
                Text(item, "type")?.Split('2') is not [var source, var target] ||
                !ResponseLanguages.Contains(source) ||
                !target.Equals(to, StringComparison.OrdinalIgnoreCase) ||
                (from != "auto" && !source.Equals(from, StringComparison.OrdinalIgnoreCase)))
                return Invalid();
            items.Add(new(translation, source, target));
        }
        return new(items, status);
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static FastTranslationProviderResult Failure(int? status, ApplicationErrorCode code, string outcome,
        CostBasis basis = CostBasis.Unknown, TimeSpan? retryAfter = null) =>
        new([], status, new(code, outcome, retryAfter), basis);
}
