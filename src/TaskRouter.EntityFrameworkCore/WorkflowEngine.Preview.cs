using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;
using TaskRouter.Core.Runner;

namespace TaskRouter.EntityFrameworkCore;

public sealed partial class WorkflowEngine
{
    /// <summary>
    /// Walks the same path <see cref="CompleteTaskAsync"/> does — who the task is credited
    /// to, which routes the outcome takes, whether a convergence is reached, who each new
    /// step goes to — without writing anything, and asks the dispatcher what the triggers on
    /// that path would do, with the branch context each event would carry.
    ///
    /// <para>Authorized as the completion is: an actor who may not complete the task is not
    /// told what completing it would do, who it would notify, or what it would set.</para>
    ///
    /// <para>The task is read untracked and changed in memory only, and the steps it would
    /// create are never added to the context, so nothing here can be saved by accident.</para>
    /// </summary>
    public Task<Result<IReadOnlyList<PlannedAction>>> GetCompletionPreviewAsync(
        int taskId,
        string outcomeKey,
        string actorId,
        CancellationToken ct = default) =>
        Try.RunAsync<IReadOnlyList<PlannedAction>>(async () =>
        {
            if (dispatcher is null)
            {
                return [];
            }

            var task = await LoadForPreviewAsync(taskId, ct).ConfigureAwait(false);

            await AuthorizeAsync(WorkflowOperation.CompleteTask, task, actorId, null, null, ct).ConfigureAwait(false);

            if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
            {
                return [];
            }

            var now = DateTime.UtcNow;

            if (task.ForkManifestId.HasValue && await IsRejectionOutcomeAsync(task, outcomeKey, actorId, ct).ConfigureAwait(false))
                throw new InvalidOperationException("Convergence tasks must reject through selective rejection.");
            await ValidateCompletableAsync(task, outcomeKey, ct).ConfigureAwait(false);

            // As CompleteTaskAsync leaves it: credited to whoever completes it.
            task.AssignedToActorId = actorId;
            task.Status = WorkflowTaskStatus.Completed;
            task.OutcomeKey = outcomeKey;
            task.CompletedDate = now;

            var next = await NextStepsAsync(task, outcomeKey, now, ct).ConfigureAwait(false);
            return await PreviewCascadeAsync(task, next, actorId, null, ct).ConfigureAwait(false);
        });

    /// <summary>
    /// <see cref="GetCompletionPreviewAsync"/> for <see cref="CompleteWithSelectiveRejectionAsync"/>,
    /// which takes a different path: with no branch rejected it finishes like an ordinary
    /// completion; with some it raises "branches sent back" and "completed" on the
    /// convergence step and creates one rework step per rejected branch, assigned to whoever
    /// worked that branch.
    /// </summary>
    public Task<Result<IReadOnlyList<PlannedAction>>> GetSelectiveRejectionPreviewAsync(
        int convergenceTaskId,
        string outcomeKey,
        IReadOnlyList<string> rejectedBranchKeys,
        string actorId,
        CancellationToken ct = default) =>
        Try.RunAsync<IReadOnlyList<PlannedAction>>(async () =>
        {
            ArgumentNullException.ThrowIfNull(rejectedBranchKeys);
            if (rejectedBranchKeys.Distinct(StringComparer.Ordinal).Count() != rejectedBranchKeys.Count)
                throw new ArgumentException("Duplicate rejected branch keys are not allowed.", nameof(rejectedBranchKeys));

            if (dispatcher is null)
            {
                return [];
            }

            var task = await LoadForPreviewAsync(convergenceTaskId, ct).ConfigureAwait(false);

            await AuthorizeAsync(WorkflowOperation.CompleteWithSelectiveRejection, task, actorId, null, null, ct)
                .ConfigureAwait(false);

            if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled
                || task.ForkManifestId is not { } manifestId)
            {
                return [];
            }

            await ValidateCompletableAsync(task, outcomeKey, ct).ConfigureAwait(false);

            var now = DateTime.UtcNow;

            if (rejectedBranchKeys.Count == 0)
            {
                // Straight approval finishes the step as an ordinary completion does: credited
                // to whoever completes it, "completed" raised, then the steps it routes to.
                task.AssignedToActorId = actorId;
                task.Status = WorkflowTaskStatus.Completed;
                task.OutcomeKey = outcomeKey;
                task.CompletedDate = now;

                var onward = await NextStepsAsync(task, outcomeKey, now, ct).ConfigureAwait(false);
                return await PreviewCascadeAsync(task, onward, actorId, null, ct).ConfigureAwait(false);
            }

            // Sending branches back does not hand the task to whoever completes it.
            task.Status = WorkflowTaskStatus.Completed;
            task.OutcomeKey = outcomeKey;
            task.CompletedDate = now;

            var reworkDefinition = await ReworkDefinitionAsync(task, outcomeKey, ct).ConfigureAwait(false);

            if (reworkDefinition is null)
            {
                return [];
            }

            var entries = await db.WorkflowForkManifests.AsNoTracking()
                .Where(m => m.Id == manifestId)
                .SelectMany(m => m.Entries)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var rework = new List<(WorkflowTask Task, bool Converges)>();

            foreach (var branchKey in rejectedBranchKeys)
            {
                var entry = entries.FirstOrDefault(e => e.BranchKey == branchKey);

                if (entry is null)
                {
                    // The completion would refuse an unknown branch; there is nothing to preview.
                    return [];
                }

                var assignment = await ResolveAssignmentAsync(
                    reworkDefinition,
                    new WorkflowAssignment(entry.AssignedToActorId, branchKey),
                    task.ToSnapshot(),
                    AssignmentOrigin.Explicit,
                    ct).ConfigureAwait(false);

                rework.Add((Planned(task, reworkDefinition, assignment with { BranchKey = branchKey }, now), false));
            }

            var rejection = new BranchContext(null, null, null, [.. rejectedBranchKeys]);
            return await PreviewCascadeAsync(task, rework, actorId, rejection, ct).ConfigureAwait(false);
        });

    private async Task<WorkflowTask> LoadForPreviewAsync(int taskId, CancellationToken ct) =>
        await db.WorkflowTasks.AsNoTracking()
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .SingleOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false)
        ?? throw new WorkflowNotFoundException("Task", $"{taskId}");

    private async Task<WorkflowTaskDefinition?> ReworkDefinitionAsync(
        WorkflowTask task, string outcomeKey, CancellationToken ct)
    {
        var eligible = await GetValidRoutesAsync(task, outcomeKey, ct).ConfigureAwait(false);
        return eligible.Count == 1 && eligible[0].IsReworkRoute ? eligible[0].NextTaskDefinition : null;
    }

    /// <summary>As <see cref="HandleConvergenceAsync"/> builds it: the manifest and every branch on it.</summary>
    private async Task<BranchContext?> ConvergedBranchAsync(WorkflowTask convergence, CancellationToken ct)
    {
        if (convergence.ForkManifestId is not { } manifestId)
        {
            return null;
        }

        var manifest = await db.WorkflowForkManifests.AsNoTracking()
            .Include(m => m.Entries)
            .SingleAsync(m => m.Id == manifestId, ct)
            .ConfigureAwait(false);

        return new BranchContext(manifest.ForkGroupId, manifest.Id, null, [.. manifest.Entries.Select(e => e.BranchKey)]);
    }

    /// <summary>
    /// The steps completing <paramref name="task"/> would create, assigned as
    /// <see cref="RouteFromAsync"/> and <see cref="HandleConvergenceAsync"/> would assign
    /// them. A convergence counts only if this is the last branch outstanding and the
    /// convergence step does not already exist.
    /// </summary>
    private async Task<List<(WorkflowTask Task, bool Converges)>> NextStepsAsync(
        WorkflowTask task, string outcomeKey, DateTime now, CancellationToken ct)
    {
        var next = new List<(WorkflowTask, bool)>();

        var routes = await GetValidRoutesAsync(task, outcomeKey, ct).ConfigureAwait(false);
        await EnsureEligibleRoutesAsync(task, outcomeKey, routes, ct).ConfigureAwait(false);
        foreach (var route in routes)
        {
            var definition = route.NextTaskDefinition
                ?? throw new InvalidOperationException($"Route {route.Id} has no resolved target definition.");

            var converges = task.ForkGroupId.HasValue && task.ConvergenceTaskDefinitionId == route.NextTaskDefinitionId;
            WorkflowAssignment assignment;
            int? manifestId = null;

            if (converges)
            {
                var pending = await db.WorkflowTasks
                    .CountAsync(t => t.ForkGroupId == task.ForkGroupId
                                  && t.Id != task.Id
                                  && !t.IsForkOrigin
                                  && t.Status != WorkflowTaskStatus.Completed
                                  && t.Status != WorkflowTaskStatus.Cancelled
                                  && t.Status != WorkflowTaskStatus.Forked, ct)
                    .ConfigureAwait(false);

                var manifest = await db.WorkflowForkManifests.AsNoTracking()
                    .SingleOrDefaultAsync(m => m.ForkGroupId == task.ForkGroupId, ct)
                    .ConfigureAwait(false);

                if (pending > 0 || manifest is null
                    || await db.WorkflowTasks.AnyAsync(t => t.ForkManifestId == manifest.Id
                                                         && t.Status != WorkflowTaskStatus.Cancelled, ct)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                manifestId = manifest.Id;

                var origin = await db.WorkflowTasks.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == manifest.OriginTaskId, ct)
                    .ConfigureAwait(false) ?? task;

                assignment = await ResolveAssignmentAsync(
                    definition,
                    new WorkflowAssignment(origin.AssignedToActorId, origin.AssignedBranchKey),
                    origin.ToSnapshot(),
                    AssignmentOrigin.Inherited,
                    ct,
                    task.WorkflowRunId).ConfigureAwait(false);
            }
            else
            {
                assignment = await ResolveAssignmentAsync(
                    definition,
                    new WorkflowAssignment(task.AssignedToActorId, task.AssignedBranchKey),
                    task.ToSnapshot(),
                    AssignmentOrigin.Inherited,
                    ct,
                    task.ForkGroupId.HasValue ? 0 : task.WorkflowRunId).ConfigureAwait(false);
            }

            var step = Planned(task, definition, assignment, now);
            step.ForkGroupId = converges ? null : task.ForkGroupId;
            step.ForkManifestId = manifestId;
            next.Add((step, converges));
        }

        return next;
    }

    /// <summary>A step as it would be created. Never added to the context.</summary>
    private static WorkflowTask Planned(
        WorkflowTask from, WorkflowTaskDefinition definition, WorkflowAssignment assignment, DateTime now) => new()
        {
            WorkflowRunId = from.WorkflowRunId,
            TaskDefinitionId = definition.Id,
            TaskDefinition = definition,
            Status = WorkflowTaskStatus.NotStarted,
            AssignedToActorId = assignment.ActorId,
            AssignedBranchKey = assignment.BranchKey,
            SubWorkflowInstanceId = from.SubWorkflowInstanceId,
            ParentTaskId = from.ParentTaskId,
            Created = now,
            Modified = now
        };
}
