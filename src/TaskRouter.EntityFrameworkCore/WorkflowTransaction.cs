using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

        var dbContext = context.Database.GetService<ICurrentDbContext>().Context;

        if (context.Database.CurrentTransaction is { } ambient)
        {
            return await JoinAsync(ambient, dbContext, work, postCommit, ct).ConfigureAwait(false);
        }

        var strategy = context.Database.CreateExecutionStrategy();

        var result = await strategy.ExecuteAsync(async () =>
        {
            // Taken per attempt: a retry must start from what the context held before the
            // first attempt, not from whatever the failed attempt left tracked.
            using var checkpoint = new WorkflowTrackingCheckpoint(dbContext);
            var callbacks = postCommit?.Checkpoint();

            await using IDbContextTransaction tx =
                await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            try
            {
                var value = await work(ct).ConfigureAwait(false);

                await context.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);

                return value;
            }
            catch
            {
                await RollBackQuietlyAsync(() => tx.RollbackAsync(CancellationToken.None)).ConfigureAwait(false);

                // The rows are back as they were, so the tracked entities must be too --
                // otherwise the next operation on this context sees a completion that
                // never happened.
                checkpoint.Restore();
                Rewind(postCommit, callbacks);
                throw;
            }
        }).ConfigureAwait(false);

        // Outside the strategy: a retried transaction must not re-run side effects that
        // reach beyond the database, and these only make sense once the data is durable.
        if (postCommit is not null)
        {
            await postCommit.RunAsync(ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Already inside a transaction -- a nested engine call, or one the host started.
    /// Joins it rather than opening a second; post-commit actions belong to whoever owns
    /// the outermost transaction, so they are left for that caller to run.
    ///
    /// <para>With savepoints, a failure here undoes only this piece of work, so a caller
    /// that catches it (a LogAndContinue trigger, a host) can carry on with the rest.</para>
    ///
    /// <para>Without them (SQL Server with MultipleActiveResultSets) nothing can be undone
    /// short of the whole transaction, so this simply joins, as it always did. The tracker
    /// is deliberately not restored: the writes this attempt saved are still in the
    /// transaction, and the tracker has to keep agreeing with them. Whoever rolls the
    /// transaction back restores its own checkpoint.</para>
    /// </summary>
    private static async Task<T> JoinAsync<T>(
        IDbContextTransaction ambient,
        DbContext dbContext,
        Func<CancellationToken, Task<T>> work,
        IWorkflowPostCommitActions? postCommit,
        CancellationToken ct)
    {
        if (!ambient.SupportsSavepoints)
        {
            return await work(ct).ConfigureAwait(false);
        }

        using var checkpoint = new WorkflowTrackingCheckpoint(dbContext);
        var callbacks = postCommit?.Checkpoint();
        var savepoint = "taskrouter_" + Guid.NewGuid().ToString("N")[..16];

        await ambient.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);

        try
        {
            var value = await work(ct).ConfigureAwait(false);
            await ambient.ReleaseSavepointAsync(savepoint, ct).ConfigureAwait(false);
            return value;
        }
        catch
        {
            await RollBackQuietlyAsync(
                () => ambient.RollbackToSavepointAsync(savepoint, CancellationToken.None)).ConfigureAwait(false);

            checkpoint.Restore();
            Rewind(postCommit, callbacks);
            throw;
        }
    }

    /// <summary>
    /// A rollback that itself fails -- the connection dropped, or SQL Server already
    /// doomed the transaction -- must not replace the error that caused it; that error
    /// is the one the caller needs. The context should be discarded afterwards either way.
    /// </summary>
    private static async Task RollBackQuietlyAsync(Func<Task> rollback)
    {
        try
        {
            await rollback().ConfigureAwait(false);
        }
        catch (Exception rollbackError) when (rollbackError is not OperationCanceledException)
        {
            // Swallowed on purpose: the original exception is rethrown by the caller.
        }
    }

    private static void Rewind(IWorkflowPostCommitActions? postCommit, int? callbacks)
    {
        if (postCommit is not null && callbacks is { } position)
        {
            postCommit.Rewind(position);
        }
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
