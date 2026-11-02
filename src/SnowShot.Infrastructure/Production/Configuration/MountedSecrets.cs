using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SnowShot.Infrastructure.Configuration;

public static class MountedSecrets
{
    public const string DirectoryVariable = "SNOWSHOT_SECRETS_DIRECTORY";

    public static IConfigurationManager AddMountedSecrets(this IConfigurationManager configuration, string environmentName)
    {
        var directory = configuration[DirectoryVariable];
        if (string.IsNullOrWhiteSpace(directory))
        {
            if (!environmentName.Equals(Environments.Development, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{DirectoryVariable} is required outside Development.");
            return configuration;
        }
        var path = Path.GetFullPath(directory);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Secrets directory does not exist: {path}");
        configuration.AddKeyPerFile(path, optional: false, reloadOnChange: false);
        return configuration;
    }
}
