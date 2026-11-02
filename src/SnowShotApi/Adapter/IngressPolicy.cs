using System.Net;
using Microsoft.Extensions.Configuration;

namespace SnowShot.Api;

public static class IngressPolicy
{
    public static IReadOnlyList<IPAddress> Validate(IConfiguration configuration, bool deployedEnvironment)
    {
        var trustedProxies = configuration.GetSection("TrustedProxies").Get<string[]>() ?? [];
        if (deployedEnvironment && trustedProxies.Length == 0)
            throw new InvalidOperationException("At least one TrustedProxies entry is required outside Development.");

        var allowedHosts = (configuration["AllowedHosts"] ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (deployedEnvironment && (allowedHosts.Length == 0 || allowedHosts.Any(IsWildcard)))
            throw new InvalidOperationException("Deployed AllowedHosts must contain only explicit hostnames.");

        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (deployedEnvironment && (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var collector) ||
            !(collector.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              collector.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException(
                "OTEL_EXPORTER_OTLP_ENDPOINT must be an absolute HTTP or HTTPS collector endpoint outside Development.");

        return trustedProxies.Select(value => IPAddress.TryParse(value, out var address)
            ? address
            : throw new InvalidOperationException($"Trusted proxy '{value}' is not a valid IP address.")).ToArray();
    }

    private static bool IsWildcard(string host) => host.Contains('*', StringComparison.Ordinal) ||
        host is "+" or "0.0.0.0" or "[::]";
}
