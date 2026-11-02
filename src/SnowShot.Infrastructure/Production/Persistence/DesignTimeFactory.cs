using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SnowShot.Infrastructure.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SnowShotDbContext>
{
    public SnowShotDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SnowShotDbContext>()
        .UseNpgsql("Host=localhost;Database=snowshot;Username=snowshot;Password=snowshot", npgsql => npgsql
            .MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema)).Options);
}
