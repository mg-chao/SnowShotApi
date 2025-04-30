using System.Text;
using SnowShotApi.Data;
using SnowShotApi.Models;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnowShotApi.AppEnvs;
using SnowShotApi.Utiles;

namespace SnowShotApi.Services.TranslationServices;

public interface IYoudaoTranslationService : ITranslationService
{
}

public class YoudaoTranslationService
(ApplicationDbContext context, HttpClient httpClient, IUserOrderService userOrderService, ITranslationOrderStatsService translationOrderStatsService) :
TranslationService(context, httpClient, userOrderService, translationOrderStatsService), IYoudaoTranslationService
{
    private readonly YoudaoApiEnv _youdaoApiEnv = new();
    public async Task<string?> TranslateAsync(long userId, string content, string from, string to, string domain)
    {
        await CreateTranslationOrderAsync(userId, UserTranslationType.Youdao, content);

        var salt = Guid.NewGuid().ToString();
        var curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sign = GenerateSign(content, salt, curtime);

        var parameters = new Dictionary<string, string>
        {
            { "q", content },
            { "from", from },
            { "to", to },
            { "appKey", _youdaoApiEnv.AppId },
            { "salt", salt },
            { "sign", sign },
            { "signType", "v3" },
            { "curtime", curtime },
            { "domain", domain }
        };

        var response = await _httpClient.PostAsync("https://openapi.youdao.com/api", new FormUrlEncodedContent(parameters));
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadAsStringAsync();

        var translationResponse = JsonSerializer.Deserialize<YoudaoTranslationResponse>(result);

        if (translationResponse?.ErrorCode != "0")
        {
            return null;
        }

        return translationResponse.Translation?.FirstOrDefault();
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
}
