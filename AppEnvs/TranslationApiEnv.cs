using DotNetEnv;

namespace SnowShotApi.AppEnvs;

public class TranslationApiEnv : AppEnvBase
{
    public int ContentLengthLimit { get; set; }

    public TranslationApiEnv()
    {
        ContentLengthLimit = Env.GetInt("TRANSLATION_API_CONTENT_LENGTH_LIMIT", 1000000);
    }
}


