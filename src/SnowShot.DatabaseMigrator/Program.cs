using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Persistence;

var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Production;
var configuration = new ConfigurationManager();
configuration.AddEnvironmentVariables();
configuration.AddMountedSecrets(environmentName);

var connectionString = configuration.GetConnectionString("SnowShot");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:SnowShot must contain the migration database connection string.");
}

var options = new DbContextOptionsBuilder<SnowShotDbContext>()
    .UseNpgsql(connectionString, npgsql => npgsql
        .MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema)
        .EnableRetryOnFailure(5))
    .Options;

await using var context = new SnowShotDbContext(options);
var pendingMigrations = (await context.Database.GetPendingMigrationsAsync()).ToArray();
await context.Database.MigrateAsync();

Console.WriteLine(
    pendingMigrations.Length == 0
        ? "Database schema is already current."
        : $"Applied {pendingMigrations.Length} migration(s): {string.Join(", ", pendingMigrations)}");
