using SnowShotApi.Data;
using SnowShotApi.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnowShotApi.AppEnvs;
using SnowShotApi.Utiles;
using SnowShotApi.Services.OrderServices;
using SnowShotApi.Controllers.TranslationControllers;
using System.Net.Http;
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
        var sign = GenerateSign(request.Content, salt, curtime);

        var parameters = new Dictionary<string, string>
        {
            { "q", request.Content },
            { "from", request.From },
            { "to", request.To },
            { "appKey", _youdaoApiEnv.AppId },
            { "salt", salt },
            { "sign", sign },
            { "signType", "v3" },
            { "curtime", curtime },
            { "domain", request.Domain }
        };

        try
        {
            // 设置超时取消令牌
            using var cts = new CancellationTokenSource(TranslationService.DefaultTimeout);

            var clientResponse = await _httpClient.PostAsync(
                $"{_youdaoApiEnv.BaseUrl}api",
                new FormUrlEncodedContent(parameters),
                cts.Token
            );

            clientResponse.EnsureSuccessStatusCode();
            var result = await clientResponse.Content.ReadAsStringAsync(cts.Token);

            var translationResponse = JsonSerializer.Deserialize<YoudaoTranslationResponse>(result);

            if (translationResponse == null || translationResponse.ErrorCode != "0")
            {
                return null;
            }

            var translationFromTo = translationResponse.FromJoinTo.Split('2');
            var translationFrom = translationFromTo[0];
            var translationTo = translationFromTo[1];

            var translationContent = translationResponse.Translation?.FirstOrDefault() ?? string.Empty;
            var res = new TranslateResult(translationFrom, translationTo);

            AppControllerBase.DelatInit(response);
            await AppControllerBase.DelatStreamSuccess(response, new TranslateResponseData(translationContent, res.From, res.To));

            return res;
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

public class YoudaoTranslationResponse
{
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public List<string>? Translation { get; set; } = [];

    [JsonPropertyName("l")]
    public string FromJoinTo { get; set; } = string.Empty;
}
