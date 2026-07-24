using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class DeepSeekApiEnv : TranslationApiEnv
{
    public string BaseUrl { get; set; }
    public string ApiKey { get; set; }

    public DeepSeekApiEnv()
    {
        BaseUrl = Env.GetString("DEEPSEEK_API_BASE_URL", "https://api.deepseek.com/");
        ApiKey = Env.GetString("DEEPSEEK_API_KEY", "");
    }
}
