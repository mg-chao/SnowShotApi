using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SnowShot.Application;

namespace SnowShotApi.Tests.Api;

public sealed class PaidAuthenticationTests
{
    [Theory]
    [InlineData("/api/v1/chat/completions")]
    [InlineData("/api/v1/chat/completions/")]
    public async Task InvalidBearerCannotConsumeAnonymousFreeAllowance(string path)
    {
        await using var baseFactory = new ApiFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver(null));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
        using var response = await client.PostAsync(path, Body(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(baseFactory.Ledger.Reservations);
        Assert.Empty(baseFactory.Chat.Commands);
    }

    [Fact]
    public async Task ValidUnlinkedMiniSessionKeepsAnonymousAllowance()
    {
        await using var baseFactory = new ApiFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver(new(Guid.NewGuid(), null, "unlinked-wechat")));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-token");
        using var response = await client.PostAsync("/api/v1/chat/completions", Body(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reservation = Assert.Single(baseFactory.Ledger.Reservations);
        Assert.Null(reservation.AccountId);
        Assert.False(reservation.Snapshot.StrictPublicReservation);
    }

    [Fact]
    public async Task LinkedSessionSelectsAccountFundingWithoutChangingFreePrincipal()
    {
        var account = Guid.NewGuid();
        await using var baseFactory = new ApiFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver(new(Guid.NewGuid(), account, "linked-wechat")));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-token");
        using var response = await client.PostAsync("/api/v1/chat/completions", Body(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reservation = Assert.Single(baseFactory.Ledger.Reservations);
        Assert.Equal(account, reservation.AccountId);
        Assert.NotEqual(account, reservation.PrincipalId);
        Assert.True(reservation.Snapshot.StrictPublicReservation);
        Assert.True(reservation.Snapshot.PublicReservation.Value > 1);
    }

    private static StringContent Body() => new(
        """{"model":"ai-model","max_tokens":4096,"messages":[{"role":"user","content":"hello"}]}""",
        Encoding.UTF8, "application/json");

    private sealed class Resolver(AccountSessionIdentity? identity) : IAccountSessionResolver
    {
        public Task<AccountSessionIdentity?> ResolveAsync(string bearerToken, CancellationToken cancellationToken) => Task.FromResult(identity);
    }
}
