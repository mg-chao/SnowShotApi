using Microsoft.EntityFrameworkCore;
using SnowShotApi.Data;
using SnowShotApi.Models;
using SnowShotApi.Services.OrderServices;

namespace SnowShotApi.Services.TranslationServices.AI;

public sealed class TranslationReservationCleanupService(
    IServiceScopeFactory scopeFactory,
    AITranslationOptions options,
    TranslationQuotaClock clock,
    ILogger<TranslationReservationCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.ReservationCleanupInterval, TimeProvider.System);

        do
        {
            await CleanupAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var orderService = scope.ServiceProvider.GetRequiredService<ITranslationOrderService>();
            var staleBefore = clock.UtcNow
                .Subtract(options.BatchTimeout)
                .Subtract(options.StaleReservationGrace)
                .UtcDateTime;

            var staleOrderIds = await context.UserTranslationOrders
                .AsNoTracking()
                .Where(order =>
                    order.Status == UserTranslationOrderStatus.Created &&
                    order.CreatedAt < staleBefore)
                .OrderBy(order => order.Id)
                .Select(order => order.Id)
                .Take(100)
                .ToListAsync(cancellationToken);

            foreach (var orderId in staleOrderIds)
            {
                if (await orderService.FinalizeAsync(
                        orderId,
                        UserTranslationOrderStatus.Failed,
                        cancellationToken: cancellationToken))
                {
                    logger.LogWarning(
                        "Released stale AI translation quota reservation for order {OrderId}",
                        orderId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to clean up stale AI translation quota reservations");
        }
    }
}
