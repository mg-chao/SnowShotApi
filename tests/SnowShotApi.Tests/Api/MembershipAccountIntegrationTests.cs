using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;
using SnowShotApi.Tests.Domain;

namespace SnowShotApi.Tests.Api;

[Collection(DistributedIntegrationGroup.Name)]
public sealed class MembershipAccountIntegrationTests
{
    [Theory, Trait("Category", "Integration")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AccountSummaryPublishesExactMoneyTierAndBenefits(bool linked, bool temporary)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SNOWSHOT_RUN_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Enable the isolated PostgreSQL integration fixture.");
        var token = TestContext.Current.CancellationToken;
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var id = Guid.CreateVersion7();
        if (linked)
        {
            await using var db = new SnowShotDbContext(DistributedIntegrationTests.DatabaseOptions(connection));
            db.Accounts.Add(new() { Id = id, Username = "member", NormalizedUsername = id.ToString("N"), PasswordHash = "test", Active = true, IsTemporary = temporary, CreatedAt = DateTimeOffset.UtcNow });
            db.AccountWallets.Add(new() { AccountId = id, CumulativeRechargeNanoYuan = MembershipRules.FreeRecognitionThresholdNanoYuan, OutstandingNanoYuan = 10 });
            await db.SaveChangesAsync(token);
        }
        await using var original = new ApiFactory();
        original.ExtraConfiguration["ConnectionStrings:SnowShot"] = connection;
        await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver(new(Guid.NewGuid(), linked ? id : null, "test-wechat")));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-session");
        using var response = await client.GetAsync("/api/v2/account", token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(linked, data.GetProperty("linked").GetBoolean());
        Assert.Equal(temporary, data.GetProperty("is_temporary").GetBoolean());
        Assert.Equal(linked ? "active" : "unlinked", data.GetProperty("membership_status").GetString());
        Assert.Equal(linked ? "188000000000" : "0", data.GetProperty("cumulative_recharge_nano_yuan").GetString());
        Assert.Equal(linked ? "cny-188" : null, data.GetProperty("membership_tier").GetString());
        Assert.Equal(linked, data.GetProperty("free_table_extraction").GetBoolean());
        Assert.Equal(linked, data.GetProperty("free_latex_extraction").GetBoolean());
        Assert.Equal("0", data.GetProperty("available_credit_nano_yuan").GetString());
        Assert.Equal(linked ? "10" : "0", data.GetProperty("outstanding_balance_nano_yuan").GetString());
    }

    private sealed class Resolver(AccountSessionIdentity identity) : IAccountSessionResolver
    {
        public Task<AccountSessionIdentity?> ResolveAsync(string bearerToken, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSessionIdentity?>(identity);
    }
}
