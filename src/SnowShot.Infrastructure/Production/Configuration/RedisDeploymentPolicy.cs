using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace SnowShot.Infrastructure.Configuration;

public enum RedisDeploymentMode
{
    DevelopmentSingleNode,
    ManagedFailover,
    Cluster,
    Sentinel,
}

public static class RedisDeploymentPolicy
{
    public const string ModeKey = "Redis:DeploymentMode";

    public static ConfigurationOptions? Build(IConfiguration configuration, bool deployedEnvironment)
    {
        var connectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (deployedEnvironment)
                throw new InvalidOperationException("Redis is required outside Development.");
            return null;
        }

        ConfigurationOptions options;
        try { options = ConfigurationOptions.Parse(connectionString); }
        catch (ArgumentException exception) { throw new InvalidOperationException("Redis connection configuration is invalid.", exception); }

        var mode = ParseMode(configuration[ModeKey], deployedEnvironment);
        if (!deployedEnvironment) return options;

        if (!options.Ssl)
            throw new InvalidOperationException("Deployed Redis connections must enable TLS.");
        if (string.IsNullOrWhiteSpace(options.Password))
            throw new InvalidOperationException("Deployed Redis connections must authenticate with an ACL password.");
        if (string.IsNullOrWhiteSpace(options.User))
            throw new InvalidOperationException("Deployed Redis connections must identify a dedicated ACL user.");
        if (options.AbortOnConnectFail)
            throw new InvalidOperationException("Deployed Redis connections must set abortConnect=false for failover recovery.");
        if (mode == RedisDeploymentMode.DevelopmentSingleNode)
            throw new InvalidOperationException("Deployed Redis must use managed failover, cluster, or Sentinel topology.");
        if (mode == RedisDeploymentMode.Sentinel && string.IsNullOrWhiteSpace(options.ServiceName))
            throw new InvalidOperationException("Redis Sentinel topology requires a serviceName in the connection configuration.");

        return options;
    }

    private static RedisDeploymentMode ParseMode(string? value, bool deployedEnvironment)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (deployedEnvironment)
                throw new InvalidOperationException($"{ModeKey} is required outside Development.");
            return RedisDeploymentMode.DevelopmentSingleNode;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "development-single-node" => RedisDeploymentMode.DevelopmentSingleNode,
            "managed-failover" => RedisDeploymentMode.ManagedFailover,
            "cluster" => RedisDeploymentMode.Cluster,
            "sentinel" => RedisDeploymentMode.Sentinel,
            _ => throw new InvalidOperationException($"{ModeKey} must be development-single-node, managed-failover, cluster, or sentinel."),
        };
    }
}
