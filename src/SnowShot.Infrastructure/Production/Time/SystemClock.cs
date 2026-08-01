using SnowShot.Application;

namespace SnowShot.Infrastructure.Time;

public sealed class SystemClock(TimeProvider timeProvider) : ISystemClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}
