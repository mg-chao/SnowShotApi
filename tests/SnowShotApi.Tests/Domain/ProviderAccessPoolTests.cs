using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Providers;

namespace SnowShotApi.Tests.Domain;

public sealed class ProviderAccessPoolTests
{
    [Fact]
    public void RedisCircuitKeysShareOneClusterHashTag()
    {
        var selection = Catalog().Get(Resources.QwenFlash, "a").Selection;
        var keys = RedisProviderCircuitRegistry.Keys(selection);
        var tags = new[] { keys.State, keys.Total, keys.Failures }.Select(value =>
        {
            var start = value.IndexOf('{');
            var end = value.IndexOf('}');
            return start >= 0 && end > start ? value[(start + 1)..end] : string.Empty;
        }).ToArray();

        Assert.DoesNotContain(tags, string.IsNullOrEmpty);
        Assert.Single(tags.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CircuitOpensAfterConsecutiveFailuresAndPoolFallsBackToAnotherAccess()
    {
        var catalog = Catalog();
        var options = new ProviderCircuitOptions { ConsecutiveFailuresToOpen = 5 };
        var circuits = new InMemoryProviderCircuitRegistry(options, TimeProvider.System);
        var failing = catalog.Get(Resources.QwenFlash, "a").Selection;
        for (var index = 0; index < 5; index++)
            await circuits.ReportAsync(failing, ProviderCircuitOutcome.TransientFailure, null,
                TestContext.Current.CancellationToken);
        var pool = new InMemoryProviderAccessPool(catalog, circuits);

        await using var lease = await pool.AcquireAsync(Request(), TestContext.Current.CancellationToken);

        Assert.True(lease.Acquired);
        Assert.Equal("b", lease.Selection!.AccessId);
        var snapshot = Assert.Single(await circuits.SnapshotAsync(TestContext.Current.CancellationToken),
            value => value.Selection.AccessId == "a");
        Assert.Equal(ProviderCircuitState.Open, snapshot.State);
    }

    [Fact]
    public async Task HalfOpenCircuitAllowsOneProbeAndRequiresTwoSuccessesToClose()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));
        var options = new ProviderCircuitOptions
        {
            ConsecutiveFailuresToOpen = 2,
            InitialBreakSeconds = 5,
            HalfOpenSuccessesToClose = 2,
            ProbeLeaseSeconds = 5,
        };
        var circuits = new InMemoryProviderCircuitRegistry(options, time);
        var selection = Catalog().Get(Resources.QwenFlash, "a").Selection;
        await circuits.ReportAsync(selection, ProviderCircuitOutcome.TransientFailure, null, CancellationToken.None);
        await circuits.ReportAsync(selection, ProviderCircuitOutcome.TransientFailure, null, CancellationToken.None);
        Assert.False(await circuits.TryAcquireAsync(selection, CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await circuits.TryAcquireAsync(selection, CancellationToken.None));
        Assert.False(await circuits.TryAcquireAsync(selection, CancellationToken.None));
        await circuits.ReportAsync(selection, ProviderCircuitOutcome.Success, null, CancellationToken.None);
        Assert.True(await circuits.TryAcquireAsync(selection, CancellationToken.None));
        await circuits.ReportAsync(selection, ProviderCircuitOutcome.Success, null, CancellationToken.None);

        var snapshot = Assert.Single(await circuits.SnapshotAsync(CancellationToken.None));
        Assert.Equal(ProviderCircuitState.Closed, snapshot.State);
    }

    [Fact]
    public async Task InMemoryPoolBalancesAndCapsEveryAccessAtConfiguredCapacity()
    {
        var pool = Pool(Catalog());
        var leases = new List<IProviderAccessLease>();
        var request = Request();

        for (var index = 0; index < 32; index++)
            leases.Add(await pool.AcquireAsync(request, TestContext.Current.CancellationToken));

        Assert.All(leases, lease => Assert.True(lease.Acquired));
        Assert.Equal(16, leases.Count(lease => lease.Selection!.AccessId == "a"));
        Assert.Equal(16, leases.Count(lease => lease.Selection!.AccessId == "b"));
        await using var rejected = await pool.AcquireAsync(request with { QueueWait = TimeSpan.Zero }, TestContext.Current.CancellationToken);
        Assert.False(rejected.Acquired);
        Assert.Equal(ProviderAccessRejectionReason.Saturated, rejected.RejectionReason);

        foreach (var lease in leases) await lease.DisposeAsync();
    }

    [Fact]
    public async Task InMemoryPoolUsesTheConfiguredCapacityPerAccess()
    {
        var pool = Pool(Catalog(2));
        var request = Request();
        var leases = new List<IProviderAccessLease>();

        for (var index = 0; index < 4; index++)
            leases.Add(await pool.AcquireAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(2, leases.Count(lease => lease.Selection!.AccessId == "a"));
        Assert.Equal(2, leases.Count(lease => lease.Selection!.AccessId == "b"));
        await using var rejected = await pool.AcquireAsync(request with { QueueWait = TimeSpan.Zero },
            TestContext.Current.CancellationToken);
        Assert.False(rejected.Acquired);
        Assert.Equal(ProviderAccessRejectionReason.Saturated, rejected.RejectionReason);

        foreach (var lease in leases) await lease.DisposeAsync();
    }

    [Fact]
    public async Task InMemoryPoolSkipsExcludedAndSaturatedAccesses()
    {
        var pool = Pool(Catalog());
        var onlyB = Request() with { ExcludedAccessIds = new HashSet<string>(StringComparer.Ordinal) { "a" } };
        var bLeases = new List<IProviderAccessLease>();
        for (var index = 0; index < 16; index++) bLeases.Add(await pool.AcquireAsync(onlyB, TestContext.Current.CancellationToken));
        Assert.All(bLeases, lease => Assert.Equal("b", lease.Selection!.AccessId));

        await using var next = await pool.AcquireAsync(Request(), TestContext.Current.CancellationToken);
        Assert.True(next.Acquired);
        Assert.Equal("a", next.Selection!.AccessId);

        foreach (var lease in bLeases) await lease.DisposeAsync();
    }

    private static ProviderAccessRequest Request() => new(Resources.QwenFlash,
        new HashSet<string>(StringComparer.Ordinal), TimeSpan.FromMilliseconds(100),
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

    private static InMemoryProviderAccessPool Pool(ProviderModelCatalog catalog) => new(catalog,
        new InMemoryProviderCircuitRegistry(new ProviderCircuitOptions(), TimeProvider.System));

    private static ProviderModelCatalog Catalog(int maxConcurrentRequests = 16)
    {
        ProviderModelOptions Model(string upstream) => new()
        {
            Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
            {
                ["a"] = new() { Provider = "one", UpstreamModel = upstream, MaxConcurrentRequests = maxConcurrentRequests },
                ["b"] = new() { Provider = "two", UpstreamModel = upstream, MaxConcurrentRequests = maxConcurrentRequests },
            },
        };
        return new(new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["one"] = new() { Endpoint = "https://one.test/chat", ApiKey = "a" },
                ["two"] = new() { Endpoint = "https://two.test/chat", ApiKey = "b" },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.QwenFlash] = Model("flash"),
                [Resources.QwenPlus] = Model("plus"),
                [Resources.QwenVisionFlash] = Model("vision"),
            },
        }, new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, requireHttps: true);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
