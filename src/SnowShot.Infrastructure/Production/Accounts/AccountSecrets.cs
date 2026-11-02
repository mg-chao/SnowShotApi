using System.Security.Cryptography;
using System.Text;
using SnowShot.Infrastructure.Configuration;

namespace SnowShot.Infrastructure.Accounts;

internal sealed class AccountSecrets
{
    private readonly byte[][] _keys;
    internal AccountSecrets(IdentityOptions identity, AccountEncryptionOptions? options = null)
    {
        var configured = options is not null && !string.IsNullOrWhiteSpace(options.KeyBase64);
        var sources = configured ? new[] { Convert.FromBase64String(options!.KeyBase64) }.Concat(options.PreviousKeysBase64.Select(Convert.FromBase64String))
            : new[] { identity.CurrentKey }.Concat(identity.PreviousKey is { } previous ? new[] { previous } : []);
        _keys = sources.Select(key => HMACSHA256.HashData(key, "snowshot-account-secrets-v1"u8)).ToArray();
    }
    internal static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    internal static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    internal string Fingerprint(string value) => Convert.ToHexStringLower(HMACSHA256.HashData(_keys[0], Encoding.UTF8.GetBytes("client:" + value)));
    internal string Protect(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var source = Encoding.UTF8.GetBytes(plain);
        var result = new byte[28 + source.Length];
        nonce.CopyTo(result, 0);
        using var aes = new AesGcm(_keys[0], 16);
        aes.Encrypt(nonce, source, result.AsSpan(28), result.AsSpan(12, 16));
        return Convert.ToBase64String(result);
    }
    internal string Unprotect(string cipher)
    {
        var source = Convert.FromBase64String(cipher);
        if (source.Length < 28) throw new CryptographicException("Invalid account secret.");
        var plain = new byte[source.Length - 28];
        foreach (var key in _keys)
        {
            try
            {
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(source.AsSpan(0, 12), source.AsSpan(28), source.AsSpan(12, 16), plain);
                return Encoding.UTF8.GetString(plain);
            }
            catch (AuthenticationTagMismatchException) { }
        }
        throw new CryptographicException("No configured account encryption key authenticates this secret.");
    }
}
