using System.Text.Json;
using System.Text.Json.Serialization;
using SnowShotApi.AppEnvs;
using SnowShotApi.Utiles;
using SnowShotApi.Controllers.TranslationControllers;
using SnowShotApi.Controllers;

namespace SnowShotApi.Services.TranslationServices;

public interface IYoudaoTranslationService : ITranslationService
{
}

public class YoudaoTranslationService(HttpClient httpClient) : IYoudaoTranslationService
{
    private readonly YoudaoApiEnv _youdaoApiEnv = new();
    private readonly HttpClient _httpClient = httpClient;

    public async Task<TranslateResult?> TranslateAsync(TranslationRequest request, HttpResponse response, long userId)
    {
        var salt = Guid.NewGuid().ToString();
        var curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sign = GenerateSign(string.Join("", request.Content), salt, curtime);

        var parameters = new List<KeyValuePair<string, string>>();
        foreach (var content in request.Content)
        {
            parameters.Add(new KeyValuePair<string, string>("q", content));
        }
        parameters.Add(new KeyValuePair<string, string>("from", request.From));
        parameters.Add(new KeyValuePair<string, string>("to", request.To));
        parameters.Add(new KeyValuePair<string, string>("appKey", _youdaoApiEnv.AppId));
        parameters.Add(new KeyValuePair<string, string>("salt", salt));
        parameters.Add(new KeyValuePair<string, string>("sign", sign));
        parameters.Add(new KeyValuePair<string, string>("signType", "v3"));
        parameters.Add(new KeyValuePair<string, string>("curtime", curtime));
        parameters.Add(new KeyValuePair<string, string>("domain", request.Domain));

        try
        {
            // 设置超时取消令牌
            using var cts = new CancellationTokenSource(TranslationService.DefaultTimeout);

            var clientResponse = await _httpClient.PostAsync(
                $"{_youdaoApiEnv.BaseUrl}v2/api",
                new FormUrlEncodedContent(parameters),
                cts.Token
            );

            clientResponse.EnsureSuccessStatusCode();
            var result = await clientResponse.Content.ReadAsStringAsync();

            var translationResponse = JsonSerializer.Deserialize<YoudaoTranslationResponse>(result);

            if (translationResponse == null || translationResponse.ErrorCode != "0")
            {
                return null;
            }

            var translationResults = translationResponse.TranslateResults.Select(t => new TranslationContent(t.Translation)).ToList();

            return new TranslateResult(translationResults, request.From, request.To);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private string GenerateSign(string content, string salt, string curtime)
    {
        var input = content.Length <= 20 ? content : content[..10] + content.Length + content[^10..];
        var signStr = _youdaoApiEnv.AppId + input + salt + curtime + _youdaoApiEnv.AppSecret;
        return AppUtiles.GenerateSHA256(signStr);
    }
}
public class YoudaoTranslateResult(string query, string translation)
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = query;

    [JsonPropertyName("translation")]
    public string Translation { get; set; } = translation;
}

public class YoudaoTranslationResponse
{
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; } = string.Empty;

    [JsonPropertyName("errorIndex")]
    public List<int> ErrorIndex { get; set; } = [];

    [JsonPropertyName("translateResults")]
    public List<YoudaoTranslateResult> TranslateResults { get; set; } = [];
}
