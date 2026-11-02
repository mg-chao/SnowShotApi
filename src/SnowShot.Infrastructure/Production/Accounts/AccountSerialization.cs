using System.Globalization;
using System.Text.Json;

namespace SnowShot.Infrastructure.Accounts;

internal static class AccountSerialization
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Money(long value) => value.ToString(CultureInfo.InvariantCulture);
}
