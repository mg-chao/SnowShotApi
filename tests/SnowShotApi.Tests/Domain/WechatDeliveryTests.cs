using System.Text.Json;
using SnowShot.Application;
using SnowShot.Infrastructure.Accounts;

namespace SnowShotApi.Tests.Domain;

public sealed class WechatDeliveryTests
{
    private const string OrderId = "1234567890abcdef1234567890abcdef";
    private static VirtualPaymentParameters Parameters => ParametersFor(0);
    private static VirtualPaymentParameters ParametersFor(int environment) => new("short_series_goods", JsonSerializer.Serialize(new
    { env = environment, outTradeNo = OrderId, currencyType = "CNY", productId = "product-990", buyQuantity = 1, goodsPrice = 990, attach = OrderId }), "", "");

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void NestedDeliveryUsesPlatformOrderAndActualPayment(bool xml, int environment)
    {
        var message = WechatPlatformMessage.Parse(Payload(xml, environment));
        var order = message.Delivery(OrderId, "payer", 990, ParametersFor(environment), null, true);
        Assert.Equal("platform-order", order.TransactionId);
        Assert.Equal(980, order.PaidFen);
        Assert.Equal(990, order.OrderFeeFen);
        Assert.True(order.Delivered);
        Assert.Contains(xml ? "<ErrCode>0</ErrCode>" : "\"ErrCode\":0", message.SuccessReply, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(-1, -1)]
    public void DeliveryRejectsMismatchedOrUnsupportedEnvironments(int signedEnvironment, int receivedEnvironment)
    {
        var message = WechatPlatformMessage.Parse(Payload(false, receivedEnvironment));
        var error = Assert.Throws<AccountException>(() => message.Delivery(OrderId, "payer", 990, ParametersFor(signedEnvironment), null, false));
        Assert.Equal("reconciliation_required", error.Code);
    }

    [Theory]
    [InlineData("product-990", "other-product")]
    [InlineData("\"Quantity\":1", "\"Quantity\":2")]
    [InlineData("\"OrigPrice\":990", "\"OrigPrice\":991")]
    [InlineData("\"ActualPrice\":980", "\"ActualPrice\":0")]
    [InlineData("\"ActualPrice\":980", "\"ActualPrice\":991")]
    [InlineData("\"Env\":0", "\"Env\":1")]
    [InlineData("\"OpenId\":\"payer\"", "\"OpenId\":\"other-payer\"")]
    [InlineData("\"Attach\":\"" + OrderId + "\"", "\"Attach\":\"other-order\"")]
    public void DeliveryRejectsConflictsWithImmutableSignedOrder(string original, string replacement)
    {
        var message = WechatPlatformMessage.Parse(Payload(false).Replace(original, replacement, StringComparison.Ordinal));
        var error = Assert.Throws<AccountException>(() => message.Delivery(OrderId, "payer", 990, Parameters, null, false));
        Assert.Equal("reconciliation_required", error.Code);
    }

    [Fact]
    public void NonWechatChannelRequiresMatchingQueryBeforeDelivery()
    {
        var message = WechatPlatformMessage.Parse(Payload(false).Replace("\"MchOrderNo\":\"platform-order\"", "\"MchOrderNo\":\"\"", StringComparison.Ordinal));
        var error = Assert.Throws<AccountException>(() => message.Delivery(OrderId, "payer", 990, Parameters, null, true));
        Assert.Equal(503, error.Status);
        var query = new VerifiedVirtualOrder(OrderId, "payer", 980, "paid", "ios-platform-order", [], 990);
        Assert.Equal("ios-platform-order", message.Delivery(OrderId, "payer", 990, Parameters, query, true).TransactionId);
        Assert.Throws<AccountException>(() => message.Delivery(OrderId, "payer", 990, Parameters, query with { PaidFen = 990 }, true));
        Assert.Throws<AccountException>(() => message.Delivery(OrderId, "payer", 990, Parameters, query with { State = "payment_pending" }, true));
    }

    [Theory]
    [InlineData("{\"Event\":\"a\",\"Event\":\"b\"}")]
    [InlineData("{\"GoodsInfo.ProductId\":\"spoofed\"}")]
    [InlineData("<xml><Event>a</Event><Event>b</Event></xml>")]
    [InlineData("<xml><GoodsInfo.ProductId>spoofed</GoodsInfo.ProductId></xml>")]
    [InlineData("<!DOCTYPE xml [<!ENTITY value 'expanded'>]><xml><Event>&value;</Event></xml>")]
    public void MalformedAndAmbiguousMessagesAreRejected(string payload)
    {
        Assert.Equal("invalid_platform_message", Assert.Throws<AccountException>(() => WechatPlatformMessage.Parse(payload)).Code);
    }

    private static string Payload(bool xml, int environment = 0) => xml ? $"""
        <xml><Event>xpay_goods_deliver_notify</Event><OpenId>payer</OpenId><OutTradeNo>{OrderId}</OutTradeNo><Env>{environment}</Env>
        <WeChatPayInfo><MchOrderNo>platform-order</MchOrderNo><TransactionId>channel-order</TransactionId></WeChatPayInfo>
        <GoodsInfo><ProductId>product-990</ProductId><Quantity>1</Quantity><OrigPrice>990</OrigPrice><ActualPrice>980</ActualPrice><Attach>{OrderId}</Attach></GoodsInfo></xml>
        """ : JsonSerializer.Serialize(new
    {
        Event = "xpay_goods_deliver_notify",
        OpenId = "payer",
        OutTradeNo = OrderId,
        Env = environment,
        WeChatPayInfo = new { MchOrderNo = "platform-order", TransactionId = "channel-order" },
        GoodsInfo = new { ProductId = "product-990", Quantity = 1, OrigPrice = 990, ActualPrice = 980, Attach = OrderId }
    });
}
