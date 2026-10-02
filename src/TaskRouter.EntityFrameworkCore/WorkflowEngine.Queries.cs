using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

public sealed partial class WorkflowEngine
{
    public Task<Result<Unit>> CancelTaskAsync(
        int taskId,
        string actorId,
        string? note = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var task = await LoadTaskAsync(taskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.CancelTask, task, actorId, null, null, token)
                .ConfigureAwait(false);

            if (task.Status == WorkflowTaskStatus.Completed)
            {
                throw new InvalidOperationException("Cannot cancel a completed task.");
            }

            if (task.Status == WorkflowTaskStatus.Cancelled)
            {
                throw new InvalidOperationException("Task is already cancelled.");
            }

            var now = DateTime.UtcNow;
            task.Status = WorkflowTaskStatus.Cancelled;
            task.ModifierId = actorId;
            task.Modified = now;

            Log(task.Id, "Cancelled", note, actorId, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            // Cancelling a parent takes its sub-workflows with it. Left running they
            // would be work nobody can reach, assigned to people, that nothing will ever
            // close — and a blocking instance would go on blocking a task that no longer
            // exists to be blocked.
            await CancelInstancesForAsync(task.Id, actorId, now, token).ConfigureAwait(false);

            await FireAsync(task, WorkflowEventKind.TaskCancelled, actorId, ct: token)
                .ConfigureAwait(false);

            // The instance may itself have been the last open thing in its own parent.
            if (task.SubWorkflowInstanceId is { } cancelledInstanceId)
            {
                await MaybeCompleteInstanceAsync(cancelledInstanceId, actorId, now, token)
                    .ConfigureAwait(false);
            }

            await MaybeCompleteRunAsync(task.WorkflowRunId, actorId, now, token).ConfigureAwait(false);
        }, ct));

    public Task<Result<Unit>> CancelRunAsync(
        int runId,
        string actorId,
        string? reason = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var run = await db.WorkflowRuns
                .SingleOrDefaultAsync(r => r.Id == runId, token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Run {runId} not found.");

            // Authorized against the run's subject rather than a task: this is an act on the
            // whole run, and picking one of its tasks to authorize against would make the
            // answer depend on which one.
            await AuthorizeAsync(
                WorkflowOperation.CancelRun, null, actorId, run.Subject, null, token)
                .ConfigureAwait(false);

            if (run.Status != WorkflowRunStatus.Running)
            {
                // Refused rather than ignored. Cancelling a finished run would erase the fact
                // that it finished, and cancelling a cancelled one would re-stamp its modifier
                // and log a cancellation that did not happen.
                throw new InvalidOperationException(
                    $"Run {runId} is {run.Status}, so there is nothing to cancel.");
            }

            var now = DateTime.UtcNow;

            var openTasks = await db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId
                            && t.Status != WorkflowTaskStatus.Completed
                            && t.Status != WorkflowTaskStatus.Cancelled
                            && t.Status != WorkflowTaskStatus.Forked)
                .ToListAsync(token)
                .ConfigureAwait(false);

            foreach (var task in openTasks)
            {
                task.Status = WorkflowTaskStatus.Cancelled;
                task.ModifierId = actorId;
                task.Modified = now;

                // Against every task rather than the run, because the run has no log of its
                // own -- and a person looking at why their task vanished looks at the task.
                Log(task.Id, "Cancelled", reason, actorId, now);
            }

            // Completed tasks are left exactly as they are. Cancelling ends what is
            // outstanding; it does not rewrite what was already decided.
            run.Status = WorkflowRunStatus.Cancelled;
            run.CompletedDate = now;
            run.ModifierId = actorId;
            run.Modified = now;

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            // Sub-workflows hanging off those tasks go too. Left running they are work nobody
            // can reach, assigned to people, that nothing will ever close.
            foreach (var task in openTasks)
            {
                await CancelInstancesForAsync(task.Id, actorId, now, token).ConfigureAwait(false);
            }

            // Raised against the last open task for the same reason RunCompleted is: triggers
            // are configured per task definition and there is no run-level definition to hang
            // one off.
            if (openTasks.Count > 0)
            {
                await FireAsync(openTasks[^1], WorkflowEventKind.RunCancelled, actorId, ct: token)
                    .ConfigureAwait(false);
            }
        }, ct));

    public Task<Result<Unit>> ReassignTaskAsync(
        int taskId,
        WorkflowAssignment assignment,
        string actorId,
        string? note = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(assignment);

            var task = await LoadTaskAsync(taskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.ReassignTask, task, actorId, null, null, token)
                .ConfigureAwait(false);

            if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
            {
                throw new InvalidOperationException($"Cannot reassign a {task.Status} task.");
            }

            var now = DateTime.UtcNow;

            // Single assignment path, so there is no way for one code path to
            // overwrite another's result. The original engine had two: reassigning by
            // org unit resolved a lead, and a later block silently clobbered it.
            task.AssignedToActorId = assignment.ActorId;
            task.AssignedBranchKey = assignment.BranchKey ?? task.AssignedBranchKey;
            task.ModifierId = actorId;
            task.Modified = now;

            Log(task.Id, "Reassigned",
                note ?? $"Assigned to '{assignment.ActorId}' / branch '{task.AssignedBranchKey}'.",
                actorId, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            await FireAsync(task, WorkflowEventKind.TaskAssigned, actorId, ct: token)
                .ConfigureAwait(false);
        }, ct));

    public Task<Result<WorkflowTaskSnapshot>> AddAdHocTaskAsync(
        int parentTaskId,
        int taskDefinitionId,
        string actorId,
        WorkflowAssignment? assignment = null,
        string? notes = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var parent = await LoadTaskAsync(parentTaskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.AddAdHocTask, parent, actorId, null, null, token)
                .ConfigureAwait(false);

            var definition = await db.WorkflowTaskDefinitions
                .Include(d => d.TaskType)
                .SingleOrDefaultAsync(d => d.Id == taskDefinitionId && !d.IsArchived, token)
                .ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException(
                    "Task definition", taskDefinitionId.ToString());

            if (!definition.IsAdHoc)
            {
                throw new InvalidOperationException(
                    $"Task definition {taskDefinitionId} is not marked ad-hoc.");
            }

            var now = DateTime.UtcNow;

            var resolved = await ResolveAssignmentAsync(
                definition,
                assignment ?? new WorkflowAssignment(parent.AssignedToActorId, parent.AssignedBranchKey),
                parent.ToSnapshot(),
                // Raising an ad-hoc task against a named person is a decision; falling
                // back to the parent's assignee is not.
                assignment is not null ? AssignmentOrigin.Explicit : AssignmentOrigin.Inherited,
                token).ConfigureAwait(false);

            var task = await NewTaskAsync(parent.WorkflowRunId, definition, resolved, actorId, now, token).ConfigureAwait(false);
            task.ParentTaskId = parent.Id;
            task.SubWorkflowInstanceId = parent.SubWorkflowInstanceId;
            task.Notes = notes;

            db.WorkflowTasks.Add(task);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            Log(task.Id, "Created", $"Ad-hoc task under task {parent.Id}.", actorId, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            await SpawnAutomaticAsync(task, actorId, token).ConfigureAwait(false);

            await FireAsync(task, WorkflowEventKind.TaskCreated, actorId, ct: token)
                .ConfigureAwait(false);

            return task.ToSnapshot();
        }, ct));

    public Task<Result<WorkflowRunSnapshot>> GetRunAsync(int runId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var run = await db.WorkflowRuns
                .Include(r => r.DefinitionVersion)
                .AsNoTracking()
                .SingleOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Run", runId.ToString());

            var tasks = await LoadRunTasksAsync(runId, ct).ConfigureAwait(false);
            return run.ToSnapshot(tasks);
        });

    public Task<Result<IReadOnlyList<WorkflowRunSnapshot>>> GetRunsForSubjectAsync(
        WorkflowSubject subject,
        CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(subject);

            var runs = await db.WorkflowRuns
                .Include(r => r.DefinitionVersion)
                .AsNoTracking()
                .Where(r => r.Subject.SubjectType == subject.SubjectType
                         && r.Subject.SubjectId == subject.SubjectId
                         && !r.IsArchived)
                .OrderBy(r => r.Created)
                .ToListAsync(ct).ConfigureAwait(false);

            var result = new List<WorkflowRunSnapshot>(runs.Count);

            foreach (var run in runs)
            {
                var tasks = await LoadRunTasksAsync(run.Id, ct).ConfigureAwait(false);
                result.Add(run.ToSnapshot(tasks));
            }

            return (IReadOnlyList<WorkflowRunSnapshot>)result;
        });

    private async Task<List<WorkflowTask>> LoadRunTasksAsync(int runId, CancellationToken ct) =>
        await db.WorkflowTasks
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .AsNoTracking()
            .Where(t => t.WorkflowRunId == runId)
            .OrderBy(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);
}
