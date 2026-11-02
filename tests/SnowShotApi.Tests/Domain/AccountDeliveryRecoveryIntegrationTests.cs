using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed partial class AccountLifecycleIntegrationTests
{
    [Theory, Trait("Category", "Integration")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("<html>unavailable</html>")]
    public async Task AmbiguousDeliveryAcknowledgementsRemainRetryableWithoutRepeatingCredits(string response)
    {
        var f = Fixture();
        var registration = await Register(f, Name());
        var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null,
            DeliveryPayload(order.OrderId, mini.OpenId!, "wx-" + order.OrderId), "", Token);
        var linked = (await f.Service.ResolveAsync((await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token)).AccessToken, Token))!;
        var before = await f.Service.SummaryAsync(linked, Token);
        await using var db = new SnowShotDbContext(f.Options);
        await db.AccountPaymentOrders.Where(x => x.Id == order.OrderId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.DeliveryConfirmed, false), Token);

        using var handler = new DeliveryResponseHandler(order.OrderId, response);
        using var platform = new WechatAccountPlatform(new DeliveryClients(handler), new()
        { AppId = "review-app", AppSecret = "review-secret", AppKey = "review-key", Environment = 0 }, TimeProvider.System);
        var recovering = AccountTestServices.Create(new DistributedIntegrationTests.ContextFactory(f.Options),
            platform, f.Secrets, TimeProvider.System, new());
        await recovering.RefreshOrderAsync(mini, order.OrderId, Token);
        var pending = await db.AccountPaymentOrders.AsNoTracking().SingleAsync(x => x.Id == order.OrderId, Token);
        Assert.False(pending.DeliveryConfirmed);
        Assert.Equal(1, pending.DeliveryAttempts);
        Assert.Null(pending.DeliveryLeaseToken);
        Assert.True(pending.NextDeliveryAttemptAt > DateTimeOffset.UtcNow);
        Assert.Equal(before.AvailableCreditNanoYuan, (await f.Service.SummaryAsync(linked, Token)).AvailableCreditNanoYuan);

        handler.DeliveryResponse = "{\"errcode\":0}";
        await db.AccountPaymentOrders.Where(x => x.Id == order.OrderId).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.LastQueriedAt, (DateTimeOffset?)null)
            .SetProperty(x => x.NextDeliveryAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Token);
        await recovering.RefreshOrderAsync(mini, order.OrderId, Token);
        var completed = await db.AccountPaymentOrders.AsNoTracking().SingleAsync(x => x.Id == order.OrderId, Token);
        Assert.True(completed.DeliveryConfirmed);
        Assert.Equal(2, completed.DeliveryAttempts);
        Assert.Single((await f.Service.TransactionsAsync(linked, null, 20, Token)).Items);
        Assert.Equal(before, await f.Service.SummaryAsync(linked, Token));
    }

    private sealed class DeliveryClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://platform.invalid/") };
    }

    private sealed class DeliveryResponseHandler(string orderId, string response) : HttpMessageHandler
    {
        internal string DeliveryResponse { get; set; } = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/cgi-bin/stable_token" => "{\"access_token\":\"test-token\",\"expires_in\":7200}",
                "/xpay/notify_provide_goods" => DeliveryResponse,
                "/xpay/query_order" => JsonSerializer.Serialize(new
                {
                    errcode = 0,
                    order = new { order_id = orderId, env_type = 1, status = 3, order_type = 0, paid_fee = 990, order_fee = 990, wx_order_id = "wx-" + orderId }
                }),
                _ => throw new InvalidOperationException("Unexpected platform request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
