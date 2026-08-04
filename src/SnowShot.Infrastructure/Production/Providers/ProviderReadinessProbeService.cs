using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SnowShot.Application;
using SnowShot.Infrastructure.Configuration;

namespace SnowShot.Infrastructure.Providers;

public sealed class ProviderReadinessProbeService(
    ProviderModelCatalog catalog,
    TranslationProviderOptions translation,
    IProviderCircuitRegistry circuits,
    IHttpClientFactory clients,
    IDependencyHealth health,
    ILogger<ProviderReadinessProbeService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ProbeFailed = LoggerMessage.Define(
        LogLevel.Warning, new EventId(5601, nameof(ProbeFailed)), "Dependency readiness probe failed");
    private readonly HttpClient _providerClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        MaxConnectionsPerServer = 8,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { await ProbeAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { ProbeFailed(logger, exception); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        _providerClient.Dispose();
        base.Dispose();
    }

    internal async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var snapshots = await circuits.SnapshotAsync(cancellationToken);
        var states = snapshots.ToDictionary(value => Identity(value.Selection), value => value.State, StringComparer.Ordinal);
        var probes = catalog.All.Select(async access => new
        {
            access.Selection,
            Healthy = states.GetValueOrDefault(Identity(access.Selection), ProviderCircuitState.Closed) == ProviderCircuitState.Closed &&
                await ProbeProviderAsync(access, cancellationToken),
        });
        var results = await Task.WhenAll(probes);
        var healthy = results.Where(value => value.Healthy).Select(value => value.Selection.LogicalModel)
            .ToHashSet(StringComparer.Ordinal);
        health.Report("chat_provider", catalog.Models.All(value => healthy.Contains(value.Model)));
        health.Report("translation_provider", translation.LogicalModels.Any(healthy.Contains));
        health.Report("table_worker", await ProbeTableAsync(cancellationToken));
    }

    private async Task<bool> ProbeProviderAsync(ProviderAccessDefinition access, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var request = new HttpRequestMessage(HttpMethod.Head, access.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.ApiKey);
        request.Headers.UserAgent.ParseAdd("SnowShotApi-Readiness");
        try
        {
            using var response = await _providerClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var status = (int)response.StatusCode;
            return response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                       HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) && status is < 500 or > 599;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return false; }
    }

    private async Task<bool> ProbeTableAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await clients.CreateClient("table").GetAsync("health/ready",
                HttpCompletionOption.ResponseHeadersRead, linked.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { return false; }
    }

    private static string Identity(ProviderAccessSelection selection) =>
        $"{selection.LogicalModel}/{selection.Provider}/{selection.AccessId}";
}
