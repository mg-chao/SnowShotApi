using SnowShotApi.AppEnvs;

namespace SnowShotApi.Services.TranslationServices;

public interface IOpenAITranslationService : ITranslationService
{
}

public class QwenTranslationService(string modelName) : ModelTranslationServiceBase(modelName), IOpenAITranslationService
{
    private readonly QwenApiEnv _qwenApiEnv = new();

    protected override string GetApiKey() => _qwenApiEnv.ApiKey;

    protected override string GetBaseUrl() => $"{_qwenApiEnv.BaseUrl}compatible-mode/v1";
}