using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;

namespace SnowShotApi.Tests.Domain;

public sealed class DomainTests
{
    [Fact]
    public void RetentionHorizonsPreserveDependencyOrder()
    {
        Assert.True(new RetentionOptions { OperationDays = 90, AggregateDays = 400, IdentityDays = 400 }.HasValidHierarchy());
        Assert.False(new RetentionOptions { OperationDays = 401, AggregateDays = 400, IdentityDays = 400 }.HasValidHierarchy());
        Assert.False(new RetentionOptions { OperationDays = 90, AggregateDays = 400, IdentityDays = 399 }.HasValidHierarchy());
    }

    [Fact]
    public void MoneyRejectsNegativeAndOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NanoYuan(-1));
        Assert.Throws<OverflowException>(() => _ = new NanoYuan(long.MaxValue) + new NanoYuan(1));
        Assert.Throws<OverflowException>(() => _ = new NanoYuan(long.MaxValue) * 2);
    }

    [Fact]
    public void TranslationValidationUsesCombinedContentLength()
    {
        var valid = new TranslationCommand([new string('a', 2_500), new string('b', 2_500)],
            "en", "zh-CHS", "general");
        var invalid = valid with { Content = [new string('a', 2_500), new string('b', 2_501)] };

        Assert.Empty(CommandValidator.Validate(valid));
        Assert.Contains(CommandValidator.Validate(invalid), error =>
            error.Contains("total length", StringComparison.Ordinal));
    }

    [Fact]
    public void TranslationRoutingIsStableBalancedAndAlternatesRetries()
    {
        var routing = new TranslationRouting([Resources.DeepSeekV4, Resources.QwenPlus], 4, 3,
            TimeSpan.FromSeconds(90), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        var operation = Guid.Parse("019fc2f0-d649-73ad-a65e-4efcf336c49a");
        var initial = routing.InitialModelIndex(operation);

        Assert.Equal(initial, routing.InitialModelIndex(operation));
        Assert.Equal(routing.LogicalModels[initial], routing.ModelForAttempt(initial, 1));
        Assert.NotEqual(routing.ModelForAttempt(initial, 1), routing.ModelForAttempt(initial, 2));
        Assert.Equal(routing.ModelForAttempt(initial, 1), routing.ModelForAttempt(initial, 3));

        var counts = Enumerable.Range(0, 1_000)
            .Select(index => routing.LogicalModels[routing.InitialModelIndex(Guid.Parse($"00000000-0000-0000-0000-{index:x12}"))])
            .GroupBy(model => model).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.InRange(counts[Resources.DeepSeekV4], 400, 600);
        Assert.InRange(counts[Resources.QwenPlus], 400, 600);
    }

    [Fact]
    public void DefaultPolicyHasStableRequiredResourcesAndLimits()
    {
        var policy = ServicePolicy.Defaults();
        Assert.Equal(64, policy.Fingerprint.Length);
        Assert.Equal(3_000_000_000, policy.PrincipalDailyAllowance.Value);
        Assert.Equal(50_000_000_000, policy.DailyOperatorBudget.Value);
        Assert.Equal(200_000_000_000, policy.MonthlyOperatorBudget.Value);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Get(Resources.Translation).ExecutionDeadline);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.Get(Resources.TableExtraction).ExecutionDeadline);

        foreach (var resource in new[]
        {
            Resources.Translation, Resources.QwenFlash, Resources.QwenPlus,
            Resources.QwenVisionFlash, Resources.DeepSeekV4,
        })
        {
            var admission = policy.Get(resource).Admission;
            Assert.Equal(16, admission.PerPrincipalConcurrency);
            Assert.Equal(64, admission.GlobalConcurrency);
            Assert.Equal(64, admission.GlobalQueueLength);
        }

        var tableAdmission = policy.Get(Resources.TableExtraction).Admission;
        Assert.Equal(3, tableAdmission.PerPrincipalConcurrency);
        Assert.Equal(6, tableAdmission.GlobalConcurrency);
        Assert.Equal(12, tableAdmission.GlobalQueueLength);
    }

    [Fact]
    public void PolicyLoadsPricesFromDedicatedConfiguration()
    {
        var policy = new PolicyOptions { Pricing = DefaultPricing() }.Build();

        Assert.Equal(2_000, policy.Get(Resources.Translation).Price.Input.Value);
        Assert.Equal(8_000, policy.Get(Resources.Translation).Price.Output.Value);
        Assert.Equal(200, policy.Get(Resources.QwenFlash).Price.Input.Value);
        Assert.Equal(800, policy.Get(Resources.QwenFlash).Price.Output.Value);
        Assert.Equal(2_000, policy.Get(Resources.QwenPlus).Price.Input.Value);
        Assert.Equal(8_000, policy.Get(Resources.QwenPlus).Price.Output.Value);
        Assert.Equal(150, policy.Get(Resources.QwenVisionFlash).Price.Input.Value);
        Assert.Equal(1_500, policy.Get(Resources.QwenVisionFlash).Price.Output.Value);
        Assert.Equal(1_000, policy.Get(Resources.DeepSeekV4).Price.Input.Value);
        Assert.Equal(2_000, policy.Get(Resources.DeepSeekV4).Price.Output.Value);
        Assert.Equal(30_000_000, policy.Get(Resources.TableExtraction).Price.Input.Value);
        Assert.Equal(0, policy.Get(Resources.TableExtraction).Price.Output.Value);
    }

    [Fact]
    public void PolicyLoadsAdmissionLimitsFromDedicatedConfiguration()
    {
        var resources = new Dictionary<string, ResourcePolicyOptions>(StringComparer.Ordinal)
        {
            [Resources.QwenFlash] = new()
            {
                RequestsPerMinute = 99,
                PerPrincipalConcurrency = 2,
                GlobalConcurrency = 8,
                GlobalQueueLength = 12,
                PerPrincipalQueueLength = 3,
                QueueWaitSeconds = 4,
                ExecutionDeadlineSeconds = 120,
                OperatorMaximumNanoYuan = 1_000_000,
            },
        };

        var policy = new PolicyOptions { Pricing = DefaultPricing(), Resources = resources }.Build();
        var configured = policy.Get(Resources.QwenFlash);

        Assert.Equal(99, configured.Admission.RequestsPerMinute);
        Assert.Equal(2, configured.Admission.PerPrincipalConcurrency);
        Assert.Equal(8, configured.Admission.GlobalConcurrency);
        Assert.Equal(12, configured.Admission.GlobalQueueLength);
        Assert.Equal(3, configured.Admission.PerPrincipalQueueLength);
        Assert.Equal(TimeSpan.FromSeconds(4), configured.Admission.QueueWait);
        Assert.Equal(TimeSpan.FromSeconds(120), configured.ExecutionDeadline);
    }

    [Fact]
    public void PolicyRequiresPricesForEveryKnownResource()
    {
        var exception = Assert.Throws<PolicyValidationException>(() => new PolicyOptions().Build());

        Assert.Contains("missing pricing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PricingChangesPolicyFingerprint()
    {
        var first = new PolicyOptions { Pricing = DefaultPricing() }.Build();
        var changedPricing = DefaultPricing();
        changedPricing[Resources.QwenFlash] = new() { InputRateNanoYuan = 201, OutputRateNanoYuan = 800 };
        var second = new PolicyOptions { Pricing = changedPricing }.Build();

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void PolicyRejectsCrossFieldErrors()
    {
        var defaults = ServicePolicy.Defaults();
        Assert.Throws<PolicyValidationException>(() => new ServicePolicy(1, defaults.ResourcePolicies, NanoYuan.ThreeYuan,
            new(201_000_000_000), new(200_000_000_000), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)));
        Assert.Throws<PolicyValidationException>(() => new ServicePolicy(1, defaults.ResourcePolicies, NanoYuan.ThreeYuan,
            new(50_000_000_000), new(200_000_000_000), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        var unsafePrice = defaults.ResourcePolicies.Select(resource => resource.Resource == Resources.QwenFlash
            ? resource with { Price = new UnitPrice(new NanoYuan(long.MaxValue), NanoYuan.Zero) }
            : resource);
        Assert.Throws<PolicyValidationException>(() => new ServicePolicy(1, unsafePrice, NanoYuan.ThreeYuan,
            new(50_000_000_000), new(200_000_000_000), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void PolicyBuildsLimitsForAConfiguredAdditionalModel()
    {
        var pricing = DefaultPricing();
        pricing["custom-model"] = new() { InputRateNanoYuan = 100, OutputRateNanoYuan = 200 };
        var options = new PolicyOptions
        {
            Pricing = pricing,
            Resources = new Dictionary<string, ResourcePolicyOptions>(StringComparer.Ordinal)
            {
                ["custom-model"] = new()
                {
                    RequestsPerMinute = 10,
                    PerPrincipalConcurrency = 1,
                    GlobalConcurrency = 2,
                    GlobalQueueLength = 2,
                    QueueWaitSeconds = 1,
                    ExecutionDeadlineSeconds = 60,
                    OperatorMaximumNanoYuan = 1_000_000,
                },
            },
        };

        var policy = options.Build(["custom-model"]);

        Assert.Equal(100, policy.Get("custom-model").Price.Input.Value);
        Assert.Equal(200, policy.Get("custom-model").Price.Output.Value);
    }

    [Fact]
    public void PolicyRevisionIsPositiveAndExcludedFromTheFingerprint()
    {
        var first = ServicePolicy.Defaults();
        var second = new ServicePolicy(2, first.ResourcePolicies, first.PrincipalDailyAllowance,
            first.DailyOperatorBudget, first.MonthlyOperatorBudget, first.ActiveLeaseTtl,
            first.LeaseRenewalInterval);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.CanonicalDocument, second.CanonicalDocument);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Throws<PolicyValidationException>(() => new ServicePolicy(0, first.ResourcePolicies,
            first.PrincipalDailyAllowance, first.DailyOperatorBudget, first.MonthlyOperatorBudget,
            first.ActiveLeaseTtl, first.LeaseRenewalInterval));
    }

    [Theory]
    [InlineData(null, null, 1, "configured", PolicyActivationDecision.Initial)]
    [InlineData(1L, "configured", 1L, "configured", PolicyActivationDecision.Idempotent)]
    [InlineData(1L, "old", 2L, "configured", PolicyActivationDecision.Advance)]
    [InlineData(2L, "new", 1L, "configured", PolicyActivationDecision.LowerRevision)]
    [InlineData(1L, "different", 1L, "configured", PolicyActivationDecision.RevisionConflict)]
    public void PolicyActivationDecisionIsMonotonic(
        long? activeRevision, string? activeFingerprint, long configuredRevision,
        string configuredFingerprint, PolicyActivationDecision expected)
    {
        Assert.Equal(expected, PolicyActivationRules.Decide(activeRevision, activeFingerprint,
            configuredRevision, configuredFingerprint));
    }

    [Fact]
    public void CapChangesApplyImmediatelyWithoutInvalidatingExistingUsage()
    {
        Assert.False(ReservationRules.WouldExceed(committed: 80, reserved: 10, requested: 10, limit: 100));
        Assert.True(ReservationRules.WouldExceed(committed: 80, reserved: 10, requested: 1, limit: 75));
        Assert.True(ReservationRules.WouldExceed(committed: 80, reserved: 30, requested: 0, limit: 100));
        Assert.False(ReservationRules.WouldExceed(committed: 80, reserved: 30, requested: 1, limit: 200));
    }

    [Fact]
    public void SettlementCommitsPostpaidPublicChargeAndVerifiedOperatorOverage()
    {
        var snapshot = new ReservationSnapshot(1, new string('a', 64), Resources.QwenFlash,
            new(new(1), new(1)), NanoYuan.ThreeYuan, new(100), new(100));
        var decision = ReservationRules.Settle(ReservationState.Dispatched, snapshot, new(150), new(175), true,
            costKnown: true, verifiableOverage: true, 1, 1, "success");
        Assert.Equal(150, decision.PublicCost.Value);
        Assert.Equal(175, decision.OperatorCost.Value);
        Assert.Equal(75, decision.OperatorOverage.Value);
        Assert.Equal(ReservationState.Committed, decision.State);
    }

    [Fact]
    public void UnknownCostConservativelyCommitsOperatorReservation()
    {
        var snapshot = new ReservationSnapshot(1, new string('b', 64), Resources.Translation,
            new(new(1), new(1)), NanoYuan.ThreeYuan, new(100), new(200));
        var decision = ReservationRules.Settle(ReservationState.Dispatched, snapshot, NanoYuan.Zero, NanoYuan.Zero,
            delivered: false, costKnown: false, verifiableOverage: false, 0, 0, "abandoned");
        Assert.Equal(ReservationState.UnknownCost, decision.State);
        Assert.Equal(200, decision.OperatorCost.Value);
        Assert.Equal(0, decision.PublicCost.Value);
    }

    [Fact]
    public void DeadlineNeverReturnsNegativeRemainingTime()
    {
        var now = DateTimeOffset.Parse("2026-08-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var deadline = ExecutionDeadline.Start(now, TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(4), deadline.Remaining(now.AddMinutes(1)));
        Assert.Equal(TimeSpan.Zero, deadline.Remaining(now.AddMinutes(6)));
    }

    [Theory]
    [InlineData(ReservationState.Reserved, ReservationState.Dispatched, true)]
    [InlineData(ReservationState.Reserved, ReservationState.Released, true)]
    [InlineData(ReservationState.Dispatched, ReservationState.Committed, true)]
    [InlineData(ReservationState.Dispatched, ReservationState.UnknownCost, true)]
    [InlineData(ReservationState.Committed, ReservationState.Released, false)]
    [InlineData(ReservationState.Released, ReservationState.Dispatched, false)]
    public void ReservationTransitionsAreExplicit(
        ReservationState source,
        ReservationState target,
        bool allowed) => Assert.Equal(allowed, ReservationRules.CanTransition(source, target));

    [Fact]
    public void OperationHandleCopiesOwnerCredentialAndRequiresPositiveFence()
    {
        var token = new byte[32];
        var snapshot = new ReservationSnapshot(1, new string('c', 64), Resources.QwenFlash,
            new(new(1), new(1)), NanoYuan.ThreeYuan, new(1), new(1));
        var handle = new OperationHandle(Guid.CreateVersion7(), token, 1, DateTimeOffset.UtcNow.AddMinutes(1), snapshot);
        token[0] = 42;

        Assert.Equal(0, handle.OwnerToken[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OperationHandle(Guid.CreateVersion7(), token, 0, DateTimeOffset.UtcNow.AddMinutes(1), snapshot));
    }

    private static Dictionary<string, ResourcePricingOptions> DefaultPricing() => new(StringComparer.Ordinal)
    {
        [Resources.Translation] = new() { InputRateNanoYuan = 2_000, OutputRateNanoYuan = 8_000 },
        [Resources.QwenFlash] = new() { InputRateNanoYuan = 200, OutputRateNanoYuan = 800 },
        [Resources.QwenPlus] = new() { InputRateNanoYuan = 2_000, OutputRateNanoYuan = 8_000 },
        [Resources.QwenVisionFlash] = new() { InputRateNanoYuan = 150, OutputRateNanoYuan = 1_500 },
        [Resources.DeepSeekV4] = new() { InputRateNanoYuan = 1_000, OutputRateNanoYuan = 2_000 },
        [Resources.TableExtraction] = new() { InputRateNanoYuan = 30_000_000, OutputRateNanoYuan = 0 },
    };
}
