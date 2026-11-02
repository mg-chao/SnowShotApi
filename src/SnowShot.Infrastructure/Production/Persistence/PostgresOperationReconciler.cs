using SnowShot.Application;

namespace SnowShot.Infrastructure.Persistence;

internal sealed class PostgresOperationReconciler(PostgresOperationLedger ledger) : IOperationReconciler
{
    public Task<int> ReconcileExpiredAsync(int maxOperations, CancellationToken cancellationToken) =>
        ledger.ReconcileExpiredAsync(maxOperations, cancellationToken);
}
