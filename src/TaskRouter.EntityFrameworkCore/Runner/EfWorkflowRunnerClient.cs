using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;
using TaskRouter.Core.Runner;

namespace TaskRouter.EntityFrameworkCore.Runner;

/// <summary>
/// The in-process half of the runner UI, for a host running the engine in the same process.
/// A WebAssembly host uses <c>HttpWorkflowRunnerClient</c> instead — it has no DbContext,
/// because it runs in a browser.
///
/// <para>Almost everything here is a straight delegate to <see cref="IWorkflowEngine"/> with
/// the engine's <c>Result</c> flattened into a message the component can render. The parts
/// that are not are the things the engine deliberately has no opinion on:</para>
///
/// <list type="bullet">
///   <item><b>Who may act on a task.</b> The rule is the host's, in its
///   <see cref="IWorkflowAuthorizationPolicy"/>. <see cref="RunTaskView.CanComplete"/> is
///   derived from that same policy rather than decided here, so the button and the engine's
///   gate cannot drift apart — a button the engine would then refuse is worse than no
///   button.</item>
///   <item><b>Who people are and what org units exist.</b> Asked of
///   <see cref="IWorkflowActorResolver"/>, because the engine stores opaque ids and models no
///   user entity.</item>
/// </list>
///
/// <para><b>Reads degrade, writes do not.</b> The read methods flatten a failed
/// <c>Result</c> into an empty list, because a run view missing one panel is more useful than
/// an error page. The write methods return the failure as a
/// <see cref="RunnerResult"/> for the component to show. The one place that must not degrade
/// is the inbox, and it is a separate class for that reason.</para>
/// </summary>
public sealed class EfWorkflowRunnerClient(
    IWorkflowDbContext db,
    IWorkflowEngine engine,
    IWorkflowActorResolver actors,
    IWorkflowAuthorizationPolicy authorization) : IWorkflowRunnerClient
{
    /// <summary>Subject type used by the builder's test pane. Nothing in the engine
    /// treats it specially; the <c>IsTest</c> flag on the run is what matters.</summary>
    public const string TestSubjectType = "WorkflowTest";

    // ───────────────────────────────── Reads ─────────────────────────────────

    public async Task<IReadOnlyList<RunView>> GetRunsAsync(
        string subjectType, string subjectId, CancellationToken ct = default)
    {
        var result = await engine
            .GetRunsForSubjectAsync(new WorkflowSubject(subjectType, subjectId), ct)
            .ConfigureAwait(false);

        if (result.IsError)
        {
            return [];
        }

        var runs = result.Unwrap();
        var names = await WorkflowNamesAsync(runs.Select(r => r.WorkflowDefinitionVersionId), ct)
            .ConfigureAwait(false);
        var started = await StartedAtAsync(runs.Select(r => r.Id), ct).ConfigureAwait(false);

        return [.. runs
            .OrderByDescending(r => r.Id)
            .Select(r => ToRunView(r, names, started))];
    }

    public async Task<RunDetail?> GetRunAsync(int runId, string actorId, CancellationToken ct = default)
    {
        var result = await engine.GetRunAsync(runId, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return null;
        }

        var run = result.Unwrap();
        var names = await WorkflowNamesAsync([run.WorkflowDefinitionVersionId], ct).ConfigureAwait(false);
        var started = await StartedAtAsync([run.Id], ct).ConfigureAwait(false);

        // One query for the definition flags the snapshot does not carry, rather than
        // one per task.
        var definitionIds = run.Tasks.Select(t => t.TaskDefinitionId).Distinct().ToList();

        // The version comes back alongside the flags because an attachment is scoped by
        // version, and a run's tasks can span two of them once sub-workflow instances
        // are involved.
        var definitions = await db.WorkflowTaskDefinitions
            .AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id))
            .Select(d => new
            {
                d.Id,
                d.IsAdHoc,
                d.IsBlocking,
                d.IsForkable,
                d.IsConvergencePoint,
                d.WorkflowDefinitionVersionId
            })
            .ToDictionaryAsync(d => d.Id, ct)
            .ConfigureAwait(false);

        var created = await db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.WorkflowRunId == runId)
            .Select(t => new { t.Id, t.Created })
            .ToDictionaryAsync(t => t.Id, t => t.Created, ct)
            .ConfigureAwait(false);

        // Blocking is counted separately from running: only a blocking instance stops
        // the parent completing, and the runner has to disable Complete for exactly
        // those rather than for any delegation at all.
        var subWorkflowCounts = await db.WorkflowSubWorkflowInstances
            .AsNoTracking()
            .Where(i => i.WorkflowRunId == runId && i.Status == SubWorkflowStatus.Running)
            .GroupBy(i => i.ParentTaskId)
            .Select(g => new
            {
                ParentTaskId = g.Key,
                Running = g.Count(),
                Blocking = g.Count(i => i.IsBlocking)
            })
            .ToDictionaryAsync(g => g.ParentTaskId, g => (g.Running, g.Blocking), ct)
            .ConfigureAwait(false);

        // Both halves of the scope matter. A null TaskDefinitionId is a version-wide
        // attachment and applies to every task in its version, so it can never be
        // matched by comparing ids; and an attachment authored against another version
        // must not make a task here look attachable.
        var attachments = await db.WorkflowSubWorkflowAttachments
            .AsNoTracking()
            .Where(a => !a.IsArchived)
            .Select(a => new { a.WorkflowDefinitionVersionId, a.TaskDefinitionId })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // What the org units are called, so a task does not show a bare branch key. Fetched
        // once for the run, and only when a task actually carries one.
        var branchLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        if (run.Tasks.Any(t => t.AssignedBranchKey is not null))
        {
            foreach (var option in await actors.GetBranchOptionsAsync(ct).ConfigureAwait(false))
            {
                branchLabels[option.Key] = option.DisplayName;
            }
        }

        var displayNames = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var assignee in run.Tasks
                     .Select(t => t.AssignedToActorId)
                     .Where(a => !string.IsNullOrWhiteSpace(a))
                     .Distinct()!)
        {
            displayNames[assignee!] = await actors.GetDisplayNameAsync(assignee!, ct).ConfigureAwait(false);
        }

        var tasks = new List<RunTaskView>();

        foreach (var t in run.Tasks)
        {
            var d = definitions.GetValueOrDefault(t.TaskDefinitionId);

            // Null only if the definition has since been deleted; a lifted
            // comparison then matches no attachment, which is the right answer.
            var versionId = d?.WorkflowDefinitionVersionId;

            tasks.Add(new RunTaskView(
                Id: t.Id,
                TaskDefinitionId: t.TaskDefinitionId,
                TaskTypeKey: t.TaskTypeKey,
                Label: t.DisplayName,
                Status: t.Status,
                OutcomeKey: t.OutcomeKey,
                AssignedToActorId: t.AssignedToActorId,
                AssignedToDisplayName: t.AssignedToActorId is { } a
                    ? displayNames.GetValueOrDefault(a)
                    : null,
                AssignedBranchKey: t.AssignedBranchKey,
                DueDate: t.DueDate,
                Notes: t.Notes,
                ParentTaskId: t.ParentTaskId,
                IsAdHoc: d?.IsAdHoc ?? false,
                IsBlocking: d?.IsBlocking ?? false,
                IsForkOrigin: t.IsForkOrigin,
                IsForkable: d?.IsForkable ?? false,
                RunningSubWorkflowCount: subWorkflowCounts.GetValueOrDefault(t.Id).Running,
                BlockingSubWorkflowCount: subWorkflowCounts.GetValueOrDefault(t.Id).Blocking,
                HasSubWorkflowOptions: attachments.Any(a =>
                    a.WorkflowDefinitionVersionId == versionId
                    && (a.TaskDefinitionId is null
                     || a.TaskDefinitionId == t.TaskDefinitionId)),
                IsConvergencePoint: d?.IsConvergencePoint ?? false,
                ForkGroupId: t.ForkGroupId,
                Created: created.GetValueOrDefault(t.Id),
                CompletedDate: t.CompletedDate,
                CanComplete: await CanCompleteAsync(t, actorId, ct).ConfigureAwait(false),
                OverdueFiredAt: t.OverdueFiredAt,
                AssignedBranchLabel: t.AssignedBranchKey is { } key
                                     && branchLabels.TryGetValue(key, out var label)
                    ? label
                    : null,
                SubWorkflowInstanceId: t.SubWorkflowInstanceId));
        }

        tasks = [.. tasks.OrderBy(t => t.Created).ThenBy(t => t.Id)];

        return new RunDetail(ToRunView(run, names, started), tasks);
    }

    /// <summary>
    /// Whether the runner offers to complete a task.
    ///
    /// Two conditions, and they answer different questions. The status check asks whether
    /// the task is actionable at all; the policy asks whether this actor may act — the
    /// same policy the engine consults, so the button and the gate cannot disagree. This
    /// used to be a hand-written rule, and a hand-written rule is one that drifts.
    ///
    /// One policy call per task in the list. In-process against the small lists a single
    /// run produces that is fine; a host with a remote directory behind its policy would
    /// want to memoize per request, which the seam does not prevent.
    /// </summary>
    private async Task<bool> CanCompleteAsync(
        WorkflowTaskSnapshot task, string actorId, CancellationToken ct)
    {
        if (task.Status is not (WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress))
        {
            return false;
        }

        // Called directly rather than through the engine, so it inherits neither
        // WorkflowEngine.AuthorizeAsync's try/catch nor its null-result guard. Fail
        // closed here too, the same way the engine does: a policy that throws or answers
        // null must hide the button rather than take the whole run-detail page down with
        // it, or (for null) throw a NullReferenceException reaching for IsAllowed.
        try
        {
            var result = await authorization
                .EvaluateAsync(
                    WorkflowOperation.CompleteTask,
                    new WorkflowAuthorizationContext(actorId, task, null, null),
                    ct)
                .ConfigureAwait(false);

            return result?.IsAllowed ?? false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OutcomeOption>> GetOutcomesAsync(
        int taskId, CancellationToken ct = default)
    {
        var result = await engine.GetValidOutcomesAsync(taskId, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return [];
        }

        // Which outcomes route backwards. On a convergence task those may only be taken
        // through selective rejection, so the UI needs to know before it offers a button.
        var definitionId = await db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.TaskDefinitionId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var rework = await db.WorkflowTaskRoutes
            .AsNoTracking()
            .Where(r => r.TaskDefinitionId == definitionId && r.IsReworkRoute && !r.IsArchived)
            .Select(r => r.Outcome!.OutcomeKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. result.Unwrap().Select(o => new OutcomeOption(
            o.OutcomeKey, o.DisplayName, o.Order,
            rework.Contains(o.OutcomeKey, StringComparer.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// The run's steps, its tasks, and its standing pre-assignments, lined up.
    ///
    /// <para>Definitions rather than tasks, because the point of the screen is the steps the
    /// run has <b>not</b> reached — those have no task to list. Where a step has run more
    /// than once, the latest task wins: that is the one somebody is holding.</para>
    /// </summary>
    public async Task<IReadOnlyList<PlannedStepView>> GetPlannedStepsAsync(
        int runId, CancellationToken ct = default)
    {
        var versionId = await db.WorkflowRuns
            .AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => (int?)r.WorkflowDefinitionVersionId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (versionId is null)
        {
            return [];
        }

        var definitions = await db.WorkflowTaskDefinitions
            .AsNoTracking()
            .Include(d => d.TaskType)
            .Where(d => d.WorkflowDefinitionVersionId == versionId && !d.IsArchived && !d.IsAdHoc)
            .OrderBy(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var tasks = await db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.WorkflowRunId == runId)
            .OrderByDescending(t => t.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var promised = (await engine.GetPreAssignmentsForRunAsync(runId, ct).ConfigureAwait(false))
            .Match(list => list, _ => []);

        // One directory call for the screen, and only when there is a name to resolve.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        if (tasks.Any(t => t.AssignedToActorId is not null) || promised.Count > 0)
        {
            foreach (var actor in await actors.GetActorsAsync(ct).ConfigureAwait(false))
            {
                names[actor.ActorId] = actor.DisplayName;
            }
        }

        var views = new List<PlannedStepView>();

        foreach (var definition in definitions)
        {
            var latest = tasks.FirstOrDefault(t => t.TaskDefinitionId == definition.Id);
            var standing = promised.FirstOrDefault(p => p.TaskDefinitionId == definition.Id);

            var isOpen = latest is { Status: WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress };

            views.Add(new PlannedStepView(
                definition.Id,
                definition.DisplayName ?? definition.TaskType?.DisplayName ?? $"#{definition.Id}",
                latest?.Status,
                latest?.AssignedToActorId,
                latest?.AssignedToActorId is { } holder ? names.GetValueOrDefault(holder) : null,
                standing?.ActorId,
                standing?.ActorId is { } named ? names.GetValueOrDefault(named) : null,
                standing?.BranchKey,
                CanPreAssign: !definition.IsForkable && !isOpen,
                AssignedDate: latest?.Created,
                CompletedDate: latest?.CompletedDate));
        }

        return views;
    }

    public async Task<RunnerResult> PreAssignAsync(
        int runId, int taskDefinitionId, string assignToActorId, string? branchKey,
        string actorId, CancellationToken ct = default) =>
        Flatten(await engine.PreAssignAsync(
            runId, taskDefinitionId, new WorkflowAssignment(assignToActorId, branchKey), actorId, ct)
            .ConfigureAwait(false));

    public async Task<RunnerResult> RemovePreAssignmentAsync(
        int runId, int taskDefinitionId, string actorId, CancellationToken ct = default) =>
        Flatten(await engine.RemovePreAssignmentAsync(runId, taskDefinitionId, actorId, ct)
            .ConfigureAwait(false));

    public async Task<IReadOnlyList<LogEntryView>> GetLogAsync(int taskId, CancellationToken ct = default)
    {
        var result = await engine.GetTaskLogsAsync(taskId, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return [];
        }

        var entries = result.Unwrap();
        var views = new List<LogEntryView>(entries.Count);

        foreach (var e in entries)
        {
            views.Add(new LogEntryView(
                e.Id, e.Action, e.Note, e.PerformedBy,
                await actors.GetDisplayNameAsync(e.PerformedBy, ct).ConfigureAwait(false),
                e.PerformedAt));
        }

        return views;
    }

    public async Task<ForkInfo?> GetForkInfoAsync(int taskId, CancellationToken ct = default)
    {
        var contextResult = await engine.GetForkContextAsync(taskId, ct).ConfigureAwait(false);

        if (contextResult.IsError)
        {
            return null;
        }

        var context = contextResult.Unwrap();

        if (!context.IsPartOfFork && !context.IsConvergenceTask)
        {
            return null;
        }

        var branches = context.IsConvergenceTask && context.ForkManifestId is { } manifestId
            ? await ManifestBranchesAsync(manifestId, ct).ConfigureAwait(false)
            : await LiveBranchesAsync(context.ForkGroupId, ct).ConfigureAwait(false);

        return new ForkInfo(
            context.IsPartOfFork,
            context.ForkGroupId,
            context.IsConvergenceTask,
            context.ForkManifestId,
            context.CompletedBranchCount,
            context.CancelledBranchCount,
            context.PendingBranchCount,
            branches);
    }

    /// <summary>
    /// Branches as the manifest recorded them. This is what selective rejection picks
    /// from: by the time a convergence task is actionable the branch tasks are closed,
    /// and the manifest is the record of what each one decided.
    /// </summary>
    private async Task<IReadOnlyList<ForkBranchView>> ManifestBranchesAsync(
        int manifestId, CancellationToken ct)
    {
        var result = await engine.GetForkManifestAsync(manifestId, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            return [];
        }

        var views = new List<ForkBranchView>();

        foreach (var entry in result.Unwrap().Entries)
        {
            views.Add(new ForkBranchView(
                entry.BranchKey,
                TaskId: null,
                Status: null,
                entry.BranchOutcomeKey,
                entry.AssignedToActorId,
                entry.AssignedToActorId is { } a
                    ? await actors.GetDisplayNameAsync(a, ct).ConfigureAwait(false)
                    : null,
                entry.BranchNotes));
        }

        return views;
    }

    /// <summary>Branches as they stand right now, for a fork still in progress.</summary>
    private async Task<IReadOnlyList<ForkBranchView>> LiveBranchesAsync(
        Guid? forkGroupId, CancellationToken ct)
    {
        if (forkGroupId is not { } groupId)
        {
            return [];
        }

        var tasks = await db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.ForkGroupId == groupId && !t.IsForkOrigin)
            .OrderBy(t => t.AssignedBranchKey)
            .Select(t => new
            {
                t.Id, t.AssignedBranchKey, t.Status, t.OutcomeKey, t.AssignedToActorId, t.Notes
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var views = new List<ForkBranchView>(tasks.Count);

        foreach (var t in tasks)
        {
            views.Add(new ForkBranchView(
                t.AssignedBranchKey ?? "(no branch)",
                t.Id,
                t.Status,
                t.OutcomeKey,
                t.AssignedToActorId,
                t.AssignedToActorId is { } a
                    ? await actors.GetDisplayNameAsync(a, ct).ConfigureAwait(false)
                    : null,
                t.Notes));
        }

        return views;
    }

    /// <summary>
    /// The host's org units. Sections here; the engine only ever sees the key.
    /// Ones already branched on are left out, since the engine rejects duplicates.
    /// </summary>
    public async Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(
        int taskId, CancellationToken ct = default)
    {
        var forkGroupId = await db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.ForkGroupId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var taken = forkGroupId is null
            ? []
            : await db.WorkflowTasks
                .AsNoTracking()
                .Where(t => t.ForkGroupId == forkGroupId && !t.IsForkOrigin && t.AssignedBranchKey != null)
                .Select(t => t.AssignedBranchKey!)
                .ToListAsync(ct)
                .ConfigureAwait(false);

        // The host lists its org units; the engine removes the ones this fork has already
        // branched on. Split here rather than at the seam because "already branched on" is
        // fork state, not org chart -- a host filtering it would be reimplementing a rule
        // the engine enforces anyway, and would get an exception at fork time when it got
        // it wrong.
        var all = await actors.GetBranchOptionsAsync(ct).ConfigureAwait(false);

        return [.. all.Where(o => !taken.Contains(o.Key))];
    }

    /// <summary>
    /// Every org unit, straight from the directory. No fork filtering — see the seam's
    /// documentation for why reassignment and forking want different lists.
    /// </summary>
    public async Task<IReadOnlyList<BranchOption>> GetOrgUnitsAsync(CancellationToken ct = default) =>
        await actors.GetBranchOptionsAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Convergence candidates from the version <b>this task's definition</b> belongs to — the
    /// engine refuses a convergence task from any other.
    ///
    /// <para><b>Not the run's version.</b> A run is pinned to the mainline version, but a task
    /// inside a sub-workflow belongs to the sub-workflow's. Reading it from the run offered
    /// the mainline's task definitions for a delegated task, so every choice on the screen was
    /// one the engine would refuse — forking inside a sub-workflow could not succeed at all,
    /// and the refusal named the version rather than the picker.</para>
    /// </summary>
    public async Task<IReadOnlyList<ConvergenceOption>> GetConvergenceOptionsAsync(
        int taskId, CancellationToken ct = default)
    {
        var context = await db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new
            {
                VersionId = t.TaskDefinition!.WorkflowDefinitionVersionId,
                t.TaskDefinitionId
            })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (context is null)
        {
            return [];
        }

        return await db.WorkflowTaskDefinitions
            .AsNoTracking()
            .Include(d => d.TaskType)
            .Where(d => d.WorkflowDefinitionVersionId == context.VersionId
                     && d.Id != context.TaskDefinitionId
                     && !d.IsAdHoc
                     && !d.IsArchived)
            .OrderByDescending(d => d.IsConvergencePoint)
            .ThenBy(d => d.Id)
            .Select(d => new ConvergenceOption(
                d.Id,
                d.DisplayName ?? d.TaskType!.DisplayName,
                d.IsConvergencePoint))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ad-hoc task definitions from the version this task's definition belongs to.
    ///
    /// <para>From the definition rather than the run, for the reason spelled out on
    /// <see cref="GetConvergenceOptionsAsync"/>: a delegated task belongs to the
    /// sub-workflow's version, and offering the mainline's ad-hoc definitions under it
    /// produces a task the engine will not accept.</para>
    /// </summary>
    public async Task<IReadOnlyList<AdHocOption>> GetAdHocOptionsAsync(
        int taskId, CancellationToken ct = default)
    {
        var versionId = await db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => (int?)t.TaskDefinition!.WorkflowDefinitionVersionId)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (versionId is null)
        {
            return [];
        }

        return await db.WorkflowTaskDefinitions
            .AsNoTracking()
            .Include(d => d.TaskType)
            .Where(d => d.WorkflowDefinitionVersionId == versionId && d.IsAdHoc && !d.IsArchived)
            .Select(d => new AdHocOption(d.Id, d.DisplayName ?? d.TaskType!.DisplayName))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubWorkflowOptionView>> GetSubWorkflowOptionsAsync(
        int taskId, CancellationToken ct = default)
    {
        var result = await engine.GetSubWorkflowOptionsAsync(taskId, ct).ConfigureAwait(false);

        return result.IsError
            ? []
            : [.. result.Unwrap().Select(o => new SubWorkflowOptionView(
                o.SubWorkflowDefinitionId, o.Name, o.IsBlocking, o.CanStart))];
    }

    public async Task<RunnerResult> StartSubWorkflowAsync(
        int parentTaskId, int subWorkflowDefinitionId, string actorId,
        string? assignToActorId, string? assignToBranchKey, string? notes,
        CancellationToken ct = default)
    {
        var assignment = await SubWorkflowAssignmentAsync(
            assignToActorId, assignToBranchKey, ct).ConfigureAwait(false);

        var result = await engine
            .StartSubWorkflowAsync(
                parentTaskId, subWorkflowDefinitionId, actorId, assignment, notes, ct)
            .ConfigureAwait(false);

        return result.IsOk ? RunnerResult.Ok : RunnerResult.Failed(result.UnwrapError().Message);
    }

    /// <summary>
    /// What a delegated chain belongs to: an org unit, a person, both, or neither.
    ///
    /// <para>Neither is null — inherit the parent task's assignment, which is what starting a
    /// sub-workflow meant before anything could be chosen.</para>
    ///
    /// <para>A person with no unit asks the directory for theirs. Losing that fallback would
    /// silently change what an untouched unit picker does, and every role in the chain
    /// resolves against the unit.</para>
    ///
    /// <para>A unit with no person is a section taking the work with nobody named yet — the
    /// first task lands unassigned in that unit, for somebody there to pick up.</para>
    /// </summary>
    private async Task<WorkflowAssignment?> SubWorkflowAssignmentAsync(
        string? actorId, string? branchKey, CancellationToken ct)
    {
        if (actorId is null && branchKey is null)
        {
            return null;
        }

        if (branchKey is not null)
        {
            return new WorkflowAssignment(actorId, branchKey);
        }

        return await actors.GetAssignmentForAsync(actorId!, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubWorkflowInstanceView>> GetSubWorkflowInstancesAsync(
        int runId, CancellationToken ct = default)
    {
        var result = await engine.GetSubWorkflowInstancesAsync(runId, ct).ConfigureAwait(false);

        return result.IsError
            ? []
            : [.. result.Unwrap().Select(i => new SubWorkflowInstanceView(
                i.Id,
                i.ParentTaskId,
                i.Name,
                i.IsBlocking,
                i.Status == SubWorkflowStatus.Running))];
    }

    public async Task<RunnerResult> CancelRunAsync(
        int runId, string actorId, string? reason, CancellationToken ct = default) =>
        Flatten(await engine.CancelRunAsync(runId, actorId, reason, ct).ConfigureAwait(false));

    public async Task<RunnerResult> CancelSubWorkflowAsync(
        int instanceId, string actorId, string? reason, CancellationToken ct = default) =>
        Flatten(await engine.CancelSubWorkflowAsync(instanceId, actorId, reason, ct)
            .ConfigureAwait(false));

    public async Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default) =>
        await actors.GetActorsAsync(ct).ConfigureAwait(false);

    // ───────────────────────────────── Writes ─────────────────────────────────

    public async Task<RunnerResult> CompleteAsync(
        int taskId, string outcomeKey, string actorId, string? notes, CancellationToken ct = default) =>
        Flatten(await engine.CompleteTaskAsync(taskId, outcomeKey, actorId, notes, ct).ConfigureAwait(false));

    public async Task<RunnerResult> CancelAsync(
        int taskId, string actorId, string? note, CancellationToken ct = default) =>
        Flatten(await engine.CancelTaskAsync(taskId, actorId, note, ct).ConfigureAwait(false));

    public async Task<RunnerResult> ReassignAsync(
        int taskId, string? toActorId, string? toBranchKey, string actorId, string? note,
        CancellationToken ct = default) =>
        Flatten(await engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(toActorId, toBranchKey), actorId, note, ct).ConfigureAwait(false));

    public async Task<RunnerResult> UpdateNotesAsync(
        int taskId, string? notes, string actorId, CancellationToken ct = default) =>
        Flatten(await engine.UpdateTaskNotesAsync(taskId, notes, actorId, ct).ConfigureAwait(false));

    public async Task<RunnerResult> AddAdHocAsync(
        int parentTaskId, int taskDefinitionId, string actorId, string? notes,
        CancellationToken ct = default)
    {
        var result = await engine
            .AddAdHocTaskAsync(parentTaskId, taskDefinitionId, actorId, notes: notes, ct: ct)
            .ConfigureAwait(false);

        return result.IsOk ? RunnerResult.Ok : RunnerResult.Failed(result.UnwrapError().Message);
    }

    public async Task<RunnerResult> ForkAsync(
        int taskId, IReadOnlyList<string> branchKeys, int convergenceTaskDefinitionId,
        string actorId, string? notes, CancellationToken ct = default)
    {
        var result = await engine
            .ForkTaskAsync(taskId, branchKeys, convergenceTaskDefinitionId, actorId, notes, ct)
            .ConfigureAwait(false);

        return result.IsOk ? RunnerResult.Ok : RunnerResult.Failed(result.UnwrapError().Message);
    }

    public async Task<RunnerResult> AddBranchesAsync(
        Guid forkGroupId, IReadOnlyList<string> branchKeys, string actorId, string? notes,
        CancellationToken ct = default)
    {
        var result = await engine
            .AddBranchToForkAsync(forkGroupId, branchKeys, actorId, notes, ct)
            .ConfigureAwait(false);

        return result.IsOk ? RunnerResult.Ok : RunnerResult.Failed(result.UnwrapError().Message);
    }

    public async Task<RunnerResult> CompleteWithSelectiveRejectionAsync(
        int convergenceTaskId, string outcomeKey, IReadOnlyList<string> rejectedBranchKeys,
        string actorId, string? notes, CancellationToken ct = default) =>
        Flatten(await engine.CompleteWithSelectiveRejectionAsync(
            convergenceTaskId, outcomeKey, rejectedBranchKeys, actorId, notes, ct).ConfigureAwait(false));

    public async Task<TestRunResult> StartTestRunAsync(
        int workflowDefinitionVersionId, string actorId, CancellationToken ct = default)
    {
        // A synthetic subject id per run, so repeated tests do not pile onto each other.
        var subject = new WorkflowSubject(TestSubjectType, Guid.NewGuid().ToString("N")[..12]);

        var result = await engine
            .StartRunOnVersionAsync(subject, workflowDefinitionVersionId, actorId, isTest: true, ct: ct)
            .ConfigureAwait(false);

        return result.IsOk
            ? TestRunResult.Started(result.Unwrap().Id)
            : TestRunResult.Failed(result.UnwrapError().Message);
    }

    public async Task<RunnerResult> DeleteTestRunAsync(
        int runId, string actorId, CancellationToken ct = default)
    {
        var run = await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);

        if (run is null)
        {
            return RunnerResult.Failed($"Run {runId} not found.");
        }

        // The guard is the point: this is a hard delete, and it must never be reachable
        // for work someone actually did.
        if (!run.IsTest)
        {
            return RunnerResult.Failed("Only test runs can be deleted.");
        }

        var taskIds = await db.WorkflowTasks
            .Where(t => t.WorkflowRunId == runId).Select(t => t.Id).ToListAsync(ct).ConfigureAwait(false);

        db.WorkflowTaskLogs.RemoveRange(
            await db.WorkflowTaskLogs.Where(l => taskIds.Contains(l.TaskId))
                .ToListAsync(ct).ConfigureAwait(false));

        db.WorkflowTriggerExecutions.RemoveRange(
            await db.WorkflowTriggerExecutions.Where(x => taskIds.Contains(x.TaskId))
                .ToListAsync(ct).ConfigureAwait(false));

        db.WorkflowVariables.RemoveRange(
            await db.WorkflowVariables.Where(v => v.WorkflowRunId == runId)
                .ToListAsync(ct).ConfigureAwait(false));

        // Delegated chains and forks, which the run's cascade cannot reach.
        //
        // A sub-workflow instance points at the task it hangs off, and that relationship is
        // Restrict on purpose -- a task with live delegated work under it must not vanish.
        // Discarding a test run is the one case where the whole tree is meant to go, so it
        // has to take these out itself. Until it did, the first delegation tried in the
        // builder's test pane left that run undeletable, and the only signal was a 500.
        db.WorkflowSubWorkflowInstances.RemoveRange(
            await db.WorkflowSubWorkflowInstances.Where(i => i.WorkflowRunId == runId)
                .ToListAsync(ct).ConfigureAwait(false));

        // Manifest entries cascade from the manifest; the manifests themselves are reached
        // through the run. A convergence task references its manifest with Restrict, so the
        // manifests have to outlive the tasks -- which they do, because the tasks go with
        // the run in the second save below.
        var manifests = await db.WorkflowForkManifests
            .Where(m => m.WorkflowRunId == runId).ToListAsync(ct).ConfigureAwait(false);

        // Two saves, and the split is load-bearing.
        //
        // The tasks are never tracked here -- only their ids were selected -- so EF does not
        // know that run -> task -> log is a chain, and is free to order the run's DELETE
        // before the logs'. The database's cascade then removes those log rows itself, and
        // EF's own DELETE for each affects zero rows, which it reports as a concurrency
        // conflict. Saving the children first removes the ambiguity rather than relying on
        // an ordering EF never promised.
        //
        // This survived in the demo only by accident: there the client shared one DbContext
        // with everything else the test did, so the tasks happened to be tracked and the
        // chain happened to be visible. Over HTTP each request is its own scope and nothing
        // is tracked, which is what exposed it.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        db.WorkflowRuns.Remove(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Last, because a task referencing a manifest is Restrict and those tasks only went
        // with the run a moment ago.
        if (manifests.Count > 0)
        {
            db.WorkflowForkManifests.RemoveRange(manifests);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return RunnerResult.Ok;
    }

    // ───────────────────────────────── Mapping ─────────────────────────────────

    private async Task<Dictionary<int, (string Name, int Version)>> WorkflowNamesAsync(
        IEnumerable<int> versionIds, CancellationToken ct) =>
        await db.WorkflowDefinitionVersions
            .AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, Name = v.WorkflowDefinition!.Name, v.Version })
            .ToDictionaryAsync(v => v.Id, v => (v.Name, v.Version), ct)
            .ConfigureAwait(false);

    /// <summary>Run start times. The snapshot does not carry one, and the runner lists
    /// runs newest first with a date beside each.</summary>
    private async Task<Dictionary<int, DateTime>> StartedAtAsync(
        IEnumerable<int> runIds, CancellationToken ct) =>
        await db.WorkflowRuns
            .AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Created })
            .ToDictionaryAsync(r => r.Id, r => r.Created, ct)
            .ConfigureAwait(false);

    private static RunView ToRunView(
        WorkflowRunSnapshot run,
        Dictionary<int, (string Name, int Version)> names,
        Dictionary<int, DateTime> started)
    {
        var (name, version) = names.GetValueOrDefault(
            run.WorkflowDefinitionVersionId, ("(unknown workflow)", run.Version));

        return new RunView(
            Id: run.Id,
            SubjectType: run.Subject.SubjectType,
            SubjectId: run.Subject.SubjectId,
            WorkflowName: name,
            Version: version,
            Status: run.Status,
            IsTest: run.IsTest,
            Created: started.GetValueOrDefault(run.Id),
            OpenTaskCount: run.Tasks.Count(t =>
                t.Status is WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress),
            PercentComplete: WorkflowProgress.Calculate(run, null));
    }

    /// <summary>The component gets a message, never an exception to render.</summary>
    private static RunnerResult Flatten(Result<Unit> result) =>
        result.IsOk ? RunnerResult.Ok : RunnerResult.Failed(result.UnwrapError().Message);
}
