using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Sub-workflow lifecycle: spawn, completion, blocking and cancellation.
///
/// There is no second engine here. A sub-workflow's tasks are ordinary tasks in the
/// parent's run, tagged with <see cref="WorkflowTask.SubWorkflowInstanceId"/>, and they
/// route through the same <c>CompleteTaskAsync</c> path as everything else — routing,
/// forking and convergence already carry the tag across. What this file adds is only
/// the beginning and the end: creating the instance and its entry task, noticing when
/// the instance has run out of work, and keeping a parent from completing while a
/// blocking instance is still going.
///
/// This is what definition-id routing was for. Routes target a task definition rather
/// than a task type, so the same task type can appear in a mainline workflow and in
/// every sub-workflow attached to it without collision — which is the thing the original engine could
/// not express.
/// </summary>
public sealed partial class WorkflowEngine
{
    /// <summary>
    /// Starts a sub-workflow against a task, on the sub-workflow's latest published
    /// version.
    ///
    /// <c>AddAdHocTaskAsync</c> is the degenerate case of this — a sub-workflow of one
    /// task and no routes — and both remain because the cheap case should stay cheap.
    ///
    /// <paramref name="assignment"/> names who the chain is delegated to, in the same
    /// position <c>AddAdHocTaskAsync</c> takes one. The actor and the branch key move
    /// together because the engine inherits them as a pair everywhere else, and this is
    /// the one operation that moves an actor out of the parent's org unit.
    /// </summary>
    public Task<Result<SubWorkflowInstanceSnapshot>> StartSubWorkflowAsync(
        int parentTaskId,
        int subWorkflowDefinitionId,
        string actorId,
        WorkflowAssignment? assignment = null,
        string? notes = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var parent = await LoadTaskAsync(parentTaskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.StartSubWorkflow, parent, actorId, null, null, token)
                .ConfigureAwait(false);

            if (parent.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled
                or WorkflowTaskStatus.Forked)
            {
                throw new InvalidOperationException(
                    $"Cannot attach a sub-workflow to a {parent.Status} task.");
            }

            // The version owning the parent's *definition*, not the run's pinned version.
            // They differ when the parent is itself inside a sub-workflow instance, and
            // the definition's version is the right one: a sub-workflow nested under a
            // sub-workflow task should find the attachments its own author wrote.
            var versionId = parent.TaskDefinition!.WorkflowDefinitionVersionId;

            var attachment = await FindAttachmentAsync(
                versionId, parent.TaskDefinitionId, subWorkflowDefinitionId, token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "That sub-workflow is not attached to this task. Attachments are what " +
                    "decide where a sub-workflow may hang, so an unattached one is a " +
                    "configuration error rather than a runtime choice.");

            if (!attachment.AllowMultiple)
            {
                var running = await db.WorkflowSubWorkflowInstances
                    .AnyAsync(i => i.ParentTaskId == parentTaskId
                                && i.SubWorkflowDefinitionVersionId != 0
                                && i.Status == SubWorkflowStatus.Running
                                && !i.IsArchived, token)
                    .ConfigureAwait(false);

                if (running)
                {
                    throw new InvalidOperationException(
                        "A sub-workflow is already running on this task, and this " +
                        "attachment does not allow more than one.");
                }
            }

            return await SpawnAsync(parent, attachment, actorId, assignment, notes, token)
                .ConfigureAwait(false);
        }, ct));

    /// <summary>
    /// Abandons a running sub-workflow without touching its parent.
    ///
    /// The counterpart to cancelling the parent, which takes its instances down with it.
    /// This is the other direction: the delegated work turned out not to be needed, and
    /// the parent should become completable again. Without it a blocking instance can
    /// only ever be released by finishing it.
    /// </summary>
    public Task<Result<Unit>> CancelSubWorkflowAsync(
        int instanceId,
        string actorId,
        string? reason = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var instance = await db.WorkflowSubWorkflowInstances
                .SingleOrDefaultAsync(i => i.Id == instanceId, token).ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException(
                    "Sub-workflow instance", instanceId.ToString());

            var parent = await LoadTaskAsync(instance.ParentTaskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.CancelSubWorkflow, parent, actorId, null, null, token)
                .ConfigureAwait(false);

            if (instance.Status != SubWorkflowStatus.Running)
            {
                throw new InvalidOperationException(
                    $"Sub-workflow instance {instanceId} is already {instance.Status}.");
            }

            var now = DateTime.UtcNow;

            // One string, so the task-level note and the parent's audit entry — which a
            // person reads side by side — cannot disagree about why this happened.
            var effectiveReason = reason ?? "Sub-workflow cancelled.";

            await CancelInstanceAsync(instance, actorId, effectiveReason, now, token)
                .ConfigureAwait(false);

            Log(instance.ParentTaskId, "SubWorkflowCancelled", effectiveReason, actorId, now);

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            await FireSubWorkflowCancelledAsync(instance.ParentTaskId, actorId, token)
                .ConfigureAwait(false);
        }, ct));

    /// <summary>
    /// The attachment governing a sub-workflow on a task, version-wide rows included.
    ///
    /// Task-specific wins: an author who narrowed a sub-workflow to one task meant it,
    /// so a version-wide row is the fallback rather than a competitor. Ordering on
    /// <c>TaskDefinitionId != null</c> descending puts the specific row first.
    /// </summary>
    private async Task<SubWorkflowAttachment?> FindAttachmentAsync(
        int versionId, int taskDefinitionId, int subWorkflowDefinitionId, CancellationToken ct) =>
        await db.WorkflowSubWorkflowAttachments
            .Include(a => a.SubWorkflowDefinition)
            .Where(a => a.WorkflowDefinitionVersionId == versionId
                     && a.SubWorkflowDefinitionId == subWorkflowDefinitionId
                     && (a.TaskDefinitionId == null || a.TaskDefinitionId == taskDefinitionId)
                     && !a.IsArchived)
            .OrderByDescending(a => a.TaskDefinitionId != null)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Creates the instance and its entry task. Shared by the explicit start and the
    /// automatic spawn that happens when a parent task is created.
    /// </summary>
    private async Task<SubWorkflowInstanceSnapshot> SpawnAsync(
        WorkflowTask parent,
        SubWorkflowAttachment attachment,
        string actorId,
        WorkflowAssignment? requested,
        string? notes,
        CancellationToken ct)
    {
        // Pinned at spawn, exactly as a run pins: editing the sub-workflow afterwards
        // cannot reroute an instance already in flight.
        var version = await db.WorkflowDefinitionVersions
            .Include(v => v.Tasks).ThenInclude(t => t.TaskType)
            .Where(v => v.WorkflowDefinitionId == attachment.SubWorkflowDefinitionId
                     && v.IsPublished && v.IsLatest && !v.IsArchived)
            .SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Sub-workflow {attachment.SubWorkflowDefinitionId} has no published version.");

        if (version.EntryTaskDefinitionId is not { } entryId)
        {
            throw new InvalidOperationException(
                $"Sub-workflow version {version.Id} declares no entry task.");
        }

        var entryDef = version.Tasks.SingleOrDefault(t => t.Id == entryId && !t.IsArchived)
            ?? throw new InvalidOperationException(
                $"Entry task {entryId} is missing from sub-workflow version {version.Id}.");

        var now = DateTime.UtcNow;

        var instance = new SubWorkflowInstance
        {
            SubWorkflowDefinitionVersionId = version.Id,
            ParentTaskId = parent.Id,
            WorkflowRunId = parent.WorkflowRunId,
            Status = SubWorkflowStatus.Running,
            // Copied, not read through the attachment later: changing the attachment
            // must not unblock work that started under the old rule.
            IsBlocking = attachment.IsBlocking,
            CreatorId = actorId,
            ModifierId = actorId,
            Created = now,
            Modified = now
        };

        db.WorkflowSubWorkflowInstances.Add(instance);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // The requested assignment replaces the parent's as the resolver's input, which
        // is the whole delegation mechanism: an entry task with no role key inherits the
        // named person, and one with a role key resolves on top of them.
        var assignment = await ResolveAssignmentAsync(
            entryDef,
            requested ?? new WorkflowAssignment(parent.AssignedToActorId, parent.AssignedBranchKey),
            parent.ToSnapshot(),
            // Delegating to a named person is the whole mechanism -- see the comment
            // above -- so a requested assignment must survive regardless of the setting.
            requested is not null ? AssignmentOrigin.Explicit : AssignmentOrigin.Inherited,
            ct).ConfigureAwait(false);

        var task = await NewTaskAsync(parent.WorkflowRunId, entryDef, assignment, actorId, now, ct).ConfigureAwait(false);
        task.ParentTaskId = parent.Id;
        task.SubWorkflowInstanceId = instance.Id;
        task.Notes = notes;

        db.WorkflowTasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        Log(task.Id, "Created",
            $"Sub-workflow instance {instance.Id} started on version {version.Version}.",
            actorId, now);

        Log(parent.Id, "SubWorkflowStarted",
            $"Instance {instance.Id}{(attachment.IsBlocking ? " (blocking)" : string.Empty)}.",
            actorId, now);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await FireAsync(task, WorkflowEventKind.SubWorkflowStarted, actorId, ct: ct).ConfigureAwait(false);
        await FireAsync(task, WorkflowEventKind.TaskCreated, actorId, ct: ct).ConfigureAwait(false);

        // The name comes from the definition, which is loaded here but not on the
        // instance's navigation property — reading it from there would return blank.
        var name = await db.WorkflowDefinitions
            .Where(d => d.Id == attachment.SubWorkflowDefinitionId)
            .Select(d => d.Name)
            .SingleAsync(ct).ConfigureAwait(false);

        return ToSnapshot(instance, name, version.Version);
    }

    /// <summary>
    /// Spawns whatever is attached to a newly created task with <c>IsAutomatic</c> set.
    ///
    /// Called wherever a task is created rather than only on routing, so a task reached
    /// by a fork branch or a re-fork gets its automatic sub-workflows too — a rule that
    /// applied on some paths and not others would be worse than no rule.
    /// </summary>
    private async Task SpawnAutomaticAsync(WorkflowTask task, string actorId, CancellationToken ct)
    {
        var attachments = await db.WorkflowSubWorkflowAttachments
            .Where(a => a.TaskDefinitionId == task.TaskDefinitionId && a.IsAutomatic && !a.IsArchived)
            .OrderBy(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var attachment in attachments)
        {
            // Nobody to ask on an automatic spawn, so it inherits the parent as before.
            await SpawnAsync(task, attachment, actorId, requested: null, notes: null, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes an instance once nothing in it is still open, and unblocks the parent.
    ///
    /// Called after a sub-workflow task completes. "Nothing open" rather than "this was
    /// the last task" because a sub-workflow can fork, and a branch completing is not the
    /// instance completing.
    /// </summary>
    private async Task MaybeCompleteInstanceAsync(
        int instanceId, string actorId, DateTime now, CancellationToken ct)
    {
        var instance = await db.WorkflowSubWorkflowInstances
            .SingleOrDefaultAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);

        if (instance is null || instance.Status != SubWorkflowStatus.Running)
        {
            return;
        }

        var stillOpen = await db.WorkflowTasks
            .AnyAsync(t => t.SubWorkflowInstanceId == instanceId
                        && t.Status != WorkflowTaskStatus.Completed
                        && t.Status != WorkflowTaskStatus.Cancelled
                        && t.Status != WorkflowTaskStatus.Forked, ct)
            .ConfigureAwait(false);

        if (stillOpen)
        {
            return;
        }

        instance.Status = SubWorkflowStatus.Completed;
        instance.CompletedDate = now;
        instance.ModifierId = actorId;
        instance.Modified = now;

        Log(instance.ParentTaskId, "SubWorkflowCompleted", $"Instance {instance.Id} finished.",
            actorId, now);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var parent = await db.WorkflowTasks
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .SingleOrDefaultAsync(t => t.Id == instance.ParentTaskId, ct).ConfigureAwait(false);

        if (parent is not null)
        {
            await FireAsync(parent, WorkflowEventKind.SubWorkflowCompleted, actorId, ct: ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels the instances hanging off a task that is being cancelled.
    ///
    /// Without this, cancelling a parent leaves its sub-workflow tasks assigned to
    /// people and its instances Running forever — work nobody can reach and nothing will
    /// ever close.
    /// </summary>
    private async Task CancelInstancesForAsync(
        int parentTaskId, string actorId, DateTime now, CancellationToken ct)
    {
        var instances = await db.WorkflowSubWorkflowInstances
            .Where(i => i.ParentTaskId == parentTaskId && i.Status == SubWorkflowStatus.Running)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var instance in instances)
        {
            await CancelInstanceAsync(instance, actorId, "Parent task cancelled.", now, ct)
                .ConfigureAwait(false);
        }

        if (instances.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // One event per instance, matching how completion fires: a trigger counting
            // delegated chains should see the same number of endings either way.
            foreach (var _ in instances)
            {
                await FireSubWorkflowCancelledAsync(parentTaskId, actorId, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Announces that a sub-workflow was abandoned. Mirrors how
    /// <see cref="MaybeCompleteInstanceAsync"/> announces the other ending, so a trigger
    /// hears about both and can tell them apart.
    /// </summary>
    private async Task FireSubWorkflowCancelledAsync(
        int parentTaskId, string actorId, CancellationToken ct)
    {
        var parent = await db.WorkflowTasks
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .SingleOrDefaultAsync(t => t.Id == parentTaskId, ct).ConfigureAwait(false);

        if (parent is not null)
        {
            await FireAsync(parent, WorkflowEventKind.SubWorkflowCancelled, actorId, ct: ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels one instance and everything still open inside it. Shared by the
    /// parent-cancellation cascade and by <see cref="CancelSubWorkflowAsync"/>, so the
    /// two can never disagree about what cancelling an instance means.
    ///
    /// Does not save: callers batch.
    /// </summary>
    private async Task CancelInstanceAsync(
        SubWorkflowInstance instance, string actorId, string reason, DateTime now,
        CancellationToken ct)
    {
        instance.Status = SubWorkflowStatus.Cancelled;
        instance.CompletedDate = now;
        instance.ModifierId = actorId;
        instance.Modified = now;

        // Forked is excluded because it is terminal: a task superseded by a fork is
        // already closed, and overwriting it with Cancelled would destroy the record of
        // why it closed. MaybeCompleteInstanceAsync defines "still open" the same way,
        // and the two must agree or an instance can close on tasks this would reopen.
        var open = await db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId == instance.Id
                     && t.Status != WorkflowTaskStatus.Completed
                     && t.Status != WorkflowTaskStatus.Cancelled
                     && t.Status != WorkflowTaskStatus.Forked)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var task in open)
        {
            task.Status = WorkflowTaskStatus.Cancelled;
            task.ModifierId = actorId;
            task.Modified = now;

            Log(task.Id, "Cancelled", reason, actorId, now);
        }
    }

    /// <summary>
    /// Blocking instances that would stop a task completing. The inbox's own blocked-ids
    /// query (WorkflowEngine.Inbox.cs) is the sibling definition of the same rule — if
    /// this changes, check there too, and vice versa.
    /// </summary>
    private async Task<List<string>> BlockingInstanceNamesAsync(int taskId, CancellationToken ct) =>
        await db.WorkflowSubWorkflowInstances
            .Where(i => i.ParentTaskId == taskId
                     && i.IsBlocking
                     && i.Status == SubWorkflowStatus.Running
                     && !i.IsArchived)
            .Select(i => i.DefinitionVersion!.WorkflowDefinition!.Name)
            .ToListAsync(ct).ConfigureAwait(false);

    // ───────────────────────────────── Reads ─────────────────────────────────

    /// <summary>Sub-workflows that may be attached to a task, and whether each already
    /// has one running.</summary>
    public Task<Result<IReadOnlyList<SubWorkflowOption>>> GetSubWorkflowOptionsAsync(
        int taskId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var task = await db.WorkflowTasks
                .Where(t => t.Id == taskId)
                .Select(t => new
                {
                    t.TaskDefinitionId,
                    VersionId = t.TaskDefinition!.WorkflowDefinitionVersionId
                })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new WorkflowNotFoundException("Task", taskId.ToString());

            var attachments = await db.WorkflowSubWorkflowAttachments
                .Include(a => a.SubWorkflowDefinition)
                .Where(a => a.WorkflowDefinitionVersionId == task.VersionId
                         && (a.TaskDefinitionId == null
                          || a.TaskDefinitionId == task.TaskDefinitionId)
                         && !a.IsArchived)
                .ToListAsync(ct).ConfigureAwait(false);

            // One row per sub-workflow, the task-specific one where both exist.
            attachments = [.. attachments
                .GroupBy(a => a.SubWorkflowDefinitionId)
                .Select(g => g.OrderByDescending(a => a.TaskDefinitionId != null).First())
                .OrderBy(a => a.SubWorkflowDefinition!.Name)];

            var runningIds = await db.WorkflowSubWorkflowInstances
                .Where(i => i.ParentTaskId == taskId && i.Status == SubWorkflowStatus.Running)
                .Select(i => i.DefinitionVersion!.WorkflowDefinitionId)
                .ToListAsync(ct).ConfigureAwait(false);

            return (IReadOnlyList<SubWorkflowOption>)
            [
                .. attachments.Select(a => new SubWorkflowOption(
                    a.SubWorkflowDefinitionId,
                    a.SubWorkflowDefinition?.Name ?? $"#{a.SubWorkflowDefinitionId}",
                    a.IsBlocking,
                    a.IsAutomatic,
                    // Nothing to offer when one is running and only one is allowed.
                    CanStart: a.AllowMultiple || !runningIds.Contains(a.SubWorkflowDefinitionId)))
            ];
        });

    public Task<Result<IReadOnlyList<SubWorkflowInstanceSnapshot>>> GetSubWorkflowInstancesAsync(
        int runId, CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            var instances = await db.WorkflowSubWorkflowInstances
                .Include(i => i.DefinitionVersion!).ThenInclude(v => v.WorkflowDefinition)
                .AsNoTracking()
                .Where(i => i.WorkflowRunId == runId && !i.IsArchived)
                .OrderBy(i => i.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            return (IReadOnlyList<SubWorkflowInstanceSnapshot>)
            [
                .. instances.Select(i => new SubWorkflowInstanceSnapshot(
                    i.Id,
                    i.SubWorkflowDefinitionVersionId,
                    i.DefinitionVersion?.WorkflowDefinition?.Name ?? "(unknown)",
                    i.DefinitionVersion?.Version ?? 0,
                    i.ParentTaskId,
                    i.WorkflowRunId,
                    i.Status,
                    i.IsBlocking,
                    i.CompletedDate))
            ];
        });

    private static SubWorkflowInstanceSnapshot ToSnapshot(
        SubWorkflowInstance i, string name, int version) =>
        new(i.Id, i.SubWorkflowDefinitionVersionId, name, version,
            i.ParentTaskId, i.WorkflowRunId, i.Status, i.IsBlocking, i.CompletedDate);
}

public sealed record SubWorkflowOption(
    int SubWorkflowDefinitionId, string Name, bool IsBlocking, bool IsAutomatic, bool CanStart);

public sealed record SubWorkflowInstanceSnapshot(
    int Id,
    int SubWorkflowDefinitionVersionId,
    string Name,
    int Version,
    int ParentTaskId,
    int WorkflowRunId,
    SubWorkflowStatus Status,
    bool IsBlocking,
    DateTime? CompletedDate);
