using System.Text.Json.Serialization;

namespace SnowShot.Contracts;

public sealed record AccountLoginRequest([property: JsonPropertyName("username")] string? Username, [property: JsonPropertyName("password")] string? Password);
public sealed record AccountWechatLoginRequest([property: JsonPropertyName("code")] string? Code);
public sealed record AccountRefreshRequest([property: JsonPropertyName("refresh_token")] string? RefreshToken);
public sealed record AccountRegistrationRequest(
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("password_confirmation")] string? PasswordConfirmation,
    [property: JsonPropertyName("captcha_id")] Guid CaptchaId,
    [property: JsonPropertyName("captcha_answer")] string? CaptchaAnswer);
public sealed record AccountOrderRequest([property: JsonPropertyName("request_id")] Guid RequestId,
    [property: JsonPropertyName("package_id")] string? PackageId,
    [property: JsonPropertyName("checkout_attempt_id")] Guid? CheckoutAttemptId = null);
public sealed record AccountDeletionRequest(
    [property: JsonPropertyName("confirmation")] string? Confirmation,
    [property: JsonPropertyName("wechat_code")] string? WechatCode,
    [property: JsonPropertyName("acknowledge_forfeiture")] bool AcknowledgeForfeiture,
    [property: JsonPropertyName("deletion_version")] string? DeletionVersion);
