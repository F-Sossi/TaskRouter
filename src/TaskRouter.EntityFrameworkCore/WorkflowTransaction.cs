using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Runs a unit of work in a single transaction, through the provider's execution
/// strategy.
///
/// This exists because of the original engine's review finding C1: every operation there committed
/// three or more separate times (task change, then log, then triggers), so a failure
/// between commits left a workflow permanently inconsistent with no reconciliation path.
///
/// The execution-strategy wrapper is required, not optional: when the provider is
/// configured with EnableRetryOnFailure, calling BeginTransaction directly throws.
/// </summary>
public static class WorkflowTransaction
{
    public static async Task<T> ExecuteAsync<T>(
        IWorkflowDbContext context,
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct = default,
        IWorkflowPostCommitActions? postCommit = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);

        // Already inside a transaction (nested call, or the host started one):
        // join it rather than opening a second. Post-commit actions belong to whoever
        // owns the outermost transaction, so they are left for that caller to run.
        if (context.Database.CurrentTransaction is not null)
        {
            return await work(ct).ConfigureAwait(false);
        }

        var strategy = context.Database.CreateExecutionStrategy();

        var result = await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction tx =
                await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var value = await work(ct).ConfigureAwait(false);

            await context.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            return value;
        }).ConfigureAwait(false);

        // Outside the strategy: a retried transaction must not re-run side effects that
        // reach beyond the database, and these only make sense once the data is durable.
        if (postCommit is not null)
        {
            await postCommit.RunAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    public static Task ExecuteAsync(
        IWorkflowDbContext context,
        Func<CancellationToken, Task> work,
        CancellationToken ct = default,
        IWorkflowPostCommitActions? postCommit = null) =>
        ExecuteAsync(context, async token =>
        {
            await work(token).ConfigureAwait(false);
            return 0;
        }, ct, postCommit);
}
