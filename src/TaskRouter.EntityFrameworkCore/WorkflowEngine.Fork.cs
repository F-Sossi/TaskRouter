using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

public sealed partial class WorkflowEngine
{
    public Task<Result<ForkResult>> ForkTaskAsync(
        int taskId,
        IReadOnlyList<string> branchKeys,
        int convergenceTaskDefinitionId,
        string actorId,
        string? notes = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(branchKeys);

            if (branchKeys.Count < 2)
            {
                throw new ArgumentException("A fork needs at least two branches.", nameof(branchKeys));
            }

            if (branchKeys.Distinct(StringComparer.Ordinal).Count() != branchKeys.Count)
            {
                throw new ArgumentException("Duplicate branch keys are not allowed.", nameof(branchKeys));
            }

            var origin = await LoadTaskAsync(taskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.ForkTask, origin, actorId, null, null, token)
                .ConfigureAwait(false);

            if (origin.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled
                or WorkflowTaskStatus.Forked)
            {
                throw new InvalidOperationException($"Cannot fork a {origin.Status} task.");
            }

            if (origin.ForkGroupId.HasValue)
            {
                throw new InvalidOperationException("Task is already part of a fork.");
            }

            var convergenceDef = await db.WorkflowTaskDefinitions
                .SingleOrDefaultAsync(d => d.Id == convergenceTaskDefinitionId && !d.IsArchived, token)
                .ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException(
                    "Convergence task definition", convergenceTaskDefinitionId.ToString());

            var originDef = origin.TaskDefinition
                ?? throw new InvalidOperationException("Origin task has no definition loaded.");

            if (convergenceDef.WorkflowDefinitionVersionId != originDef.WorkflowDefinitionVersionId)
            {
                throw new InvalidOperationException(
                    "Convergence task must belong to the same workflow version as the forked task.");
            }

            var now = DateTime.UtcNow;
            var forkGroupId = Guid.NewGuid();

            origin.Status = WorkflowTaskStatus.Forked;
            origin.ForkGroupId = forkGroupId;
            origin.IsForkOrigin = true;
            origin.ConvergenceTaskDefinitionId = convergenceTaskDefinitionId;
            origin.ModifierId = actorId;
            origin.Modified = now;
            origin.Notes = Append(origin.Notes, $"Forked into {branchKeys.Count} branches.");

            var manifest = new ForkManifest
            {
                ForkGroupId = forkGroupId,
                WorkflowRunId = origin.WorkflowRunId,
                SubWorkflowInstanceId = origin.SubWorkflowInstanceId,
                OriginTaskId = origin.Id,
                ConvergenceTaskDefinitionId = convergenceTaskDefinitionId,
                CreatorId = actorId,
                ModifierId = actorId,
                Created = now,
                Modified = now
            };

            var branches = new List<WorkflowTask>();

            foreach (var branchKey in branchKeys)
            {
                var assignment = await ResolveAssignmentAsync(
                    originDef,
                    new WorkflowAssignment(null, branchKey),
                    origin.ToSnapshot(),
                    // A branch starts with its unit and no person by design, so there is
                    // nothing here for carry-forward to drop either way.
                    AssignmentOrigin.Inherited,
                    token).ConfigureAwait(false);

                // The branch key is authoritative even if a resolver returns something else.
                assignment = assignment with { BranchKey = branchKey };

                var branch = await NewTaskAsync(origin.WorkflowRunId, originDef, assignment, actorId, now, token).ConfigureAwait(false);
                branch.ForkGroupId = forkGroupId;
                branch.ConvergenceTaskDefinitionId = convergenceTaskDefinitionId;
                branch.SubWorkflowInstanceId = origin.SubWorkflowInstanceId;
                branch.ParentTaskId = origin.ParentTaskId;
                branch.Notes = notes;

                branches.Add(branch);

                manifest.Entries.Add(new ForkManifestEntry
                {
                    ForkManifestId = 0,
                    BranchKey = branchKey,
                    AssignedToActorId = assignment.ActorId,
                    CreatorId = actorId,
                    ModifierId = actorId,
                    Created = now,
                    Modified = now
                });
            }

            db.WorkflowForkManifests.Add(manifest);
            db.WorkflowTasks.AddRange(branches);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            Log(origin.Id, "Forked",
                $"Forked to branches: {string.Join(", ", branchKeys)}.", actorId, now);

            foreach (var branch in branches)
            {
                Log(branch.Id, "Created",
                    $"Fork branch '{branch.AssignedBranchKey}' from task {origin.Id}.", actorId, now);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            var forkBranchContext = new BranchContext(
                forkGroupId, manifest.Id, null, branchKeys.ToList());

            await FireAsync(origin, WorkflowEventKind.TaskForked, actorId, forkBranchContext, token)
                .ConfigureAwait(false);

            foreach (var branch in branches)
            {
                await SpawnAutomaticAsync(branch, actorId, token).ConfigureAwait(false);

                await FireAsync(branch, WorkflowEventKind.TaskCreated, actorId, ct: token)
                    .ConfigureAwait(false);
            }

            return new ForkResult(
                forkGroupId,
                manifest.Id,
                branches.Select(b => b.ToSnapshot()).ToList(),
                origin.Id);
        }, ct));

    public Task<Result<Unit>> CompleteWithSelectiveRejectionAsync(
        int convergenceTaskId,
        string outcomeKey,
        IReadOnlyList<string> rejectedBranchKeys,
        string actorId,
        string? notes = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(rejectedBranchKeys);

            var task = await db.WorkflowTasks
                .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
                .Include(t => t.ForkManifest!).ThenInclude(m => m.Entries)
                .SingleOrDefaultAsync(t => t.Id == convergenceTaskId, token).ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Task", convergenceTaskId.ToString());

            await AuthorizeAsync(
                WorkflowOperation.CompleteWithSelectiveRejection, task, actorId, null, null, token)
                .ConfigureAwait(false);

            if (task.ForkManifest is null)
            {
                throw new InvalidOperationException(
                    "This task is not a convergence task; use CompleteTaskAsync.");
            }

            await ValidateCompletableAsync(task, outcomeKey, token).ConfigureAwait(false);

            var manifest = task.ForkManifest;
            var now = DateTime.UtcNow;

            if (rejectedBranchKeys.Count == 0)
            {
                // Straight approval. Handled here rather than delegating to
                // CompleteTaskAsync, which refuses rework outcomes on convergence tasks
                // and would produce advice the caller cannot act on (the original engine's finding M5).
                task.Status = WorkflowTaskStatus.Completed;
                task.OutcomeKey = outcomeKey;
                task.CompletedDate = now;
                task.Notes = notes ?? task.Notes;
                task.ModifierId = actorId;
                task.Modified = now;

                Log(task.Id, "Completed", $"Outcome: {outcomeKey}; no branches rejected.", actorId, now);
                await RouteFromAsync(task, outcomeKey, actorId, now, token).ConfigureAwait(false);
                await MaybeCompleteRunAsync(task.WorkflowRunId, actorId, now, token).ConfigureAwait(false);
                return;
            }

            var known = manifest.Entries.Select(e => e.BranchKey).ToHashSet(StringComparer.Ordinal);
            var unknown = rejectedBranchKeys.Where(k => !known.Contains(k)).ToList();

            if (unknown.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Branch key(s) not in the fork manifest: {string.Join(", ", unknown)}.");
            }

            var reworkOutcomeId =
                await ResolveOutcomeIdAsync(task.TaskDefinitionId, outcomeKey, token)
                    .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"This task declares no outcome '{outcomeKey}'.");

            var reworkRoute = await db.WorkflowTaskRoutes
                .Include(r => r.NextTaskDefinition!).ThenInclude(d => d.TaskType)
                .FirstOrDefaultAsync(r => r.TaskDefinitionId == task.TaskDefinitionId
                                       && r.TaskOutcomeDefinitionId == reworkOutcomeId
                                       && !r.IsArchived, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No route for outcome '{outcomeKey}'.");

            var reworkDef = reworkRoute.NextTaskDefinition
                ?? throw new InvalidOperationException("Rework route has no target definition.");

            task.Status = WorkflowTaskStatus.Completed;
            task.OutcomeKey = outcomeKey;
            task.CompletedDate = now;
            task.ModifierId = actorId;
            task.Modified = now;
            // Append rather than overwrite — the original engine's finding M4 discarded existing notes here.
            task.Notes = Append(task.Notes,
                notes ?? $"Selective rejection: {rejectedBranchKeys.Count} branch(es) sent back.");

            var newForkGroupId = Guid.NewGuid();

            var newManifest = new ForkManifest
            {
                ForkGroupId = newForkGroupId,
                WorkflowRunId = task.WorkflowRunId,
                SubWorkflowInstanceId = task.SubWorkflowInstanceId,
                OriginTaskId = task.Id,
                ConvergenceTaskDefinitionId = task.TaskDefinitionId,   // re-converge here
                CreatorId = actorId,
                ModifierId = actorId,
                Created = now,
                Modified = now
            };

            var reworkTasks = new List<WorkflowTask>();

            foreach (var branchKey in rejectedBranchKeys)
            {
                var entry = manifest.Entries.First(e => e.BranchKey == branchKey);

                var assignment = await ResolveAssignmentAsync(
                    reworkDef,
                    new WorkflowAssignment(entry.AssignedToActorId, branchKey),
                    task.ToSnapshot(),
                    // Rework goes back to whoever worked the branch that was rejected --
                    // which is why the manifest records them. Returning it to the section
                    // pool instead would lose "you fix what you did".
                    AssignmentOrigin.Explicit,
                    token).ConfigureAwait(false);

                assignment = assignment with { BranchKey = branchKey };

                var rework = await NewTaskAsync(task.WorkflowRunId, reworkDef, assignment, actorId, now, token).ConfigureAwait(false);
                rework.ForkGroupId = newForkGroupId;
                rework.ConvergenceTaskDefinitionId = task.TaskDefinitionId;
                rework.SubWorkflowInstanceId = task.SubWorkflowInstanceId;
                rework.Notes = $"Rework requested. Previous notes: {entry.BranchNotes}";

                reworkTasks.Add(rework);

                newManifest.Entries.Add(new ForkManifestEntry
                {
                    ForkManifestId = 0,
                    BranchKey = branchKey,
                    AssignedToActorId = assignment.ActorId,
                    CreatorId = actorId,
                    ModifierId = actorId,
                    Created = now,
                    Modified = now
                });
            }

            db.WorkflowForkManifests.Add(newManifest);
            db.WorkflowTasks.AddRange(reworkTasks);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            Log(task.Id, "SelectiveRejection",
                $"Rejected branches: {string.Join(", ", rejectedBranchKeys)}. " +
                $"New fork group {newForkGroupId}.", actorId, now);

            foreach (var rework in reworkTasks)
            {
                Log(rework.Id, "Created",
                    $"Re-forked for branch '{rework.AssignedBranchKey}'.", actorId, now);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            var rejectionContext = new BranchContext(
                newForkGroupId, newManifest.Id, null, rejectedBranchKeys.ToList());

            await FireAsync(task, WorkflowEventKind.SelectiveRejection, actorId, rejectionContext, token)
                .ConfigureAwait(false);
            await FireAsync(task, WorkflowEventKind.TaskCompleted, actorId, rejectionContext, token)
                .ConfigureAwait(false);

            foreach (var rework in reworkTasks)
            {
                await SpawnAutomaticAsync(rework, actorId, token).ConfigureAwait(false);

                await FireAsync(rework, WorkflowEventKind.TaskCreated, actorId, ct: token)
                    .ConfigureAwait(false);
            }
        }, ct));

    private static string? Append(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : $"{existing}{Environment.NewLine}{addition}";
}
