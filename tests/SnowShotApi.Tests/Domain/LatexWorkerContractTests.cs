using System.Net;
using System.Text;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Providers;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShotApi.Tests.Domain;

public sealed class LatexWorkerContractTests
{
    [Theory]
    [InlineData("{}", LatexExtractionStatus.InferenceFailed, true)]
    [InlineData("{\"latex\":\" \"}", LatexExtractionStatus.InferenceFailed, true)]
    [InlineData("{\"latex\":7}", LatexExtractionStatus.Unavailable, false)]
    [InlineData("not-json", LatexExtractionStatus.Unavailable, false)]
    public async Task InvalidSuccessResponsesAreNeverCharged(string json, LatexExtractionStatus status, bool known)
    {
        var result = await Client(new FixtureHandler(HttpStatusCode.OK, json))
            .ExtractAsync(Command(), TestContext.Current.CancellationToken);
        Assert.Equal(status, result.Status);
        Assert.Equal(NanoYuan.Zero, result.Attempt.Cost);
        Assert.Equal(known, result.Attempt.CostKnown);
    }

    [Fact]
    public async Task OversizedResponseIsBoundedAndNotCharged()
    {
        var result = await Client(new FixtureHandler(HttpStatusCode.OK, new string('x', 2 * 1024 * 1024 + 1)))
            .ExtractAsync(Command(), TestContext.Current.CancellationToken);
        Assert.Equal(LatexExtractionStatus.Unavailable, result.Status);
        Assert.Equal("invalid_response", result.Attempt.Outcome);
        Assert.Equal(NanoYuan.Zero, result.Attempt.Cost);
    }

    [Theory]
    [InlineData(false, LatexExtractionStatus.Unavailable)]
    [InlineData(true, LatexExtractionStatus.Timeout)]
    public async Task TransportFailurePreservesUncertainCost(bool timeout, LatexExtractionStatus expected)
    {
        var result = await Client(new FailureHandler(timeout)).ExtractAsync(Command(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Equal(CostBasis.Unknown, result.Attempt.Basis);
        Assert.Equal(AttemptDispatchState.Unknown, result.Attempt.DispatchState);
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(new FailureHandler(true)).ExtractAsync(Command(), cancelled.Token));
    }

    [Fact]
    public async Task SuccessfulFixtureIsAcceptedAndIdentifiersArePropagated()
    {
        var handler = new FixtureHandler(HttpStatusCode.OK, Fixture("success.json"));
        var client = Client(handler);
        var result = await client.ExtractAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(LatexExtractionStatus.Success, result.Status);
        Assert.Equal("x^{2}", result.Latex);
        Assert.Equal("01900000000070008000000000000002", handler.OperationId);
        Assert.Equal("request-1", handler.RequestId);
    }

    [Fact]
    public async Task BusyFixtureIsKnownZeroCostBecauseInferenceDidNotStart()
    {
        var client = Client(new FixtureHandler(HttpStatusCode.ServiceUnavailable, Fixture("worker_busy.json")));
        var result = await client.ExtractAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(LatexExtractionStatus.Busy, result.Status);
        Assert.True(result.Attempt.CostKnown);
        Assert.Equal(NanoYuan.Zero, result.Attempt.Cost);
    }

    [Fact]
    public async Task WorkerPayloadRejectionIsKnownZeroCost()
    {
        var client = Client(new FixtureHandler(HttpStatusCode.RequestEntityTooLarge,
            "{\"error\":{\"code\":\"payload_too_large\",\"message\":\"Payload too large\"}}"));
        var result = await client.ExtractAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(LatexExtractionStatus.InvalidRequest, result.Status);
        Assert.True(result.Attempt.CostKnown);
        Assert.Equal(NanoYuan.Zero, result.Attempt.Cost);
    }

    private static LatexWorkerClient Client(HttpMessageHandler handler) => new(
        new SingleClientFactory(new HttpClient(handler) { BaseAddress = new Uri("http://worker.test/") }),
        new LatexWorkerOptions { BaseUrl = "http://worker.test/" },
        ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

    private static LatexProviderCommand Command()
    {
        var policy = ServicePolicy.Defaults();
        var resource = policy.Get(Resources.LatexExtraction);
        var snapshot = new ReservationSnapshot(policy.Revision, policy.Fingerprint, Resources.LatexExtraction, resource.Price,
            policy.PrincipalDailyAllowance, resource.Price.Input, resource.OperatorMaximum);
        var handle = new OperationHandle(Guid.Parse("01900000-0000-7000-8000-000000000002"), new byte[32], 1,
            DateTimeOffset.UtcNow.AddMinutes(1), snapshot);
        return new(handle, new LatexCommand("RIFF0000WEBP"u8.ToArray()),
            "request-1", "trace-1", Guid.Parse("01900000-0000-7000-8000-000000000003"), DateTimeOffset.UtcNow);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LatexFixtures", name), Encoding.UTF8);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FailureHandler(bool timeout) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(timeout ? new OperationCanceledException() : new HttpRequestException("offline"));
    }

    private sealed class FixtureHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public string? OperationId { get; private set; }
        public string? RequestId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OperationId = request.Headers.GetValues("X-Operation-ID").Single();
            RequestId = request.Headers.GetValues("X-Request-ID").Single();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
