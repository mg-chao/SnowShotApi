using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SnowShot.Domain;

namespace SnowShot.Application;

public abstract record ChatApplicationEvent
{
    private ChatApplicationEvent() { }
    public sealed record Data(ReadOnlyMemory<byte> Utf8Json) : ChatApplicationEvent;
    public sealed record Completed : ChatApplicationEvent;
    public sealed record Failed(ApplicationError Error) : ChatApplicationEvent;
}

public sealed class ChatUseCase(
    OperationCoordinator operations,
    IChatProviderClient provider,
    IProviderAccessPool providerAccess,
    ISystemClock clock,
    ServicePolicy policy,
    IChatModelCatalog modelCatalog)
{
    public IReadOnlyList<string> Validate(ChatCommand request) => CommandValidator.Validate(request, policy, modelCatalog);

    public async IAsyncEnumerable<ChatApplicationEvent> ExecuteAsync(
        RequestContext context,
        ChatCommand request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resourcePolicy = policy.Get(request.Model);
        var started = await operations.StartAsync(request.Model, UsageKind.Chat, context, new(1),
            resourcePolicy.OperatorMaximum, cancellationToken);
        if (!started.IsSuccess) { yield return new ChatApplicationEvent.Failed(started.Error!); yield break; }

        await using var scope = started.Value!;
        var dispatchError = await scope.DispatchAsync(cancellationToken);
        if (dispatchError is not null) { yield return new ChatApplicationEvent.Failed(dispatchError); yield break; }

        using var deadline = Deadline(scope.Handle.AbsoluteDeadline, clock.UtcNow);
        using var operationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.OwnershipLost, deadline.Token);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        for (var attemptNumber = 1; attemptNumber <= 3; attemptNumber++)
        {
            var remaining = scope.Handle.AbsoluteDeadline - clock.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            var wait = resourcePolicy.Admission.QueueWait < remaining ? resourcePolicy.Admission.QueueWait : remaining;
            await using var accessLease = await providerAccess.AcquireAsync(new(request.Model, excluded, wait,
                policy.ActiveLeaseTtl, policy.LeaseRenewalInterval), operationToken.Token);
            if (!accessLease.Acquired)
            {
                var accessError = AccessError(accessLease);
                var poolStartedAt = clock.UtcNow;
                var poolPreparation = await scope.PrepareAttemptAsync(attemptNumber, "provider-pool", request.Model,
                    poolStartedAt, operationToken.Token);
                if (!poolPreparation.IsSuccess)
                {
                    yield return new ChatApplicationEvent.Failed(poolPreparation.Error!);
                    yield break;
                }
                var poolAttempt = new ProviderAttempt(poolPreparation.Value!.Id, scope.Handle.OperationId, attemptNumber,
                    "provider-pool", request.Model, accessError.Detail, null, 0, 0, NanoYuan.Zero, true,
                    AttemptDispatchState.NotDispatched, poolStartedAt, clock.UtcNow);
                var poolSettlementError = await scope.CompleteAsync(new(scope.Handle, NanoYuan.Zero, NanoYuan.Zero,
                    false, true, true, 0, 0, accessError.Detail), poolAttempt);
                yield return new ChatApplicationEvent.Failed(poolSettlementError ?? accessError);
                yield break;
            }
            var access = accessLease.Selection!;
            var attemptStartedAt = clock.UtcNow;
            var prepared = await scope.PrepareAttemptAsync(attemptNumber, access.AttemptProvider, request.Model,
                attemptStartedAt, operationToken.Token);
            if (!prepared.IsSuccess) { yield return new ChatApplicationEvent.Failed(prepared.Error!); yield break; }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationToken.Token, accessLease.OwnershipLost);
            await using var enumerator = provider.StreamAsync(new(scope.Handle, request, access,
                context.ClientRequestId, context.TraceId, attemptNumber, prepared.Value!.Id, attemptStartedAt), linked.Token)
                .GetAsyncEnumerator(linked.Token);
            ChatProviderEvent.Terminal? terminal = null;
            ChatProviderEvent.Failure? failure = null;
            var deliveredFrame = false;
            while (terminal is null && failure is null)
            {
                var moved = false;
                ApplicationError? cancellationFailure = null;
                try { moved = await enumerator.MoveNextAsync(); }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    cancellationFailure = CancellationError(scope, deadline, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    failure = SyntheticFailure("access_lease_lost", true, AttemptDispatchState.Unknown);
                }
                if (cancellationFailure is not null)
                {
                    await scope.CompleteAsync(new(scope.Handle, NanoYuan.Zero, NanoYuan.Zero,
                        false, false, false, 0, 0, cancellationFailure.Detail));
                    if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
                    yield return new ChatApplicationEvent.Failed(cancellationFailure);
                    yield break;
                }
                if (failure is not null) break;
                if (!moved)
                {
                    failure = SyntheticFailure("incomplete_stream", false, AttemptDispatchState.Unknown);
                    break;
                }
                switch (enumerator.Current)
                {
                    case ChatProviderEvent.Frame frame when frame.Utf8Json.Length is > 0 and <= 256 * 1024:
                        deliveredFrame = true;
                        yield return new ChatApplicationEvent.Data(frame.Utf8Json);
                        break;
                    case ChatProviderEvent.Frame:
                        failure = SyntheticFailure("invalid_stream_frame", false, AttemptDispatchState.Dispatched);
                        break;
                    case ChatProviderEvent.Terminal value:
                        terminal = value;
                        break;
                    case ChatProviderEvent.Failure value:
                        failure = value;
                        break;
                }
            }

            if (terminal is not null)
            {
                var usage = terminal.Usage;
                var actual = usage is null ? NanoYuan.Zero : resourcePolicy.Price.Calculate(usage.PromptTokens, usage.CompletionTokens);
                var settlement = await scope.CompleteAsync(new(scope.Handle, actual, terminal.Attempt.Cost,
                    terminal.Delivered, terminal.CostKnown, terminal.CostKnown,
                    usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0, terminal.Outcome), terminal.Attempt);
                if (settlement is not null) { yield return new ChatApplicationEvent.Failed(settlement); yield break; }
                yield return new ChatApplicationEvent.Completed();
                yield break;
            }

            var failed = failure!;
            if (!deliveredFrame && failed.Retryable && attemptNumber < 3)
            {
                var attemptError = await scope.CompleteAttemptAsync(failed.Attempt, operationToken.Token);
                if (attemptError is not null) { yield return new ChatApplicationEvent.Failed(attemptError); yield break; }
                excluded.Add(access.AccessId);
                continue;
            }

            var failedSettlement = await scope.CompleteAsync(new(scope.Handle, NanoYuan.Zero, failed.Attempt.Cost,
                false, failed.Attempt.CostKnown, failed.Attempt.CostKnown,
                failed.Attempt.InputUnits, failed.Attempt.OutputUnits, failed.Category), failed.Attempt);
            yield return new ChatApplicationEvent.Failed(failedSettlement ??
                new(ApplicationErrorCode.ProviderFailure, failed.Category));
            yield break;

            ChatProviderEvent.Failure SyntheticFailure(string outcome, bool retryable, AttemptDispatchState state) =>
                new(outcome, retryable, new(prepared.Value!.Id, scope.Handle.OperationId, attemptNumber,
                    access.AttemptProvider, request.Model, outcome, null, 0, 0, NanoYuan.Zero,
                    false, state, attemptStartedAt, clock.UtcNow));
        }

        var deadlineError = new ApplicationError(ApplicationErrorCode.DeadlineExceeded, "chat_deadline");
        await scope.CompleteAsync(new(scope.Handle, NanoYuan.Zero, NanoYuan.Zero,
            false, false, false, 0, 0, deadlineError.Detail));
        yield return new ChatApplicationEvent.Failed(deadlineError);
    }

    private static CancellationTokenSource Deadline(DateTimeOffset expiresAt, DateTimeOffset now) =>
        new(expiresAt <= now ? TimeSpan.Zero : expiresAt - now);

    private static ApplicationError CancellationError(OperationScope scope, CancellationTokenSource deadline, CancellationToken caller) =>
        scope.OwnershipLost.IsCancellationRequested ? new(ApplicationErrorCode.LeaseLost, "ownership_lost") :
        deadline.IsCancellationRequested ? new(ApplicationErrorCode.DeadlineExceeded, "execution_deadline") :
        caller.IsCancellationRequested ? new(ApplicationErrorCode.DeadlineExceeded, "caller_cancelled") :
        new(ApplicationErrorCode.ProviderFailure, "provider_cancelled");

    private static ApplicationError AccessError(IProviderAccessLease lease) => lease.RejectionReason == ProviderAccessRejectionReason.Saturated
        ? new(ApplicationErrorCode.QueueFull, "provider_access_saturated", lease.RetryAfter)
        : new(ApplicationErrorCode.DependencyUnavailable, "provider_access_unavailable", lease.RetryAfter);
}

public sealed class TranslationUseCase(
    OperationCoordinator operations,
    ITranslationProviderClient provider,
    IProviderAccessPool providerAccess,
    ISystemClock clock,
    ServicePolicy policy,
    TranslationRouting routing,
    ITranslationTelemetry telemetry)
{
    public static IReadOnlyList<string> Validate(TranslationCommand request) => CommandValidator.Validate(request);

    public async Task<ApplicationResult<TranslationResult>> ExecuteAsync(
        RequestContext context,
        TranslationCommand request,
        CancellationToken cancellationToken)
    {
        var content = request.Content.Select(value => value!).ToArray();
        var resourcePolicy = policy.Get(Resources.Translation);
        var started = await operations.StartAsync(Resources.Translation, UsageKind.Translation,
            context, new(1), resourcePolicy.OperatorMaximum, cancellationToken);
        if (!started.IsSuccess) return ApplicationResult.Failure<TranslationResult>(started.Error!);

        await using var scope = started.Value!;
        var dispatchError = await scope.DispatchAsync(cancellationToken);
        if (dispatchError is not null) return ApplicationResult.Failure<TranslationResult>(dispatchError);
        telemetry.BatchStarted(content.Length);

        using var deadline = Deadline(scope.Handle.AbsoluteDeadline, clock.UtcNow);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.OwnershipLost, deadline.Token);
        if (request.From == request.To)
        {
            var localStarted = clock.UtcNow;
            var local = await scope.PrepareAttemptAsync(1, "translation-local", Resources.Translation, localStarted, linked.Token);
            if (!local.IsSuccess) return ApplicationResult.Failure<TranslationResult>(local.Error!);
            var attempt = new ProviderAttempt(local.Value!.Id, scope.Handle.OperationId, 1, "translation-local",
                Resources.Translation, "unchanged", null, 0, 0, NanoYuan.Zero, true,
                AttemptDispatchState.NotDispatched, localStarted, clock.UtcNow);
            var localSettlementError = await scope.CompleteAsync(new(scope.Handle, NanoYuan.Zero, NanoYuan.Zero,
                true, true, true, 0, 0, "unchanged"), attempt);
            return localSettlementError is null
                ? ApplicationResult.Success(new TranslationResult(content, request.From, request.To))
                : ApplicationResult.Failure<TranslationResult>(localSettlementError);
        }
        var completedAttempts = new ConcurrentBag<TranslationProviderResult>();
        var itemResults = new ItemSuccess?[content.Length];
        BatchFailure? rootFailure = null;
        var initialModelIndex = routing.InitialModelIndex(scope.Handle.OperationId);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, content.Length), new ParallelOptions
            {
                MaxDegreeOfParallelism = routing.MaximumConcurrentConversations,
                CancellationToken = batch.Token,
            }, async (itemIndex, batchToken) =>
            {
                var result = await TranslateItemAsync(itemIndex, content[itemIndex], content.Length, context, request,
                    resourcePolicy, scope, deadline, initialModelIndex, completedAttempts, linked.Token, batchToken);
                if (result.Success is not null)
                {
                    itemResults[itemIndex] = result.Success;
                    return;
                }
                if (result.Failure is null) return;
                if (Interlocked.CompareExchange(ref rootFailure, result.Failure, null) is null)
                    batch.Cancel();
            });
        }
        catch (OperationCanceledException) when (batch.IsCancellationRequested) { }

        var attempts = completedAttempts.ToArray();
        var operatorCost = attempts.Aggregate(NanoYuan.Zero, (total, attempt) => total + attempt.Attempt.Cost);
        var operatorInput = attempts.Aggregate(0L, (total, attempt) => checked(total + attempt.OperatorInputCharacters));
        var operatorOutput = attempts.Aggregate(0L, (total, attempt) => checked(total + attempt.OperatorOutputCharacters));
        var allKnown = attempts.All(attempt => attempt.CostKnown);

        BatchFailure? failure = linked.IsCancellationRequested
            ? new(CancellationError(scope, deadline, cancellationToken), CancellationOutcome(scope, deadline, cancellationToken))
            : rootFailure;
        if (failure is not null || itemResults.Any(result => result is null))
        {
            failure ??= new(new(ApplicationErrorCode.ProviderFailure, "translation_incomplete"), "translation_incomplete");
            var settlementError = await scope.CompleteAsync(new(scope.Handle, NanoYuan.Zero, operatorCost, false,
                allKnown, allKnown, operatorInput, operatorOutput, failure.Outcome));
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            return ApplicationResult.Failure<TranslationResult>(settlementError ?? failure.Error);
        }

        var successful = itemResults.Select(result => result!).ToArray();
        var publicInput = successful.Aggregate(0L, (total, result) => checked(total + result.PublicInputCharacters));
        var publicOutput = successful.Aggregate(0L, (total, result) => checked(total + result.PublicOutputCharacters));
        var publicActual = resourcePolicy.Price.Calculate(publicInput, publicOutput);
        var error = await scope.CompleteAsync(new(scope.Handle, publicActual, operatorCost, true, allKnown, allKnown,
            operatorInput, operatorOutput, "success"));
        return error is null
            ? ApplicationResult.Success(new TranslationResult(successful.Select(result => result.Value).ToArray(), request.From, request.To))
            : ApplicationResult.Failure<TranslationResult>(error);
    }

    private async Task<ItemWorkResult> TranslateItemAsync(
        int itemIndex,
        string content,
        int batchSize,
        RequestContext context,
        TranslationCommand request,
        ResourcePolicy resourcePolicy,
        OperationScope scope,
        CancellationTokenSource deadline,
        int initialModelIndex,
        ConcurrentBag<TranslationProviderResult> completedAttempts,
        CancellationToken operationToken,
        CancellationToken batchToken)
    {
        var excluded = routing.LogicalModels.ToDictionary(model => model,
            _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        for (var itemAttempt = 1; itemAttempt <= routing.MaximumAttemptsPerConversation; itemAttempt++)
        {
            if (batchToken.IsCancellationRequested)
                return operationToken.IsCancellationRequested
                    ? ItemWorkResult.Failed(CancellationError(scope, deadline, CancellationToken.None),
                        CancellationOutcome(scope, deadline, CancellationToken.None))
                    : ItemWorkResult.Cancelled;

            var remaining = scope.Handle.AbsoluteDeadline - clock.UtcNow;
            if (remaining < routing.AttemptTimeout)
                return ItemWorkResult.Failed(new(ApplicationErrorCode.DeadlineExceeded, "translation_deadline"),
                    "translation_deadline");

            var waitBudget = remaining - routing.AttemptTimeout;
            var wait = resourcePolicy.Admission.QueueWait < waitBudget ? resourcePolicy.Admission.QueueWait : waitBudget;
            var logicalModel = routing.ModelForAttempt(initialModelIndex, itemAttempt);
            IProviderAccessLease accessLease;
            try
            {
                accessLease = await providerAccess.AcquireAsync(new(logicalModel, excluded[logicalModel], wait,
                    policy.ActiveLeaseTtl, policy.LeaseRenewalInterval), batchToken);
            }
            catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
            {
                return operationToken.IsCancellationRequested
                    ? ItemWorkResult.Failed(CancellationError(scope, deadline, CancellationToken.None),
                        CancellationOutcome(scope, deadline, CancellationToken.None))
                    : ItemWorkResult.Cancelled;
            }

            await using (accessLease)
            {
                var attemptNumber = checked(itemIndex * routing.MaximumAttemptsPerConversation + itemAttempt);
                if (!accessLease.Acquired)
                {
                    var accessError = AccessError(accessLease);
                    var poolProvider = $"{logicalModel}/provider-pool";
                    var poolStartedAt = clock.UtcNow;
                    ApplicationResult<ProviderAttemptPreparation> preparation;
                    try
                    {
                        preparation = await scope.PrepareAttemptAsync(attemptNumber, poolProvider, Resources.Translation,
                            poolStartedAt, operationToken);
                    }
                    catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                    {
                        return ItemWorkResult.Failed(CancellationError(scope, deadline, CancellationToken.None),
                            CancellationOutcome(scope, deadline, CancellationToken.None));
                    }
                    if (!preparation.IsSuccess) return ItemWorkResult.Failed(preparation.Error!, preparation.Error!.Detail);
                    var poolAttempt = new ProviderAttempt(preparation.Value!.Id, scope.Handle.OperationId, attemptNumber,
                        poolProvider, Resources.Translation, accessError.Detail, null, 0, 0, NanoYuan.Zero, true,
                        AttemptDispatchState.NotDispatched, poolStartedAt, clock.UtcNow);
                    var poolResult = new TranslationProviderResult(false, [], 0, 0, 0, 0, accessError.Detail,
                        true, accessLease.RejectionReason == ProviderAccessRejectionReason.Saturated,
                        accessLease.RetryAfter, poolAttempt);
                    completedAttempts.Add(poolResult);
                    var poolCompletion = await scope.CompleteAttemptAsync(poolAttempt, CancellationToken.None);
                    if (poolCompletion is not null)
                        return ItemWorkResult.Failed(poolCompletion, poolCompletion.Detail);
                    if (!poolResult.Retryable || itemAttempt == routing.MaximumAttemptsPerConversation)
                        return ItemWorkResult.Failed(accessError, accessError.Detail);

                    var poolRetryDelay = accessLease.RetryAfter ?? RetryDelay(itemAttempt);
                    if (poolRetryDelay + routing.AttemptTimeout > scope.Handle.AbsoluteDeadline - clock.UtcNow)
                        return ItemWorkResult.Failed(new(ApplicationErrorCode.DeadlineExceeded, "translation_deadline"),
                            "translation_deadline");
                    try { await Task.Delay(poolRetryDelay, batchToken); }
                    catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
                    {
                        return operationToken.IsCancellationRequested
                            ? ItemWorkResult.Failed(CancellationError(scope, deadline, CancellationToken.None),
                                CancellationOutcome(scope, deadline, CancellationToken.None))
                            : ItemWorkResult.Cancelled;
                    }
                    continue;
                }

                if (batchToken.IsCancellationRequested) return ItemWorkResult.Cancelled;
                if (scope.Handle.AbsoluteDeadline - clock.UtcNow < routing.AttemptTimeout)
                    return ItemWorkResult.Failed(new(ApplicationErrorCode.DeadlineExceeded, "translation_deadline"),
                        "translation_deadline");

                var access = accessLease.Selection!;
                var attemptStartedAt = clock.UtcNow;
                ApplicationResult<ProviderAttemptPreparation> prepared;
                try
                {
                    prepared = await scope.PrepareAttemptAsync(attemptNumber, access.AttemptProvider,
                        Resources.Translation, attemptStartedAt, operationToken);
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    return ItemWorkResult.Failed(CancellationError(scope, deadline, CancellationToken.None),
                        CancellationOutcome(scope, deadline, CancellationToken.None));
                }
                if (!prepared.IsSuccess) return ItemWorkResult.Failed(prepared.Error!, prepared.Error!.Detail);

                using var timeout = new CancellationTokenSource(routing.AttemptTimeout);
                using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(
                    operationToken, batchToken, accessLease.OwnershipLost, timeout.Token);
                TranslationProviderResult providerResult;
                try
                {
                    providerResult = await provider.TranslateAsync(new(content, request.From, request.To, request.Domain,
                        access, scope.Handle, context.ClientRequestId, context.TraceId, attemptNumber, itemIndex,
                        itemAttempt, batchSize, prepared.Value!.Id, attemptStartedAt, routing.AttemptTimeout), attemptToken.Token);
                }
                catch (HttpRequestException)
                {
                    var attempt = new ProviderAttempt(prepared.Value!.Id, scope.Handle.OperationId, attemptNumber,
                        access.AttemptProvider, Resources.Translation, "network", null, 0, 0, NanoYuan.Zero, false,
                        AttemptDispatchState.Unknown, attemptStartedAt, clock.UtcNow);
                    providerResult = new(false, [], 0, 0, 0, 0, "network", false, true, null, attempt);
                }
                catch (OperationCanceledException)
                {
                    var outcome = timeout.IsCancellationRequested ? "attempt_timeout" : "cancelled";
                    var attempt = new ProviderAttempt(prepared.Value!.Id, scope.Handle.OperationId, attemptNumber,
                        access.AttemptProvider, Resources.Translation, outcome, null, 0, 0, NanoYuan.Zero, false,
                        AttemptDispatchState.Unknown, attemptStartedAt, clock.UtcNow);
                    providerResult = new(false, [], 0, 0, 0, 0, outcome, false, true, null, attempt);
                }

                completedAttempts.Add(providerResult);
                var completionError = await scope.CompleteAttemptAsync(providerResult.Attempt, CancellationToken.None);
                await accessLease.ReleaseAsync(CancellationToken.None);
                if (completionError is not null) return ItemWorkResult.Failed(completionError, completionError.Detail);
                if (operationToken.IsCancellationRequested)
                    return ItemWorkResult.Failed(CancellationError(scope, deadline, CancellationToken.None),
                        CancellationOutcome(scope, deadline, CancellationToken.None));
                if (batchToken.IsCancellationRequested) return ItemWorkResult.Cancelled;

                if (providerResult.Success)
                {
                    if (providerResult.Results.Count != 1)
                        return ItemWorkResult.Failed(new(ApplicationErrorCode.ProviderFailure, "invalid_output"), "invalid_output");
                    return ItemWorkResult.Succeeded(new(providerResult.Results[0], providerResult.PublicInputCharacters,
                        providerResult.PublicOutputCharacters));
                }
                if (!providerResult.Retryable || itemAttempt == routing.MaximumAttemptsPerConversation)
                    return ItemWorkResult.Failed(new(ApplicationErrorCode.ProviderFailure, providerResult.Outcome),
                        providerResult.Outcome);

                excluded[logicalModel].Add(access.AccessId);
                var retryDelay = providerResult.RetryAfter ?? RetryDelay(itemAttempt);
                if (retryDelay + routing.AttemptTimeout > scope.Handle.AbsoluteDeadline - clock.UtcNow)
                    return ItemWorkResult.Failed(new(ApplicationErrorCode.DeadlineExceeded, "translation_deadline"),
                        "translation_deadline");
                try { await Task.Delay(retryDelay, batchToken); }
                catch (OperationCanceledException) when (batchToken.IsCancellationRequested)
                {
                    return operationToken.IsCancellationRequested
                        ? ItemWorkResult.Failed(CancellationError(scope, deadline, CancellationToken.None),
                            CancellationOutcome(scope, deadline, CancellationToken.None))
                        : ItemWorkResult.Cancelled;
                }
            }
        }
        return ItemWorkResult.Failed(new(ApplicationErrorCode.ProviderFailure, "translation_failed"), "translation_failed");
    }

    private TimeSpan RetryDelay(int itemAttempt)
    {
        var exponential = routing.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, itemAttempt - 1);
        var ceiling = Math.Min(routing.MaximumRetryDelay.TotalMilliseconds, exponential);
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling);
    }

    private static ApplicationError AccessError(IProviderAccessLease lease) =>
        lease.RejectionReason == ProviderAccessRejectionReason.Saturated
            ? new(ApplicationErrorCode.QueueFull, "provider_access_saturated", lease.RetryAfter)
            : new(ApplicationErrorCode.DependencyUnavailable, "provider_access_unavailable", lease.RetryAfter);

    private static ApplicationError CancellationError(
        OperationScope scope, CancellationTokenSource deadline, CancellationToken caller) =>
        scope.OwnershipLost.IsCancellationRequested ? new(ApplicationErrorCode.LeaseLost, "ownership_lost") :
        deadline.IsCancellationRequested ? new(ApplicationErrorCode.DeadlineExceeded, "translation_deadline") :
        caller.IsCancellationRequested ? new(ApplicationErrorCode.DeadlineExceeded, "caller_cancelled") :
        new(ApplicationErrorCode.ProviderFailure, "provider_cancelled");

    private static string CancellationOutcome(
        OperationScope scope, CancellationTokenSource deadline, CancellationToken caller) =>
        scope.OwnershipLost.IsCancellationRequested ? "lease_lost" :
        deadline.IsCancellationRequested ? "deadline" :
        caller.IsCancellationRequested ? "caller_cancelled" : "cancelled";

    private static CancellationTokenSource Deadline(DateTimeOffset expiresAt, DateTimeOffset now) =>
        new(expiresAt <= now ? TimeSpan.Zero : expiresAt - now);

    private sealed record ItemSuccess(string Value, long PublicInputCharacters, long PublicOutputCharacters);
    private sealed record BatchFailure(ApplicationError Error, string Outcome);
    private sealed record ItemWorkResult(ItemSuccess? Success, BatchFailure? Failure)
    {
        public static ItemWorkResult Cancelled { get; } = new(null, null);
        public static ItemWorkResult Succeeded(ItemSuccess success) => new(success, null);
        public static ItemWorkResult Failed(ApplicationError error, string outcome) => new(null, new(error, outcome));
    }
}

public sealed class TableUseCase(
    OperationCoordinator operations,
    ITableWorkerClient worker,
    ISystemClock clock,
    ServicePolicy policy)
{
    public async Task<ApplicationResult<TableExtractionResult>> ExecuteAsync(
        RequestContext context,
        TableCommand request,
        CancellationToken cancellationToken)
    {
        var resourcePolicy = policy.Get(Resources.TableExtraction);
        var price = resourcePolicy.Price.Input;
        var started = await operations.StartAsync(Resources.TableExtraction, UsageKind.TableExtraction,
            context, new(1), resourcePolicy.OperatorMaximum, cancellationToken);
        if (!started.IsSuccess) return ApplicationResult.Failure<TableExtractionResult>(started.Error!);

        await using var scope = started.Value!;
        var dispatchError = await scope.DispatchAsync(cancellationToken);
        if (dispatchError is not null) return ApplicationResult.Failure<TableExtractionResult>(dispatchError);
        var attemptStartedAt = clock.UtcNow;
        var prepared = await scope.PrepareAttemptAsync(1, "table-worker", Resources.TableExtraction,
            attemptStartedAt, cancellationToken);
        if (!prepared.IsSuccess) return ApplicationResult.Failure<TableExtractionResult>(prepared.Error!);
        using var deadline = new CancellationTokenSource(scope.Handle.AbsoluteDeadline <= clock.UtcNow
            ? TimeSpan.Zero : scope.Handle.AbsoluteDeadline - clock.UtcNow);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, scope.OwnershipLost, deadline.Token);
        TableExtractionResult result;
        try
        {
            result = await worker.ExtractAsync(new(scope.Handle, request, context.ClientRequestId, context.TraceId,
                prepared.Value!.Id, attemptStartedAt), linked.Token);
        }
        catch (OperationCanceledException)
        {
            var error = scope.OwnershipLost.IsCancellationRequested
                ? new ApplicationError(ApplicationErrorCode.LeaseLost, "ownership_lost")
                : new ApplicationError(ApplicationErrorCode.DeadlineExceeded, "table_deadline");
            await scope.CompleteAsync(new(scope.Handle, NanoYuan.Zero, NanoYuan.Zero, false, false, false, 0, 0, error.Detail));
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            return ApplicationResult.Failure<TableExtractionResult>(error);
        }

        var success = result.Status == TableExtractionStatus.Success;
        var settlementError = await scope.CompleteAsync(new(scope.Handle, success ? price : NanoYuan.Zero,
            result.Attempt.Cost, success, result.Attempt.CostKnown, result.Attempt.CostKnown,
            success ? 1 : 0, 0, result.Status.ToString().ToLowerInvariant()), result.Attempt);
        return settlementError is null
            ? ApplicationResult.Success(result)
            : ApplicationResult.Failure<TableExtractionResult>(settlementError);
    }

}
