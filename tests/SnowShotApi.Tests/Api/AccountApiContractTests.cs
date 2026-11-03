using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SnowShotApi.Tests.Api;

public sealed class AccountApiContractTests
{
    [Theory]
    [InlineData("/api/v2/purchases/mini-program/requests")]
    [InlineData("/api/v2/purchases/requests/ed6057bc-ab1b-4a61-8dc5-a60f2d7c2b21/cancel")]
    [InlineData("/api/v2/account/deletion")]
    [InlineData("/api/v2/registration/requests/ed6057bc-ab1b-4a61-8dc5-a60f2d7c2b21/link")]
    public async Task MiniProgramPurchaseAndLinkRequireAuthentication(string path)
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var response = await client.PostAsJsonAsync(path, new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestCardCatalogRequiresManualTesting(bool allowTesting)
    {
        await using var factory = new ApiFactory();
        factory.ExtraConfiguration["Accounts:Wechat:AllowPaymentTesting"] = allowTesting.ToString();
        factory.ExtraConfiguration["Accounts:Wechat:ProductIds:cny-0-1"] = "p0";
        using var client = factory.CreateAnonymousClient();
        using var response = await client.GetAsync("/api/v2/purchases/packages", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var packages = json.RootElement.GetProperty("data").GetProperty("packages");
        Assert.Equal(allowTesting ? 6 : 5, packages.GetArrayLength());
        var testCards = packages.EnumerateArray().Where(x => x.GetProperty("id").GetString() == "cny-0-1").ToArray();
        if (!allowTesting)
        {
            Assert.Empty(testCards);
            return;
        }
        var testCard = Assert.Single(testCards);
        Assert.Equal(10, testCard.GetProperty("price_fen").GetInt32());
        Assert.Equal("100000000", testCard.GetProperty("credits_nano_yuan").GetString());
    }

    [Fact]
    public async Task PackageCatalogUsesSnakeCaseAndExactMoneyWhilePaymentsAreDisabled()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var response = await client.GetAsync("/api/v2/purchases/packages", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var data = json.RootElement.GetProperty("data"); Assert.False(data.GetProperty("payments_available").GetBoolean());
        var packages = data.GetProperty("packages"); Assert.Equal(5, packages.GetArrayLength());
        Assert.Equal(990, packages[0].GetProperty("price_fen").GetInt32()); Assert.Equal("9900000000", packages[0].GetProperty("credits_nano_yuan").GetString());
    }
    [Fact]
    public async Task DisabledPurchasingDoesNotReserveUsernameOrUseDatabase()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var response = await client.PostAsJsonAsync("/api/v2/registration/requests", new
        { username = "Example", password = "password-123", password_confirmation = "password-123", captcha_id = Guid.NewGuid(), captcha_answer = "ABC234" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("payments_unavailable", json.RootElement.GetProperty("code").GetString());
    }
    [Fact]
    public async Task AccountRequiresAuthentication()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var response = await client.GetAsync("/api/v2/account", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Fact]
    public async Task DeletionEligibilityRequiresAuthenticationAndCannotBeCached()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateAnonymousClient();
        using var response = await client.GetAsync("/api/v2/account/deletion", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
