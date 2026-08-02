using System.Text.Json;
using System.Security.Cryptography;
using SnowShot.Domain;

namespace SnowShot.Application;

public static class ApplicationResources
{
    public const string Translation = Resources.Translation;
    public const string QwenFlash = Resources.QwenFlash;
    public const string QwenPlus = Resources.QwenPlus;
    public const string QwenVisionFlash = Resources.QwenVisionFlash;
    public const string DeepSeekV4 = Resources.DeepSeekV4;
    public const string TableExtraction = Resources.TableExtraction;
}

public sealed record ChatModelDefinition(
    string Model,
    bool Thinking,
    bool SupportVision);

public interface IChatModelCatalog
{
    IReadOnlyList<ChatModelDefinition> Models { get; }
    bool Contains(string model);
}

public sealed record TranslationRouting(
    IReadOnlyList<string> LogicalModels,
    int MaximumConcurrentConversations,
    int MaximumAttemptsPerConversation,
    TimeSpan AttemptTimeout,
    TimeSpan InitialRetryDelay,
    TimeSpan MaximumRetryDelay)
{
    public int InitialModelIndex(Guid operationId)
    {
        var hash = SHA256.HashData(operationId.ToByteArray());
        return hash[0] % LogicalModels.Count;
    }

    public string ModelForAttempt(int initialModelIndex, int itemAttemptNumber) =>
        LogicalModels[(initialModelIndex + itemAttemptNumber - 1) % LogicalModels.Count];
}
public sealed record TableRequestLimits(long MaximumUploadBytes);
public sealed record LifecycleTimeouts(TimeSpan Cleanup, TimeSpan AttemptRecording, TimeSpan Settlement)
{
    public static LifecycleTimeouts Defaults { get; } = new(
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
}

public sealed record RequestContext(string? ClientAddress, string ClientRequestId, string TraceId);
public sealed record ChatCommand(
    string Model,
    ReadOnlyMemory<byte> Utf8Json);
public sealed record TranslationCommand(
    IReadOnlyList<string?> Content,
    string From,
    string To,
    string Domain);
public sealed record TranslationResult(IReadOnlyList<string> Results, string From, string To);
public sealed record TableCommand(ReadOnlyMemory<byte> WebpData);

public sealed record AnonymousPrincipal(Guid Id, string AdmissionKey);

public interface IPrincipalIdentity
{
    Task<AnonymousPrincipal?> ResolveAsync(string? clientAddress, CancellationToken cancellationToken);
}

public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

public enum AdmissionRejectionReason
{
    None,
    RateLimit,
    PrincipalConcurrency,
    QueueFull,
    QueueTimeout,
    DependencyUnavailable,
}

public sealed record AdmissionRequest(
    string Resource,
    string PrincipalKey,
    AdmissionPolicy Policy,
    TimeSpan QueueWait,
    TimeSpan LeaseTtl);

public interface IAdmissionLease : IAsyncDisposable
{
    bool Acquired { get; }
    TimeSpan? RetryAfter { get; }
    AdmissionRejectionReason RejectionReason { get; }
    CancellationToken OwnershipLost { get; }
    Task<bool> RenewAsync(CancellationToken cancellationToken);
    Task ReleaseAsync(CancellationToken cancellationToken);
}

public interface IAdmissionController
{
    Task<IAdmissionLease> AcquireAsync(AdmissionRequest request, CancellationToken cancellationToken);
    Task<bool> CheckReadyAsync(CancellationToken cancellationToken);
}

public sealed record ReserveOperation(
    Guid Id,
    Guid PrincipalId,
    UsageKind Kind,
    ReservationSnapshot Snapshot,
    byte[] IdempotencyHash,
    byte[] OwnerToken,
    TimeSpan ExecutionTimeout,
    TimeSpan LeaseTtl);

public enum ReservationRejectionReason
{
    None,
    DuplicateRequest,
    AllowanceExhausted,
    OperatorBudgetExhausted,
    PolicyUnavailable,
}

public sealed record OperationReservation(
    bool Accepted,
    OperationHandle? Handle,
    ReservationState State,
    ReservationRejectionReason RejectionReason = ReservationRejectionReason.None,
    TimeSpan? RetryAfter = null);

public enum OwnershipMutationResult { Applied, LeaseLost }
public enum SettlementRejectionReason { None, LeaseLost, Conflict }
public enum AttemptDispatchState { Prepared, NotDispatched, Dispatched, Unknown }

public sealed record OperationSettlement(
    OperationHandle Handle,
    NanoYuan ReportedPublicCost,
    NanoYuan ReportedOperatorCost,
    bool Delivered,
    bool CostKnown,
    bool VerifiableOverage,
    long InputUnits,
    long OutputUnits,
    string Outcome);

public sealed record SettlementResult(
    bool Accepted,
    SettlementDecision? Decision,
    SettlementRejectionReason RejectionReason = SettlementRejectionReason.None);

public sealed record ProviderAttempt(
    Guid Id,
    Guid OperationId,
    int AttemptNumber,
    string Provider,
    string Resource,
    string Outcome,
    int? HttpStatus,
    long InputUnits,
    long OutputUnits,
    NanoYuan Cost,
    bool CostKnown,
    AttemptDispatchState DispatchState,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record ProviderAttemptPreparation(
    Guid Id,
    OperationHandle Handle,
    int AttemptNumber,
    string Provider,
    string Resource,
    DateTimeOffset StartedAt);

public sealed record OperationCompletion(OperationSettlement Settlement, ProviderAttempt? FinalAttempt = null);

public interface IOperationLedger
{
    Task<OperationReservation> ReserveAsync(ReserveOperation operation, CancellationToken cancellationToken);
    Task<OwnershipMutationResult> MarkDispatchedAsync(OperationHandle handle, TimeSpan leaseTtl, CancellationToken cancellationToken);
    Task<OwnershipMutationResult> RenewAsync(OperationHandle handle, TimeSpan leaseTtl, CancellationToken cancellationToken);
    Task<OwnershipMutationResult> PrepareAttemptAsync(ProviderAttemptPreparation attempt, CancellationToken cancellationToken);
    Task<OwnershipMutationResult> CompleteAttemptAsync(OperationHandle handle, ProviderAttempt attempt, CancellationToken cancellationToken);
    Task<SettlementResult> CompleteAsync(OperationCompletion completion, CancellationToken cancellationToken);
}

public interface IOperationReconciler
{
    Task<int> ReconcileExpiredAsync(int maxOperations, CancellationToken cancellationToken);
}

public interface IPersistenceReadinessProbe
{
    Task<PersistenceReadiness> CheckReadyAsync(CancellationToken cancellationToken);
}

public sealed record PersistenceReadiness(
    bool Connected,
    bool SchemaCurrent,
    bool PolicyConverged,
    long? ActivePolicyRevision,
    string? ActivePolicyFingerprint);

public sealed record ChatUsage(long PromptTokens, long CompletionTokens, long TotalTokens, long ReasoningTokens = 0);

public abstract record ChatProviderEvent
{
    private ChatProviderEvent() { }
    public sealed record Frame(ReadOnlyMemory<byte> Utf8Json) : ChatProviderEvent;
    public sealed record Terminal(ChatUsage? Usage, bool Delivered, bool CostKnown, string Outcome, ProviderAttempt Attempt) : ChatProviderEvent;
    public sealed record Failure(string Category, bool Retryable, ProviderAttempt Attempt) : ChatProviderEvent;
}

public sealed record ProviderAccessSelection(
    string LogicalModel,
    string AccessId,
    string Provider,
    string UpstreamModel)
{
    public string AttemptProvider => $"{LogicalModel}/{Provider}/{AccessId}";
}

public enum ProviderAccessRejectionReason { None, Saturated, DependencyUnavailable }

public sealed record ProviderAccessRequest(
    string LogicalModel,
    IReadOnlySet<string> ExcludedAccessIds,
    TimeSpan QueueWait,
    TimeSpan LeaseTtl,
    TimeSpan RenewalInterval);

public interface IProviderAccessLease : IAsyncDisposable
{
    bool Acquired { get; }
    ProviderAccessSelection? Selection { get; }
    ProviderAccessRejectionReason RejectionReason { get; }
    TimeSpan? RetryAfter { get; }
    CancellationToken OwnershipLost { get; }
    Task ReleaseAsync(CancellationToken cancellationToken);
}

public interface IProviderAccessPool
{
    Task<IProviderAccessLease> AcquireAsync(ProviderAccessRequest request, CancellationToken cancellationToken);
    Task<bool> CheckReadyAsync(CancellationToken cancellationToken);
}

public sealed record ChatProviderCommand(OperationHandle Operation, ChatCommand Request,
    ProviderAccessSelection Access, string RequestId, string TraceId, int AttemptNumber,
    Guid AttemptId, DateTimeOffset AttemptStartedAt);

public interface IChatProviderClient
{
    IAsyncEnumerable<ChatProviderEvent> StreamAsync(ChatProviderCommand command, CancellationToken cancellationToken);
}

public sealed record TranslationProviderCommand(
    string Content,
    string From,
    string To,
    string Domain,
    ProviderAccessSelection Access,
    OperationHandle Operation,
    string RequestId,
    string TraceId,
    int AttemptNumber,
    int ItemIndex,
    int ItemAttemptNumber,
    int BatchSize,
    Guid AttemptId,
    DateTimeOffset AttemptStartedAt,
    TimeSpan Timeout);

public sealed record TranslationProviderResult(
    bool Success,
    IReadOnlyList<string> Results,
    long PublicInputCharacters,
    long PublicOutputCharacters,
    long OperatorInputCharacters,
    long OperatorOutputCharacters,
    string Outcome,
    bool CostKnown,
    bool Retryable,
    TimeSpan? RetryAfter,
    ProviderAttempt Attempt);

public interface ITranslationProviderClient
{
    Task<TranslationProviderResult> TranslateAsync(TranslationProviderCommand command, CancellationToken cancellationToken);
}

public enum TableExtractionStatus { Success, InvalidRequest, NoTable, InferenceFailed, Unavailable, Busy, Timeout }
public sealed record TableProviderCommand(OperationHandle Operation, TableCommand Request, string RequestId, string TraceId,
    Guid AttemptId, DateTimeOffset AttemptStartedAt);
public sealed record TableExtractionResult(TableExtractionStatus Status, string? Html, ProviderAttempt Attempt);

public interface ITableWorkerClient
{
    Task<TableExtractionResult> ExtractAsync(TableProviderCommand command, CancellationToken cancellationToken);
}

public sealed record ReadinessReport(
    bool Ready,
    long ConfiguredPolicyRevision,
    string ConfiguredPolicyFingerprint,
    long? ActivePolicyRevision,
    string? ActivePolicyFingerprint,
    IReadOnlyDictionary<string, bool> Components);

public interface IReadinessService
{
    Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken);
}

public interface IDependencyHealth
{
    void Report(string component, bool healthy);
    IReadOnlyDictionary<string, DependencyStatus> Snapshot();
}

public sealed record DependencyStatus(bool Healthy, DateTimeOffset ObservedAt);

public interface IOperationTelemetry
{
    void RenewalFailed(string reason);
    void FencedMutation(string mutation, string result);
    void AttemptCheckpointFailed(string stage, string reason);
    void LifecycleFailed(string stage, string reason, Exception exception);
}

public interface ITranslationTelemetry
{
    void BatchStarted(int batchSize);
}
