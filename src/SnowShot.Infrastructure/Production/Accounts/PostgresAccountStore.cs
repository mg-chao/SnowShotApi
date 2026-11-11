using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Infrastructure.Persistence;
using static SnowShot.Infrastructure.Accounts.AccountErrors;

namespace SnowShot.Infrastructure.Accounts;

/// <summary>Shared database mechanics; workflows own their transaction and lock boundaries.</summary>
internal sealed class PostgresAccountStore(IDbContextFactory<SnowShotDbContext> factory, AccountSecrets secrets)
{
    internal Task<SnowShotDbContext> OpenAsync(CancellationToken token) => factory.CreateDbContextAsync(token);

    internal Task<T> ExecuteAsync<T>(Func<SnowShotDbContext, DateTimeOffset, Task<T>> action, CancellationToken token) =>
        ExecuteAsync(action, changeIdentity: false, token);

    internal Task<T> ExecuteIdentityChangeAsync<T>(Func<SnowShotDbContext, DateTimeOffset, Task<T>> action, CancellationToken token) =>
        ExecuteAsync(action, changeIdentity: true, token);

    private async Task<T> ExecuteAsync<T>(Func<SnowShotDbContext, DateTimeOffset, Task<T>> action, bool changeIdentity, CancellationToken token)
    {
        await using var strategyDb = await factory.CreateDbContextAsync(token);
        return await strategyDb.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            if (changeIdentity) await AccountSynchronization.ChangeIdentityAsync(db, token);
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
            var result = await action(db, now); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return result;
        });
    }

    internal async Task ThrottleAsync(string key, int maximum, CancellationToken token)
    {
        var id = secrets.Fingerprint(key);
        await using var db = await factory.CreateDbContextAsync(token);
        var counts = await db.Database.SqlQuery<int>($"""
            INSERT INTO snowshot.account_throttles ("Id","PeriodStart","Count") VALUES ({id},date_trunc('minute',clock_timestamp()),1)
            ON CONFLICT ("Id") DO UPDATE SET "Count"=CASE WHEN account_throttles."PeriodStart"=date_trunc('minute',clock_timestamp()) THEN account_throttles."Count"+1 ELSE 1 END,
            "PeriodStart"=date_trunc('minute',clock_timestamp()) RETURNING "Count" AS "Value"
            """).ToListAsync(token);
        var count = counts.Single();
        if (count > maximum) throw new AccountException("rate_limited", "Too many attempts. Wait a minute before trying again.", 429);
    }

    internal static async Task<AccountWalletEntity> WalletAsync(SnowShotDbContext db, Guid accountId, CancellationToken token) =>
        await db.AccountWallets.FromSqlInterpolated($"SELECT * FROM snowshot.account_wallets WHERE \"AccountId\"={accountId} FOR UPDATE").SingleAsync(token);

    internal static async Task RequireLiveSessionAsync(SnowShotDbContext db, AccountSessionIdentity identity,
        DateTimeOffset now, CancellationToken token, bool requireWechat = false)
    {
        if (requireWechat && identity.OpenId is null) throw InvalidSession();
        if (!await db.AccountSessions.AnyAsync(x => x.Id == identity.SessionId && x.RevokedAt == null &&
            x.AccessExpiresAt > now && x.OpenId == identity.OpenId, token)) throw InvalidSession();
        if (identity.AccountId is { } id && !await db.Accounts.AnyAsync(x => x.Id == id && x.Active && x.DeletedAt == null, token))
            throw InvalidSession();
    }

    internal static Task<int> EraseOrphanedRegistrationCredentialsAsync(SnowShotDbContext db, AccountEntity account,
        DateTimeOffset erasedAt, string reason, CancellationToken token)
    {
        var erasedName = "deleted_" + Guid.NewGuid().ToString("N")[..24];
        return db.Accounts.Where(x => x.Id != account.Id && !x.Active && x.DeletedAt == null &&
            x.NormalizedUsername == account.NormalizedUsername && x.PasswordHash == account.PasswordHash &&
            !db.AccountPurchaseRequests.Any(r => r.AccountId == x.Id) && !db.AccountUsernameClaims.Any(c => c.AccountId == x.Id) &&
            !db.AccountWechatLinks.Any(l => l.AccountId == x.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Username, erasedName).SetProperty(x => x.NormalizedUsername, erasedName.ToUpperInvariant())
                .SetProperty(x => x.PasswordHash, string.Empty).SetProperty(x => x.DeletedAt, erasedAt)
                .SetProperty(x => x.DeletionConsentVersion, reason), token);
    }
}
