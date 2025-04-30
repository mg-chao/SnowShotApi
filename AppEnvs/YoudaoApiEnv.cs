using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class YoudaoApiEnv : AppEnvBase
{
    public string BaseUrl { get; set; }
    public string AppId { get; set; }
    public string AppSecret { get; set; }
    public int IpLimit { get; set; }

    public YoudaoApiEnv()
    {
        BaseUrl = Env.GetString("YOUDAOU_API_BASE_URL", "");
        AppId = Env.GetString("YOUDAOU_API_APP_ID", "");
        AppSecret = Env.GetString("YOUDAOU_API_APP_SECRET", "");
        IpLimit = Env.GetInt("YOUDAOU_API_IP_LIMIT", 1000000);
    }
}


