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
        var proofHash = await db.AccountPurchaseRequests.Where(x => x.Id == id && x.Purpose == "registration")
            .Select(x => x.ProofHash).SingleOrDefaultAsync(token);
        if (proofHash is null || !CryptographicOperations.FixedTimeEquals(proofHash, AccountSecrets.Hash(proof)))
            throw new AccountException("invalid_registration_proof", "The registration proof is invalid.", 401);
    }
    internal async Task ExpireDueRequestsAsync(Guid? requestId, CancellationToken token)
    {
        await using var read = await store.OpenAsync(token);
        var now = clock.GetUtcNow();
        if (!await DueRequests(read, now).AnyAsync(x => requestId == null || x.Id == requestId, token)) return;
        await store.ExecuteIdentityChangeAsync(async (db, _) =>
        {
            await ExpireRequestsAsync(db, token, requestId);
            return true;
        }, token);
    }

    internal static IQueryable<AccountPurchaseRequestEntity> DueRequests(SnowShotDbContext db, DateTimeOffset now) =>
        db.AccountPurchaseRequests.Where(x => x.ExpiresAt <= now && (x.State == "awaiting_payment" ||
            (x.Purpose == "registration" && x.OrderId != null && x.FulfilledAt == null &&
             (x.State == "payment_pending" || x.State == "reconciliation_required" || x.State == "failed" ||
              (x.State == "expired" && db.AccountPayerClaims.Any(claim => claim.RequestId == x.Id))))));

    internal static async Task ExpireRequestsAsync(SnowShotDbContext db, CancellationToken token, Guid? requestId = null,
        string? payerOpenId = null)
    {
        // The order retains its original payer. The expired code no longer reserves that payer.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                UPDATE snowshot.account_purchase_requests
                SET "State"='expired', "CodeImage"=NULL
                WHERE "ExpiresAt"<=statement_timestamp()
                  AND (({requestId}::uuid IS NULL AND {payerOpenId}::text IS NULL) OR "Id"={requestId}
                       OR EXISTS (SELECT 1 FROM snowshot.account_payer_claims AS payer
                                  WHERE payer."OpenId"={payerOpenId} AND payer."RequestId"=account_purchase_requests."Id"))
                  AND ("State"='awaiting_payment' OR ("Purpose"='registration' AND "OrderId" IS NOT NULL AND "FulfilledAt" IS NULL
                       AND ("State" IN ('payment_pending', 'reconciliation_required', 'failed') OR
                            ("State"='expired' AND EXISTS (SELECT 1 FROM snowshot.account_payer_claims AS claim
                                                           WHERE claim."RequestId"=account_purchase_requests."Id")))))
                RETURNING "Id"
            ), released_payers AS (
                DELETE FROM snowshot.account_payer_claims AS claim USING expired
                WHERE claim."RequestId"=expired."Id"
            )
            DELETE FROM snowshot.account_username_claims AS claim USING expired WHERE claim."RequestId"=expired."Id"
            """, token);
    }
    internal static IQueryable<AccountPurchaseRequestEntity> StatusFields(SnowShotDbContext db) =>
        db.AccountPurchaseRequests.AsNoTracking().Select(request => new AccountPurchaseRequestEntity
        {
            Id = request.Id,
            AccountId = request.AccountId,
            Purpose = request.Purpose,
            State = request.State,
            PayerOpenId = request.PayerOpenId,
            CompletionKind = request.CompletionKind,
            GeneratedAt = request.GeneratedAt,
            ExpiresAt = request.ExpiresAt,
            OrderId = request.OrderId,
            FulfilledAt = request.FulfilledAt,
        });

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
        (request.Purpose == "mini_registration" && request.FulfilledAt is null) || AccountPurchaseLifecycle.IsExpiredRegistration(request)
            ? null : username;

    internal static string? CompletionKind(AccountPurchaseRequestEntity request) =>
        request.CompletionKind ?? (request.State == "fulfilled" && request.OrderId is not null ? "payment" : null);
}
