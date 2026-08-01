using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Persistence;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShot.Infrastructure.Identity;

public sealed class HmacPrincipalIdentity(
    IDbContextFactory<SnowShotDbContext> contextFactory,
    IdentityOptions options) : IPrincipalIdentity
{
    private readonly byte[] _current = options.CurrentKey;
    private readonly byte[]? _previous = options.PreviousKey;

    public async Task<AnonymousPrincipal?> ResolveAsync(string? clientAddress, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(clientAddress, out var address)) return null;
        using var activity = SnowShotTelemetry.Activities.StartActivity("identity.resolve");
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var canonical = Encoding.ASCII.GetBytes(address.ToString().ToLowerInvariant());
        var candidates = new[]
        {
            HMACSHA256.HashData(_current, canonical),
            _previous is null ? null : HMACSHA256.HashData(_previous, canonical),
        }.Where(value => value is not null).Select(value => value!).Distinct(ByteArrayComparer.Instance)
            .OrderBy(Convert.ToHexString, StringComparer.Ordinal).ToArray();

        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            foreach (var candidate in candidates)
            {
                var lockKey = BinaryPrimitives.ReadInt64BigEndian(candidate.AsSpan(0, sizeof(long)));
                await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
            }

            var aliases = new List<PrincipalFingerprintEntity>(candidates.Length);
            foreach (var candidate in candidates)
            {
                var alias = await context.PrincipalFingerprints
                    .SingleOrDefaultAsync(value => value.Fingerprint == candidate, cancellationToken);
                if (alias is not null) aliases.Add(alias);
            }
            var principalIds = aliases.Select(value => value.PrincipalId).Distinct().ToArray();
            if (principalIds.Length > 1)
            {
                SnowShotTelemetry.IdentityIntegrityConflicts.Add(1);
                throw new IdentityIntegrityException();
            }

            var now = await DatabaseNowAsync(context, cancellationToken);
            var principalId = principalIds.SingleOrDefault();
            if (principalId == Guid.Empty)
            {
                principalId = Guid.CreateVersion7(now);
                context.Principals.Add(new PrincipalEntity { Id = principalId, CreatedAt = now });
            }
            foreach (var alias in aliases) alias.LastSeenAt = now;
            foreach (var candidate in candidates.Where(candidate => aliases.All(alias => !alias.Fingerprint.SequenceEqual(candidate))))
            {
                context.PrincipalFingerprints.Add(new PrincipalFingerprintEntity
                {
                    Fingerprint = candidate,
                    PrincipalId = principalId,
                    CreatedAt = now,
                    LastSeenAt = now,
                });
            }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AnonymousPrincipal(principalId, principalId.ToString("N"));
        });
    }

    private static Task<DateTimeOffset> DatabaseNowAsync(SnowShotDbContext context, CancellationToken token) =>
        context.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public bool Equals(byte[]? left, byte[]? right) => left is not null && right is not null && left.AsSpan().SequenceEqual(right);
        public int GetHashCode(byte[] value) => BinaryPrimitives.ReadInt32BigEndian(value);
    }
}

public sealed class IdentityIntegrityException() : InvalidOperationException("Identity aliases resolve to different principals.");
