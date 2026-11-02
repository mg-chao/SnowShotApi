using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SnowShot.Infrastructure.Accounts;

namespace SnowShotApi.Tests.Domain;

public sealed class AccountPaymentConfigurationTests
{
    [Theory]
    [InlineData("Development", 0, true, false, true)]
    [InlineData("Development", 0, false, true, true)]
    [InlineData("Development", 0, false, false, false)]
    [InlineData("Production", 0, true, false, false)]
    [InlineData("Production", 0, true, true, false)]
    [InlineData("Production", 0, false, false, false)]
    [InlineData("Production", 0, false, true, true)]
    [InlineData("Staging", 0, true, false, false)]
    [InlineData("Development", 1, true, false, true)]
    [InlineData("Development", 1, true, true, true)]
    [InlineData("Development", 1, false, true, false)]
    [InlineData("Development", 1, false, false, false)]
    [InlineData("Production", 1, true, true, false)]
    [InlineData("Production", 1, false, true, false)]
    [InlineData("Staging", 1, true, true, false)]
    [InlineData("Staging", 1, false, true, false)]
    [InlineData("Development", -1, true, true, false)]
    [InlineData("Development", 2, true, true, false)]
    public void ManualTestingRequiresDevelopmentAndAValidPaymentEnvironment(
        string environment, int paymentEnvironment, bool allowTesting, bool releaseConfirmed, bool accepted)
    {
        var values = new Dictionary<string, string?>
        {
            ["Accounts:Wechat:PaymentsEnabled"] = "true",
            ["Accounts:Wechat:Environment"] = paymentEnvironment.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Accounts:Wechat:AllowPaymentTesting"] = allowTesting.ToString(),
            ["Accounts:Wechat:ReleasePrerequisitesConfirmed"] = releaseConfirmed.ToString(),
            ["Accounts:Wechat:AppId"] = "test-app",
            ["Accounts:Wechat:AppSecret"] = "test-secret",
            ["Accounts:Wechat:OfferId"] = "test-offer",
            ["Accounts:Wechat:AppKey"] = "test-key",
            ["Accounts:Wechat:MessageToken"] = "test-token",
            ["Accounts:Wechat:EncodingAesKey"] = Convert.ToBase64String(new byte[32]).TrimEnd('='),
        };
        foreach (var package in MembershipCatalog.Packages)
            values["Accounts:Wechat:ProductIds:" + package.Id] = package.Id;
        values["Accounts:Wechat:ProductIds:cny-0-1"] = "p0";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSnowShotAccounts(configuration, new TestEnvironment(environment));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WechatAccountOptions>>();
        if (!accepted)
        {
            Assert.Throws<OptionsValidationException>(() => options.Value);
            return;
        }
        Assert.True(options.Value.IsPaymentConfigured);
        Assert.True(options.Value.IsReconciliationConfigured);
        Assert.Equal(releaseConfirmed, options.Value.ReleasePrerequisitesConfirmed);
    }

    [Theory]
    [InlineData("Development", null, null, true)]
    [InlineData("Production", null, null, true)]
    [InlineData("Staging", null, null, true)]
    [InlineData("Development", "develop", false, true)]
    [InlineData("Development", "develop", true, true)]
    [InlineData("Development", "trial", false, true)]
    [InlineData("Development", "release", false, true)]
    [InlineData("Production", "release", true, true)]
    [InlineData("Production", "develop", false, false)]
    [InlineData("Production", "develop", true, false)]
    [InlineData("Production", "trial", false, false)]
    [InlineData("Production", "release", false, false)]
    [InlineData("Staging", "develop", false, false)]
    [InlineData("Development", "debug", false, false)]
    [InlineData("Development", "Develop", false, false)]
    [InlineData("Development", "", false, false)]
    public void MiniProgramCodeSettingsAreValidatedForTheHostEnvironment(
        string environment, string? version, bool? checkPath, bool accepted)
    {
        var values = new Dictionary<string, string?>();
        if (version is not null) values["Accounts:Wechat:MiniProgramCodeVersion"] = version;
        if (checkPath is not null) values["Accounts:Wechat:MiniProgramCodeCheckPath"] = checkPath.Value.ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSnowShotAccounts(configuration, new TestEnvironment(environment));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WechatAccountOptions>>();
        if (!accepted)
        {
            Assert.Throws<OptionsValidationException>(() => options.Value);
            return;
        }
        Assert.Equal(version ?? "release", options.Value.MiniProgramCodeVersion);
        Assert.Equal(checkPath ?? true, options.Value.MiniProgramCodeCheckPath);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AccountPaymentConfigurationTests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
