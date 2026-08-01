using System.Collections.Concurrent;
using SnowShot.Application;

namespace SnowShot.Infrastructure.Telemetry;

public sealed class DependencyHealth(TimeProvider timeProvider) : IDependencyHealth
{
    private readonly ConcurrentDictionary<string, DependencyStatus> _components = new(StringComparer.Ordinal)
    {
        ["chat_provider"] = new(false, DateTimeOffset.MinValue),
        ["translation_provider"] = new(false, DateTimeOffset.MinValue),
        ["table_worker"] = new(false, DateTimeOffset.MinValue),
    };

    public void Report(string component, bool healthy) => _components[component] = new(healthy, timeProvider.GetUtcNow());

    public IReadOnlyDictionary<string, DependencyStatus> Snapshot() =>
        new Dictionary<string, DependencyStatus>(_components, StringComparer.Ordinal);
}
