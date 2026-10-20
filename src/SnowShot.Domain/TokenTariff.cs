using System.Collections.Immutable;

namespace SnowShot.Domain;

public sealed record TokenPriceTier(
    long MaximumInputTokens,
    long InputRateNanoYuan,
    long OutputRateNanoYuan,
    long CachedInputRateNanoYuan);

/// <summary>The total prompt selects one tier for all input and output tokens.</summary>
public sealed class TokenTariff
{
    public TokenTariff(ImmutableArray<TokenPriceTier> tiers)
    {
        if (tiers.IsDefaultOrEmpty)
            throw new PolicyValidationException("A token tariff requires at least one tier.");
        long previous = 0;
        foreach (var tier in tiers)
        {
            if (tier.MaximumInputTokens <= previous ||
                tier.MaximumInputTokens > AccountingLimits.MaximumUnitsPerDimension ||
                tier.InputRateNanoYuan < 0 || tier.OutputRateNanoYuan < 0 ||
                tier.CachedInputRateNanoYuan < 0 || tier.CachedInputRateNanoYuan > tier.InputRateNanoYuan)
                throw new PolicyValidationException("Token tariff thresholds or rates are invalid.");
            try
            {
                _ = new UnitPrice(new(tier.InputRateNanoYuan), new(tier.OutputRateNanoYuan))
                    .Calculate(AccountingLimits.MaximumUnitsPerDimension, AccountingLimits.MaximumUnitsPerDimension);
            }
            catch (OverflowException exception)
            {
                throw new PolicyValidationException("Token tariff can overflow the accounting envelope.", exception);
            }
            previous = tier.MaximumInputTokens;
        }
        Tiers = tiers;
    }

    public ImmutableArray<TokenPriceTier> Tiers { get; }

    public NanoYuan Calculate(long input, long output, long cachedInput = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        ArgumentOutOfRangeException.ThrowIfNegative(output);
        ArgumentOutOfRangeException.ThrowIfNegative(cachedInput);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, AccountingLimits.MaximumUnitsPerDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(output, AccountingLimits.MaximumUnitsPerDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cachedInput, input);
        // Estimates may exceed the context window (e.g. encoded images). The final
        // tier still provides a conservative price; provider context validation is separate.
        var tier = Tiers.FirstOrDefault(value => input <= value.MaximumInputTokens) ?? Tiers[^1];
        return new NanoYuan(tier.InputRateNanoYuan) * (input - cachedInput) +
            new NanoYuan(tier.CachedInputRateNanoYuan) * cachedInput +
            new NanoYuan(tier.OutputRateNanoYuan) * output;
    }

    public string CanonicalDocument => string.Join(';', Tiers.Select(tier =>
        FormattableString.Invariant($"{tier.MaximumInputTokens}:{tier.InputRateNanoYuan}:{tier.OutputRateNanoYuan}:{tier.CachedInputRateNanoYuan}")));
}

public sealed record ModelPricingSnapshot(string ExecutionModel, TokenTariff Tariff);
