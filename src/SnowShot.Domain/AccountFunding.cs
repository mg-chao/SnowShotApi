namespace SnowShot.Domain;

public enum FundingRejection { None, InsufficientCredits, TemporarilyReserved, OutstandingBalance }

public sealed record FundingAllocation(long Free, long Wallet, FundingRejection Rejection)
{
    public bool Accepted => Rejection == FundingRejection.None;
}

public sealed record FundingCapture(long FreeCharge, long WalletCharge, long Available, long Held, long Outstanding);

/// <summary>Pure accounting rules; callers serialize mutations of the free bucket and wallet.</summary>
public static class AccountFunding
{
    public static FundingAllocation Reserve(long maximum, long freeLimit, long freeCommitted,
        long freeReserved, long available, long held, long outstanding)
    {
        foreach (var value in new[] { maximum, freeLimit, freeCommitted, freeReserved, available, held, outstanding })
            ArgumentOutOfRangeException.ThrowIfNegative(value);
        var freeRemainder = Math.Max(0, freeLimit - Math.Min(freeLimit, freeCommitted));
        var free = Math.Min(maximum, Math.Max(0, freeRemainder - Math.Min(freeRemainder, freeReserved)));
        var wallet = maximum - free;
        if (wallet == 0) return new(free, 0, FundingRejection.None);
        if (outstanding > 0) return new(0, 0, FundingRejection.OutstandingBalance);
        if (wallet <= available) return new(free, wallet, FundingRejection.None);
        // A completed concurrent call may release either its free or purchased hold.
        var potentialFree = Math.Min(maximum, freeRemainder);
        var potentialWallet = maximum - potentialFree;
        return new(0, 0, potentialWallet <= available || potentialWallet - available <= held
            ? FundingRejection.TemporarilyReserved : FundingRejection.InsufficientCredits);
    }

    public static FundingCapture Capture(long actual, long freeHold, long walletHold,
        long available, long held, long outstanding)
    {
        foreach (var value in new[] { actual, freeHold, walletHold, available, held, outstanding })
            ArgumentOutOfRangeException.ThrowIfNegative(value);
        if (actual > checked(freeHold + walletHold) || walletHold > held)
            throw new InvalidOperationException("A charge must be funded by its original reservation.");
        var freeCharge = Math.Min(actual, freeHold);
        var walletCharge = actual - freeCharge;
        var released = walletHold - walletCharge;
        var debtPayment = Math.Min(released, outstanding);
        return new(freeCharge, walletCharge, checked(available + released - debtPayment),
            held - walletHold, outstanding - debtPayment);
    }
}

public sealed record TextInputBillingEnvelope(int RequestOverheadTokens, int MessageOverheadTokens)
{
    // Opt-in for byte-level BPE routes with a bounded chat template. Serialized
    // UTF-8 bytes also cover names, escaping and other user-supplied text fields.
    public long MaximumInputTokens(int serializedBytes, int messages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(serializedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(messages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RequestOverheadTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MessageOverheadTokens);
        return checked((long)serializedBytes + RequestOverheadTokens + (long)messages * MessageOverheadTokens);
    }
}

public sealed record ModelBillingEnvelope(long MaximumInputTokens, long MaximumThinkingInputTokens,
    int CompletionTolerance = 10, TextInputBillingEnvelope? TextInput = null)
{
    public NanoYuan MaximumCharge(TokenTariff tariff, bool thinking, int completionLimit, long? inputUpperBound = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(completionLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumThinkingInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(CompletionTolerance);
        if (inputUpperBound is { } bound) ArgumentOutOfRangeException.ThrowIfNegative(bound);
        var input = thinking ? MaximumThinkingInputTokens : MaximumInputTokens;
        if (inputUpperBound is { } requestBound) input = Math.Min(input, requestBound);
        var output = checked((long)completionLimit + CompletionTolerance);
        return tariff.MaximumCharge(input, output);
    }
}
