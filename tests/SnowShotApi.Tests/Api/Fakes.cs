using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SnowShot.Application;
using SnowShot.Domain;

namespace SnowShotApi.Tests.Api;

internal sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeLedger Ledger { get; } = new();
    public FakeChatClient Chat { get; } = new();
    public FakeTranslationClient Translation { get; } = new();
    public FakeTableClient Table { get; } = new();
    public ServicePolicy Policy => Services.GetRequiredService<ServicePolicy>();

    public HttpClient CreateAnonymousClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SnowShot"] = "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            ["ConnectionStrings:Redis"] = "",
            ["Identity:HmacKeyBase64"] = Convert.ToBase64String(new byte[32]),
            ["Providers:Translation:LogicalModel"] = "qwen-flash",
            ["Providers:CloudProviders:aliyun:Endpoint"] = "https://provider.test/chat",
            ["Providers:CloudProviders:aliyun:ApiKey"] = "test-key",
            ["Providers:CloudProviders:deepseek:Endpoint"] = "https://provider.test/chat",
            ["Providers:CloudProviders:deepseek:ApiKey"] = "test-key",
            ["Providers:CloudProviders:test:Endpoint"] = "https://provider.test/chat",
            ["Providers:CloudProviders:test:ApiKey"] = "test-key",
            ["Providers:Models:qwen-flash:Accesses:aliyun:Provider"] = "test",
            ["Providers:Models:qwen-flash:Accesses:aliyun:UpstreamModel"] = "qwen-flash",
            ["Providers:Models:qwen-flash:Accesses:aliyun:MaxConcurrentRequests"] = "16",
            ["Providers:Models:qwen-plus:Accesses:aliyun:Provider"] = "test",
            ["Providers:Models:qwen-plus:Accesses:aliyun:UpstreamModel"] = "qwen-plus",
            ["Providers:Models:qwen-plus:Accesses:aliyun:MaxConcurrentRequests"] = "16",
            ["Providers:Models:qwen3-vl-flash:Accesses:aliyun:Provider"] = "test",
            ["Providers:Models:qwen3-vl-flash:Accesses:aliyun:UpstreamModel"] = "qwen3-vl-flash",
            ["Providers:Models:qwen3-vl-flash:Accesses:aliyun:MaxConcurrentRequests"] = "16",
            ["Providers:Table:BaseUrl"] = "http://table.test/",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IPrincipalIdentity>();
            services.RemoveAll<IAdmissionController>();
            services.RemoveAll<IProviderAccessPool>();
            services.RemoveAll<IOperationLedger>();
            services.RemoveAll<IChatProviderClient>();
            services.RemoveAll<ITranslationProviderClient>();
            services.RemoveAll<ITableWorkerClient>();
            services.RemoveAll<IReadinessService>();
            services.AddSingleton<IPrincipalIdentity, FakeIdentity>();
            services.AddSingleton<IAdmissionController, FakeAdmission>();
            services.AddSingleton<IProviderAccessPool, FakeProviderAccessPool>();
            services.AddSingleton<IOperationLedger>(Ledger);
            services.AddSingleton<IChatProviderClient>(Chat);
            services.AddSingleton<ITranslationProviderClient>(Translation);
            services.AddSingleton<ITableWorkerClient>(Table);
            services.AddSingleton<IReadinessService, FakeReadiness>();
        });
    }
}

internal sealed class FakeProviderAccessPool : IProviderAccessPool
{
    public Task<IProviderAccessLease> AcquireAsync(ProviderAccessRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IProviderAccessLease>(new Lease(new(request.LogicalModel, "aliyun", "test", request.LogicalModel)));
    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    private sealed class Lease(ProviderAccessSelection selection) : IProviderAccessLease
    {
        public bool Acquired => true;
        public ProviderAccessSelection Selection { get; } = selection;
        ProviderAccessSelection? IProviderAccessLease.Selection => Selection;
        public ProviderAccessRejectionReason RejectionReason => ProviderAccessRejectionReason.None;
        public TimeSpan? RetryAfter => null;
        public CancellationToken OwnershipLost => CancellationToken.None;
        public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeIdentity : IPrincipalIdentity
{
    public Task<AnonymousPrincipal?> ResolveAsync(string? clientAddress, CancellationToken cancellationToken) => Task.FromResult<AnonymousPrincipal?>(
        new(Guid.Parse("01900000-0000-7000-8000-000000000001"), "principal"));
}

internal sealed class FakeAdmission : IAdmissionController
{
    public Task<IAdmissionLease> AcquireAsync(AdmissionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IAdmissionLease>(new Lease());
    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    private sealed class Lease : IAdmissionLease
    {
        public bool Acquired => true;
        public TimeSpan? RetryAfter => null;
        public AdmissionRejectionReason RejectionReason => AdmissionRejectionReason.None;
        public CancellationToken OwnershipLost => CancellationToken.None;
        public Task<bool> RenewAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeLedger : IOperationLedger
{
    private readonly ConcurrentDictionary<Guid, ReserveOperation> _operations = new();
    public ConcurrentQueue<ReserveOperation> Reservations { get; } = new();
    public ConcurrentQueue<OperationSettlement> Settlements { get; } = new();
    public ConcurrentQueue<ProviderAttempt> Attempts { get; } = new();
    public ConcurrentQueue<ProviderAttemptPreparation> Preparations { get; } = new();
    public bool SettlementCompleted { get; private set; }
    public ReservationRejectionReason RejectWith { get; set; }
    public SettlementRejectionReason RejectSettlementWith { get; set; }

    public Task<OperationReservation> ReserveAsync(ReserveOperation operation, CancellationToken cancellationToken)
    {
        if (RejectWith != ReservationRejectionReason.None)
            return Task.FromResult(new OperationReservation(false, null, ReservationState.Released, RejectWith,
                RejectWith == ReservationRejectionReason.DuplicateRequest ? null : TimeSpan.FromMinutes(1)));
        Reservations.Enqueue(operation); _operations[operation.Id] = operation;
        var handle = new OperationHandle(operation.Id, operation.OwnerToken, 1,
            DateTimeOffset.UtcNow.Add(operation.ExecutionTimeout), operation.Snapshot);
        return Task.FromResult(new OperationReservation(true, handle, ReservationState.Reserved));
    }
    public Task<OwnershipMutationResult> MarkDispatchedAsync(OperationHandle handle, TimeSpan leaseTtl, CancellationToken cancellationToken) =>
        Task.FromResult(OwnershipMutationResult.Applied);
    public Task<OwnershipMutationResult> RenewAsync(OperationHandle handle, TimeSpan leaseTtl, CancellationToken cancellationToken) =>
        Task.FromResult(OwnershipMutationResult.Applied);
    public Task<OwnershipMutationResult> PrepareAttemptAsync(ProviderAttemptPreparation attempt, CancellationToken cancellationToken)
    {
        Preparations.Enqueue(attempt);
        return Task.FromResult(OwnershipMutationResult.Applied);
    }
    public Task<OwnershipMutationResult> CompleteAttemptAsync(OperationHandle handle, ProviderAttempt attempt, CancellationToken cancellationToken)
    {
        Attempts.Enqueue(attempt);
        return Task.FromResult(OwnershipMutationResult.Applied);
    }
    public Task<SettlementResult> CompleteAsync(OperationCompletion completion, CancellationToken cancellationToken)
    {
        var settlement = completion.Settlement;
        if (RejectSettlementWith != SettlementRejectionReason.None)
            return Task.FromResult(new SettlementResult(false, null, RejectSettlementWith));
        if (completion.FinalAttempt is not null) Attempts.Enqueue(completion.FinalAttempt);
        Settlements.Enqueue(settlement);
        var operation = _operations[settlement.Handle.OperationId];
        var decision = ReservationRules.Settle(ReservationState.Dispatched, operation.Snapshot, settlement.ReportedPublicCost,
            settlement.ReportedOperatorCost, settlement.Delivered, settlement.CostKnown, settlement.VerifiableOverage,
            settlement.InputUnits, settlement.OutputUnits, settlement.Outcome);
        SettlementCompleted = true;
        return Task.FromResult(new SettlementResult(true, decision));
    }
}

internal sealed class FakeChatClient : IChatProviderClient
{
    public bool ThrowAfterFrame { get; set; }

    public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(ChatProviderCommand command,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new ChatProviderEvent.Frame(System.Text.Encoding.UTF8.GetBytes("{\"id\":\"chat-1\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}"));
        await Task.Yield();
        if (ThrowAfterFrame) throw new InvalidOperationException("Simulated post-frame failure.");
        yield return new ChatProviderEvent.Terminal(new ChatUsage(100, 20, 120), true, true, "success",
            new(command.AttemptId, command.Operation.OperationId, command.AttemptNumber, command.Access.AttemptProvider, command.Request.Model, "success", 200, 100, 20,
                new(36_000), true, AttemptDispatchState.Dispatched, command.AttemptStartedAt, command.AttemptStartedAt.AddMilliseconds(10)));
    }
}

internal sealed class FakeTranslationClient : ITranslationProviderClient
{
    private int _active;
    private int _maximumActive;
    public ConcurrentQueue<TranslationProviderCommand> Commands { get; } = new();
    public Func<TranslationProviderCommand, CancellationToken, Task<TranslationProviderResult>>? Handler { get; set; }
    public int MaximumActive => Volatile.Read(ref _maximumActive);

    public async Task<TranslationProviderResult> TranslateAsync(TranslationProviderCommand command, CancellationToken cancellationToken)
    {
        Commands.Enqueue(command);
        var active = Interlocked.Increment(ref _active);
        var maximum = Volatile.Read(ref _maximumActive);
        while (active > maximum)
            maximum = Interlocked.CompareExchange(ref _maximumActive, active, maximum);
        try
        {
            return Handler is null ? Result(command) : await Handler(command, cancellationToken);
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    public static TranslationProviderResult Result(
        TranslationProviderCommand command,
        bool success = true,
        string? value = null,
        string outcome = "success",
        bool retryable = false,
        TimeSpan? retryAfter = null,
        int? status = 200,
        bool costKnown = true,
        AttemptDispatchState dispatchState = AttemptDispatchState.Dispatched) =>
        new(success, success ? [value ?? $"translated:{command.Content}"] : [],
            10, success ? 20 : 0, 10, 20, outcome, costKnown, retryable, retryAfter,
            new(command.AttemptId, command.Operation.OperationId, command.AttemptNumber, command.Access.AttemptProvider, Resources.Translation,
                outcome, status, 10, 20,
                costKnown && dispatchState == AttemptDispatchState.Dispatched ? new(180_000) : NanoYuan.Zero,
                costKnown, dispatchState, command.AttemptStartedAt, command.AttemptStartedAt.AddMilliseconds(10)));
}

internal sealed class FakeTableClient : ITableWorkerClient
{
    public TableExtractionStatus Status { get; set; } = TableExtractionStatus.Success;
    public int Invocations { get; private set; }
    public Task<TableExtractionResult> ExtractAsync(TableProviderCommand command, CancellationToken cancellationToken)
    {
        Invocations++;
        var cost = Status == TableExtractionStatus.Success ? new NanoYuan(30_000_000) : NanoYuan.Zero;
        return Task.FromResult(new TableExtractionResult(Status,
            Status == TableExtractionStatus.Success ? "<table><tr><td>ok</td></tr></table>" : null,
            new(command.AttemptId, command.Operation.OperationId, 1, "table-worker", Resources.TableExtraction, Status.ToString(), 200,
                Status == TableExtractionStatus.Success ? 1 : 0, 0, cost, true, AttemptDispatchState.Dispatched,
                command.AttemptStartedAt, command.AttemptStartedAt.AddMilliseconds(10))));
    }
}

internal sealed class FakeReadiness(ServicePolicy policy) : IReadinessService
{
    public Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(
        new ReadinessReport(true, policy.Revision, policy.Fingerprint, policy.Revision, policy.Fingerprint,
            new Dictionary<string, bool> { ["postgresql"] = true, ["admission"] = true, ["policy"] = true }));
}
