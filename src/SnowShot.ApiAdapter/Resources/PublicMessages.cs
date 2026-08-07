using System.Globalization;
using SnowShot.Application;

namespace SnowShot.Api.Resources;

public sealed class PublicMessages
{
    private static readonly Dictionary<string, (string English, string Chinese)> Values = new(StringComparer.Ordinal)
    {
        ["Request success"] = ("Request success", "请求成功"),
        ["Validation failed"] = ("Validation failed", "请求参数无效"),
        ["Cannot get client IP address"] = ("Cannot get client IP address", "无法获取客户端 IP 地址"),
        ["User translation limit reached"] = ("User translation limit reached", "已达到用户翻译限额"),
        ["User chat limit reached"] = ("User chat limit reached", "已达到用户聊天限额"),
        ["Failed to translate"] = ("Failed to translate", "翻译失败"),
        ["AI Translation"] = ("AI Translation", "AI 翻译"),
        ["Invalid table image request"] = ("The request must contain exactly one valid WebP table image", "请求必须且只能包含一张有效的 WebP 表格图片"),
        ["Table extraction queue full"] = ("The table extraction queue is full; retry later", "表格提取队列已满，请稍后重试"),
        ["Table extraction service unavailable"] = ("The table extraction service is unavailable", "表格提取服务暂不可用"),
        ["Table extraction failed"] = ("Table extraction failed", "表格提取失败"),
        ["Internal server error"] = ("Internal server error", "服务器内部错误"),
        ["Database connection error"] = ("Database connection error", "数据库连接失败"),
        ["Operator budget exhausted"] = ("The service budget is temporarily exhausted", "服务预算暂时已用尽"),
        ["Duplicate request"] = ("A request with this id already exists", "具有此请求 ID 的请求已存在"),
        ["Request ownership lost"] = ("Request ownership was lost", "请求所有权已丢失"),
        ["Request limit reached"] = ("The request limit has been reached", "已达到请求限额"),
        ["Request queue full"] = ("The request queue is full", "请求队列已满"),
        ["Service unavailable"] = ("A required service is unavailable", "所需服务暂不可用"),
        ["Payload too large"] = ("The request payload is too large", "请求负载过大"),
        ["Request deadline exceeded"] = ("The request deadline was exceeded", "请求已超过截止时间"),
        ["Upstream service failed"] = ("The upstream service failed", "上游服务失败"),
        ["Request ID invalid"] = ("X-Request-ID must contain exactly one value with at most 64 visible ASCII characters.", "X-Request-ID 必须且只能包含一个值，且最多为 64 个可见 ASCII 字符。"),
        ["Model not found"] = ("The model `{0}` does not exist or you do not have access to it.", "模型 `{0}` 不存在，或您无权访问该模型。"),
        ["Content count invalid"] = ("Content must contain between 1 and 50 items.", "content 必须包含 1 到 50 项。"),
        ["Content item null"] = ("Content[{0}] cannot be null.", "content[{0}] 不能为 null。"),
        ["Content too long"] = ("Content total length cannot exceed 5000 characters.", "content 总长度不能超过 5000 个字符。"),
        ["Unsupported language"] = ("Unsupported language code: {0}", "不支持的语言代码：{0}"),
        ["Unsupported domain"] = ("Unsupported domain: {0}", "不支持的领域：{0}"),
        ["Bad Request"] = ("Bad Request", "错误请求"),
        ["Conflict"] = ("Conflict", "请求冲突"),
        ["Payload Too Large"] = ("Payload Too Large", "请求负载过大"),
        ["Unprocessable Content"] = ("Unprocessable Content", "无法处理的内容"),
        ["Unprocessable Entity"] = ("Unprocessable Entity", "无法处理的内容"),
        ["Too Many Requests"] = ("Too Many Requests", "请求过多"),
        ["Internal Server Error"] = ("Internal Server Error", "服务器内部错误"),
        ["Bad Gateway"] = ("Bad Gateway", "网关错误"),
        ["Service Unavailable"] = ("Service Unavailable", "服务不可用"),
        ["Gateway Timeout"] = ("Gateway Timeout", "网关超时"),
        ["qwen-flash"] = ("Qwen Flash", "通义千问 Flash"),
        ["qwen-plus"] = ("Qwen Plus", "通义千问 Plus"),
        ["qwen3-vl-flash"] = ("Qwen VL Flash", "通义千问 VL Flash"),
        ["deepseek-v4-flash"] = ("DeepSeek V4 Flash", "DeepSeek V4 Flash"),
    };

    public string this[string key] => Values.TryGetValue(key, out var value)
        ? IsChinese ? value.Chinese : value.English
        : key;

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, this[key], arguments);

    public string Validation(ValidationIssue issue) => issue.Code switch
    {
        ValidationIssueCode.UnsupportedModel => Format("Model not found", issue.Value),
        ValidationIssueCode.ContentCount => this["Content count invalid"],
        ValidationIssueCode.NullContentItem => Format("Content item null", issue.Index),
        ValidationIssueCode.ContentTooLong => this["Content too long"],
        ValidationIssueCode.UnsupportedLanguage => Format("Unsupported language", issue.Value),
        ValidationIssueCode.UnsupportedDomain => Format("Unsupported domain", issue.Value),
        _ => this["Validation failed"],
    };

    private static bool IsChinese =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
}
