using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>A step somebody has been named for, on a run that has not reached it yet.</summary>
public sealed record PreAssignmentSnapshot(
    int WorkflowRunId,
    int TaskDefinitionId,
    string Label,
    string ActorId,
    string? BranchKey);

/// <summary>
/// A step somebody is promised, with enough to find it.
/// </summary>
/// <param name="Subject">
/// What the run is about, so a host can label and link the row the way it labels an inbox
/// row — the engine's subject is opaque and means nothing to a person on its own.
/// </param>
public sealed record UpcomingTaskSnapshot(
    int WorkflowRunId,
    WorkflowSubject Subject,
    string WorkflowName,
    int TaskDefinitionId,
    string Label,
    string? BranchKey);

/// <summary>
/// Naming who will handle a step before the step exists.
///
/// <para><b>Why this is in the engine and not in a host.</b> The obvious implementation is a
/// host table consulted from <c>IWorkflowAssignmentResolver</c>, and it cannot work: the
/// resolver is not called at all for a step with no assignment role, and when it is called it
/// is handed the task that just <i>completed</i> rather than the one being created — so it
/// cannot tell which step it is being asked about. Two steps sharing a role key are
/// indistinguishable to it. The task being assigned does not exist yet, which is the whole
/// difficulty: pre-assignment has to be a stored intention consulted at creation time.</para>
/// </summary>
public sealed partial class WorkflowEngine
{
    /// <summary>
    /// Names who will handle a step of this run. Replaces any previous answer for that step.
    ///
    /// <para>Beats the step's assignment role when the task is created, and <b>stands until
    /// removed</b> — a step re-created by rework goes to the same person again. It is the
    /// manager's standing instruction about this run, not a token that gets used up.</para>
    ///
    /// <para>Gated as <see cref="WorkflowOperation.PreAssignTask"/>, which is deliberately its
    /// own operation: committing somebody to work that does not exist is a different act from
    /// doing your own.</para>
    /// </summary>
    public Task<Result<Unit>> PreAssignAsync(
        int workflowRunId,
        int taskDefinitionId,
        WorkflowAssignment assignment,
        string actorId,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(assignment);
            ArgumentException.ThrowIfNullOrWhiteSpace(assignment.ActorId);

            var run = await db.WorkflowRuns
                .SingleOrDefaultAsync(r => r.Id == workflowRunId && !r.IsArchived, token)
                .ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Run", workflowRunId.ToString());

            await AuthorizeAsync(
                WorkflowOperation.PreAssignTask, null, actorId, run.Subject, null, token)
                .ConfigureAwait(false);

            var definition = await db.WorkflowTaskDefinitions
                .Include(d => d.TaskType)
                .SingleOrDefaultAsync(d => d.Id == taskDefinitionId && !d.IsArchived, token)
                .ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException(
                    "Task definition", taskDefinitionId.ToString());

            // Refused rather than accepted and ignored. A pre-assignment for a step that
            // cannot occur in this run is a mistake, and the manager who made it would
            // otherwise watch the work go somewhere else with nothing to explain why.
            if (definition.WorkflowDefinitionVersionId != run.WorkflowDefinitionVersionId)
            {
                throw new InvalidOperationException(
                    $"Task definition {taskDefinitionId} belongs to another version of the "
                    + "workflow, so this run can never reach it.");
            }

            // Fork branches ignore pre-assignment by decision -- one named person cannot hold
            // every section's copy of a forked step. Saying so beats silently doing nothing.
            if (definition.IsForkable)
            {
                throw new InvalidOperationException(
                    $"'{Label(definition)}' is forkable, and a fork gives each unit its own "
                    + "copy. Assign those once the branches exist.");
            }

            // An open task for this step is a reassignment, and ReassignTaskAsync is the
            // operation for it -- with its own permission. Two ways to do one thing, gated
            // differently, is how they drift apart.
            var live = await db.WorkflowTasks
                .AnyAsync(t => t.WorkflowRunId == workflowRunId
                            && t.TaskDefinitionId == taskDefinitionId
                            && (t.Status == WorkflowTaskStatus.NotStarted
                             || t.Status == WorkflowTaskStatus.InProgress), token)
                .ConfigureAwait(false);

            if (live)
            {
                throw new InvalidOperationException(
                    $"'{Label(definition)}' has already started. Reassign it instead.");
            }

            var now = DateTime.UtcNow;

            var existing = await db.WorkflowPreAssignments
                .SingleOrDefaultAsync(
                    p => p.WorkflowRunId == workflowRunId
                      && p.TaskDefinitionId == taskDefinitionId, token)
                .ConfigureAwait(false);

            if (existing is null)
            {
                db.WorkflowPreAssignments.Add(new PreAssignment
                {
                    WorkflowRunId = workflowRunId,
                    TaskDefinitionId = taskDefinitionId,
                    ActorId = assignment.ActorId!,
                    BranchKey = assignment.BranchKey,
                    CreatorId = actorId,
                    ModifierId = actorId,
                    Created = now,
                    Modified = now
                });
            }
            else
            {
                // Updated rather than deleted and re-added, so one row keeps its history --
                // and un-archived, because setting it again is how somebody reverses a
                // removal.
                existing.ActorId = assignment.ActorId!;
                existing.BranchKey = assignment.BranchKey;
                existing.IsArchived = false;
                existing.ModifierId = actorId;
                existing.Modified = now;
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            return new Unit();
        }, ct));

    /// <summary>
    /// Removes a standing pre-assignment. The step falls back to its assignment role, and to
    /// the previous step's assignee if it declares no role.
    /// </summary>
    public Task<Result<Unit>> RemovePreAssignmentAsync(
        int workflowRunId,
        int taskDefinitionId,
        string actorId,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var run = await db.WorkflowRuns
                .SingleOrDefaultAsync(r => r.Id == workflowRunId && !r.IsArchived, token)
                .ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Run", workflowRunId.ToString());

            await AuthorizeAsync(
                WorkflowOperation.PreAssignTask, null, actorId, run.Subject, null, token)
                .ConfigureAwait(false);

            var existing = await db.WorkflowPreAssignments
                .SingleOrDefaultAsync(
                    p => p.WorkflowRunId == workflowRunId
                      && p.TaskDefinitionId == taskDefinitionId, token)
                .ConfigureAwait(false);

            // Removing one that is not there is not an error: two managers tidying the same
            // plan should not produce a failure for the second.
            if (existing is not null)
            {
                db.WorkflowPreAssignments.Remove(existing);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
            }

            return new Unit();
        }, ct));

    /// <summary>
    /// Every standing pre-assignment on a run — what a manager reopening a workflow needs in
    /// order to see the plan and change it.
    /// </summary>
    public Task<Result<IReadOnlyList<PreAssignmentSnapshot>>> GetPreAssignmentsForRunAsync(
        int workflowRunId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
            (IReadOnlyList<PreAssignmentSnapshot>)await db.WorkflowPreAssignments
                .AsNoTracking()
                .Include(p => p.TaskDefinition!).ThenInclude(d => d.TaskType)
                .Where(p => p.WorkflowRunId == workflowRunId && !p.IsArchived)
                .OrderBy(p => p.TaskDefinitionId)
                .Select(p => new PreAssignmentSnapshot(
                    p.WorkflowRunId,
                    p.TaskDefinitionId,
                    p.TaskDefinition!.DisplayName ?? p.TaskDefinition.TaskType!.DisplayName,
                    p.ActorId,
                    p.BranchKey))
                .ToListAsync(ct)
                .ConfigureAwait(false));

    /// <summary>
    /// Work an actor is promised but does not yet hold.
    ///
    /// <para><b>Deliberately separate from <c>GetOpenTasksForActorAsync</c>.</b> An inbox is
    /// work you can act on now; this is work you cannot. Merging them would make every
    /// existing caller start showing rows with no task to open, and each would have to learn
    /// the difference. A host that wants one list concatenates two.</para>
    ///
    /// <para>Excludes steps whose task now exists — it is in the inbox instead, and showing
    /// both would double-count — and runs that are no longer running.</para>
    /// </summary>
    public Task<Result<IReadOnlyList<UpcomingTaskSnapshot>>> GetUpcomingTasksForActorAsync(
        string actorId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorId);

            return (IReadOnlyList<UpcomingTaskSnapshot>)await db.WorkflowPreAssignments
                .AsNoTracking()
                .Include(p => p.TaskDefinition!).ThenInclude(d => d.TaskType)
                .Include(p => p.Run!).ThenInclude(r => r.DefinitionVersion!).ThenInclude(v => v.WorkflowDefinition)
                .Where(p => p.ActorId == actorId
                         && !p.IsArchived
                         && p.Run!.Status == WorkflowRunStatus.Running
                         && !p.Run.IsArchived
                         && !p.Run.IsTest
                         && !db.WorkflowTasks.Any(t =>
                                t.WorkflowRunId == p.WorkflowRunId
                             && t.TaskDefinitionId == p.TaskDefinitionId
                             && (t.Status == WorkflowTaskStatus.NotStarted
                              || t.Status == WorkflowTaskStatus.InProgress)))
                .OrderBy(p => p.WorkflowRunId).ThenBy(p => p.TaskDefinitionId)
                .Select(p => new UpcomingTaskSnapshot(
                    p.WorkflowRunId,
                    p.Run!.Subject,
                    p.Run.DefinitionVersion!.WorkflowDefinition!.Name,
                    p.TaskDefinitionId,
                    p.TaskDefinition!.DisplayName ?? p.TaskDefinition.TaskType!.DisplayName,
                    p.BranchKey))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        });

    private static string Label(WorkflowTaskDefinition definition) =>
        definition.DisplayName ?? definition.TaskType?.DisplayName ?? $"#{definition.Id}";
}
