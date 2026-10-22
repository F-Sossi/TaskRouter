using Microsoft.EntityFrameworkCore;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Makes concurrent operations on one run (or one workflow definition) take turns.
///
/// <para>Each method writes a column to itself. The value does not change, but the update
/// still takes the row's exclusive lock, held until the transaction ends -- so a second
/// operation on the same run waits here, then reads what the first committed. Without it,
/// two requests could both read a task as open and both complete it, or two branches could
/// both see the other still open and neither converge. Under read-committed snapshot a plain
/// read never blocks, which is why this has to be a write.</para>
///
/// <para>Must be called inside a transaction, before the reads it protects; outside one
/// the lock is released as soon as the statement ends.</para>
/// </summary>
internal static class WorkflowSerialization
{
    public static Task<int> RunAsync(IWorkflowDbContext db, int runId, CancellationToken ct) =>
        db.WorkflowRuns
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.Modified, run => run.Modified), ct);

    /// <summary>Locks the run a task belongs to. Does nothing for a task that does not exist.</summary>
    public static async Task TaskAsync(IWorkflowDbContext db, int taskId, CancellationToken ct)
    {
        var runId = await db.WorkflowTasks
            .Where(task => task.Id == taskId)
            .Select(task => (int?)task.WorkflowRunId)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);

        if (runId is { } id)
        {
            await RunAsync(db, id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Serializes publishing, so exactly one version of a definition ends up latest.</summary>
    public static Task<int> DefinitionAsync(IWorkflowDbContext db, int definitionId, CancellationToken ct) =>
        db.WorkflowDefinitions
            .Where(definition => definition.Id == definitionId)
            .ExecuteUpdateAsync(update => update.SetProperty(definition => definition.Modified, definition => definition.Modified), ct);
}
