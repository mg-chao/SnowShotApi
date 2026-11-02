using SnowShot.Domain;

namespace SnowShotApi.Tests.Domain;

public sealed class AccountRulesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData("1234567890123456", true)]
    [InlineData("12345678901234567", false)]
    [InlineData("  secret", true)]
    [InlineData("1234567890123456 ", false)]
    public void PasswordRequiresEightThroughSixteenCharactersWithoutTrimming(string? password, bool expected)
    {
        Assert.Equal(expected, AccountRules.IsPassword(password));
    }
}
