using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;

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
        Assert.True(reservation.UsesFreeAllowance);
        Assert.False(reservation.Snapshot.StrictPublicReservation);
    }

    [Theory]
    [InlineData(Resources.AiModel, true)]
    [InlineData(Resources.QwenFlash, false)]
    [InlineData(Resources.QwenMtFlash, false)]
    public async Task LinkedSessionPreservesTheRequestedModelForFreeAllowance(string model, bool usesFreeAllowance)
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
        using var response = await client.PostAsync("/api/v1/chat/completions", Body(model), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reservation = Assert.Single(baseFactory.Ledger.Reservations);
        Assert.Equal(account, reservation.AccountId);
        Assert.NotEqual(account, reservation.PrincipalId);
        Assert.Equal(Resources.AiModel, reservation.Snapshot.Resource);
        Assert.Equal(usesFreeAllowance, reservation.UsesFreeAllowance);
        Assert.True(reservation.Snapshot.StrictPublicReservation);
        Assert.True(reservation.Snapshot.PublicReservation.Value > 1);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(Resources.QwenFlash, false)]
    [InlineData(Resources.QwenMtFlash, true)]
    public async Task WalletOnlyServicesRequireAnAccountBeforeDispatch(string? model, bool unlinkedSession)
    {
        await using var baseFactory = new ApiFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOperationLedger>();
            services.AddSingleton<IOperationLedger>(provider => provider.GetRequiredService<PostgresOperationLedger>());
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver(new(Guid.NewGuid(), null, "unlinked-wechat")));
        }));
        using var client = factory.CreateClient();
        if (unlinkedSession)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid-token");
        var path = model is null ? "/api/v2/translation/translate" : "/api/v1/chat/completions";
        using var body = model is null
            ? new StringContent("""{"type":0,"content":["hello"],"from":"en","to":"zh-CHS","domain":"general"}""", Encoding.UTF8, "application/json")
            : Body(model);
        var token = TestContext.Current.CancellationToken;
        using var response = await client.PostAsync(path, body, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Assert.Equal("account_required", problem.RootElement.GetProperty("code").GetString());
        Assert.Empty(baseFactory.Chat.Commands);
        Assert.Empty(baseFactory.Translation.Commands);
    }

    private static StringContent Body(string model = Resources.AiModel) => new(
        $$"""{"model":"{{model}}","max_tokens":4096,"messages":[{"role":"user","content":"hello"}]}""",
        Encoding.UTF8, "application/json");

    private sealed class Resolver(AccountSessionIdentity? identity) : IAccountSessionResolver
    {
        public Task<AccountSessionIdentity?> ResolveAsync(string bearerToken, CancellationToken cancellationToken) => Task.FromResult(identity);
    }
}
