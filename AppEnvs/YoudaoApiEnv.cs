using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class YoudaoApiEnv : TranslationApiEnv
{
    public string BaseUrl { get; set; }
    public string AppId { get; set; }
    public string AppSecret { get; set; }
    public YoudaoApiEnv()
    {
        BaseUrl = Env.GetString("YOUDAOU_API_BASE_URL", "");
        AppId = Env.GetString("YOUDAOU_API_APP_ID", "");
        AppSecret = Env.GetString("YOUDAOU_API_APP_SECRET", "");
    }
}


