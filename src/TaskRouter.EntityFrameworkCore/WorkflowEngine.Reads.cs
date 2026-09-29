using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

public sealed record TaskOutcomeOption(string OutcomeKey, string DisplayName, int Order);

public sealed record TaskLogEntry(
    int Id, int TaskId, string Action, string? Note, string PerformedBy, DateTime PerformedAt);

/// <summary>
/// Where a fork has got to.
///
/// <para><b>The counts are over every task carrying the fork group, not only the tasks the
/// fork created.</b> A fork spans all the steps between the forked task and the convergence
/// point: each branch follows its own routes, and fork context propagates to each follow-on,
/// so three branches through a two-step chain produce six tasks. Convergence happens when the
/// last of them reaches the convergence definition.</para>
///
/// <para>Which means <see cref="PendingBranchCount"/> is "steps still open before this fork
/// can converge", not "branches nobody has done yet" — and a fork showing every branch
/// complete can still be outstanding, because those branches have moved on to their next
/// step. Rendering it as "branches outstanding" reads as a contradiction to somebody who has
/// just completed all of them.</para>
/// </summary>
public sealed record ForkContext(
    bool IsPartOfFork,
    Guid? ForkGroupId,
    int? ConvergenceTaskDefinitionId,
    bool IsConvergenceTask,
    int? ForkManifestId,
    int CompletedBranchCount,
    int CancelledBranchCount,
    int PendingBranchCount);

public sealed record ForkManifestView(
    int Id,
    Guid ForkGroupId,
    int WorkflowRunId,
    int OriginTaskId,
    int ConvergenceTaskDefinitionId,
    IReadOnlyList<ForkManifestEntryView> Entries);

public sealed record ForkManifestEntryView(
    string BranchKey, string? AssignedToActorId, string? BranchOutcomeKey, string? BranchNotes);

public sealed record AddBranchResult(
    Guid ForkGroupId, int ForkManifestId,
    IReadOnlyList<WorkflowTaskSnapshot> NewBranches, int TotalBranchCount);

/// <summary>
/// Read-side operations and branch management. Split out because these are the
/// methods any UI needs, distinct from the state-machine operations.
/// </summary>
public sealed partial class WorkflowEngine
{
    public Task<Result<IReadOnlyList<TaskOutcomeOption>>> GetValidOutcomesAsync(
        int taskId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var definitionId = await db.WorkflowTasks
                .Where(t => t.Id == taskId)
                .Select(t => t.TaskDefinitionId)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);

            if (definitionId == 0)
            {
                throw new WorkflowNotFoundException("Task", taskId.ToString());
            }

            var outcomes = await db.WorkflowTaskOutcomes
                .AsNoTracking()
                .Where(o => o.TaskDefinitionId == definitionId && !o.IsArchived)
                .OrderBy(o => o.Order)
                .Select(o => new TaskOutcomeOption(o.OutcomeKey, o.DisplayName, o.Order))
                .ToListAsync(ct).ConfigureAwait(false);

            return (IReadOnlyList<TaskOutcomeOption>)outcomes;
        });

    public Task<Result<IReadOnlyList<TaskLogEntry>>> GetTaskLogsAsync(
        int taskId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var logs = await db.WorkflowTaskLogs
                .AsNoTracking()
                .Where(l => l.TaskId == taskId)
                .OrderByDescending(l => l.PerformedAt).ThenByDescending(l => l.Id)
                .Select(l => new TaskLogEntry(
                    l.Id, l.TaskId, l.Action, l.Note, l.PerformedBy, l.PerformedAt))
                .ToListAsync(ct).ConfigureAwait(false);

            return (IReadOnlyList<TaskLogEntry>)logs;
        });

    public Task<Result<IReadOnlyList<WorkflowTaskSnapshot>>> GetChildTasksAsync(
        int parentTaskId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var children = await db.WorkflowTasks
                .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
                .AsNoTracking()
                .Where(t => t.ParentTaskId == parentTaskId)
                .OrderBy(t => t.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            return (IReadOnlyList<WorkflowTaskSnapshot>)children.Select(c => c.ToSnapshot()).ToList();
        });

    /// <summary>
    /// Updates editable task metadata. Deliberately does not accept a status or an
    /// outcome: completing a task goes through CompleteTaskAsync so routing, blocking
    /// checks and triggers cannot be bypassed (the original engine's review finding C3).
    /// </summary>
    public Task<Result<Unit>> UpdateTaskNotesAsync(
        int taskId,
        string? notes,
        string actorId,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var task = await LoadTaskAsync(taskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.UpdateTaskNotes, task, actorId, null, null, token)
                .ConfigureAwait(false);

            if (task.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled)
            {
                throw new InvalidOperationException($"Cannot update a {task.Status} task.");
            }

            if (task.Notes == notes)
            {
                return;
            }

            task.Notes = notes;
            task.ModifierId = actorId;
            task.Modified = DateTime.UtcNow;

            Log(task.Id, "Updated", "Notes updated.", actorId, task.Modified);
        }, ct));

    public Task<Result<ForkContext>> GetForkContextAsync(int taskId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var task = await db.WorkflowTasks
                .AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Task", taskId.ToString());

            var completed = 0;
            var cancelled = 0;
            var pending = 0;

            if (task.ForkGroupId.HasValue)
            {
                var branches = await db.WorkflowTasks
                    .AsNoTracking()
                    .Where(t => t.ForkGroupId == task.ForkGroupId && !t.IsForkOrigin)
                    .Select(t => t.Status)
                    .ToListAsync(ct).ConfigureAwait(false);

                // Counted separately rather than lumping cancelled in with completed,
                // which is what the original engine's GetForkContextAsync did — it overstated progress.
                completed = branches.Count(s => s == WorkflowTaskStatus.Completed);
                cancelled = branches.Count(s => s == WorkflowTaskStatus.Cancelled);
                pending = branches.Count(s => s is not (WorkflowTaskStatus.Completed
                                                     or WorkflowTaskStatus.Cancelled
                                                     or WorkflowTaskStatus.Forked));
            }

            return new ForkContext(
                IsPartOfFork: task.ForkGroupId.HasValue,
                ForkGroupId: task.ForkGroupId,
                ConvergenceTaskDefinitionId: task.ConvergenceTaskDefinitionId,
                IsConvergenceTask: task.ForkManifestId.HasValue,
                ForkManifestId: task.ForkManifestId,
                CompletedBranchCount: completed,
                CancelledBranchCount: cancelled,
                PendingBranchCount: pending);
        });

    public Task<Result<ForkManifestView>> GetForkManifestAsync(
        int manifestId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var manifest = await db.WorkflowForkManifests
                .Include(m => m.Entries)
                .AsNoTracking()
                .SingleOrDefaultAsync(m => m.Id == manifestId, ct).ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Fork manifest", manifestId.ToString());

            return ToView(manifest);
        });

    public Task<Result<IReadOnlyList<ForkManifestView>>> GetForkManifestsForRunAsync(
        int runId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var manifests = await db.WorkflowForkManifests
                .Include(m => m.Entries)
                .AsNoTracking()
                .Where(m => m.WorkflowRunId == runId && !m.IsArchived)
                .OrderByDescending(m => m.Created)
                .ToListAsync(ct).ConfigureAwait(false);

            return (IReadOnlyList<ForkManifestView>)manifests.Select(ToView).ToList();
        });

    private static ForkManifestView ToView(ForkManifest m) => new(
        m.Id, m.ForkGroupId, m.WorkflowRunId, m.OriginTaskId, m.ConvergenceTaskDefinitionId,
        m.Entries
            .Select(e => new ForkManifestEntryView(
                e.BranchKey, e.AssignedToActorId, e.BranchOutcomeKey, e.BranchNotes))
            .ToList());

    /// <summary>
    /// Adds branches to an active fork. Runs in one transaction with the
    /// convergence-already-fired check, so a branch cannot be added to a fork that
    /// converged concurrently — the race that would otherwise strand it forever
    /// (the original engine's review finding H5).
    /// </summary>
    public Task<Result<AddBranchResult>> AddBranchToForkAsync(
        Guid forkGroupId,
        IReadOnlyList<string> branchKeys,
        string actorId,
        string? notes = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(branchKeys);

            if (branchKeys.Count == 0)
            {
                throw new ArgumentException("At least one branch key is required.", nameof(branchKeys));
            }

            if (branchKeys.Distinct(StringComparer.Ordinal).Count() != branchKeys.Count)
            {
                throw new ArgumentException("Duplicate branch keys are not allowed.", nameof(branchKeys));
            }

            // The manifest first: it is what this method mutates, and loading it also
            // validates the group exists. Every branch ForkTaskAsync creates gets its own
            // distinct AssignedBranchKey (see WorkflowEngine.Fork.cs), so unlike the
            // manifest, no branch task in the group is a stand-in for the others — a query
            // with no ORDER BY picking one at random would authorize against whichever
            // section the query plan happened to return.
            var manifest = await db.WorkflowForkManifests
                .Include(m => m.Entries)
                .SingleOrDefaultAsync(m => m.ForkGroupId == forkGroupId && !m.IsArchived, token)
                .ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Fork group", forkGroupId.ToString());

            // Authorized against the origin task — the task whose forking created this
            // group, and the one stable, unambiguous answer to "which task is this
            // decision about". OriginTaskId is set once, at fork time, and never
            // reassigned, so unlike a branch task it does not depend on which branch a
            // caller happens to be adding.
            var originTask = await LoadTaskAsync(manifest.OriginTaskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.AddBranchToFork, originTask, actorId, null, null, token)
                .ConfigureAwait(false);

            var alreadyConverged = await db.WorkflowTasks
                .AnyAsync(t => t.ForkManifestId == manifest.Id
                            && t.Status != WorkflowTaskStatus.Cancelled, token)
                .ConfigureAwait(false);

            if (alreadyConverged)
            {
                throw new InvalidOperationException(
                    "Cannot add branches: this fork has already converged.");
            }

            var existing = manifest.Entries.Select(e => e.BranchKey).ToHashSet(StringComparer.Ordinal);
            var duplicates = branchKeys.Where(existing.Contains).ToList();

            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Branch(es) already in this fork: {string.Join(", ", duplicates)}.");
            }

            // Any existing branch serves as the template; unlike the original engine this does not
            // require an IsForkOrigin task, so branches can be added to a fork created
            // by selective rejection too.
            var template = await db.WorkflowTasks
                .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
                .Where(t => t.ForkGroupId == forkGroupId && !t.IsForkOrigin)
                .OrderBy(t => t.Id)
                .FirstOrDefaultAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Fork group {forkGroupId} has no branches to use as a template.");

            var templateDef = template.TaskDefinition
                ?? throw new InvalidOperationException("Template branch has no definition loaded.");

            var now = DateTime.UtcNow;
            var created = new List<WorkflowTask>();

            foreach (var branchKey in branchKeys)
            {
                var assignment = await ResolveAssignmentAsync(
                    templateDef,
                    new WorkflowAssignment(null, branchKey),
                    template.ToSnapshot(),
                    token).ConfigureAwait(false);

                assignment = assignment with { BranchKey = branchKey };

                var branch = await NewTaskAsync(template.WorkflowRunId, templateDef, assignment, actorId, now, token).ConfigureAwait(false);
                branch.ForkGroupId = forkGroupId;
                branch.ConvergenceTaskDefinitionId = template.ConvergenceTaskDefinitionId;
                branch.SubWorkflowInstanceId = template.SubWorkflowInstanceId;
                branch.ParentTaskId = template.ParentTaskId;
                branch.Notes = notes;

                created.Add(branch);

                manifest.Entries.Add(new ForkManifestEntry
                {
                    ForkManifestId = manifest.Id,
                    BranchKey = branchKey,
                    AssignedToActorId = assignment.ActorId,
                    CreatorId = actorId,
                    ModifierId = actorId,
                    Created = now,
                    Modified = now
                });
            }

            manifest.ModifierId = actorId;
            manifest.Modified = now;

            db.WorkflowTasks.AddRange(created);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            foreach (var branch in created)
            {
                Log(branch.Id, "Created",
                    $"Branch '{branch.AssignedBranchKey}' added to fork {forkGroupId}.", actorId, now);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            foreach (var branch in created)
            {
                await FireAsync(branch, WorkflowEventKind.BranchAdded, actorId,
                    new BranchContext(forkGroupId, manifest.Id, branch.AssignedBranchKey, []), token)
                    .ConfigureAwait(false);
            }

            var total = await db.WorkflowTasks
                .CountAsync(t => t.ForkGroupId == forkGroupId && !t.IsForkOrigin, token)
                .ConfigureAwait(false);

            return new AddBranchResult(
                forkGroupId, manifest.Id, created.Select(c => c.ToSnapshot()).ToList(), total);
        }, ct));
}
