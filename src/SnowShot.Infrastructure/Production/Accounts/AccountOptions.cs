using SnowShot.Domain;

namespace SnowShot.Infrastructure.Accounts;

public sealed class AccountEncryptionOptions
{
    public string KeyBase64 { get; init; } = string.Empty;
    public string[] PreviousKeysBase64 { get; init; } = [];
}

public sealed class WechatAccountOptions
{
    public bool PaymentsEnabled { get; init; }
    public bool AllowPaymentTesting { get; init; }
    public string AppId { get; init; } = string.Empty;
    public string AppSecret { get; init; } = string.Empty;
    public string OfferId { get; init; } = string.Empty;
    public string AppKey { get; init; } = string.Empty;
    public string MessageToken { get; init; } = string.Empty;
    public string EncodingAesKey { get; init; } = string.Empty;
    public string PurchasePage { get; init; } = "pages/purchase/purchase";
    public string MiniProgramCodeVersion { get; init; } = "release";
    public bool MiniProgramCodeCheckPath { get; init; } = true;
    public int Environment { get; init; }
    public bool ReleasePrerequisitesConfirmed { get; init; }
    public Dictionary<string, string> ProductIds { get; init; } = [];
    public bool IsReconciliationConfigured => Environment is 0 or 1 && !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret) && !string.IsNullOrWhiteSpace(AppKey);

    public bool IsPaymentConfigured => Environment is 0 or 1 && PaymentsEnabled && (ReleasePrerequisitesConfirmed || AllowPaymentTesting) &&
        !string.IsNullOrWhiteSpace(AppId) && !string.IsNullOrWhiteSpace(AppSecret) &&
        !string.IsNullOrWhiteSpace(OfferId) && !string.IsNullOrWhiteSpace(AppKey) &&
        !string.IsNullOrWhiteSpace(MessageToken) && ValidAesKey(EncodingAesKey) &&
        MembershipCatalog.For(AllowPaymentTesting).All(package => ProductIds.TryGetValue(package.Id, out var value) && !string.IsNullOrWhiteSpace(value));
    private static bool ValidAesKey(string value)
    {
        if (value.Length != 43) return false;
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(value + "=", decoded, out var written) && written == 32;
    }
}

internal static class MembershipCatalog
{
    internal static readonly (string Id, int Fen)[] Packages =
        [.. MembershipRules.Tiers];

    private static readonly (string Id, int Fen)[] TestingPackages = [("cny-0-1", 10), .. Packages];

    // AllowPaymentTesting is validated as Development-only at startup.
    internal static (string Id, int Fen)[] For(bool allowPaymentTesting) => allowPaymentTesting ? TestingPackages : Packages;
}
