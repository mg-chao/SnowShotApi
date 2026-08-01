using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Providers;

namespace SnowShotApi.Tests.Domain;

public sealed class ProviderAccessPoolTests
{
    [Fact]
    public async Task InMemoryPoolBalancesAndCapsEveryAccessAtConfiguredCapacity()
    {
        var pool = new InMemoryProviderAccessPool(Catalog());
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
        var pool = new InMemoryProviderAccessPool(Catalog(2));
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
        var pool = new InMemoryProviderAccessPool(Catalog());
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
        }, new TranslationProviderOptions { LogicalModel = Resources.QwenFlash }, requireHttps: true);
    }
}
