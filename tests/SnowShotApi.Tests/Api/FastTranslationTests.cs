using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Providers;

namespace SnowShotApi.Tests.Api;

public sealed class FastTranslationTests
{
    private const string TypesPath = "/api/v2/translation/types";
    private const string TranslatePath = "/api/v2/translation/translate";
    private const string SuccessBody = """
        {"errorCode":"0","translateResults":[
          {"query":"你😀!","translation":"first","type":"zh-CHS2en"},
          {"query":"witaj &","translation":"second","type":"pl2en"},
          {"query":"你😀!","translation":"third","type":"zh-CHS2en"}]}
        """;

    [Fact]
    public async Task FastTranslationIsOptInAndUsesOrderedSignedBatchesWithExactWalletSettlement()
    {
        await using var disabled = new ApiFactory();
        using var disabledClient = disabled.CreateAnonymousClient();
        using var disabledCatalog = await disabledClient.GetFromJsonAsync<JsonDocument>(TypesPath, Token);
        Assert.Equal(0, Assert.Single(disabledCatalog!.RootElement.GetProperty("data").EnumerateArray()).GetProperty("type").GetInt32());
        using var disabledResponse = await disabledClient.PostAsJsonAsync(TranslatePath, Request(["hello"]), Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledResponse.StatusCode);

        await using var api = EnabledFactory();
        var transport = new BatchHandler(SuccessBody);
        var account = Guid.NewGuid();
        await using var factory = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver(account));
            services.AddHttpClient(YoudaoBatchTranslationClient.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "linked");
        client.DefaultRequestHeaders.Add("X-Request-ID", "fast-translation-test");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-TW");
        using var types = await client.GetAsync(TypesPath, Token);
        Assert.Equal(HttpStatusCode.OK, types.StatusCode);
        Assert.Equal("zh-TW", Assert.Single(types.Content.Headers.ContentLanguage));
        using var catalog = await types.Content.ReadFromJsonAsync<JsonDocument>(Token);
        var options = catalog!.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal([0, 1], options.Select(option => option.GetProperty("type").GetInt32()).ToArray());
        Assert.Equal(["AI 翻譯", "快速翻譯"], options.Select(option => option.GetProperty("name").GetString()!).ToArray());

        var request = Request(["你😀!", "witaj &", "你😀!"]);
        using var response = await client.PostAsJsonAsync(TranslatePath, request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(["first", "second", "third"], data.GetProperty("results").EnumerateArray()
            .Select(item => item.GetProperty("content").GetString()!).ToArray());
        Assert.Equal("auto", data.GetProperty("from").GetString());
        Assert.Equal("en", data.GetProperty("to").GetString());

        var form = Assert.Single(transport.Forms);
        Assert.Contains("q=%E4%BD%A0%F0%9F%98%80%21&q=witaj+%26&q=%E4%BD%A0%F0%9F%98%80%21", form, StringComparison.Ordinal);
        var fields = form.Split('&').Select(part => part.Split('=', 2)).Where(part => part[0] != "q")
            .ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
        Assert.Equal("auto", fields["from"]);
        Assert.Equal("1", fields["detectLevel"]);
        Assert.Equal("v3", fields["signType"]);
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            "test-app你😀!witaj &你😀!" + fields["salt"] + fields["curtime"] + "test-secret")));
        Assert.Equal(signature, fields["sign"]);

        var reservation = Assert.Single(api.Ledger.Reservations);
        Assert.Equal(account, reservation.AccountId);
        Assert.False(reservation.UsesFreeAllowance);
        Assert.Equal(Resources.FastTranslation, reservation.Snapshot.Resource);
        Assert.Equal(1_300_000, reservation.Snapshot.PublicReservation.Value);
        var settlement = Assert.Single(api.Ledger.Settlements);
        Assert.Equal(988_000, settlement.ReportedPublicCost.Value);
        Assert.Equal(settlement.ReportedPublicCost, settlement.ReportedOperatorCost);
        Assert.Equal(13, settlement.InputUnits);
        Assert.Equal(0, settlement.OutputUnits);
        Assert.Single(api.Ledger.Attempts);

        api.Ledger.RejectWith = ReservationRejectionReason.DuplicateRequest;
        using var duplicate = await client.PostAsJsonAsync(TranslatePath, request, Token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Single(transport.Forms);
        Assert.Single(api.Ledger.Settlements);
    }

    [Fact]
    public async Task FastTranslationValidatesLimitsAndRejectsUnfundedAccountsBeforeCallingYoudao()
    {
        await using var api = EnabledFactory();
        var transport = new BatchHandler(SuccessBody);
        await using var factory = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver(Guid.NewGuid()));
            services.AddHttpClient(YoudaoBatchTranslationClient.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        using var client = factory.CreateClient();
        using var guest = await client.PostAsJsonAsync(TranslatePath, Request(["hello"]), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, guest.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "linked");
        foreach (var request in new[] { Request(new string[51]), Request([new string('a', 5_001)]),
                     Request(["hello"], "en", "auto") })
        {
            using var invalid = await client.PostAsJsonAsync(TranslatePath, request, Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-Hant");
        using var invalidDomain = await client.PostAsJsonAsync(TranslatePath, Request(["hello"], domain: "finance"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidDomain.StatusCode);
        Assert.Equal("zh-TW", Assert.Single(invalidDomain.Content.Headers.ContentLanguage));
        using var problem = await invalidDomain.Content.ReadFromJsonAsync<JsonDocument>(Token);
        Assert.Equal("不支援的領域：finance", problem!.RootElement.GetProperty("detail").GetString());

        api.Ledger.RejectWith = ReservationRejectionReason.InsufficientCredits;
        using var insufficient = await client.PostAsJsonAsync(TranslatePath, Request(["hello"]), Token);
        Assert.Equal(HttpStatusCode.PaymentRequired, insufficient.StatusCode);
        Assert.Empty(transport.Forms);
        Assert.Empty(api.Ledger.Preparations);
    }

    [Theory]
    [InlineData("""{"errorCode":"0","errorIndex":[1],"translateResults":[]}""")]
    [InlineData("invalid-json")]
    [InlineData("""{"errorCode":"0","translateResults":[{"query":"world","translation":"1","type":"en2zh-CHT"},{"query":"hello","translation":"2","type":"en2zh-CHT"}]}""")]
    [InlineData("""{"errorCode":"0","translateResults":[{"query":"hello","translation":"1","type":"en2zh-CHS"},{"query":"world","translation":"2","type":"en2zh-CHT"}]}""")]
    public async Task FastTranslationReleasesTheWalletHoldForAnInvalidResponse(string upstream)
    {
        await using var api = EnabledFactory();
        var transport = new BatchHandler(upstream);
        await using var factory = api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver(Guid.NewGuid()));
            services.AddHttpClient(YoudaoBatchTranslationClient.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => transport);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "linked");
        using var failure = await client.PostAsJsonAsync(TranslatePath, Request(["hello", "world"], "en", "zh-CHT"), Token);
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Single(transport.Forms);
        Assert.Equal(480_000, Assert.Single(api.Ledger.Reservations).Snapshot.PublicReservation.Value);
        var failed = Assert.Single(api.Ledger.Settlements);
        Assert.False(failed.Delivered);
        Assert.Equal(NanoYuan.Zero, failed.ReportedPublicCost);
        Assert.Equal(CostBasis.Unknown, failed.Basis);

        using var unchanged = await client.PostAsJsonAsync(TranslatePath, Request(["hello"], "en", "en"), Token);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Single(transport.Forms);
        Assert.Equal(NanoYuan.Zero, api.Ledger.Settlements.Last().ReportedPublicCost);
        Assert.Equal(AttemptDispatchState.NotDispatched, api.Ledger.Attempts.Last().DispatchState);
    }

    [Theory]
    [InlineData("zh-CHS", "en", 48_000)]
    [InlineData("hi", "zh-CHT", 48_000)]
    [InlineData("zh-CHS", "zh-CHT", 48_000)]
    [InlineData("tr", "zh-CHS", 100_000)]
    [InlineData("pl", "zh-CHS", 100_000)]
    [InlineData("fr", "en", 100_000)]
    [InlineData("en", "pt", 100_000)]
    [InlineData("hi", "nl", 100_000)]
    [InlineData("auto", "en", 100_000)]
    public void FastTranslationUsesThePublishedRates(string source, string target, long rate) =>
        Assert.Equal(rate, FastTranslationUseCase.RateNanoYuan(source, target));

    [Fact]
    public void FastTranslationSignsLongConcatenatedInputs()
    {
        var form = YoudaoBatchTranslationClient.BuildForm(["0123456789abc", "defghijklmnopqrstuv"], "en", "fr",
            "key", "secret", "salt", "123").ToArray();
        var expected = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes("key012345678932mnopqrstuvsalt123secret")));
        Assert.Equal(expected, Assert.Single(form, pair => pair.Key == "sign").Value);
        Assert.DoesNotContain(form, pair => pair.Key == "detectLevel");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static object Request(string[] texts, string source = "auto", string target = "en", string domain = "general") =>
        new { type = 1, content = texts, from = source, to = target, domain };

    private static ApiFactory EnabledFactory() => new()
    {
        ExtraConfiguration =
        {
            ["Providers:YoudaoBatchTranslation:Enabled"] = "true",
            ["Providers:YoudaoBatchTranslation:AppKey"] = "test-app",
            ["Providers:YoudaoBatchTranslation:AppSecret"] = "test-secret",
            ["Policy:Resources:fast-translation:RequestsPerMinute"] = "30",
            ["Policy:Resources:fast-translation:PerPrincipalConcurrency"] = "4",
            ["Policy:Resources:fast-translation:GlobalConcurrency"] = "32",
            ["Policy:Resources:fast-translation:GlobalQueueLength"] = "64",
            ["Policy:Resources:fast-translation:QueueWaitSeconds"] = "30",
            ["Policy:Resources:fast-translation:ExecutionDeadlineSeconds"] = "300",
            ["Policy:Resources:fast-translation:OperatorMaximumNanoYuan"] = "500000000",
            ["Policy:Pricing:fast-translation:InputRateNanoYuan"] = "100000",
            ["Policy:Pricing:fast-translation:OutputRateNanoYuan"] = "0",
        },
    };

    private sealed class Resolver(Guid account) : IAccountSessionResolver
    {
        public Task<AccountSessionIdentity?> ResolveAsync(string bearerToken, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSessionIdentity?>(new(Guid.NewGuid(), account, "linked"));
    }

    private sealed class BatchHandler(string response) : HttpMessageHandler
    {
        public List<string> Forms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://openapi.youdao.com/v2/api", request.RequestUri?.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/x-www-form-urlencoded", request.Content?.Headers.ContentType?.MediaType);
            Forms.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
