using System.Net;
using System.Net.Http.Headers;
using SnowShot.Application;
using SnowShot.Infrastructure.Configuration;
using StackExchange.Redis;

namespace SnowShot.Infrastructure.Providers;

public interface IProviderHttpClientRegistry
{
    HttpClient CreateClient(ProviderAccessSelection selection);
    ValueTask ReportAsync(ProviderAccessSelection selection, ProviderCircuitOutcome outcome,
        TimeSpan? retryAfter = null) => ValueTask.CompletedTask;
}

public sealed class ProviderHttpClientRegistry : IProviderHttpClientRegistry, IDisposable
{
    private readonly IReadOnlyDictionary<string, HttpClient> _clients;
    private readonly IProviderCircuitRegistry _circuits;

    public ProviderHttpClientRegistry(ProviderModelCatalog catalog, IProviderCircuitRegistry circuits)
    {
        _circuits = circuits;
        _clients = catalog.All.ToDictionary(value => Key(value.Selection),
            value => CreateClient(value, circuits), StringComparer.Ordinal);
    }

    public HttpClient CreateClient(ProviderAccessSelection selection) => _clients.TryGetValue(Key(selection), out var client)
        ? client
        : throw new KeyNotFoundException($"No HTTP client exists for provider access '{selection.LogicalModel}/{selection.AccessId}'.");

    public async ValueTask ReportAsync(ProviderAccessSelection selection, ProviderCircuitOutcome outcome,
        TimeSpan? retryAfter = null)
    {
        try { await _circuits.ReportAsync(selection, outcome, retryAfter, CancellationToken.None); }
        catch (Exception exception) when (exception is RedisException or TimeoutException) { }
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values) client.Dispose();
    }

    private static HttpClient CreateClient(ProviderAccessDefinition access, IProviderCircuitRegistry circuits)
    {
        var transport = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = access.MaxConcurrentRequests,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = false,
        };
        var client = new HttpClient(new CircuitReportingHandler(access.Selection, circuits) { InnerHandler = transport })
        { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SnowShotApi");
        return client;
    }

    private static string Key(ProviderAccessSelection selection) => $"{selection.LogicalModel}\n{selection.AccessId}";

    private sealed class CircuitReportingHandler(
        ProviderAccessSelection selection,
        IProviderCircuitRegistry circuits) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                var status = (int)response.StatusCode;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    await ReportAsync(ProviderCircuitOutcome.AuthenticationFailure, TimeSpan.FromMinutes(10));
                else if (status is 408 or 429 || status is >= 500 and <= 599)
                    await ReportAsync(ProviderCircuitOutcome.TransientFailure, RetryAfter(response.Headers.RetryAfter));
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                await ReportAsync(ProviderCircuitOutcome.TransientFailure, null);
                throw;
            }
        }

        private async Task ReportAsync(ProviderCircuitOutcome outcome, TimeSpan? retryAfter)
        {
            try { await circuits.ReportAsync(selection, outcome, retryAfter, CancellationToken.None); }
            catch (Exception exception) when (exception is RedisException or TimeoutException) { }
        }

        private static TimeSpan? RetryAfter(RetryConditionHeaderValue? value)
        {
            if (value?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
            if (value?.Date is not { } date) return null;
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
    }
}
