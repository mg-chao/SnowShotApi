using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class QwenApiEnv : ChatApiEnv
{
    public string BaseUrl { get; set; }
    public string ApiKey { get; set; }

    public QwenApiEnv()
    {
        BaseUrl = Env.GetString("QWEN_API_BASE_URL", "https://dashscope.aliyuncs.com/");
        ApiKey = Env.GetString("QWEN_API_KEY", "");
    }
}


