using System.Net;
using System.Text;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Providers;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShotApi.Tests.Domain;

public sealed class WorkerContractTests
{
    [Fact]
    public async Task SuccessfulFixtureIsAcceptedAndIdentifiersArePropagated()
    {
        var handler = new FixtureHandler(HttpStatusCode.OK, Fixture("success.json"));
        var client = Client(handler);
        var result = await client.ExtractAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(TableExtractionStatus.Success, result.Status);
        Assert.Contains("<table>", result.Html, StringComparison.Ordinal);
        Assert.Equal("01900000000070008000000000000002", handler.OperationId);
        Assert.Equal("request-1", handler.RequestId);
    }

    [Fact]
    public async Task MaliciousFixtureIsRejectedWithoutRewriting()
    {
        var client = Client(new FixtureHandler(HttpStatusCode.OK, Fixture("malicious.json")));
        var result = await client.ExtractAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(TableExtractionStatus.InferenceFailed, result.Status);
        Assert.Null(result.Html);
        Assert.Equal("invalid_html", result.Attempt.Outcome);
    }

    [Fact]
    public async Task BusyFixtureIsKnownZeroCostBecauseInferenceDidNotStart()
    {
        var client = Client(new FixtureHandler(HttpStatusCode.ServiceUnavailable, Fixture("worker_busy.json")));
        var result = await client.ExtractAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(TableExtractionStatus.Busy, result.Status);
        Assert.True(result.Attempt.CostKnown);
        Assert.Equal(NanoYuan.Zero, result.Attempt.Cost);
    }

    [Fact]
    public async Task WorkerPayloadRejectionIsKnownZeroCost()
    {
        var client = Client(new FixtureHandler(HttpStatusCode.RequestEntityTooLarge,
            "{\"error\":{\"code\":\"payload_too_large\",\"message\":\"Payload too large\"}}"));
        var result = await client.ExtractAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(TableExtractionStatus.InvalidRequest, result.Status);
        Assert.True(result.Attempt.CostKnown);
        Assert.Equal(NanoYuan.Zero, result.Attempt.Cost);
    }

    private static TableWorkerClient Client(HttpMessageHandler handler) => new(
        new SingleClientFactory(new HttpClient(handler) { BaseAddress = new Uri("http://worker.test/") }),
        new TableWorkerOptions { BaseUrl = "http://worker.test/" },
        ServicePolicy.Defaults(), new DependencyHealth(TimeProvider.System), TimeProvider.System);

    private static TableProviderCommand Command()
    {
        var policy = ServicePolicy.Defaults();
        var resource = policy.Get(Resources.TableExtraction);
        var snapshot = new ReservationSnapshot(policy.Revision, policy.Fingerprint, Resources.TableExtraction, resource.Price,
            policy.PrincipalDailyAllowance, resource.Price.Input, resource.OperatorMaximum);
        var handle = new OperationHandle(Guid.Parse("01900000-0000-7000-8000-000000000002"), new byte[32], 1,
            DateTimeOffset.UtcNow.AddMinutes(1), snapshot);
        return new(handle, new TableCommand("RIFF0000WEBP"u8.ToArray()),
            "request-1", "trace-1", Guid.Parse("01900000-0000-7000-8000-000000000003"), DateTimeOffset.UtcNow);
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Encoding.UTF8);

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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
