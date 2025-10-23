using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class ChatApiEnv : AppEnvBase
{
    public decimal UserCostLimit { get; set; }

    public ChatApiEnv()
    {
        UserCostLimit = Env.GetInt("CHAT_API_USER_COST_LIMIT", 1);
    }
}


