using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SnowShot.Application;

namespace SnowShot.Infrastructure.Accounts;

internal sealed class WechatPlatformMessage
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);
    private bool _xml;
    internal string? this[string key] => _fields.GetValueOrDefault(key);
    internal string SuccessReply => _xml ? "<xml><ErrCode>0</ErrCode><ErrMsg><![CDATA[success]]></ErrMsg></xml>" : "{\"ErrCode\":0,\"ErrMsg\":\"success\"}";

    internal static WechatPlatformMessage Parse(string payload)
    {
        try
        {
            var message = new WechatPlatformMessage { _xml = payload.TrimStart().StartsWith('<') };
            if (message._xml)
            {
                using var reader = XmlReader.Create(new StringReader(payload), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 });
                var root = XElement.Load(reader);
                if (root.Name != "xml") throw Invalid();
                foreach (var element in root.Elements()) message.ReadXml(element, "", 0);
            }
            else
            {
                using var json = JsonDocument.Parse(payload);
                message.ReadJson(json.RootElement, "", 0);
            }
            return message;
        }
        catch (Exception exception) when (exception is XmlException or JsonException or ArgumentException or InvalidOperationException)
        { throw Invalid(); }
    }

    internal VerifiedVirtualOrder Delivery(string orderId, string openId, int priceFen,
        VirtualPaymentParameters parameters, VerifiedVirtualOrder? queried, bool acknowledged)
    {
        try
        {
            using var document = JsonDocument.Parse(parameters.SignData);
            var sign = document.RootElement;
            if (parameters.Mode != "short_series_goods" || this["Event"] != "xpay_goods_deliver_notify" ||
                this["OutTradeNo"] != orderId || this["OpenId"] != openId ||
                !Integer("Env", out var env) || env is not (0 or 1) || sign.GetProperty("env").GetInt32() != env ||
                sign.GetProperty("outTradeNo").GetString() != orderId ||
                sign.GetProperty("currencyType").GetString() != "CNY" ||
                sign.GetProperty("goodsPrice").GetInt32() != priceFen ||
                this["GoodsInfo.ProductId"] != sign.GetProperty("productId").GetString() ||
                this["GoodsInfo.Attach"] != sign.GetProperty("attach").GetString() ||
                !Integer("GoodsInfo.Quantity", out var quantity) || quantity != 1 || sign.GetProperty("buyQuantity").GetInt32() != quantity ||
                !Integer("GoodsInfo.OrigPrice", out var original) || original != priceFen ||
                !Integer("GoodsInfo.ActualPrice", out var actual) || actual <= 0 || actual > original)
                throw Conflict();
            // Some non-WeChat payment channels omit WeChatPayInfo. Recover their
            // platform order ID through query_order before acknowledging delivery.
            var platformId = this["WeChatPayInfo.MchOrderNo"];
            if (string.IsNullOrWhiteSpace(platformId))
            {
                if (queried is null || queried.State != "paid" || queried.OrderId != orderId || queried.OpenId != openId ||
                    queried.PaidFen != actual || queried.OrderFeeFen != original || string.IsNullOrWhiteSpace(queried.TransactionId))
                    throw new AccountException("payments_unavailable", "The delivery payment is not yet confirmed. Retry later.", 503);
                platformId = queried.TransactionId;
            }
            return new(orderId, openId, actual, "paid", platformId, [], original, acknowledged);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw Conflict(); }
    }

    private bool Integer(string key, out int value) => int.TryParse(this[key], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    private void ReadXml(XElement element, string prefix, int depth)
    {
        if (depth > 8 || element.Name.Namespace != XNamespace.None || element.Name.LocalName.Contains('.', StringComparison.Ordinal)) throw Invalid();
        var path = prefix + element.Name.LocalName;
        if (element.HasElements)
            foreach (var child in element.Elements()) ReadXml(child, path + ".", depth + 1);
        else if (!_fields.TryAdd(path, element.Value)) throw Invalid();
    }
    private void ReadJson(JsonElement element, string prefix, int depth)
    {
        if (depth > 8 || element.ValueKind != JsonValueKind.Object) throw Invalid();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Contains('.', StringComparison.Ordinal)) throw Invalid();
            var path = prefix + property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object) ReadJson(property.Value, path + ".", depth + 1);
            else if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                if (!_fields.TryAdd(path, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText())) throw Invalid();
            }
        }
    }
    private static AccountException Invalid() => new("invalid_platform_message", "The platform message is malformed.");
    private static AccountException Conflict() => new("reconciliation_required", "The delivery notification conflicts with the original signed order.", 409);
}
