using Microsoft.EntityFrameworkCore;
using SnowShotApi.Models;

namespace SnowShotApi.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<IpUser> IpUsers { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<UserTranslationOrder> UserTranslationOrders { get; set; }
    public DbSet<UserTranslationOrderStats> UserTranslationOrderStats { get; set; }
    public DbSet<UserChatOrder> UserChatOrders { get; set; }
    public DbSet<UserChatOrderStats> UserChatOrderStats { get; set; }


    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<IpUser>();

        modelBuilder.Entity<User>()
            .Property(u => u.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<UserTranslationOrder>()
            .Property(u => u.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<UserTranslationOrderStats>()
            .Property(u => u.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<UserChatOrder>()
            .Property(u => u.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");

        modelBuilder.Entity<UserChatOrderStats>()
            .Property(u => u.CreatedAt)
            .HasDefaultValueSql("CURRENT_TIMESTAMP");
    }
}