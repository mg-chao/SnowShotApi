using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using SnowShot.Application;
using SnowShot.Infrastructure.Configuration;

namespace SnowShot.Infrastructure.Providers;

public interface IProviderHttpClientRegistry
{
    HttpClient CreateClient(ProviderAccessSelection selection);
}

public sealed class ProviderHttpClientRegistry : IProviderHttpClientRegistry, IDisposable
{
    private readonly IReadOnlyDictionary<string, HttpClient> _clients;

    public ProviderHttpClientRegistry(ProviderModelCatalog catalog)
    {
        _clients = catalog.All.ToDictionary(value => Key(value.Selection), CreateClient, StringComparer.Ordinal);
    }

    public HttpClient CreateClient(ProviderAccessSelection selection) => _clients.TryGetValue(Key(selection), out var client)
        ? client
        : throw new KeyNotFoundException($"No HTTP client exists for provider access '{selection.LogicalModel}/{selection.AccessId}'.");

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
    }

    private static HttpClient CreateClient(ProviderAccessDefinition access)
    {
        var circuit = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(static response =>
                        (int)response.StatusCode is 408 or 429 or >= 500),
            }).Build();
        var resilience = new ResilienceHandler(circuit)
        {
            InnerHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = access.MaxConcurrentRequests,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                UseCookies = false,
            },
        };
        var client = new HttpClient(resilience) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SnowShotApi");
        return client;
    }

    private static string Key(ProviderAccessSelection selection) => $"{selection.LogicalModel}\n{selection.AccessId}";
}
