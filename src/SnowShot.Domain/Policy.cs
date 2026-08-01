using System.Security.Cryptography;
using System.Text;

namespace SnowShot.Domain;

public static class Resources
{
    public const string Translation = "translation";
    public const string QwenFlash = "qwen-flash";
    public const string QwenPlus = "qwen-plus";
    public const string QwenVisionFlash = "qwen3-vl-flash";
    public const string DeepSeekV4 = "deepseek-v4-flash";
    public const string TableExtraction = "table-extraction";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Translation, QwenFlash, QwenPlus, QwenVisionFlash,
        DeepSeekV4, TableExtraction,
    };
}

public static class Capabilities
{
    public static readonly IReadOnlySet<string> TranslationLanguages = new HashSet<string>(StringComparer.Ordinal)
    {
        "en", "zh-CHS", "zh-CHT", "es", "fr", "ar", "de", "it", "ja", "pt", "ru", "tr",
    };

    public static readonly IReadOnlySet<string> TranslationDomains = new HashSet<string>(StringComparer.Ordinal)
    {
        "general", "computers", "medicine", "finance", "game",
    };
}

public static class AccountingLimits
{
    public const long MaximumUnitsPerDimension = 1_000_000_000;
}

public sealed record AdmissionPolicy(
    int RequestsPerMinute,
    int PerPrincipalConcurrency,
    int GlobalConcurrency,
    int GlobalQueueLength,
    TimeSpan QueueWait)
{
    public int PerPrincipalQueueLength { get; init; } = Math.Min(PerPrincipalConcurrency, GlobalQueueLength);
}

public sealed record ResourcePolicy(
    string Resource,
    UnitPrice Price,
    AdmissionPolicy Admission,
    TimeSpan ExecutionDeadline,
    NanoYuan OperatorMaximum);

public sealed class ServicePolicy
{
    private readonly IReadOnlyDictionary<string, ResourcePolicy> _resources;
    private readonly HashSet<string> _knownResources;

    public ServicePolicy(
        long revision,
        IEnumerable<ResourcePolicy> resources,
        NanoYuan principalDailyAllowance,
        NanoYuan dailyOperatorBudget,
        NanoYuan monthlyOperatorBudget,
        TimeSpan activeLeaseTtl,
        TimeSpan leaseRenewalInterval,
        IEnumerable<string>? additionalResources = null)
    {
        Revision = revision;
        _resources = resources.ToDictionary(value => value.Resource, StringComparer.Ordinal);
        _knownResources = Resources.All.Concat(additionalResources ?? []).ToHashSet(StringComparer.Ordinal);
        PrincipalDailyAllowance = principalDailyAllowance;
        DailyOperatorBudget = dailyOperatorBudget;
        MonthlyOperatorBudget = monthlyOperatorBudget;
        ActiveLeaseTtl = activeLeaseTtl;
        LeaseRenewalInterval = leaseRenewalInterval;
        Validate();
        Fingerprint = ComputeFingerprint();
    }

    public long Revision { get; }
    public NanoYuan PrincipalDailyAllowance { get; }
    public NanoYuan DailyOperatorBudget { get; }
    public NanoYuan MonthlyOperatorBudget { get; }
    public TimeSpan ActiveLeaseTtl { get; }
    public TimeSpan LeaseRenewalInterval { get; }
    public string Fingerprint { get; }
    public string CanonicalDocument => Canonicalize();
    public IEnumerable<ResourcePolicy> ResourcePolicies => _resources.Values;

    public ResourcePolicy Get(string resource) => _resources.TryGetValue(resource, out var value)
        ? value
        : throw new KeyNotFoundException($"No policy is configured for '{resource}'.");

    public void Validate()
    {
        if (Revision <= 0)
        {
            throw new PolicyValidationException("Policy revision must be positive.");
        }
        if (_resources.Count == 0 || _resources.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new PolicyValidationException("At least one named resource policy is required.");
        }
        if (PrincipalDailyAllowance == NanoYuan.Zero || DailyOperatorBudget == NanoYuan.Zero || MonthlyOperatorBudget == NanoYuan.Zero)
        {
            throw new PolicyValidationException("Allowance and operator budgets must be positive.");
        }
        if (DailyOperatorBudget > MonthlyOperatorBudget)
        {
            throw new PolicyValidationException("The daily operator budget cannot exceed the monthly budget.");
        }
        if (ActiveLeaseTtl <= TimeSpan.Zero || LeaseRenewalInterval <= TimeSpan.Zero || LeaseRenewalInterval >= ActiveLeaseTtl)
        {
            throw new PolicyValidationException("Lease renewal must be positive and shorter than the active lease TTL.");
        }
        foreach (var resource in _resources.Values)
        {
            var admission = resource.Admission;
            if (!_knownResources.Contains(resource.Resource))
            {
                throw new PolicyValidationException($"Resource policy '{resource.Resource}' is not recognized.");
            }
            if (resource.ExecutionDeadline <= TimeSpan.Zero || resource.ExecutionDeadline < ActiveLeaseTtl ||
                resource.OperatorMaximum == NanoYuan.Zero ||
                admission.RequestsPerMinute <= 0 || admission.PerPrincipalConcurrency <= 0 ||
                admission.GlobalConcurrency <= 0 || admission.GlobalQueueLength < 0 ||
                admission.PerPrincipalQueueLength < 0 || admission.QueueWait < TimeSpan.Zero)
            {
                throw new PolicyValidationException($"Resource policy '{resource.Resource}' contains invalid limits.");
            }
            if (admission.PerPrincipalConcurrency > admission.GlobalConcurrency)
            {
                throw new PolicyValidationException($"Resource policy '{resource.Resource}' has per-principal concurrency above global concurrency.");
            }
            if (admission.PerPrincipalQueueLength > admission.GlobalQueueLength)
            {
                throw new PolicyValidationException($"Resource policy '{resource.Resource}' has a per-principal queue above the global queue.");
            }
            try
            {
                _ = resource.Price.Calculate(
                    AccountingLimits.MaximumUnitsPerDimension,
                    AccountingLimits.MaximumUnitsPerDimension);
            }
            catch (OverflowException exception)
            {
                throw new PolicyValidationException(
                    $"Resource policy '{resource.Resource}' can overflow the accounting envelope.", exception);
            }
        }
    }

    private string ComputeFingerprint()
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize()))).ToLowerInvariant();
    }

    private string Canonicalize()
    {
        return string.Join("\n", _resources.Values.OrderBy(value => value.Resource, StringComparer.Ordinal).Select(value =>
            string.Join('|', value.Resource, value.Price.Input.Value, value.Price.Output.Value,
                value.Admission.RequestsPerMinute, value.Admission.PerPrincipalConcurrency,
                value.Admission.GlobalConcurrency, value.Admission.GlobalQueueLength, value.Admission.PerPrincipalQueueLength,
                value.Admission.QueueWait.Ticks, value.ExecutionDeadline.Ticks, value.OperatorMaximum.Value))) +
            $"\n{PrincipalDailyAllowance.Value}|{DailyOperatorBudget.Value}|{MonthlyOperatorBudget.Value}|{ActiveLeaseTtl.Ticks}|{LeaseRenewalInterval.Ticks}";
    }

    public static ServicePolicy Defaults() => new(
        1,
        [
            new(SnowShot.Domain.Resources.Translation, new(NanoYuan.Zero, NanoYuan.Zero), new(30, 16, 64, 64, TimeSpan.FromSeconds(30)), TimeSpan.FromMinutes(5), new(1_000_000_000)),
            new(SnowShot.Domain.Resources.QwenFlash, new(NanoYuan.Zero, NanoYuan.Zero), new(20, 16, 64, 64, TimeSpan.FromSeconds(30)), TimeSpan.FromMinutes(5), new(1_000_000_000)),
            new(SnowShot.Domain.Resources.QwenPlus, new(NanoYuan.Zero, NanoYuan.Zero), new(20, 16, 64, 64, TimeSpan.FromSeconds(30)), TimeSpan.FromMinutes(5), new(2_000_000_000)),
            new(SnowShot.Domain.Resources.QwenVisionFlash, new(NanoYuan.Zero, NanoYuan.Zero), new(20, 16, 64, 64, TimeSpan.FromSeconds(30)), TimeSpan.FromMinutes(5), new(1_000_000_000)),
            new(SnowShot.Domain.Resources.DeepSeekV4, new(NanoYuan.Zero, NanoYuan.Zero), new(20, 16, 64, 64, TimeSpan.FromSeconds(30)), TimeSpan.FromMinutes(5), new(1_000_000_000)),
            new(SnowShot.Domain.Resources.TableExtraction, new(NanoYuan.Zero, NanoYuan.Zero), new(10, 3, 6, 12, TimeSpan.FromSeconds(30)), TimeSpan.FromSeconds(60), new(30_000_000)),
        ],
        NanoYuan.ThreeYuan,
        new NanoYuan(50_000_000_000),
        new NanoYuan(200_000_000_000),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(10));
}

public sealed class PolicyValidationException(string message, Exception? innerException = null) : Exception(message, innerException);

public enum PolicyActivationDecision { Initial, Idempotent, Advance, LowerRevision, RevisionConflict }

public static class PolicyActivationRules
{
    public static PolicyActivationDecision Decide(
        long? activeRevision,
        string? activeFingerprint,
        long configuredRevision,
        string configuredFingerprint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredFingerprint);
        if (activeRevision is null) return PolicyActivationDecision.Initial;
        if (configuredRevision < activeRevision) return PolicyActivationDecision.LowerRevision;
        if (configuredRevision > activeRevision) return PolicyActivationDecision.Advance;
        return string.Equals(activeFingerprint, configuredFingerprint, StringComparison.Ordinal)
            ? PolicyActivationDecision.Idempotent
            : PolicyActivationDecision.RevisionConflict;
    }
}
