using Microsoft.EntityFrameworkCore;
using SnowShot.Infrastructure.Persistence;

namespace SnowShot.Infrastructure.Accounts;

internal enum AccountRetryKind { Delivery, Refund }
internal sealed record AccountRetryLease(AccountRetryKind Kind, string Id, Guid Token, int Attempt);

/// <summary>
/// Work eligibility belongs to the financial row. Claims advance its durable retry
/// schedule before network I/O; expired leases recover crashes and tokens fence stale owners.
/// </summary>
internal sealed class PostgresAccountRetryQueue(IDbContextFactory<SnowShotDbContext> factory)
{
    internal async Task<AccountRetryLease?> ClaimAsync(AccountRetryKind kind, string? id, CancellationToken token)
    {
        var columns = Columns(kind);
        var leaseToken = Guid.NewGuid();
        // Identifiers and predicates come only from the closed definitions below.
        var sql = $$"""
            WITH candidate AS (
                SELECT "Id" FROM snowshot.{{columns.Table}}
                WHERE {{columns.Eligible}}
                  AND ({0}::text IS NULL OR "Id"={0})
                  AND ("{{columns.Next}}" IS NULL OR "{{columns.Next}}"<=clock_timestamp())
                  AND ("{{columns.Expires}}" IS NULL OR "{{columns.Expires}}"<=clock_timestamp())
                ORDER BY "{{columns.Next}}" NULLS FIRST,"{{columns.Created}}","Id"
                FOR UPDATE SKIP LOCKED LIMIT 1)
            UPDATE snowshot.{{columns.Table}} AS work
            SET "{{columns.Token}}"={1},"{{columns.Expires}}"=clock_timestamp()+interval '2 minutes',
                "{{columns.Next}}"=clock_timestamp()+interval '2 minutes',
                "{{columns.Attempts}}"=LEAST(work."{{columns.Attempts}}",999999)+1
            FROM candidate WHERE work."Id"=candidate."Id"
            RETURNING work."Id",work."{{columns.Attempts}}" AS "Attempt"
            """;
        await using var db = await factory.CreateDbContextAsync(token);
        var claims = await db.Database.SqlQueryRaw<Claim>(sql,
            new Npgsql.NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)id ?? DBNull.Value },
            leaseToken).ToListAsync(token);
        var claim = claims.SingleOrDefault();
        return claim is null ? null : new(kind, claim.Id, leaseToken, claim.Attempt);
    }

    internal async Task ReleaseAsync(AccountRetryLease lease, CancellationToken token)
    {
        var columns = Columns(lease.Kind);
        // A platform operation can succeed while still being pending. Give it the
        // same bounded backoff as unavailable results; later work remains eligible.
        var delaySeconds = Math.Min(300, 15 * (1 << Math.Min(lease.Attempt - 1, 5)));
        var sql = $$"""
            UPDATE snowshot.{{columns.Table}}
            SET "{{columns.Next}}"=clock_timestamp()+{0}*interval '1 second',
                "{{columns.Token}}"=NULL,"{{columns.Expires}}"=NULL
            WHERE "Id"={1} AND "{{columns.Token}}"={2}
            """;
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.ExecuteSqlRawAsync(sql, [delaySeconds, lease.Id, lease.Token], token);
    }

    private static WorkColumns Columns(AccountRetryKind kind) => kind switch
    {
        AccountRetryKind.Delivery => new("account_payment_orders", "NextDeliveryAttemptAt", "DeliveryLeaseToken", "DeliveryLeaseExpiresAt", "DeliveryAttempts", "CreatedAt", "\"Fulfilled\" AND NOT \"DeliveryConfirmed\""),
        AccountRetryKind.Refund => new("account_refunds", "NextQueryAt", "QueryLeaseToken", "QueryLeaseExpiresAt", "QueryAttempts", "UpdatedAt", "\"State\"='processing'"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    private sealed record WorkColumns(string Table, string Next, string Token, string Expires, string Attempts, string Created, string Eligible);
    private sealed class Claim
    {
        public required string Id { get; init; }
        public int Attempt { get; init; }
    }
}
