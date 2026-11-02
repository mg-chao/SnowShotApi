using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;
using static SnowShot.Infrastructure.Accounts.AccountErrors;
using static SnowShot.Infrastructure.Accounts.AccountSerialization;
using static SnowShot.Infrastructure.Accounts.PostgresAccountStore;

namespace SnowShot.Infrastructure.Accounts;

internal sealed class PostgresAccountSessions(PostgresAccountStore store, IWechatAccountPlatform platform,
    AccountSecrets secrets, TimeProvider clock, PasswordHasher<AccountEntity> passwords,
    PostgresPurchaseRequests requests) : IAccountSessionResolver
{
    private readonly PasswordHasher<AccountEntity> _passwords = passwords;
    private readonly string _dummyPasswordHash = passwords.HashPassword(null!, AccountSecrets.Token());

    public async Task<AccountSessionIdentity?> ResolveAsync(string bearerToken, CancellationToken cancellationToken)
    {
        if (bearerToken.Length is < 32 or > 256) return null;
        var hash = AccountSecrets.Hash(bearerToken);
        await using var db = await store.OpenAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var session = await db.AccountSessions.AsNoTracking().SingleOrDefaultAsync(x =>
            x.AccessHash == hash && x.RevokedAt == null && x.AccessExpiresAt > now, cancellationToken);
        if (session is null) return null;
        var accountId = session.AccountId;
        if (session.OpenId is not null)
            accountId = await db.AccountWechatLinks.Where(x => x.OpenId == session.OpenId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(cancellationToken);
        if (accountId is not null && !await db.Accounts.AnyAsync(x => x.Id == accountId && x.Active && x.DeletedAt == null, cancellationToken)) return null;
        return new(session.Id, accountId, session.OpenId);
    }

    public async Task<AccountCaptcha> CaptchaAsync(string clientKey, CancellationToken token)
    {
        await store.ThrottleAsync("captcha:" + clientKey, 20, token);
        var image = CaptchaImage.Create();
        var id = Guid.CreateVersion7();
        return await store.ExecuteAsync(async (db, now) =>
        {
            var existing = await db.AccountCaptchas.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (existing is not null) return new AccountCaptcha(id, "data:image/png;base64," + Convert.ToBase64String(image.Png), existing.ExpiresAt);
            var expiry = now.AddMinutes(5);
            db.AccountCaptchas.Add(new() { Id = id, AnswerHash = CaptchaHash(id, image.Answer), ClientKey = secrets.Fingerprint(clientKey), ExpiresAt = expiry });
            return new AccountCaptcha(id, "data:image/png;base64," + Convert.ToBase64String(image.Png), expiry);
        }, token);
    }

    public async Task<AccountSessionTokens> LoginAsync(string username, string password, string clientKey, CancellationToken token)
    {
        await store.ThrottleAsync("login:" + clientKey, 15, token);
        if (!AccountRules.IsUsername(username) || !AccountRules.IsPassword(password)) throw Credentials();
        var normalized = AccountRules.NormalizeUsername(username);
        await using var db = await store.OpenAsync(token);
        var account = await (from claim in db.AccountUsernameClaims
                             join value in db.Accounts on claim.AccountId equals value.Id
                             where claim.NormalizedUsername == normalized
                             select value).SingleOrDefaultAsync(token);
        // Always perform a password derivation, including an unknown username.
        var candidate = account is { IsTemporary: false, PasswordHash.Length: > 0 } ? account
            : new AccountEntity { Id = Guid.Empty, Username = username, NormalizedUsername = normalized, PasswordHash = _dummyPasswordHash };
        var verified = _passwords.VerifyHashedPassword(candidate, candidate.PasswordHash, password);
        if (verified == PasswordVerificationResult.Failed) throw Credentials();
        if (account is null || account.IsTemporary || account.PasswordHash.Length == 0) throw Credentials();
        if (!account.Active) throw new AccountException("account_inactive", "Complete the first payment before signing in.", 403);
        var replacementHash = verified == PasswordVerificationResult.SuccessRehashNeeded ? _passwords.HashPassword(account, password) : null;
        return await store.ExecuteAsync(async (context, now) =>
        {
            if (replacementHash is not null) await AccountSynchronization.ChangeIdentityAsync(context, token);
            else await AccountSynchronization.ReadIdentityAsync(context, token);
            await AccountSynchronization.AccountAsync(context, account.Id, token);
            var current = await context.Accounts.SingleOrDefaultAsync(x => x.Id == account.Id && x.Active && x.DeletedAt == null, token) ?? throw InvalidSession();
            if (current.PasswordHash != account.PasswordHash) throw Credentials();
            if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            {
                await EraseOrphanedRegistrationCredentialsAsync(context, current, now, "credential-copy-cleanup-v1", token);
                current.PasswordHash = replacementHash!;
            }
            return CreateSession(context, now, current.Id, null, null).Tokens;
        }, token);
    }

    public async Task<AccountSessionTokens> WechatLoginAsync(string code, string clientKey, CancellationToken token)
    {
        await store.ThrottleAsync("wechat:" + clientKey, 30, token);
        var identity = await platform.LoginAsync(code, token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            var accountId = await db.AccountWechatLinks.Where(x => x.OpenId == identity.OpenId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
            if (accountId is { } id && !await db.Accounts.AnyAsync(x => x.Id == id && x.Active && x.DeletedAt == null, token)) throw InvalidSession();
            return CreateSession(db, now, accountId, identity.OpenId, identity.SessionKey).Tokens;
        }, token);
    }

    public async Task<AccountSessionTokens> RefreshSessionAsync(string refreshToken, CancellationToken token)
    {
        if (refreshToken.Length is < 32 or > 256) throw InvalidSession();
        var hash = AccountSecrets.Hash(refreshToken);
        Guid? candidateSessionId = null;
        AccountSessionTokens? candidateTokens = null;
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            if (candidateSessionId is { } candidateId && candidateTokens is { } candidate)
            {
                var committed = await db.AccountSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidateId, token);
                if (committed is not null && committed.RevokedAt is null && committed.RefreshExpiresAt > now &&
                    CryptographicOperations.FixedTimeEquals(committed.AccessHash, AccountSecrets.Hash(candidate.AccessToken)) &&
                    CryptographicOperations.FixedTimeEquals(committed.RefreshHash, AccountSecrets.Hash(candidate.RefreshToken))) return candidate;
            }
            var session = await db.AccountSessions.FromSqlInterpolated($"SELECT * FROM snowshot.account_sessions WHERE \"RefreshHash\"={hash} FOR UPDATE").SingleOrDefaultAsync(token);
            if (session is null || session.RevokedAt is not null || session.RefreshExpiresAt <= now) throw InvalidSession();
            if (session.OpenId is not null)
                session.AccountId = await db.AccountWechatLinks.Where(x => x.OpenId == session.OpenId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
            if (session.AccountId is { } id && !await db.Accounts.AnyAsync(x => x.Id == id && x.Active && x.DeletedAt == null, token)) throw InvalidSession();
            candidateSessionId = session.Id;
            candidateTokens = Rotate(session, now);
            return candidateTokens;
        }, token);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken token)
    {
        if (refreshToken.Length is < 32 or > 256) return;
        var hash = AccountSecrets.Hash(refreshToken);
        await using var db = await store.OpenAsync(token);
        await db.AccountSessions.Where(x => x.RefreshHash == hash && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, clock.GetUtcNow()), token);
    }
    public async Task<AccountSessionTokens> ClaimAsync(Guid requestId, string proof, CancellationToken token)
    {
        await requests.CheckProofAsync(requestId, proof, token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            await PostgresPurchaseRequests.CheckProofInTransactionAsync(db, requestId, proof, token);
            var request = await db.AccountPurchaseRequests.FromSqlInterpolated($"SELECT * FROM snowshot.account_purchase_requests WHERE \"Id\"={requestId} FOR UPDATE").SingleAsync(token);
            if (!await db.Accounts.AnyAsync(x => x.Id == request.AccountId && x.Active && x.DeletedAt == null, token)) throw InvalidSession();
            if (request.State != "fulfilled" || request.FulfilledAt is null) throw new AccountException("payment_not_received", "Payment has not yet been confirmed.", 409);
            if (request.FulfilledAt.Value.AddDays(30) <= now) throw new AccountException("invalid_registration_proof", "Sign in with your username and password.", 401);
            AccountSessionTokens result;
            if (request.BootstrapSessionId is null)
            {
                var created = CreateSession(db, now, request.AccountId, null, null);
                request.BootstrapSessionId = created.Session.Id; result = created.Tokens;
            }
            else
            {
                var session = await db.AccountSessions.FromSqlInterpolated($"SELECT * FROM snowshot.account_sessions WHERE \"Id\"={request.BootstrapSessionId} FOR UPDATE").SingleAsync(token);
                if (session.RevokedAt is not null) throw InvalidSession();
                result = request.BootstrapTokensEncrypted is null ? Rotate(session, now)
                    : JsonSerializer.Deserialize<AccountSessionTokens>(secrets.Unprotect(request.BootstrapTokensEncrypted), Json)!;
                if (result.ExpiresAt <= now || session.AccessExpiresAt <= now || !CryptographicOperations.FixedTimeEquals(session.AccessHash, AccountSecrets.Hash(result.AccessToken))) result = Rotate(session, now);
            }
            request.BootstrapTokensEncrypted = secrets.Protect(JsonSerializer.Serialize(result, Json));
            return result;
        }, token);
    }
    private (AccountSessionEntity Session, AccountSessionTokens Tokens) CreateSession(SnowShotDbContext db, DateTimeOffset now,
        Guid? accountId, string? openId, string? sessionKey)
    {
        var session = new AccountSessionEntity
        {
            Id = Guid.CreateVersion7(now),
            AccountId = accountId,
            OpenId = openId,
            AccessHash = [],
            RefreshHash = [],
            CreatedAt = now,
            WechatSessionKeyEncrypted = sessionKey is null ? null : secrets.Protect(sessionKey)
        };
        var tokens = Rotate(session, now); db.AccountSessions.Add(session); return (session, tokens);
    }
    private static AccountSessionTokens Rotate(AccountSessionEntity session, DateTimeOffset now)
    {
        var access = AccountSecrets.Token(); var refresh = AccountSecrets.Token();
        session.AccessHash = AccountSecrets.Hash(access); session.RefreshHash = AccountSecrets.Hash(refresh);
        session.AccessExpiresAt = now.AddMinutes(15); session.RefreshExpiresAt = now.AddDays(30);
        return new(access, refresh, session.AccessExpiresAt, session.RefreshExpiresAt, session.AccountId);
    }
    internal static byte[] CaptchaHash(Guid id, string answer) => AccountSecrets.Hash(id.ToString("N") + ":" + answer.Trim().ToUpperInvariant());
}
