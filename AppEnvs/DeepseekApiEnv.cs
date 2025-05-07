using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class DeepseekApiEnv : ChatApiEnv
{
    public string BaseUrl { get; set; }
    public string ApiName { get; set; }
    public string ApiKey { get; set; }

    public DeepseekApiEnv()
    {
        BaseUrl = Env.GetString("DEEPSEEK_API_BASE_URL", "");
        ApiName = Env.GetString("DEEPSEEK_API_NAME", "");
        ApiKey = Env.GetString("DEEPSEEK_API_KEY", "");
    }
}


