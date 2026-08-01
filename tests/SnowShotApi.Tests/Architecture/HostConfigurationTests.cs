using Microsoft.Extensions.Configuration;
using SnowShot.Api;
using SnowShot.Infrastructure.Configuration;

namespace SnowShotApi.Tests.Architecture;

public sealed class HostConfigurationTests
{
    [Fact]
    public void ProductionRequiresExplicitAllowedHosts()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "*",
            ["TrustedProxies:0"] = "192.0.2.10",
        });

        var exception = Assert.Throws<InvalidOperationException>(() => IngressPolicy.Validate(configuration, deployedEnvironment: true));

        Assert.Contains("explicit hostnames", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsWildcardSubdomains()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "*.example.test",
            ["TrustedProxies:0"] = "192.0.2.10",
        });

        var exception = Assert.Throws<InvalidOperationException>(() => IngressPolicy.Validate(configuration, deployedEnvironment: true));

        Assert.Contains("explicit hostnames", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionParsesExplicitHostsAndTrustedProxies()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "api.example.test;health.example.test",
            ["TrustedProxies:0"] = "192.0.2.10",
            ["TrustedProxies:1"] = "2001:db8::10",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://otel-collector:4317",
        });

        var proxies = IngressPolicy.Validate(configuration, deployedEnvironment: true);

        Assert.Equal(2, proxies.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("otel-collector:4317")]
    [InlineData("file:///tmp/telemetry")]
    public void ProductionRequiresAbsoluteHttpOtlpCollector(string? endpoint)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "api.example.test",
            ["TrustedProxies:0"] = "192.0.2.10",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => IngressPolicy.Validate(configuration, deployedEnvironment: true));

        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingRequiresMountedSecretsLikeProduction()
    {
        var configuration = new ConfigurationManager();

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.AddMountedSecrets("Staging"));

        Assert.Contains("required outside Development", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cache.example.test:6380,user=snowshot-api,password=secret,ssl=false,abortConnect=false", "managed-failover", "enable TLS")]
    [InlineData("cache.example.test:6380,user=snowshot-api,ssl=true,abortConnect=false", "managed-failover", "ACL password")]
    [InlineData("cache.example.test:6380,password=secret,ssl=true,abortConnect=false", "managed-failover", "ACL user")]
    [InlineData("cache.example.test:6380,user=snowshot-api,password=secret,ssl=true", "managed-failover", "abortConnect=false")]
    [InlineData("cache.example.test:6380,user=snowshot-api,password=secret,ssl=true,abortConnect=false", "development-single-node", "managed failover")]
    public void DeployedRedisRejectsInsecureOrSingleNodeConfiguration(string connection, string mode, string expected)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = connection,
            [RedisDeploymentPolicy.ModeKey] = mode,
        });

        var exception = Assert.Throws<InvalidOperationException>(() => RedisDeploymentPolicy.Build(configuration, deployedEnvironment: true));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeployedRedisAcceptsAuthenticatedTlsManagedFailover()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = "cache.example.test:6380,user=snowshot-api,password=secret,ssl=true,abortConnect=false",
            [RedisDeploymentPolicy.ModeKey] = "managed-failover",
        });

        var options = RedisDeploymentPolicy.Build(configuration, deployedEnvironment: true);

        Assert.NotNull(options);
        Assert.True(options.Ssl);
        Assert.False(options.AbortOnConnectFail);
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
