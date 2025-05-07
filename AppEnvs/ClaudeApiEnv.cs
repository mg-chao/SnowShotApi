using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class ClaudeApiEnv : ChatApiEnv
{
    public string BaseUrl { get; }
    public string ApiKey { get; }
    public string ApiVersion { get; }
    public int HaikuTokensLimit { get; }
    public int SonnetTokensLimit { get; }

    public ClaudeApiEnv()
    {
        BaseUrl = Env.GetString("CLAUDE_API_BASE_URL", "");
        ApiKey = Env.GetString("CLAUDE_API_KEY", "");
        ApiVersion = Env.GetString("CLAUDE_ANTHROPIC_VERSION", "");
        TokensLimit = Env.GetInt("CHAT_API_CLAUDE_TOKENS_LIMIT", 0);
        HaikuTokensLimit = Env.GetInt("CHAT_API_CLAUDE_HAIKU_TOKENS_LIMIT", 0);
        SonnetTokensLimit = Env.GetInt("CHAT_API_CLAUDE_SONNET_TOKENS_LIMIT", 0);
    }
}


