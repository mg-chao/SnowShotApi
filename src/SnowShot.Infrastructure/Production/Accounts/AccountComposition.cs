using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SnowShot.Application;

namespace SnowShot.Infrastructure.Accounts;

internal static class AccountComposition
{
    internal static IServiceCollection AddSnowShotAccounts(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<WechatAccountOptions>().Bind(configuration.GetSection("Accounts:Wechat"))
            .Validate(options => options.MiniProgramCodeVersion is "release" or "trial" or "develop",
                "WeChat Mini Program code version must be release, trial or develop.")
            .Validate(options => environment.IsDevelopment() || options.MiniProgramCodeVersion == "release" && options.MiniProgramCodeCheckPath,
                "Non-release Mini Program codes and disabled path checks are only allowed in Development.")
            .Validate(options => !options.AllowPaymentTesting || environment.IsDevelopment(),
                "WeChat manual payment testing is only allowed in the Development environment.")
            .Validate(options => options.Environment == 0 || environment.IsDevelopment() && options.Environment == 1 && options.AllowPaymentTesting,
                "WeChat sandbox payments require the Development environment and AllowPaymentTesting=true.")
            .Validate(options => !options.PaymentsEnabled || options.IsPaymentConfigured,
                "WeChat payments require all published products, credentials, encrypted message configuration and confirmed release prerequisites.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<WechatAccountOptions>>().Value);
        services.AddOptions<AccountEncryptionOptions>().Bind(configuration.GetSection("Accounts:Encryption"))
            .Validate(options => ValidEncryption(options, configuration.GetValue<bool>("Accounts:Wechat:PaymentsEnabled")), "Account payments require a dedicated 32-byte encryption key and valid retained previous keys.").ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AccountEncryptionOptions>>().Value);
        services.AddSingleton(sp => new AccountSecrets(sp.GetRequiredService<Configuration.IdentityOptions>(), sp.GetRequiredService<AccountEncryptionOptions>()));
        services.AddOptions<PasswordHasherOptions>().Configure(options => options.IterationCount = configuration.GetValue<int?>("Accounts:PasswordHashIterations") ?? 210_000)
            .Validate(options => options.IterationCount is >= 100_000 and <= 2_000_000, "Password hash iterations must be between 100000 and 2000000.").ValidateOnStart();
        services.AddSingleton<PasswordHasher<Persistence.AccountEntity>>();
        services.AddHttpClient("wechat-account", client => { client.BaseAddress = new Uri("https://api.weixin.qq.com/"); client.Timeout = TimeSpan.FromSeconds(15); }).RemoveAllLoggers();
        services.AddSingleton<IWechatAccountPlatform, WechatAccountPlatform>();
        services.AddSingleton<PostgresAccountStore>();
        services.AddSingleton<PostgresPurchaseRequests>();
        services.AddSingleton<PostgresAccountEventInbox>();
        services.AddSingleton<PostgresAccountRetryQueue>();
        services.AddSingleton<PostgresAccountSessions>();
        services.AddSingleton<PostgresAccountPurchases>();
        services.AddSingleton<PostgresAccountPayments>();
        services.AddSingleton<PostgresAccountDeletion>();
        services.AddSingleton<PostgresAccountService>();
        services.AddSingleton<IAccountService>(sp => sp.GetRequiredService<PostgresAccountService>());
        services.AddSingleton<IAccountSessionResolver>(sp => sp.GetRequiredService<PostgresAccountSessions>());
        if (!(environment.IsDevelopment() && configuration.GetValue<bool>("ContractGeneration"))) services.AddHostedService<AccountReconciliationService>();
        return services;
    }
    private static bool ValidEncryption(AccountEncryptionOptions options, bool required)
    {
        if (string.IsNullOrWhiteSpace(options.KeyBase64)) return !required && options.PreviousKeysBase64.Length == 0;
        try { return options.PreviousKeysBase64.Prepend(options.KeyBase64).All(key => Convert.FromBase64String(key).Length == 32); }
        catch (FormatException) { return false; }
    }
}

internal sealed partial class AccountReconciliationService(PostgresAccountPayments accounts, ILogger<AccountReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await accounts.ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { Failed(logger, exception); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
    [LoggerMessage(Level = LogLevel.Error, EventId = 5501, Message = "Account payment reconciliation failed; unresolved orders remain pending")]
    private static partial void Failed(ILogger logger, Exception exception);
}
