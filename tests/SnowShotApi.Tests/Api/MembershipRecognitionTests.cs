using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SnowShot.Application;
using SnowShot.Domain;

namespace SnowShotApi.Tests.Api;

public sealed class MembershipRecognitionTests
{
    [Theory]
    [InlineData("table", true, 0)]
    [InlineData("latex", true, 0)]
    [InlineData("table", false, 30000000)]
    [InlineData("latex", false, 15000000)]
    public async Task RecognitionUsesBenefitFromReservedSnapshot(string resource, bool free, long publicCost)
    {
        await using var original = new ApiFactory();
        original.Ledger.SnapshotFor = operation => operation.Snapshot with
        {
            MembershipRecognitionFree = free,
            PublicReservation = free ? NanoYuan.Zero : operation.Snapshot.PublicReservation,
        };
        await using var factory = WithAccount(original);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-member");
        using var form = Image();
        using var response = await client.PostAsync($"/api/v1/{resource}/extract", form, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settlement = Assert.Single(original.Ledger.Settlements);
        Assert.Equal(new NanoYuan(publicCost), settlement.ReportedPublicCost);
        Assert.Equal(free, settlement.Handle.Snapshot.MembershipRecognitionFree);
        Assert.True(settlement.ReportedOperatorCost.Value > 0);
        Assert.Equal(1, settlement.InputUnits);
        Assert.Single(original.Admission.Requests);
    }

    [Theory]
    [InlineData("table", false)]
    [InlineData("latex", false)]
    [InlineData("table", true)]
    [InlineData("latex", true)]
    public async Task MemberRecognitionFailuresRemainFree(string resource, bool timeout)
    {
        await using var original = new ApiFactory();
        original.Ledger.SnapshotFor = operation => operation.Snapshot with
        {
            MembershipRecognitionFree = true,
            PublicReservation = NanoYuan.Zero,
        };
        original.Table.Status = timeout ? TableExtractionStatus.Timeout : TableExtractionStatus.InferenceFailed;
        original.Latex.Status = timeout ? LatexExtractionStatus.Timeout : LatexExtractionStatus.InferenceFailed;
        await using var factory = WithAccount(original);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-member");
        using var form = Image();
        using var response = await client.PostAsync($"/api/v1/{resource}/extract", form, TestContext.Current.CancellationToken);
        Assert.Equal(timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(NanoYuan.Zero, Assert.Single(original.Ledger.Settlements).ReportedPublicCost);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithAccount(ApiFactory original) =>
        original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountSessionResolver>();
            services.AddSingleton<IAccountSessionResolver>(new Resolver());
        }));

    private static MultipartFormDataContent Image()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("RIFF0000WEBP"u8.ToArray()), "image", "recognition.webp");
        return form;
    }

    private sealed class Resolver : IAccountSessionResolver
    {
        public Task<AccountSessionIdentity?> ResolveAsync(string bearerToken, CancellationToken cancellationToken) =>
            Task.FromResult<AccountSessionIdentity?>(new(Guid.NewGuid(), Guid.NewGuid(), null));
    }
}
