using System.Text.Json;
using SnowShot.Application;
using SnowShot.Domain;

namespace SnowShot.Infrastructure.Providers;

internal static class OpenAiUsage
{
    /// <summary>
    /// Normalizes a provider usage frame instead of discarding it: a missing or
    /// contradictory total is derived from its components, a missing component is derived
    /// from the total, reasoning tokens are clamped to the completion, and implausible
    /// magnitudes yield null so the attempt settles from estimation evidence instead.
    /// </summary>
    public static ChatUsage? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        return ReadDetails(usage);
    }

    public static ChatUsage? ReadDetails(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return null;
        var hasPrompt = TryNonnegative(usage, "prompt_tokens", out var prompt);
        var hasCompletion = TryNonnegative(usage, "completion_tokens", out var completion);
        var hasTotal = TryNonnegative(usage, "total_tokens", out var total);
        if (hasPrompt && hasCompletion)
        {
            total = checked(prompt + completion);
        }
        else if (hasTotal)
        {
            if (!hasPrompt && hasCompletion)
            {
                if (total < completion) return null;
                prompt = total - completion;
            }
            else if (hasPrompt && !hasCompletion)
            {
                if (total < prompt) return null;
                completion = total - prompt;
            }
            else return null;
            total = checked(prompt + completion);
        }
        else return null;
        if (prompt > AccountingLimits.MaximumUnitsPerDimension || completion > AccountingLimits.MaximumUnitsPerDimension)
            return null;
        var reasoning = 0L;
        if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("reasoning_tokens", out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out reasoning) && reasoning > 0)
            reasoning = Math.Min(reasoning, completion);
        var cached = 0L;
        if (usage.TryGetProperty("prompt_tokens_details", out var inputDetails) && inputDetails.ValueKind == JsonValueKind.Object &&
            inputDetails.TryGetProperty("cached_tokens", out var cachedValue))
        {
            if (cachedValue.ValueKind != JsonValueKind.Number ||
                !cachedValue.TryGetInt64(out cached) || cached < 0 || cached > prompt) return null;
        }
        return new(prompt, completion, total, Math.Max(0, reasoning), cached);
    }

    private static bool TryNonnegative(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value) && value >= 0;
    }
}
