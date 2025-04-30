using System.Security.Cryptography;
using System.Text;

namespace SnowShotApi.Utiles;

public static class AppUtiles
{
    private static readonly SHA256 _sha256 = SHA256.Create();

    public static string GenerateSHA256(string content)
    {
        var hashBytes = _sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hashBytes);
    }
}

