using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class ChatApiEnv : AppEnvBase
{
    public int TokensLimit { get; set; }

    public ChatApiEnv()
    {
        TokensLimit = Env.GetInt("CHAT_API_TOKENS_LIMIT", 0);
    }
}


