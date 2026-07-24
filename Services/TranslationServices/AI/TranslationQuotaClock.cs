using System.Globalization;

namespace SnowShotApi.Services.TranslationServices.AI;

public sealed class TranslationQuotaClock
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _timeZone;

    public TranslationQuotaClock(TimeProvider timeProvider, AITranslationOptions options)
    {
        _timeProvider = timeProvider;
        _timeZone = ResolveTimeZone(options.QuotaTimeZoneId);
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public int GetCurrentQuotaDate()
    {
        var localTime = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _timeZone);
        return int.Parse(localTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (timeZoneId == "Asia/Shanghai")
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }
}
