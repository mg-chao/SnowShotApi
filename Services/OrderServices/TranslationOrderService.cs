using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SnowShotApi.Data;
using SnowShotApi.Models;
using SnowShotApi.Services.TranslationServices.AI;

namespace SnowShotApi.Services.OrderServices;

public enum TranslationOrderReservationStatus
{
    Reserved,
    QuotaExceeded,
}

public sealed record TranslationOrderReservation(
    TranslationOrderReservationStatus Status,
    UserTranslationOrder? Order = null);

public interface ITranslationOrderService
{
    Task<TranslationOrderReservation> ReserveAsync(
        long userId,
        UserTranslationType type,
        IReadOnlyList<string> content,
        string from,
        string to,
        string domain,
        CancellationToken cancellationToken);

    Task<bool> FinalizeAsync(
        long orderId,
        UserTranslationOrderStatus status,
        string? from = null,
        string? to = null,
        CancellationToken cancellationToken = default);
}

public sealed class TranslationOrderService(
    ApplicationDbContext context,
    AITranslationOptions options,
    TranslationQuotaClock clock) : ITranslationOrderService
{
    public async Task<TranslationOrderReservation> ReserveAsync(
        long userId,
        UserTranslationType type,
        IReadOnlyList<string> content,
        string from,
        string to,
        string domain,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Sum(item => item?.Length ?? 0);
        var quotaDate = clock.GetCurrentQuotaDate();
        var now = clock.UtcNow.UtcDateTime;

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var statsId = await TryReserveQuotaAsync(
            userId,
            type,
            quotaDate,
            contentLength,
            options.ContentLengthLimit,
            now,
            cancellationToken);

        if (statsId == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new TranslationOrderReservation(TranslationOrderReservationStatus.QuotaExceeded);
        }

        var order = new UserTranslationOrder
        {
            UserId = userId,
            Type = type,
            Status = UserTranslationOrderStatus.Created,
            From = from,
            To = to,
            Domain = domain,
            ContentLength = contentLength,
            QuotaDate = quotaDate,
        };

        context.UserTranslationOrders.Add(order);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TranslationOrderReservation(TranslationOrderReservationStatus.Reserved, order);
    }

    public async Task<bool> FinalizeAsync(
        long orderId,
        UserTranslationOrderStatus status,
        string? from = null,
        string? to = null,
        CancellationToken cancellationToken = default)
    {
        if (status == UserTranslationOrderStatus.Created)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "A translation order must be finalized to a terminal status.");
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var finalizedOrder = await TryTransitionOrderAsync(
            orderId,
            status,
            from,
            to,
            cancellationToken);

        if (finalizedOrder == null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (status is UserTranslationOrderStatus.Failed or UserTranslationOrderStatus.Cancelled)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "UserTranslationOrderStats"
                SET "ContentLengthSum" = GREATEST("ContentLengthSum" - {finalizedOrder.ContentLength}, 0),
                    "UpdatedAt" = {clock.UtcNow.UtcDateTime}
                WHERE "UserId" = {finalizedOrder.UserId}
                  AND "Date" = {finalizedOrder.QuotaDate}
                  AND "Type" = {(int)finalizedOrder.Type}
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<long?> TryReserveQuotaAsync(
        long userId,
        UserTranslationType type,
        int quotaDate,
        int contentLength,
        int contentLengthLimit,
        DateTime now,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO "UserTranslationOrderStats"
                ("UserId", "Date", "Type", "ContentLengthSum", "UpdatedAt")
            SELECT @userId, @quotaDate, @type, @contentLength, @now
            WHERE @contentLength <= @contentLengthLimit
            ON CONFLICT ("UserId", "Date", "Type") DO UPDATE
            SET "ContentLengthSum" = "UserTranslationOrderStats"."ContentLengthSum" + EXCLUDED."ContentLengthSum",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            WHERE "UserTranslationOrderStats"."ContentLengthSum" + EXCLUDED."ContentLengthSum" <= @contentLengthLimit
            RETURNING "Id";
            """;

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = sql;
        AddParameter(command, "userId", userId);
        AddParameter(command, "quotaDate", quotaDate);
        AddParameter(command, "type", (int)type);
        AddParameter(command, "contentLength", contentLength);
        AddParameter(command, "contentLengthLimit", contentLengthLimit);
        AddParameter(command, "now", now);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private async Task<FinalizedOrder?> TryTransitionOrderAsync(
        long orderId,
        UserTranslationOrderStatus status,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE "UserTranslationOrders"
            SET "Status" = @status,
                "From" = COALESCE(@from, "From"),
                "To" = COALESCE(@to, "To")
            WHERE "Id" = @orderId AND "Status" = @createdStatus
            RETURNING "UserId", "Type", "ContentLength", "QuotaDate";
            """;

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = sql;
        AddParameter(command, "status", (int)status);
        AddParameter(command, "from", from ?? (object)DBNull.Value);
        AddParameter(command, "to", to ?? (object)DBNull.Value);
        AddParameter(command, "orderId", orderId);
        AddParameter(command, "createdStatus", (int)UserTranslationOrderStatus.Created);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new FinalizedOrder(
            reader.GetInt64(0),
            (UserTranslationType)reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record FinalizedOrder(
        long UserId,
        UserTranslationType Type,
        int ContentLength,
        int QuotaDate);
}
