using System.ComponentModel.DataAnnotations;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Admission;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Identity;
using SnowShot.Infrastructure.Operations;
using SnowShot.Infrastructure.Persistence;
using SnowShot.Infrastructure.Providers;
using SnowShot.Infrastructure.Telemetry;
using SnowShot.Infrastructure.Time;
using StackExchange.Redis;

namespace SnowShot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSnowShotInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var secureEnvironment = !environment.IsDevelopment();
        var configuredModelNames = configuration.GetSection($"{ProviderModelsOptions.SectionName}:Models")
            .GetChildren().Select(value => value.Key).ToArray();
        var redisConfiguration = RedisDeploymentPolicy.Build(configuration, secureEnvironment);
        Configure<ConnectionOptions>(services, configuration, ConnectionOptions.SectionName, options =>
            !secureEnvironment || !string.IsNullOrWhiteSpace(options.Redis), "Redis is required outside Development.");
        Configure<IdentityOptions>(services, configuration, IdentityOptions.SectionName, options =>
        {
            try
            {
                return options.CurrentKey.Length >= 32 && (options.PreviousKey is null || options.PreviousKey.Length >= 32) &&
                    (!secureEnvironment || (options.CurrentKey.Any(value => value != 0) &&
                        (options.PreviousKey is null || options.PreviousKey.Any(value => value != 0))));
            }
            catch (ValidationException) { return false; }
        }, "Current and previous identity HMAC keys must be valid, strong, and non-development outside Development.");
        Configure<PolicyOptions>(services, configuration, PolicyOptions.SectionName, options =>
        {
            try { _ = options.Build(configuredModelNames); return true; } catch (Exception exception) when (exception is PolicyValidationException or ArgumentException) { return false; }
        }, "Policy has invalid or inconsistent limits or pricing.");
        Configure<ChatProviderOptions>(services, configuration, ChatProviderOptions.SectionName, _ => true,
            "Invalid chat provider limits.");
        Configure<ProviderCircuitOptions>(services, configuration, ProviderCircuitOptions.SectionName,
            options => options.InitialBreakSeconds <= options.MaximumBreakSeconds,
            "Invalid provider circuit breaker configuration.");
        Configure<TranslationProviderOptions>(services, configuration, TranslationProviderOptions.SectionName,
            options => options.InitialRetryDelayMilliseconds <= options.MaximumRetryDelayMilliseconds &&
                options.LogicalModels.Count > 0 &&
                options.LogicalModels.All(model => !string.IsNullOrWhiteSpace(model)) &&
                options.LogicalModels.Distinct(StringComparer.Ordinal).Count() == options.LogicalModels.Count,
            "Invalid translation provider limits or retry delays.");
        services.AddOptions<ProviderModelsOptions>().Bind(configuration.GetSection(ProviderModelsOptions.SectionName))
            .Validate(options =>
            {
                try
                {
                    var translation = configuration.GetSection(TranslationProviderOptions.SectionName)
                        .Get<TranslationProviderOptions>() ?? new();
                    _ = new ProviderModelCatalog(options, translation, secureEnvironment);
                    return true;
                }
                catch (InvalidOperationException) { return false; }
            }, "Provider model access configuration is invalid.").ValidateOnStart();
        Configure<TableWorkerOptions>(services, configuration, TableWorkerOptions.SectionName, options => !secureEnvironment ||
            Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            Existing(options.ClientCertificatePath) && Existing(options.ServerCaCertificatePath),
            "Table worker requires HTTPS, a client certificate, and an explicit server CA outside Development.");
        Configure<RetentionOptions>(services, configuration, RetentionOptions.SectionName,
            options => options.HasValidHierarchy(),
            "Aggregate retention must cover operation retention, and identity retention must cover aggregate retention.");
        Configure<MaintenanceOptions>(services, configuration, MaintenanceOptions.SectionName,
            _ => true, "Invalid maintenance configuration.");

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ConnectionOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<IdentityOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PolicyOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ChatProviderOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ProviderCircuitOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TranslationProviderOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<ProviderModelsOptions>>().Value);
        services.AddSingleton(sp => new ProviderModelCatalog(sp.GetRequiredService<ProviderModelsOptions>(),
            sp.GetRequiredService<TranslationProviderOptions>(), secureEnvironment));
        services.AddSingleton<IChatModelCatalog>(sp => sp.GetRequiredService<ProviderModelCatalog>());
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TableWorkerOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<RetentionOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<MaintenanceOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<PolicyOptions>().Build(
            sp.GetRequiredService<ProviderModelsOptions>().Models.Keys));
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<TranslationProviderOptions>();
            return new TranslationRouting(options.LogicalModels.ToArray(), options.MaximumConcurrentConversations,
                options.MaximumAttemptsPerConversation, TimeSpan.FromSeconds(options.AttemptTimeoutSeconds),
                TimeSpan.FromMilliseconds(options.InitialRetryDelayMilliseconds),
                TimeSpan.FromMilliseconds(options.MaximumRetryDelayMilliseconds));
        });
        services.AddSingleton(sp => new TableRequestLimits(sp.GetRequiredService<TableWorkerOptions>().MaximumUploadBytes));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISystemClock, SystemClock>();
        services.AddSingleton(LifecycleTimeouts.Defaults);
        services.AddSingleton<IOperationTelemetry, OperationTelemetry>();
        services.AddSingleton<ITranslationTelemetry, TranslationTelemetry>();
        services.AddPooledDbContextFactory<SnowShotDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<ConnectionOptions>().SnowShot, postgres => postgres
                .MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema)
                .EnableRetryOnFailure(3)));
        services.AddSingleton<IPrincipalIdentity, HmacPrincipalIdentity>();
        services.AddSingleton<PostgresOperationLedger>();
        services.AddSingleton<IOperationLedger>(provider => provider.GetRequiredService<PostgresOperationLedger>());
        services.AddSingleton<IOperationReconciler, PostgresOperationReconciler>();
        services.AddSingleton<IPersistenceReadinessProbe, PostgresPersistenceReadinessProbe>();
        services.AddSingleton<PostgresPolicyRegistry>();
        if (!(environment.IsDevelopment() && configuration.GetValue<bool>("ContractGeneration")))
            services.AddHostedService<PolicyActivationHostedService>();
        services.AddSingleton<IDependencyHealth, DependencyHealth>();

        if (redisConfiguration is null)
        {
            services.AddSingleton<IAdmissionController, InMemoryAdmissionController>();
            services.AddSingleton<IProviderCircuitRegistry, InMemoryProviderCircuitRegistry>();
            services.AddSingleton<IProviderAccessPool, InMemoryProviderAccessPool>();
        }
        else
        {
            services.AddSingleton(redisConfiguration);
            services.AddSingleton<IConnectionMultiplexer>(provider =>
                ConnectionMultiplexer.Connect(provider.GetRequiredService<ConfigurationOptions>()));
            services.AddSingleton<IAdmissionController, RedisAdmissionController>();
            services.AddSingleton<IProviderCircuitRegistry, RedisProviderCircuitRegistry>();
            services.AddSingleton<IProviderAccessPool, RedisProviderAccessPool>();
        }

        services.AddSingleton<IProviderHttpClientRegistry, ProviderHttpClientRegistry>();
        services.AddHttpClient("table", (provider, client) =>
        {
            client.BaseAddress = new Uri(provider.GetRequiredService<TableWorkerOptions>().BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(provider => CreateTableHandler(provider.GetRequiredService<TableWorkerOptions>()));
        services.AddSingleton<IChatProviderClient, OpenAiChatClient>();
        services.AddSingleton<ITranslationProviderClient, OpenAiTranslationClient>();
        services.AddSingleton<ITableWorkerClient, TableWorkerClient>();
        services.AddSingleton<OperationCoordinator>();
        services.AddSingleton<ChatUseCase>();
        services.AddSingleton<TranslationUseCase>();
        services.AddSingleton<TableUseCase>();
        services.AddSingleton<IReadinessService, ReadinessService>();
        services.AddHostedService<ProviderCircuitInitializationService>();
        services.AddHostedService<ProviderReadinessProbeService>();
        services.AddHostedService<ReconciliationService>();
        services.AddHostedService<RetentionService>();
        return services;
    }

    private static void Configure<T>(IServiceCollection services, IConfiguration configuration, string section,
        Func<T, bool> validation, string message) where T : class => services.AddOptions<T>().Bind(configuration.GetSection(section))
        .ValidateDataAnnotations().Validate(validation, message).ValidateOnStart();

    private static bool Existing(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    private static SocketsHttpHandler CreateTableHandler(TableWorkerOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 3,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = false
        };
        if (!string.IsNullOrWhiteSpace(options.ClientCertificatePath))
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection
                {
                    X509CertificateLoader.LoadPkcs12FromFile(options.ClientCertificatePath, options.ClientCertificatePassword),
                },
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || certificate is null || string.IsNullOrWhiteSpace(options.ServerCaCertificatePath)) return false;
                    using var customChain = new X509Chain();
                    customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    customChain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificateFromFile(options.ServerCaCertificatePath));
                    customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return customChain.Build(new X509Certificate2(certificate));
                },
            };
        }
        return handler;
    }

}
