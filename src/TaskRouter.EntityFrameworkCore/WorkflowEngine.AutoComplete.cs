using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Terminal tasks that close themselves.
///
/// <para>A terminal task usually has nothing left to decide — nothing routes on from it — so
/// asking a person to press a button on it is a chore that leaves runs open until somebody
/// gets round to it. One marked <see cref="WorkflowTaskDefinition.CompletesAutomatically"/>
/// is completed the moment it is reached, with its first outcome, credited to whoever
/// completed the step that led to it.</para>
///
/// <para><b>Queued, not done on the spot.</b> A task is created halfway through somebody
/// else's operation — completing the step before it, usually — and completing it right there
/// would raise its events and close the run before that operation had raised its own. So the
/// funnel queues it and the transaction wrapper drains the queue once the operation's own work
/// is done, still inside the same transaction: one commit, events in the order things
/// happened.</para>
///
/// <para><b>A blocked task waits.</b> If something is blocking it when it is reached, it stays
/// open, and is queued again whenever one of its blockers ends; it closes itself once the last
/// one has. Nothing valid should block one — a terminal task cannot have a sub-workflow
/// attached — so this is for data written before that rule.</para>
/// </summary>
public sealed partial class WorkflowEngine
{
    /// <summary>
    /// Self-completing tasks reached during the current operation, with who reached them.
    /// Per operation like <see cref="_subjectsByRun"/> is per scope: the engine is scoped,
    /// and the wrapper clears this on entry.
    /// </summary>
    private readonly List<(WorkflowTask Task, string ActorId)> _completesAutomatically = [];

    /// <summary>
    /// The outcome a task closes itself with when it declares none. Matches what the runner
    /// offers in the same position, so a run finished either way records the same key.
    /// </summary>
    private const string DefaultOutcomeKey = "completed";

    private async Task CompleteAutomaticallyAsync(CancellationToken ct)
    {
        // A loop, not a foreach: closing one task can end a sub-workflow instance whose
        // parent is itself waiting to close, which queues it while this runs.
        while (_completesAutomatically.Count > 0)
        {
            var (task, actorId) = _completesAutomatically[0];
            _completesAutomatically.RemoveAt(0);

            // Something later in the same operation may already have closed it -- the run
            // was cancelled, say -- and the same task can be queued twice.
            if (task.Status is not (WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress))
            {
                continue;
            }

            if ((await BlockingTaskNamesAsync(task.Id, ct).ConfigureAwait(false)).Count > 0
                || (await BlockingInstanceNamesAsync(task.Id, ct).ConfigureAwait(false)).Count > 0)
            {
                continue;
            }

            var outcomeKey = await db.WorkflowTaskOutcomes
                .Where(o => o.TaskDefinitionId == task.TaskDefinitionId && !o.IsArchived)
                .OrderBy(o => o.Order).ThenBy(o => o.Id)
                .Select(o => o.OutcomeKey)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false)
                ?? DefaultOutcomeKey;

            await FinishAsync(task, outcomeKey, actorId, notes: null, automatically: true, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Queues a task again because something that may have been blocking it has ended, if it
    /// is one that closes itself. Whether it is still blocked is decided when the queue is
    /// drained, not here.
    /// </summary>
    private async Task RequeueIfCompletesAutomaticallyAsync(
        int? taskId, string actorId, CancellationToken ct)
    {
        if (taskId is not { } id)
        {
            return;
        }

        var task = await db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .SingleOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);

        if (task?.TaskDefinition is { IsTerminal: true, CompletesAutomatically: true })
        {
            _completesAutomatically.Add((task, actorId));
        }
    }
}
