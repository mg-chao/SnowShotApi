using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

[Collection(DistributedIntegrationGroup.Name)]
public sealed class MembershipMigrationIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task MigrationBackfillsFulfilledActualPaymentsMinusAppliedRefundsAndPreservesOldOperations()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SNOWSHOT_RUN_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Enable the isolated PostgreSQL integration fixture.");
        var token = TestContext.Current.CancellationToken;
        var database = "membership_migration_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!);
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync(token);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
            await create.ExecuteNonQueryAsync(token);
        try
        {
            connection.Database = database;
            connection.Pooling = false;
            await using var db = new SnowShotDbContext(DistributedIntegrationTests.DatabaseOptions(connection.ConnectionString));
            await db.Database.MigrateAsync(token);
            var a = AddAccount(db, 1);
            var b = AddAccount(db, 0);
            var c = AddAccount(db, 99900 * MembershipRules.NanoYuanPerFen);
            var first = AddOrder(db, a, 18800, 10000, fulfilled: true);
            var second = AddOrder(db, a, 18800, 9880, fulfilled: true);
            AddRefund(db, first, 400, applied: true);
            AddRefund(db, first, 680, applied: true);
            AddRefund(db, second, 100, applied: false);
            AddRefund(db, second, 200, applied: false, completed: false);
            var belowThreshold = AddOrder(db, b, 18800, 18800, fulfilled: true);
            AddRefund(db, belowThreshold, 1, applied: true);
            var pending = AddOrder(db, c, 99900, 99900, fulfilled: false);
            AddRefund(db, pending, 100, applied: false);
            var principal = Guid.CreateVersion7();
            var operation = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            db.Principals.Add(new() { Id = principal, CreatedAt = now });
            db.UsageOperations.Add(new()
            {
                Id = operation,
                PrincipalId = principal,
                AccountId = a,
                Kind = UsageKind.TableExtraction,
                Resource = Resources.TableExtraction,
                AllowanceDate = DateOnly.FromDateTime(now.UtcDateTime),
                IdempotencyHash = new byte[32],
                OwnerToken = new byte[32],
                PolicyFingerprint = new byte[32],
                Fence = 1,
                PolicyRevision = 1,
                ReservedFreeNanoYuan = 100,
                ReservedPublicNanoYuan = 100,
                ReservedOperatorNanoYuan = 100,
                State = ReservationState.Reserved,
                CreatedAt = now,
                AbsoluteDeadline = now.AddMinutes(5),
                LeaseExpiresAt = now.AddMinutes(1),
            });
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
            // Recreate the immediately preceding schema with retained financial data.
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261102073553_AddCaptchaValidationReceipt", token);
            await migrator.MigrateAsync(cancellationToken: token);
            var wallets = await db.AccountWallets.AsNoTracking().ToDictionaryAsync(x => x.AccountId, token);
            Assert.Equal(18800 * MembershipRules.NanoYuanPerFen, wallets[a].CumulativeRechargeNanoYuan);
            Assert.Equal(18799 * MembershipRules.NanoYuanPerFen, wallets[b].CumulativeRechargeNanoYuan);
            Assert.Equal(0, wallets[c].CumulativeRechargeNanoYuan);
            Assert.Equal(1, wallets[a].AvailableNanoYuan);
            Assert.Equal(99900 * MembershipRules.NanoYuanPerFen, wallets[c].AvailableNanoYuan);
            var retained = await db.UsageOperations.AsNoTracking().SingleAsync(x => x.Id == operation, token);
            Assert.False(retained.MembershipRecognitionFree);
            Assert.Equal(100, retained.ReservedPublicNanoYuan);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static Guid AddAccount(SnowShotDbContext db, long balance)
    {
        var id = Guid.CreateVersion7();
        db.Accounts.Add(new() { Id = id, Username = "member", NormalizedUsername = id.ToString("N"), PasswordHash = "test", Active = true, CreatedAt = DateTimeOffset.UtcNow });
        db.AccountWallets.Add(new() { AccountId = id, AvailableNanoYuan = balance });
        return id;
    }

    private static string AddOrder(SnowShotDbContext db, Guid account, int price, int paid, bool fulfilled)
    {
        var request = Guid.CreateVersion7();
        var order = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        db.AccountPurchaseRequests.Add(new()
        {
            Id = request,
            AccountId = account,
            Purpose = "top_up",
            State = fulfilled ? "fulfilled" : "payment_pending",
            GeneratedAt = now,
            ExpiresAt = now.AddMinutes(10),
            OrderId = order,
        });
        db.AccountPaymentOrders.Add(new()
        {
            Id = order,
            RequestId = request,
            AccountId = account,
            OpenId = account.ToString("N"),
            PackageId = "cny-188",
            PriceFen = price,
            PaidFen = paid,
            Fulfilled = fulfilled,
            State = fulfilled ? "paid" : "payment_pending",
            PaymentParametersEncrypted = "test",
            CreatedAt = now,
        });
        return order;
    }

    private static void AddRefund(SnowShotDbContext db, string order, int amount, bool applied, bool completed = true) =>
        db.AccountRefunds.Add(new()
        {
            Id = Guid.NewGuid().ToString("N"),
            OrderId = order,
            RefundFen = amount,
            Applied = applied,
            State = completed ? "completed" : "processing",
            PlatformRefundId = Guid.NewGuid().ToString("N"),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
}
