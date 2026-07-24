using System.Text.Json.Serialization;
using SnowShotApi.Models;
using SnowShotApi.Services.OrderServices;

namespace SnowShotApi.Services.TranslationServices;

public sealed class TranslationContent(string content)
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = content;
}

public sealed class TranslateResult(List<TranslationContent> results, string from, string to)
{
    public List<TranslationContent> Results { get; set; } = results;
    public string From { get; set; } = from;
    public string To { get; set; } = to;
}

public sealed record TranslationCommand(
    UserTranslationType Type,
    IReadOnlyList<string> Content,
    string From,
    string To,
    string Domain);

public enum TranslationOutcomeStatus
{
    Success,
    QuotaExceeded,
    Failed,
    Cancelled,
}

public sealed record TranslationOutcome(
    TranslationOutcomeStatus Status,
    TranslateResult? Result = null)
{
    public static TranslationOutcome Success(TranslateResult result) =>
        new(TranslationOutcomeStatus.Success, result);

    public static TranslationOutcome QuotaExceeded() =>
        new(TranslationOutcomeStatus.QuotaExceeded);

    public static TranslationOutcome Failed() => new(TranslationOutcomeStatus.Failed);

    public static TranslationOutcome Cancelled() => new(TranslationOutcomeStatus.Cancelled);
}

public interface ITranslationService
{
    Task<TranslationOutcome> TranslateAsync(
        TranslationCommand command,
        long userId,
        CancellationToken cancellationToken);
}

public sealed class TranslationService(
    ITranslationOrderService translationOrderService,
    IAITranslationService aiTranslationService,
    ILogger<TranslationService> logger) : ITranslationService
{
    public async Task<TranslationOutcome> TranslateAsync(
        TranslationCommand command,
        long userId,
        CancellationToken cancellationToken)
    {
        if (command.Type != UserTranslationType.AI)
        {
            return TranslationOutcome.Failed();
        }

        var reservation = await translationOrderService.ReserveAsync(
            userId,
            command.Type,
            command.Content,
            command.From,
            command.To,
            command.Domain,
            cancellationToken);

        if (reservation.Status == TranslationOrderReservationStatus.QuotaExceeded)
        {
            return TranslationOutcome.QuotaExceeded();
        }

        var order = reservation.Order!;
        try
        {
            var batchResult = await aiTranslationService.TranslateAsync(
                new AITranslationRequest(
                    command.Content,
                    command.From,
                    command.To,
                    command.Domain,
                    order.Id),
                cancellationToken);

            return batchResult.Status switch
            {
                AITranslationBatchStatus.Success => await CompleteAsync(order.Id, command, batchResult.Translations!),
                AITranslationBatchStatus.Cancelled => await FailAsync(
                    order.Id,
                    UserTranslationOrderStatus.Cancelled,
                    TranslationOutcome.Cancelled()),
                _ => await FailAsync(
                    order.Id,
                    UserTranslationOrderStatus.Failed,
                    TranslationOutcome.Failed()),
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Translation order {OrderId} failed unexpectedly", order.Id);
            return await FailAsync(
                order.Id,
                UserTranslationOrderStatus.Failed,
                TranslationOutcome.Failed());
        }
    }

    private async Task<TranslationOutcome> CompleteAsync(
        long orderId,
        TranslationCommand command,
        IReadOnlyList<string> translations)
    {
        using var finalizationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var finalized = await translationOrderService.FinalizeAsync(
            orderId,
            UserTranslationOrderStatus.Completed,
            command.From,
            command.To,
            finalizationCancellation.Token);

        if (!finalized)
        {
            logger.LogError("Translation order {OrderId} could not be finalized as completed", orderId);
            return TranslationOutcome.Failed();
        }

        var results = translations.Select(translation => new TranslationContent(translation)).ToList();
        return TranslationOutcome.Success(new TranslateResult(results, command.From, command.To));
    }

    private async Task<TranslationOutcome> FailAsync(
        long orderId,
        UserTranslationOrderStatus status,
        TranslationOutcome outcome)
    {
        try
        {
            using var finalizationCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await translationOrderService.FinalizeAsync(
                orderId,
                status,
                cancellationToken: finalizationCancellation.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Translation order {OrderId} could not be finalized as {Status}",
                orderId,
                status);
        }

        return outcome;
    }
}
