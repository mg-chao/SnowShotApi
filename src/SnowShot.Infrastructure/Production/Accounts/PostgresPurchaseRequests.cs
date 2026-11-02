using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;
using static SnowShot.Infrastructure.Accounts.AccountErrors;

namespace SnowShot.Infrastructure.Accounts;

/// <summary>Purchase request lookup, expiry and presentation shared by account workflows.</summary>
internal sealed class PostgresPurchaseRequests(PostgresAccountStore store, IWechatAccountPlatform platform, TimeProvider clock)
{
    internal async Task CheckProofAsync(Guid id, string proof, CancellationToken token)
    {
        if (proof.Length is < 32 or > 256) throw new AccountException("invalid_registration_proof", "The registration proof is invalid.", 401);
        await using var db = await store.OpenAsync(token);
        await CheckProofInTransactionAsync(db, id, proof, token);
    }
    internal static async Task CheckProofInTransactionAsync(SnowShotDbContext db, Guid id, string proof, CancellationToken token)
    {
        var request = await db.AccountPurchaseRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Purpose == "registration", token);
        if (request?.ProofHash is null || !CryptographicOperations.FixedTimeEquals(request.ProofHash, AccountSecrets.Hash(proof)))
            throw new AccountException("invalid_registration_proof", "The registration proof is invalid.", 401);
    }
    internal async Task ExpireDueRequestsAsync(Guid? requestId, CancellationToken token)
    {
        await using var read = await store.OpenAsync(token);
        var now = clock.GetUtcNow();
        if (!await read.AccountPurchaseRequests.AnyAsync(x => (requestId == null || x.Id == requestId) && x.State == "awaiting_payment" && x.ExpiresAt <= now, token)) return;
        await store.ExecuteAsync(async (db, databaseNow) =>
        {
            await AccountSynchronization.ChangeIdentityAsync(db, token);
            await ExpireRequestsAsync(db, databaseNow, token, requestId);
            return true;
        }, token);
    }

    internal static async Task ExpireRequestsAsync(SnowShotDbContext db, DateTimeOffset now, CancellationToken token, Guid? requestId = null)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM snowshot.account_username_claims AS claim USING snowshot.account_purchase_requests AS request
            WHERE claim."RequestId"=request."Id" AND request."State"='awaiting_payment' AND request."ExpiresAt"<={now}
              AND ({requestId}::uuid IS NULL OR request."Id"={requestId});
            DELETE FROM snowshot.account_payer_claims AS claim USING snowshot.account_purchase_requests AS request
            WHERE claim."RequestId"=request."Id" AND request."State"='awaiting_payment' AND request."ExpiresAt"<={now}
              AND ({requestId}::uuid IS NULL OR request."Id"={requestId});
            UPDATE snowshot.account_purchase_requests SET "State"='expired' WHERE "State"='awaiting_payment' AND "ExpiresAt"<={now}
              AND ({requestId}::uuid IS NULL OR "Id"={requestId})
            """, token);
    }
    internal AccountPurchaseRequest View(AccountPurchaseRequestEntity request, string username, DateTimeOffset now, bool image = false, string? proof = null) =>
        new(request.Id, VisibleUsername(request, username), request.Purpose, request.State, request.GeneratedAt, request.ExpiresAt, now,
            image && request.CodeImage is not null ? (request.CodeImage[0] == 255 ? "data:image/jpeg;base64," : "data:image/png;base64,") + Convert.ToBase64String(request.CodeImage) : null, request.OrderId, platform.PaymentsAvailable, proof, CompletionKind(request));
    internal void RequirePayments() { if (!platform.PaymentsAvailable) throw new AccountException("payments_unavailable", "Membership purchasing is temporarily unavailable.", 503); }
    internal static async Task<string> UnusedUsernameAsync(SnowShotDbContext db, string prefix, CancellationToken token)
    {
        while (true)
        {
            var username = prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            var normalized = AccountRules.NormalizeUsername(username);
            if (!await db.AccountUsernameClaims.AnyAsync(x => x.NormalizedUsername == normalized, token)) return username;
        }
    }

    internal PublicPurchaseRequest PublicView(AccountPurchaseRequestEntity request, string username, DateTimeOffset now, bool owns) =>
        new(request.Id, VisibleUsername(request, username), request.Purpose, request.State, request.ExpiresAt, now,
            platform.PaymentsAvailable, owns ? request.OrderId : null, CompletionKind(request));

    internal static string? VisibleUsername(AccountPurchaseRequestEntity request, string username) =>
        request.Purpose == "mini_registration" && request.FulfilledAt is null ? null : username;

    internal static string? CompletionKind(AccountPurchaseRequestEntity request) =>
        request.CompletionKind ?? (request.State == "fulfilled" && request.OrderId is not null ? "payment" : null);
}
