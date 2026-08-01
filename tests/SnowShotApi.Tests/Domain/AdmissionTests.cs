using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Admission;

namespace SnowShotApi.Tests.Domain;

public sealed class AdmissionTests
{
    private static readonly AdmissionPolicy Policy = new(
        RequestsPerMinute: 100,
        PerPrincipalConcurrency: 1,
        GlobalConcurrency: 1,
        GlobalQueueLength: 1,
        QueueWait: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task QueueCapacityIsExactAndHandoffIsFifo()
    {
        var limiter = new InMemoryAdmissionController(TimeProvider.System);
        await using var first = await limiter.AcquireAsync(Request("first"), TestContext.Current.CancellationToken);
        var secondTask = limiter.AcquireAsync(Request("second"), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await using var rejected = await limiter.AcquireAsync(Request("third"), TestContext.Current.CancellationToken);

        Assert.True(first.Acquired);
        Assert.False(rejected.Acquired);
        Assert.Equal(AdmissionRejectionReason.QueueFull, rejected.RejectionReason);
        await first.ReleaseAsync(TestContext.Current.CancellationToken);
        await using var second = await secondTask;
        Assert.True(second.Acquired);
    }

    [Fact]
    public async Task CancelledQueueEntryDoesNotAccumulate()
    {
        var limiter = new InMemoryAdmissionController(TimeProvider.System);
        await using var first = await limiter.AcquireAsync(Request("first"), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var cancelledTask = limiter.AcquireAsync(Request("cancelled"), cancellation.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledTask);

        var replacementTask = limiter.AcquireAsync(Request("replacement"), TestContext.Current.CancellationToken);
        await first.ReleaseAsync(TestContext.Current.CancellationToken);
        await using var replacement = await replacementTask;
        Assert.True(replacement.Acquired);
    }

    [Fact]
    public async Task ExpiredLeaseLosesOwnershipAndCannotRenew()
    {
        var limiter = new InMemoryAdmissionController(TimeProvider.System);
        var request = Request("principal") with { LeaseTtl = TimeSpan.FromMilliseconds(20) };
        await using var lease = await limiter.AcquireAsync(request, TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(await lease.RenewAsync(TestContext.Current.CancellationToken));
        Assert.True(lease.OwnershipLost.IsCancellationRequested);
    }

    private static AdmissionRequest Request(string principal) => new(
        "test",
        principal,
        Policy,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(1));
}
