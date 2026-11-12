using SnowShot.Domain;

namespace SnowShot.Application;

public sealed record FastTranslationItem(string Text, string Source, string Target);

public sealed record FastTranslationProviderResult(
    IReadOnlyList<FastTranslationItem> Items,
    int? HttpStatus,
    ApplicationError? Error = null,
    CostBasis Basis = CostBasis.Exact);

public interface IFastTranslationProvider
{
    bool Enabled { get; }
    Task<FastTranslationProviderResult> TranslateAsync(
        IReadOnlyList<string> content, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken);
}

public sealed class FastTranslationUseCase(
    OperationCoordinator operations,
    IFastTranslationProvider provider,
    ISystemClock clock)
{
    public const int MaximumItems = 50;
    public const int MaximumInputLength = 5_000;
    public const long UpperRateNanoYuan = 100_000;
    public const long MaximumBatchCostNanoYuan = UpperRateNanoYuan * MaximumInputLength;

    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
    {
        "ar", "de", "en", "es", "fr", "hi", "it", "ja", "ko", "pt", "ru", "th", "tr", "vi", "zh-CHS", "zh-CHT",
    };

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "de", "en", "es", "fr", "hi", "id", "it", "ja", "ko", "nl", "pt", "ru", "th", "vi", "zh-CHS", "zh-CHT",
    };

    private static readonly HashSet<(string From, string To)> Exceptions =
        [("de", "hi"), ("en", "pt"), ("hi", "de"), ("hi", "nl"), ("hi", "pt"), ("nl", "hi"), ("pt", "hi")];

    public bool Enabled => provider.Enabled;

    public static IReadOnlyList<ValidationIssue> Validate(TranslationCommand request)
    {
        var errors = new List<ValidationIssue>();
        if (request.Content is null || request.Content.Count is < 1 or > MaximumItems)
            errors.Add(new(ValidationIssueCode.ContentCount));
        else
        {
            long length = 0;
            for (var index = 0; index < request.Content.Count; index++)
            {
                if (request.Content[index] is { } text) length += text.Length;
                else errors.Add(new(ValidationIssueCode.NullContentItem, Index: index));
            }
            if (length > MaximumInputLength) errors.Add(new(ValidationIssueCode.ContentTooLong));
        }
        if (request.From != "auto" && !Languages.Contains(request.From))
            errors.Add(new(ValidationIssueCode.UnsupportedLanguage, request.From));
        if (!Languages.Contains(request.To))
            errors.Add(new(ValidationIssueCode.UnsupportedLanguage, request.To));
        if (request.Domain is not ("" or "general"))
            errors.Add(new(ValidationIssueCode.UnsupportedDomain, request.Domain));
        return errors;
    }

    public static long RateNanoYuan(string from, string to) =>
        (IsChinese(from) || IsChinese(to)) && Common.Contains(from) && Common.Contains(to) &&
        !Exceptions.Contains((from.ToLowerInvariant(), to.ToLowerInvariant())) ? 48_000 : UpperRateNanoYuan;

    private static bool IsChinese(string code) =>
        code.Equals("zh-CHS", StringComparison.OrdinalIgnoreCase) ||
        code.Equals("zh-CHT", StringComparison.OrdinalIgnoreCase);

    public async Task<ApplicationResult<TranslationResult>> ExecuteAsync(
        RequestContext context, TranslationCommand request, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return ApplicationResult.Failure<TranslationResult>(new(ApplicationErrorCode.DependencyUnavailable, "fast_translation_disabled"));
        if (!context.AccountId.HasValue)
            return ApplicationResult.Failure<TranslationResult>(new(ApplicationErrorCode.AccountRequired, "account_required"));

        var output = request.Content.Select(text => text!).ToArray();
        var positions = request.From == request.To ? [] :
            Enumerable.Range(0, output.Length).Where(index => !string.IsNullOrWhiteSpace(output[index])).ToArray();
        var inputs = positions.Select(index => output[index]).ToArray();
        var units = inputs.Select(TranslationPrompt.CountCharacters).ToArray();
        var totalUnits = units.Sum();
        var maximum = new NanoYuan(RateNanoYuan(request.From, request.To)) * totalUnits;
        var started = await operations.StartAsync(Resources.FastTranslation, UsageKind.Translation, context,
            maximum, maximum, cancellationToken);
        if (!started.IsSuccess) return ApplicationResult.Failure<TranslationResult>(started.Error!);

        await using var scope = started.Value!;
        var dispatchError = await scope.DispatchAsync(cancellationToken);
        if (dispatchError is not null) return ApplicationResult.Failure<TranslationResult>(dispatchError);

        var remaining = scope.Handle.AbsoluteDeadline - clock.UtcNow;
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.OwnershipLost, deadline.Token);
        var local = inputs.Length == 0;
        var startedAt = clock.UtcNow;
        ApplicationResult<ProviderAttemptPreparation> prepared;
        try
        {
            prepared = await scope.PrepareAttemptAsync(1, local ? "translation-local" : "youdao-batch",
                Resources.FastTranslation, startedAt, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            prepared = ApplicationResult.Failure<ProviderAttemptPreparation>(scope.OwnershipLost.IsCancellationRequested
                ? new(ApplicationErrorCode.LeaseLost, "ownership_lost")
                : new(ApplicationErrorCode.DeadlineExceeded, "fast_translation_cancelled"));
        }
        if (!prepared.IsSuccess)
        {
            var preparationError = prepared.Error!;
            if (preparationError.Code != ApplicationErrorCode.LeaseLost)
                await scope.CompleteAsync(new(scope.Handle, NanoYuan.Zero, NanoYuan.Zero, false,
                    CostBasis.Unknown, false, 0, 0, preparationError.Detail));
            cancellationToken.ThrowIfCancellationRequested();
            return ApplicationResult.Failure<TranslationResult>(preparationError);
        }

        FastTranslationProviderResult response;
        try
        {
            response = local ? new([], null) :
                await provider.TranslateAsync(inputs, request.From, request.To, linked.Token);
        }
        catch (OperationCanceledException)
        {
            response = new([], null, new(ApplicationErrorCode.DeadlineExceeded, "fast_translation_timeout"), CostBasis.Unknown);
        }
        var error = scope.OwnershipLost.IsCancellationRequested
            ? new(ApplicationErrorCode.LeaseLost, "ownership_lost")
            : linked.IsCancellationRequested
                ? new(ApplicationErrorCode.DeadlineExceeded, "fast_translation_cancelled")
                : response.Error;
        var cost = NanoYuan.Zero;
        var basis = response.Basis;
        var valid = response.Error is null && response.Items.Count == inputs.Length;
        if (valid)
        {
            for (var index = 0; index < inputs.Length; index++)
            {
                var item = response.Items[index];
                output[positions[index]] = item.Text;
                cost += new NanoYuan(RateNanoYuan(item.Source, item.Target)) * units[index];
            }
            if (cost > maximum)
            {
                valid = false;
                cost = NanoYuan.Zero;
            }
        }
        if (response.Error is null && !valid)
        {
            error ??= new(ApplicationErrorCode.ProviderFailure, "fast_translation_invalid_output");
            basis = CostBasis.Unknown;
        }

        var outcome = error?.Detail ?? (local ? "unchanged" : "success");
        var attempt = new ProviderAttempt(prepared.Value!.Id, scope.Handle.OperationId, 1, prepared.Value.Provider,
            Resources.FastTranslation, outcome, response.HttpStatus, valid ? totalUnits : 0, 0, cost, basis,
            local ? AttemptDispatchState.NotDispatched : AttemptDispatchState.Dispatched, startedAt, clock.UtcNow);
        var settled = await scope.CompleteAsync(new(scope.Handle, error is null ? cost : NanoYuan.Zero, cost,
            error is null, basis, basis == CostBasis.Exact, valid ? totalUnits : 0, 0, outcome), attempt);
        cancellationToken.ThrowIfCancellationRequested();
        return (settled ?? error) is { } failure
            ? ApplicationResult.Failure<TranslationResult>(failure)
            : ApplicationResult.Success(new TranslationResult(output, request.From, request.To));
    }
}
