using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class YoudaoApiEnv : AppEnvBase
{
    public string BaseUrl { get; set; }
    public string AppId { get; set; }
    public string AppSecret { get; set; }
    public int ContentLengthLimit { get; set; }

    public YoudaoApiEnv()
    {
        BaseUrl = Env.GetString("YOUDAOU_API_BASE_URL", "");
        AppId = Env.GetString("YOUDAOU_API_APP_ID", "");
        AppSecret = Env.GetString("YOUDAOU_API_APP_SECRET", "");
        ContentLengthLimit = Env.GetInt("YOUDAOU_API_CONTENT_LENGTH_LIMIT", 1000000);
    }
}


