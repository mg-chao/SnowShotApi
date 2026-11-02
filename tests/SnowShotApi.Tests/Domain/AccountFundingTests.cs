using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShotApi.Tests.Api;

namespace SnowShotApi.Tests.Domain;

public sealed class AccountFundingTests
{
    [Fact]
    public void ReservationsUseUnreservedFreeFundsBeforePurchasedFunds()
    {
        var allocation = AccountFunding.Reserve(100, 100, 40, 30, 80, 0, 0);
        Assert.True(allocation.Accepted);
        Assert.Equal(30, allocation.Free);
        Assert.Equal(70, allocation.Wallet);
    }

    [Fact]
    public void DebtAllowsFullyFreeRequestsButBlocksAnyPurchasedReservation()
    {
        Assert.True(AccountFunding.Reserve(50, 100, 20, 30, 1_000, 0, 80).Accepted);
        Assert.Equal(FundingRejection.OutstandingBalance,
            AccountFunding.Reserve(51, 100, 20, 30, 1_000, 0, 80).Rejection);
    }

    [Fact]
    public void HeldFundsAreNotPresentedAsAnEmptyAccount()
    {
        Assert.Equal(FundingRejection.TemporarilyReserved,
            AccountFunding.Reserve(20, 100, 100, 0, 5, 15, 0).Rejection);
        Assert.Equal(FundingRejection.TemporarilyReserved,
            AccountFunding.Reserve(20, 100, 80, 20, 0, 0, 0).Rejection);
        Assert.Equal(FundingRejection.InsufficientCredits,
            AccountFunding.Reserve(20, 100, 100, 0, 5, 14, 0).Rejection);
    }

    [Fact]
    public void CaptureUsesFreeFundsFirstAndUnusedHoldsRepayInterveningRefundDebt()
    {
        var capture = AccountFunding.Capture(70, 50, 50, 0, 50, 20);
        Assert.Equal(new FundingCapture(50, 20, 10, 0, 0), capture);
        Assert.Equal(new FundingCapture(0, 0, 0, 0, 50), AccountFunding.Capture(0, 0, 50, 0, 50, 100));
    }

    [Fact]
    public void CapturesCannotSpendAnotherOperationHoldOrExceedAuthorization()
    {
        Assert.Throws<InvalidOperationException>(() => AccountFunding.Capture(101, 50, 50, 0, 50, 0));
        Assert.Throws<InvalidOperationException>(() => AccountFunding.Capture(1, 0, 50, 0, 49, 0));
    }

    [Fact]
    public void CapturesConserveFundsAcrossInterveningRefundsAndTopUps()
    {
        var random = new Random(4_107);
        for (var index = 0; index < 1_000; index++)
        {
            var freeHold = random.Next(200);
            var walletHold = random.Next(200);
            var available = random.Next(200);
            var otherHolds = random.Next(200);
            var debt = random.Next(200);
            var actual = random.Next(freeHold + walletHold + 1);
            var before = available + walletHold + otherHolds - debt;
            var captured = AccountFunding.Capture(actual, freeHold, walletHold, available, walletHold + otherHolds, debt);
            Assert.Equal(before - captured.WalletCharge, captured.Available + captured.Held - captured.Outstanding);
            Assert.Equal(actual, captured.FreeCharge + captured.WalletCharge);
            Assert.Equal(otherHolds, captured.Held);
            Assert.True(captured.Available >= 0 && captured.Outstanding >= 0);
        }
    }

    [Fact]
    public void OnlyStrictCustomerReservationsCapUnexpectedProviderOverages()
    {
        var policy = ServicePolicy.Defaults();
        var resource = policy.Get(Resources.QwenFlash);
        var legacy = new ReservationSnapshot(policy.Revision, policy.Fingerprint, resource.Resource,
            resource.Price, policy.PrincipalDailyAllowance, new(100), new(100));
        var strict = legacy with { StrictPublicReservation = true };
        SettlementDecision Settle(ReservationSnapshot snapshot) => ReservationRules.Settle(
            ReservationState.Dispatched, snapshot, new(200), new(300), true, CostBasis.Exact, true, 1, 1, "success");
        Assert.Equal(100, Settle(strict).PublicCost.Value);
        Assert.Equal(200, Settle(legacy).PublicCost.Value);
        Assert.Equal(300, Settle(strict).OperatorCost.Value);
    }

    [Theory]
    [InlineData(false, 4_096, 1_209_878_400)]
    [InlineData(false, 8_192, 1_229_539_200)]
    [InlineData(true, 32_768, 1_337_673_600)]
    public void ProviderInjectedSchemasRetainFullContextAndOutputTolerance(bool thinking, int output, long expected)
    {
        var (catalog, policy) = AiModelTests.Configuration();
        var request = ChatRequestResolver.Resolve(new(Resources.AiModel, Encoding.UTF8.GetBytes(
            $$$"""{"model":"ai-model","enable_thinking":{{{thinking.ToString().ToLowerInvariant()}}},"max_tokens":{{{output}}},"response_format":{"type":"json_schema","json_schema":{}},"messages":[{"role":"user","content":"hello"}]}""")), catalog);
        var authorized = PaidChatAuthorizer.Authorize(request, catalog, policy);
        Assert.Equal(expected, authorized.MaximumCharge.Value);
        using var payload = JsonDocument.Parse(authorized.Request.Utf8Json);
        Assert.False(payload.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(output, payload.RootElement.GetProperty("max_completion_tokens").GetInt32());
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("你好😀\n\"\\")]
    public void SmallTextRequestsFitSmallBalancesAndCoverUtf8Input(string text)
    {
        var (catalog, policy) = AiModelTests.Configuration();
        var command = ChatRequestResolver.Resolve(new(Resources.AiModel, JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = "ai-model",
            max_tokens = 1,
            messages = new[] { new { role = "user", content = text } },
        })), catalog);
        var result = PaidChatAuthorizer.Authorize(command, catalog, policy);
        Assert.InRange(result.MaximumCharge.Value, policy.PricingFor(command.ExecutionModel)!.Tariff.Calculate(Encoding.UTF8.GetByteCount(text), 11).Value, 1_000_000);
        Assert.True(AccountFunding.Reserve(result.MaximumCharge.Value, 0, 0, 0, 10_000_000, 0, 0).Accepted);
    }

    [Fact]
    public void MaximumChargeCoversEveryReachableTierEvenWhenRatesDecrease()
    {
        var tariff = new TokenTariff([new(100, 10, 100, 0), new(200, 1, 1, 0)]);
        Assert.Equal(new NanoYuan(4_000), tariff.MaximumCharge(200, 30));
        Assert.Equal(new NanoYuan(3_400), tariff.MaximumCharge(40, 30));
        for (var input = 0; input <= 200; input++)
            Assert.True(tariff.Calculate(input, 30) <= tariff.MaximumCharge(200, 30));
    }

    [Fact]
    public void ImageTokensKeepTheirContextEnvelopeAndTextBoundsRespectContextCeilings()
    {
        var (catalog, policy) = AiModelTests.Configuration();
        var command = ChatRequestResolver.Resolve(new(Resources.AiModel, Encoding.UTF8.GetBytes(
            """{"model":"ai-model","max_tokens":1,"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"https://example.com/image.png"}}]}]}""")), catalog);
        var envelope = catalog.BillingEnvelope(command.ExecutionModel)!;
        var tariff = policy.PricingFor(command.ExecutionModel)!.Tariff;
        Assert.Equal(envelope.MaximumCharge(tariff, false, 1), PaidChatAuthorizer.Authorize(command, catalog, policy).MaximumCharge);
        Assert.Equal(envelope.MaximumCharge(tariff, false, 1), envelope.MaximumCharge(tariff, false, 1, long.MaxValue));
        Assert.Null(new ModelBillingEnvelope(100, 100).TextInput);
    }

    [Theory]
    [InlineData("\"n\":2")]
    [InlineData("\"enable_search\":true")]
    [InlineData("\"tools\":[{}]")]
    [InlineData("\"max_tokens\":8193")]
    [InlineData("\"max_tokens\":0")]
    [InlineData("\"max_tokens\":100,\"max_completion_tokens\":200")]
    [InlineData("\"n\":\"1\"")]
    [InlineData("\"enable_thinking\":\"false\"")]
    [InlineData("\"max_tokens\":\"4096\"")]
    [InlineData("\"max_completion_tokens\":null")]
    [InlineData("\"max_tokens\":1.5")]
    public void UnpricedFeaturesAndUnboundedCompletionsAreRejected(string field)
    {
        var (catalog, policy) = AiModelTests.Configuration();
        var request = ChatRequestResolver.Resolve(new(Resources.AiModel, Encoding.UTF8.GetBytes(
            $$"""{"model":"ai-model",{{field}},"messages":[{"role":"user","content":"hello"}]}""")), catalog);
        Assert.Throws<JsonException>(() => PaidChatAuthorizer.Authorize(request, catalog, policy));
    }

    [Fact]
    public async Task SignedInChatSnapshotsAccountAndStableIdentityAcrossIpChanges()
    {
        await using var baseFactory = new ApiFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPrincipalIdentity>();
            services.AddSingleton<IPrincipalIdentity, AddressIdentity>();
        }));
        var useCase = factory.Services.GetRequiredService<ChatUseCase>();
        var account = Guid.NewGuid();
        var command = new ChatCommand(Resources.AiModel, Encoding.UTF8.GetBytes(
            """{"model":"ai-model","max_tokens":4096,"messages":[{"role":"user","content":"hello"}]}"""));
        foreach (var ip in new[] { "203.0.113.1", "203.0.113.2" })
        {
            var context = new RequestContext(ip, "same-logical-request", "trace") { AccountId = account };
            var events = new List<ChatApplicationEvent>();
            await foreach (var item in useCase.ExecuteAsync(context, command, TestContext.Current.CancellationToken)) events.Add(item);
            Assert.IsType<ChatApplicationEvent.Completed>(events[^1]);
        }
        var reservations = baseFactory.Ledger.Reservations.ToArray();
        Assert.Equal(2, reservations.Length);
        Assert.All(reservations, item =>
        {
            Assert.Equal(account, item.AccountId);
            Assert.True(item.Snapshot.StrictPublicReservation);
            Assert.True(item.Snapshot.PublicReservation.Value > 1);
        });
        Assert.NotEqual(reservations[0].PrincipalId, reservations[1].PrincipalId);
        Assert.Equal(reservations[0].IdempotencyHash, reservations[1].IdempotencyHash);
    }

    [Fact]
    public async Task FixedPricePaidToolsReserveExactPrice()
    {
        await using var factory = new ApiFactory();
        var context = new RequestContext("203.0.113.3", "fixed-price", "trace") { AccountId = Guid.NewGuid() };
        var result = await factory.Services.GetRequiredService<TableUseCase>()
            .ExecuteAsync(context, new(new byte[] { 1 }), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(30_000_000, Assert.Single(factory.Ledger.Reservations).Snapshot.PublicReservation.Value);
    }

    [Fact]
    public void SharedTranslationPromptCountsUnicodeAndEscapedInputExactly()
    {
        var source = "a\"\\\n你好😀";
        var prompt = TranslationPrompt.Build(source, "auto", "en", "general");
        Assert.True(TranslationPrompt.CountCharacters(prompt) > TranslationPrompt.CountCharacters(source));
        using var payload = JsonDocument.Parse(prompt[(prompt.LastIndexOf('\n') + 1)..]);
        Assert.Equal(source, payload.RootElement[0].GetProperty("content").GetString());
    }

    private sealed class AddressIdentity : IPrincipalIdentity
    {
        public Task<AnonymousPrincipal?> ResolveAsync(string? clientAddress, CancellationToken cancellationToken) =>
            Task.FromResult<AnonymousPrincipal?>(new(clientAddress == "203.0.113.1"
                ? Guid.Parse("01900000-0000-7000-8000-000000000001")
                : Guid.Parse("01900000-0000-7000-8000-000000000002"), clientAddress ?? "missing"));
    }
}
