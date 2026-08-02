using System.ComponentModel.DataAnnotations;
using SnowShot.Domain;

namespace SnowShot.Infrastructure.Configuration;

public sealed class ConnectionOptions
{
    public const string SectionName = "ConnectionStrings";
    [Required] public string SnowShot { get; init; } = string.Empty;
    public string? Redis { get; init; }
}

public sealed class IdentityOptions
{
    public const string SectionName = "Identity";
    [Required] public string HmacKeyBase64 { get; init; } = string.Empty;
    public string? PreviousHmacKeyBase64 { get; init; }
    public byte[] CurrentKey => Decode(HmacKeyBase64, nameof(HmacKeyBase64));
    public byte[]? PreviousKey => string.IsNullOrWhiteSpace(PreviousHmacKeyBase64) ? null : Decode(PreviousHmacKeyBase64, nameof(PreviousHmacKeyBase64));

    private static byte[] Decode(string value, string name)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length >= 32 ? bytes : throw new ValidationException($"{name} must decode to at least 32 bytes.");
        }
        catch (FormatException exception) { throw new ValidationException($"{name} must be valid base64.", exception); }
    }
}

public sealed class PolicyOptions
{
    public const string SectionName = "Policy";
    [Range(1, long.MaxValue)] public long Revision { get; init; } = 1;
    [Range(1, long.MaxValue)] public long PrincipalDailyAllowanceNanoYuan { get; init; } = 3_000_000_000;
    [Range(1, long.MaxValue)] public long DailyOperatorBudgetNanoYuan { get; init; } = 50_000_000_000;
    [Range(1, long.MaxValue)] public long MonthlyOperatorBudgetNanoYuan { get; init; } = 200_000_000_000;
    [Range(2, 3600)] public int ActiveLeaseTtlSeconds { get; init; } = 30;
    [Range(1, 3599)] public int LeaseRenewalSeconds { get; init; } = 10;
    public Dictionary<string, ResourcePricingOptions> Pricing { get; init; } = [];
    public Dictionary<string, ResourcePolicyOptions> Resources { get; init; } = [];

    public ServicePolicy Build(IEnumerable<string>? additionalResources = null)
    {
        var defaults = ServicePolicy.Defaults();
        var defaultsByResource = defaults.ResourcePolicies.ToDictionary(value => value.Resource, StringComparer.Ordinal);
        var additional = (additionalResources ?? []).ToHashSet(StringComparer.Ordinal);
        var allowed = defaultsByResource.Keys.Concat(additional).ToHashSet(StringComparer.Ordinal);
        if (Resources.Keys.Except(allowed, StringComparer.Ordinal).Any())
            throw new PolicyValidationException("Policy contains an unknown resource identifier.");
        var missing = additional.Except(defaultsByResource.Keys, StringComparer.Ordinal)
            .Except(Resources.Keys, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new PolicyValidationException($"Policy is missing limits for configured resource '{missing[0]}'.");
        if (Pricing.Keys.Except(allowed, StringComparer.Ordinal).Any())
            throw new PolicyValidationException("Policy contains an unknown pricing resource identifier.");
        var missingPricing = allowed.Except(Pricing.Keys, StringComparer.Ordinal).ToArray();
        if (missingPricing.Length > 0)
            throw new PolicyValidationException($"Policy is missing pricing for resource '{missingPricing[0]}'.");

        var resources = defaults.ResourcePolicies.Select(current =>
        {
            var price = Price(current.Resource);
            return Resources.TryGetValue(current.Resource, out var configured)
                ? BuildResource(current.Resource, configured, price, current)
                : current with { Price = price };
        }).ToArray();
        resources = resources.Concat(Resources
            .Where(pair => !defaultsByResource.ContainsKey(pair.Key))
            .Select(pair => BuildResource(pair.Key, pair.Value, Price(pair.Key), null)))
            .ToArray();
        return new ServicePolicy(Revision, resources, new(PrincipalDailyAllowanceNanoYuan), new(DailyOperatorBudgetNanoYuan),
            new(MonthlyOperatorBudgetNanoYuan), TimeSpan.FromSeconds(ActiveLeaseTtlSeconds), TimeSpan.FromSeconds(LeaseRenewalSeconds), additional);
    }

    private UnitPrice Price(string resource)
    {
        if (!Pricing.TryGetValue(resource, out var configured) || configured is null)
            throw new PolicyValidationException($"Policy is missing pricing for resource '{resource}'.");
        return new(new(configured.InputRateNanoYuan), new(configured.OutputRateNanoYuan));
    }

    private static ResourcePolicy BuildResource(string resource, ResourcePolicyOptions configured, UnitPrice price,
        ResourcePolicy? fallback)
    {
        if (configured is null)
            throw new PolicyValidationException($"Policy resource '{resource}' is empty.");
        var admission = new AdmissionPolicy(configured.RequestsPerMinute, configured.PerPrincipalConcurrency,
            configured.GlobalConcurrency, configured.GlobalQueueLength, TimeSpan.FromSeconds(configured.QueueWaitSeconds))
        {
            PerPrincipalQueueLength = configured.PerPrincipalQueueLength ??
                Math.Min(configured.PerPrincipalConcurrency, configured.GlobalQueueLength),
        };
        var deadline = TimeSpan.FromSeconds(configured.ExecutionDeadlineSeconds);
        var maximum = new NanoYuan(configured.OperatorMaximumNanoYuan);
        return fallback is null
            ? new ResourcePolicy(resource, price, admission, deadline, maximum)
            : fallback with { Price = price, Admission = admission, ExecutionDeadline = deadline, OperatorMaximum = maximum };
    }
}

public sealed class ResourcePricingOptions
{
    [Range(0, long.MaxValue)] public long InputRateNanoYuan { get; init; }
    [Range(0, long.MaxValue)] public long OutputRateNanoYuan { get; init; }
}

public sealed class ResourcePolicyOptions
{
    [Range(1, int.MaxValue)] public int RequestsPerMinute { get; init; }
    [Range(1, int.MaxValue)] public int PerPrincipalConcurrency { get; init; }
    [Range(1, int.MaxValue)] public int GlobalConcurrency { get; init; }
    [Range(0, int.MaxValue)] public int GlobalQueueLength { get; init; }
    [Range(0, int.MaxValue)] public int? PerPrincipalQueueLength { get; init; }
    [Range(0, 3600)] public int QueueWaitSeconds { get; init; }
    [Range(1, 3600)] public int ExecutionDeadlineSeconds { get; init; }
    [Range(1, long.MaxValue)] public long OperatorMaximumNanoYuan { get; init; }
}

public sealed class ChatProviderOptions
{
    public const string SectionName = "Providers:Chat";
    [Range(1024, 1_048_576)] public int MaximumSseLineBytes { get; init; } = 262_144;
    [Range(1024, 1_048_576)] public int MaximumErrorBodyBytes { get; init; } = 65_536;
}

public sealed class TranslationProviderOptions
{
    public const string SectionName = "Providers:Translation";
    [Required, MinLength(1)] public List<string> LogicalModels { get; init; } = [];
    [Range(1024, 4_194_304)] public int MaximumResponseBytes { get; init; } = 1_048_576;
    [Range(1, 32)] public int MaximumConcurrentConversations { get; init; } = 4;
    [Range(1, 5)] public int MaximumAttemptsPerConversation { get; init; } = 3;
    [Range(1, 3600)] public int AttemptTimeoutSeconds { get; init; } = 90;
    [Range(1, 60_000)] public int InitialRetryDelayMilliseconds { get; init; } = 250;
    [Range(1, 60_000)] public int MaximumRetryDelayMilliseconds { get; init; } = 2_000;
}

public sealed class ProviderModelsOptions
{
    public const string SectionName = "Providers";
    public Dictionary<string, CloudProviderOptions> CloudProviders { get; init; } = [];
    public Dictionary<string, ProviderModelOptions> Models { get; init; } = [];
}

public sealed class CloudProviderOptions
{
    [Required, Url] public string Endpoint { get; init; } = string.Empty;
    [Required] public string ApiKey { get; init; } = string.Empty;
    public bool? TranslationEnableThinking { get; init; }
}

public sealed class ProviderModelOptions
{
    [Range(0, int.MaxValue)] public int Order { get; init; } = int.MaxValue;
    public bool Thinking { get; init; } = true;
    public bool SupportVision { get; init; }
    public Dictionary<string, ProviderAccessOptions> Accesses { get; init; } = [];
}

public sealed class ProviderAccessOptions
{
    [Required] public string Provider { get; init; } = string.Empty;
    [Required] public string UpstreamModel { get; init; } = string.Empty;
    [Range(1, int.MaxValue)] public int MaxConcurrentRequests { get; init; }
}

public sealed class TableWorkerOptions
{
    public const string SectionName = "Providers:Table";
    [Required, Url] public string BaseUrl { get; init; } = string.Empty;
    [Range(1, 500 * 1024)] public long MaximumUploadBytes { get; init; } = 500 * 1024;
    [Range(1024, 16_777_216)] public int MaximumResponseBytes { get; init; } = 2 * 1024 * 1024;
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificatePassword { get; init; }
    public string? ServerCaCertificatePath { get; init; }
}

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";
    [Range(1, 3650)] public int OperationDays { get; init; } = 90;
    [Range(1, 3650)] public int AggregateDays { get; init; } = 400;
    [Range(1, 3650)] public int IdentityDays { get; init; } = 400;

    public bool HasValidHierarchy() => AggregateDays >= OperationDays && IdentityDays >= AggregateDays;
}

public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";
    [Range(1, 10_000)] public int ReconciliationBatchSize { get; init; } = 100;
    [Range(1, 3600)] public int ReconciliationIdleSeconds { get; init; } = 30;
    [Range(10, 60_000)] public int BusyDelayMilliseconds { get; init; } = 100;
    [Range(1, 300)] public int FailureDelaySeconds { get; init; } = 5;
    [Range(100, 100_000)] public int RetentionBatchSize { get; init; } = 10_000;
    [Range(1, 168)] public int RetentionIntervalHours { get; init; } = 6;
    [Range(1, 86_400)] public int DependencyStatusStaleSeconds { get; init; } = 300;
}
