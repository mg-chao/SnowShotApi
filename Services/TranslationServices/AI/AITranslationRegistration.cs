using Microsoft.Extensions.Options;

namespace SnowShotApi.Services.TranslationServices.AI;

public static class AITranslationHttpClientNames
{
    public static string ForModel(string modelId) => $"AITranslation:{modelId}";
}

public static class AITranslationRegistration
{
    public static IServiceCollection AddAITranslation(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        var options = configuration
            .GetSection(AITranslationOptions.SectionName)
            .Get<AITranslationOptions>() ?? new AITranslationOptions();

        options.Providers = new Dictionary<string, AITranslationProviderOptions>(
            options.Providers,
            StringComparer.OrdinalIgnoreCase);
        options.Models = new Dictionary<string, AITranslationModelOptions>(
            options.Models,
            StringComparer.OrdinalIgnoreCase);

        var validation = new AITranslationOptionsValidator(environment).Validate(null, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                AITranslationOptions.SectionName,
                typeof(AITranslationOptions),
                validation.Failures);
        }

        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TranslationQuotaClock>();
        services.AddSingleton<AITranslationTelemetry>();
        services.AddSingleton<AITranslationModelRegistry>();
        services.AddSingleton<OpenAICompatibleTranslationClient>();
        services.AddSingleton<IAITranslationService, AITranslationService>();
        services.AddHostedService<TranslationReservationCleanupService>();

        foreach (var modelId in options.Models
                     .Where(entry => entry.Value.Enabled)
                     .Select(entry => entry.Key))
        {
            services.AddHttpClient(AITranslationHttpClientNames.ForModel(modelId), client =>
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SnowShotApi-AITranslation/1.0");
                });
        }

        return services;
    }
}
