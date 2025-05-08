using SnowShotApi.AppEnvs;
using SnowShotApi.Controllers.TranslationControllers;
using SnowShotApi.RequestValidations;
using OpenAI.Chat;
using System.ClientModel;
using System.Text;
using SnowShotApi.Controllers;

namespace SnowShotApi.Services.TranslationServices;

public interface IDeepseekTranslationService : ITranslationService
{
}

public class DeepseekTranslationService(string modelName) : ModelTranslationServiceBase(modelName), IDeepseekTranslationService
{
    private readonly DeepseekApiEnv _deepseekApiEnv = new();

    protected override string GetApiKey() => _deepseekApiEnv.ApiKey;

    protected override string GetBaseUrl() => _deepseekApiEnv.BaseUrl;
}
