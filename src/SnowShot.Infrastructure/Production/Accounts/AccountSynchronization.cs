using Microsoft.EntityFrameworkCore;
using SnowShot.Infrastructure.Persistence;

namespace SnowShot.Infrastructure.Accounts;

/// <summary>
/// Identity changes coordinate username claims, WeChat links, expiration and deletion.
/// Ordinary operations share that gate and serialize only their mutable account/session rows.
/// Always acquire the identity gate before account, evidence and wallet locks.
/// </summary>
internal static class AccountSynchronization
{
    private const string IdentityGate = "account-purchases";

    internal static Task<int> ReadIdentityAsync(SnowShotDbContext db, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock_shared(hashtextextended({IdentityGate},0))", token);

    internal static Task<int> ChangeIdentityAsync(SnowShotDbContext db, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({IdentityGate},0))", token);

    internal static Task<int> AccountAsync(SnowShotDbContext db, Guid id, CancellationToken token) =>
        KeyAsync(db, "account:" + id.ToString("N"), token);

    internal static Task<int> EvidenceAsync(SnowShotDbContext db, string kind, string id, CancellationToken token) =>
        KeyAsync(db, "account-evidence:" + kind + ":" + id, token);

    private static Task<int> KeyAsync(SnowShotDbContext db, string key, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", token);
}
