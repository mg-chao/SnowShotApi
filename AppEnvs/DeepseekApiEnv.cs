using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class DeepseekApiEnv : ChatApiEnv
{
    public string BaseUrl { get; set; }
    public string Name { get; set; }
    public string Key { get; set; }

    public DeepseekApiEnv()
    {
        BaseUrl = Env.GetString("DEEPSEEK_API_BASE_URL", "");
        Name = Env.GetString("DEEPSEEK_API_NAME", "");
        Key = Env.GetString("DEEPSEEK_API_KEY", "");
    }
}


