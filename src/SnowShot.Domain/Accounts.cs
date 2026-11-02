using System.Text.RegularExpressions;

namespace SnowShot.Domain;

public static partial class AccountRules
{
    public static bool IsUsername(string? value) => value is not null && UsernamePattern().IsMatch(value);
    public static string NormalizeUsername(string value) => value.ToUpperInvariant();
    public static bool IsPassword(string? value) => value is { Length: >= 8 and <= 16 };
    public static string HashEffect(Guid operationId) => $"usage:{operationId:N}";
    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{3,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public static (long Available, long Outstanding) AddFunds(long available, long outstanding, long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(available);
        ArgumentOutOfRangeException.ThrowIfNegative(outstanding);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        var repayment = Math.Min(outstanding, amount);
        return (checked(available + amount - repayment), outstanding - repayment);
    }

    public static (long Available, long Outstanding) Refund(long available, long outstanding, long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(available);
        ArgumentOutOfRangeException.ThrowIfNegative(outstanding);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        var funded = Math.Min(available, amount);
        return (available - funded, checked(outstanding + amount - funded));
    }
}
