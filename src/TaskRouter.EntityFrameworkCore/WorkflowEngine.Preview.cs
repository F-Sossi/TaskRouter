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
    /// that path would do.
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

            var task = await db.WorkflowTasks.AsNoTracking()
                .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
                .SingleOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Task", $"{taskId}");

            if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
            {
                return [];
            }

            var now = DateTime.UtcNow;

            // As CompleteTaskAsync leaves it: credited to whoever completes it.
            task.AssignedToActorId = actorId;
            task.Status = WorkflowTaskStatus.Completed;
            task.OutcomeKey = outcomeKey;
            task.CompletedDate = now;

            var next = await NextStepsAsync(task, outcomeKey, now, ct).ConfigureAwait(false);
            var preview = new TriggerPreview([.. next.Select(n => n.Task.ToSnapshot())]);
            var actions = new List<PlannedAction>();

            actions.AddRange(await dispatcher.PreviewAsync(task, WorkflowEventKind.TaskCompleted, actorId, preview, ct)
                .ConfigureAwait(false));

            if (task.ForkGroupId is not null)
            {
                actions.AddRange(await dispatcher.PreviewAsync(task, WorkflowEventKind.BranchCompleted, actorId, preview, ct)
                    .ConfigureAwait(false));
            }

            foreach (var (step, converges) in next)
            {
                if (converges)
                {
                    actions.AddRange(await dispatcher.PreviewAsync(step, WorkflowEventKind.ForkConverged, actorId, preview, ct)
                        .ConfigureAwait(false));
                }

                actions.AddRange(await dispatcher.PreviewAsync(step, WorkflowEventKind.TaskCreated, actorId, preview, ct)
                    .ConfigureAwait(false));
            }

            return actions;
        });

    /// <summary>
    /// The steps completing <paramref name="task"/> would create, assigned as
    /// <see cref="RouteFromAsync"/> and <see cref="HandleConvergenceAsync"/> would assign
    /// them. A convergence counts only if this is the last branch outstanding.
    /// </summary>
    private async Task<List<(WorkflowTask Task, bool Converges)>> NextStepsAsync(
        WorkflowTask task, string outcomeKey, DateTime now, CancellationToken ct)
    {
        var next = new List<(WorkflowTask, bool)>();

        foreach (var route in await GetValidRoutesAsync(task, outcomeKey, ct).ConfigureAwait(false))
        {
            var definition = route.NextTaskDefinition
                ?? throw new InvalidOperationException($"Route {route.Id} has no resolved target definition.");

            var converges = task.ForkGroupId.HasValue && task.ConvergenceTaskDefinitionId == route.NextTaskDefinitionId;
            WorkflowAssignment assignment;

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

                if (pending > 0)
                {
                    continue;
                }

                var origin = await db.WorkflowForkManifests.AsNoTracking()
                    .Where(m => m.ForkGroupId == task.ForkGroupId)
                    .Join(db.WorkflowTasks.AsNoTracking(), m => m.OriginTaskId, t => t.Id, (_, t) => t)
                    .FirstOrDefaultAsync(ct)
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

            next.Add((new WorkflowTask
            {
                WorkflowRunId = task.WorkflowRunId,
                TaskDefinitionId = definition.Id,
                TaskDefinition = definition,
                Status = WorkflowTaskStatus.NotStarted,
                AssignedToActorId = assignment.ActorId,
                AssignedBranchKey = assignment.BranchKey,
                ForkGroupId = converges ? null : task.ForkGroupId,
                SubWorkflowInstanceId = task.SubWorkflowInstanceId,
                Created = now,
                Modified = now
            }, converges));
        }

        return next;
    }
}
