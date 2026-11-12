using System.Globalization;
using SnowShot.Application;

namespace SnowShot.Api.Resources;

public sealed class PublicMessages
{
    private static readonly Dictionary<string, (string English, string SimplifiedChinese, string TraditionalChinese)> Values = new(StringComparer.Ordinal)
    {
        ["Request success"] = ("Request success", "请求成功", "請求成功"),
        ["Validation failed"] = ("Validation failed", "请求参数无效", "請求參數無效"),
        ["Cannot get client IP address"] = ("Cannot get client IP address", "无法获取客户端 IP 地址", "無法取得用戶端 IP 位址"),
        ["User translation limit reached"] = ("User translation limit reached", "已达到用户翻译限额", "已達使用者翻譯上限"),
        ["User chat limit reached"] = ("User chat limit reached", "已达到用户聊天限额", "已達使用者聊天上限"),
        ["Failed to translate"] = ("Failed to translate", "翻译失败", "翻譯失敗"),
        ["AI Translation"] = ("AI Translation", "AI 翻译", "AI 翻譯"),
        ["Fast Translation"] = ("Fast Translation", "快速翻译", "快速翻譯"),
        ["Invalid table image request"] = ("The request must contain exactly one valid WebP table image", "请求必须且只能包含一张有效的 WebP 表格图片", "請求必須且只能包含一張有效的 WebP 表格圖片"),
        ["Invalid latex image request"] = ("The request must contain exactly one valid WebP latex image", "请求必须且只能包含一张有效的 WebP 公式图片", "請求必須且只能包含一張有效的 WebP 公式圖片"),
        ["Table extraction queue full"] = ("The table extraction queue is full; retry later", "表格提取队列已满，请稍后重试", "表格擷取佇列已滿，請稍後再試"),
        ["Latex extraction queue full"] = ("The latex extraction queue is full; retry later", "公式提取队列已满，请稍后重试", "公式擷取佇列已滿，請稍後再試"),
        ["Table extraction service unavailable"] = ("The table extraction service is unavailable", "表格提取服务暂不可用", "表格擷取服務暫時無法使用"),
        ["Latex extraction service unavailable"] = ("The latex extraction service is unavailable", "公式提取服务暂不可用", "公式擷取服務暫時無法使用"),
        ["Table extraction failed"] = ("Table extraction failed", "表格提取失败", "表格擷取失敗"),
        ["Latex extraction failed"] = ("LaTeX extraction failed", "公式提取失败", "公式擷取失敗"),
        ["Internal server error"] = ("Internal server error", "服务器内部错误", "伺服器內部錯誤"),
        ["Database connection error"] = ("Database connection error", "数据库连接失败", "資料庫連線失敗"),
        ["Operator budget exhausted"] = ("The service budget is temporarily exhausted", "服务预算暂时已用尽", "服務預算暫時已用盡"),
        ["Duplicate request"] = ("A request with this id already exists", "具有此请求 ID 的请求已存在", "具有此請求 ID 的請求已存在"),
        ["Request ownership lost"] = ("Request ownership was lost", "请求所有权已丢失", "請求所有權已遺失"),
        ["Request limit reached"] = ("The request limit has been reached", "已达到请求限额", "已達請求上限"),
        ["Request rate limit reached"] = ("Too many requests in a short time. Wait before retrying.", "短时间内请求过多，请稍后重试。", "短時間內請求過多，請稍後再試。"),
        ["Free allowance exhausted"] = ("The free usage allowance for this service has been exhausted.", "此服务的免费使用额度已用尽。", "此服務的免費使用額度已用盡。"),
        ["Insufficient available credit for this request"] = ("Insufficient available credit for this request.", "可用点数不足以预留本次请求所需额度。", "可用點數不足以預留本次請求所需額度。"),
        ["Sign in with a Snow Shot account to use this service"] = ("Sign in with a Snow Shot account to use this service.", "请登录 Snow Shot 账号后使用此服务。", "請登入 Snow Shot 帳號後使用此服務。"),
        ["Credits temporarily reserved by other requests"] = ("Credits are temporarily reserved by other requests. Retry after they complete.", "部分点数正由其他请求预留，请在其完成后重试。", "部分點數已由其他請求預留，請等候這些請求完成後再試。"),
        ["Paid calls are paused until the outstanding balance is repaid"] = ("Paid calls are paused until the outstanding balance is repaid. Free allowance is still available.", "未结清欠款前暂停付费调用，免费额度仍可使用。", "結清欠款前將暫停付費請求，免費額度仍可使用。"),
        ["Request queue full"] = ("The request queue is full", "请求队列已满", "請求佇列已滿"),
        ["Service unavailable"] = ("A required service is unavailable", "所需服务暂不可用", "必要服務暫時無法使用"),
        ["Payload too large"] = ("The request payload is too large", "请求负载过大", "請求內容過大"),
        ["Request deadline exceeded"] = ("The request deadline was exceeded", "请求已超过截止时间", "請求已逾時"),
        ["Upstream service failed"] = ("The upstream service failed", "上游服务失败", "上游服務失敗"),
        ["Request ID invalid"] = ("X-Request-ID must contain exactly one value with at most 64 visible ASCII characters.", "X-Request-ID 必须且只能包含一个值，且最多为 64 个可见 ASCII 字符。", "X-Request-ID 必須且只能包含一個值，且最多為 64 個可見 ASCII 字元。"),
        ["Model not found"] = ("The model `{0}` does not exist or you do not have access to it.", "模型 `{0}` 不存在，或您无权访问该模型。", "模型 `{0}` 不存在，或您無權存取該模型。"),
        ["Content count invalid"] = ("Content must contain between 1 and 50 items.", "content 必须包含 1 到 50 项。", "content 必須包含 1 到 50 項。"),
        ["Content item null"] = ("Content[{0}] cannot be null.", "content[{0}] 不能为 null。", "content[{0}] 不能為 null。"),
        ["Content too long"] = ("Content total length cannot exceed 5000 characters.", "content 总长度不能超过 5000 个字符。", "content 總長度不能超過 5000 個字元。"),
        ["Unsupported language"] = ("Unsupported language code: {0}", "不支持的语言代码：{0}", "不支援的語言代碼：{0}"),
        ["Unsupported domain"] = ("Unsupported domain: {0}", "不支持的领域：{0}", "不支援的領域：{0}"),
        ["Bad Request"] = ("Bad Request", "错误请求", "無效的請求"),
        ["Conflict"] = ("Conflict", "请求冲突", "請求衝突"),
        ["Payload Too Large"] = ("Payload Too Large", "请求负载过大", "請求內容過大"),
        ["Unprocessable Content"] = ("Unprocessable Content", "无法处理的内容", "無法處理的內容"),
        ["Unprocessable Entity"] = ("Unprocessable Entity", "无法处理的内容", "無法處理的內容"),
        ["Too Many Requests"] = ("Too Many Requests", "请求过多", "請求過多"),
        ["Internal Server Error"] = ("Internal Server Error", "服务器内部错误", "伺服器內部錯誤"),
        ["Bad Gateway"] = ("Bad Gateway", "网关错误", "閘道錯誤"),
        ["Service Unavailable"] = ("Service Unavailable", "服务不可用", "服務無法使用"),
        ["Gateway Timeout"] = ("Gateway Timeout", "网关超时", "閘道逾時"),
        ["qwen3.8-flash"] = ("Qwen Flash", "通义千问 Flash", "通義千問 Flash"),
        ["ai-model"] = ("Fast Model", "快速模型", "快速模型"),
        ["expert-model"] = ("Expert Model", "专家模型", "專家模型"),
        ["qwen-mt-flash"] = ("Qwen MT Flash", "通义千问 MT Flash", "通義千問 MT Flash"),
    };

    public string this[string key] => Values.TryGetValue(key, out var value)
        ? CultureInfo.CurrentUICulture.Name switch
        {
            "zh-TW" or "zh-Hant" => value.TraditionalChinese,
            _ => IsChinese ? value.SimplifiedChinese : value.English,
        }
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
