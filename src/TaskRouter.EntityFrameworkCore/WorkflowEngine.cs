using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

public sealed partial class WorkflowEngine(
    IWorkflowDbContext db,
    IWorkflowAssignmentResolver assignmentResolver,
    IEnumerable<IRouteConditionEvaluator> conditionEvaluators,
    ILogger<WorkflowEngine> logger,
    Triggers.IWorkflowTriggerDispatcher? dispatcher = null,
    IWorkflowPostCommitActions? postCommit = null,
    IWorkflowDueDateResolver? dueDateResolver = null,
    IWorkflowAuthorizationPolicy? authorizationPolicy = null) : IWorkflowEngine
{
    /// <summary>
    /// Every engine operation runs through here, so anything queued for after the commit
    /// runs at the one moment the data is durable — and never at all if the transaction
    /// rolled back.
    /// </summary>
    private Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) =>
        WorkflowTransaction.ExecuteAsync(db, work, ct, postCommit);

    private Task InTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct) =>
        WorkflowTransaction.ExecuteAsync(db, work, ct, postCommit);

    /// <summary>
    /// Raises an engine event. In-transaction triggers run now; after-commit ones are
    /// written to the outbox inside this transaction. Optional so the engine can be
    /// constructed without a trigger subsystem.
    /// </summary>
    private Task FireAsync(
        WorkflowTask task,
        WorkflowEventKind eventKind,
        string actorId,
        BranchContext? branch = null,
        CancellationToken ct = default) =>
        dispatcher is null
            ? Task.CompletedTask
            : dispatcher.DispatchAsync(task, eventKind, actorId, null, branch, ct);

    private readonly Dictionary<string, IRouteConditionEvaluator> _conditions =
        BuildConditionMap(conditionEvaluators);

    private static Dictionary<string, IRouteConditionEvaluator> BuildConditionMap(
        IEnumerable<IRouteConditionEvaluator> evaluators)
    {
        // Grouped rather than ToDictionary: a duplicate key must not take the whole
        // engine down at first use, which is what the original engine's trigger map did (finding M6).
        var map = new Dictionary<string, IRouteConditionEvaluator>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in evaluators.GroupBy(e => e.ConditionKey))
        {
            map[group.Key] = group.Last();
        }

        return map;
    }

    // ─────────────────────────────── Run lifecycle ───────────────────────────────

    public Task<Result<WorkflowRunSnapshot>> StartRunAsync(
        WorkflowSubject subject,
        int workflowDefinitionId,
        string actorId,
        WorkflowAssignment? initialAssignment = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            await AuthorizeAsync(
                WorkflowOperation.StartRun, null, actorId, subject, workflowDefinitionId, token)
                .ConfigureAwait(false);

            var version = await db.WorkflowDefinitionVersions
                .Include(v => v.WorkflowDefinition)
                .Include(v => v.Tasks).ThenInclude(t => t.TaskType)
                .Where(v => v.WorkflowDefinitionId == workflowDefinitionId
                         && v.IsPublished && v.IsLatest && !v.IsArchived)
                .SingleOrDefaultAsync(token)
                ?? throw new InvalidOperationException(
                    $"No published version for workflow definition {workflowDefinitionId}.");

            // A sub-workflow hangs off a task; it has no subject of its own and no
            // meaning apart from the parent that started it.
            if (version.WorkflowDefinition?.IsSubWorkflow == true)
            {
                throw new InvalidOperationException(
                    $"'{version.WorkflowDefinition.Name}' is a sub-workflow. Attach it to a " +
                    "task and start it with StartSubWorkflowAsync.");
            }

            return await StartOnAsync(version, subject, actorId, isTest: false, initialAssignment, token)
                .ConfigureAwait(false);
        }, ct));

    /// <summary>
    /// Starts a run on a named version, published or not.
    ///
    /// The ordinary entry point deliberately refuses to do this: runs belong on the
    /// latest published version, and choosing otherwise is how in-flight work gets
    /// routed by a definition nobody approved. This exists for one case — trying a draft
    /// out before publishing it — which is why it will not start a non-test run on an
    /// unpublished version.
    /// </summary>
    public Task<Result<WorkflowRunSnapshot>> StartRunOnVersionAsync(
        WorkflowSubject subject,
        int workflowDefinitionVersionId,
        string actorId,
        bool isTest = false,
        WorkflowAssignment? initialAssignment = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            await AuthorizeAsync(
                WorkflowOperation.StartRun, null, actorId, subject, null, token)
                .ConfigureAwait(false);

            var version = await db.WorkflowDefinitionVersions
                .Include(v => v.Tasks).ThenInclude(t => t.TaskType)
                .Where(v => v.Id == workflowDefinitionVersionId && !v.IsArchived)
                .SingleOrDefaultAsync(token)
                ?? throw new WorkflowNotFoundException(
                    "Workflow version", workflowDefinitionVersionId.ToString());

            if (!version.IsPublished && !isTest)
            {
                throw new InvalidOperationException(
                    $"Version {version.Version} is a draft. Only test runs may start on an " +
                    "unpublished version.");
            }

            return await StartOnAsync(version, subject, actorId, isTest, initialAssignment, token)
                .ConfigureAwait(false);
        }, ct));

    private async Task<WorkflowRunSnapshot> StartOnAsync(
        WorkflowDefinitionVersion version,
        WorkflowSubject subject,
        string actorId,
        bool isTest,
        WorkflowAssignment? initialAssignment,
        CancellationToken token)
    {
        {
            if (version.EntryTaskDefinitionId is not { } entryId)
            {
                throw new InvalidOperationException(
                    $"Workflow version {version.Id} declares no entry task.");
            }

            var entryDef = version.Tasks.SingleOrDefault(t => t.Id == entryId && !t.IsArchived)
                ?? throw new InvalidOperationException(
                    $"Entry task definition {entryId} is missing from version {version.Id}.");

            var now = DateTime.UtcNow;

            var run = new WorkflowRun
            {
                Subject = subject,
                WorkflowDefinitionVersionId = version.Id,   // pinned; edits cannot reroute it
                Status = WorkflowRunStatus.Running,
                IsTest = isTest,
                CreatorId = actorId,
                ModifierId = actorId,
                Created = now,
                Modified = now
            };

            db.WorkflowRuns.Add(run);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            // Seed the funnel's subject cache with the subject we already hold, so the
            // entry task's due date costs no query. This is the one creation path that
            // knows the subject without asking.
            _subjectsByRun[run.Id] = subject;

            // The caller's org unit if it gave one, otherwise the historical default of
            // "assigned to whoever started it, belonging to nowhere". Everything downstream
            // inherits this as its starting assignment, so it is the one place a run's org
            // unit can enter.
            var assignment = await ResolveAssignmentAsync(
                entryDef,
                initialAssignment ?? new WorkflowAssignment(actorId, null),
                null,
                token,
                run.Id).ConfigureAwait(false);

            var task = await NewTaskAsync(run.Id, entryDef, assignment, actorId, now, token).ConfigureAwait(false);
            db.WorkflowTasks.Add(task);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            Log(task.Id, "Created", $"Run started on version {version.Version}", actorId, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            await FireAsync(task, WorkflowEventKind.RunStarted, actorId, ct: token).ConfigureAwait(false);
            await FireAsync(task, WorkflowEventKind.TaskCreated, actorId, ct: token).ConfigureAwait(false);
            await SpawnAutomaticAsync(task, actorId, token).ConfigureAwait(false);

            run.DefinitionVersion = version;
            return run.ToSnapshot([task]);
        }
    }

    // ────────────────────────────── Task completion ──────────────────────────────

    public Task<Result<Unit>> CompleteTaskAsync(
        int taskId,
        string outcomeKey,
        string actorId,
        string? notes = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var task = await LoadTaskAsync(taskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.CompleteTask, task, actorId, null, null, token)
                .ConfigureAwait(false);

            if (task.ForkManifestId.HasValue && await IsRejectionOutcomeAsync(task, outcomeKey, token).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Convergence tasks must reject through selective rejection so the " +
                    "rejected branches can be identified.");
            }

            await ValidateCompletableAsync(task, outcomeKey, token).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            task.Status = WorkflowTaskStatus.Completed;
            task.OutcomeKey = outcomeKey;
            task.CompletedDate = now;
            task.Notes = notes ?? task.Notes;
            task.ModifierId = actorId;
            task.Modified = now;

            Log(task.Id, "Completed", $"Outcome: {outcomeKey}", actorId, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            await RouteFromAsync(task, outcomeKey, actorId, now, token).ConfigureAwait(false);

            var branch = task.ForkGroupId is null
                ? null
                : new BranchContext(task.ForkGroupId, task.ForkManifestId, task.AssignedBranchKey, []);

            await FireAsync(task, WorkflowEventKind.TaskCompleted, actorId, branch, token)
                .ConfigureAwait(false);

            if (task.ForkGroupId is not null)
            {
                await FireAsync(task, WorkflowEventKind.BranchCompleted, actorId, branch, token)
                    .ConfigureAwait(false);
            }

            if (task.SubWorkflowInstanceId is { } instanceId)
            {
                await MaybeCompleteInstanceAsync(instanceId, actorId, now, token).ConfigureAwait(false);
            }

            await MaybeCompleteRunAsync(task.WorkflowRunId, actorId, now, token).ConfigureAwait(false);
        }, ct));

    /// <summary>
    /// Creates follow-on tasks for a completed task, handling convergence when the
    /// completing task is the last outstanding branch of a fork.
    /// </summary>
    private async Task RouteFromAsync(
        WorkflowTask task,
        string outcomeKey,
        string actorId,
        DateTime now,
        CancellationToken ct)
    {
        var routes = await GetValidRoutesAsync(task, outcomeKey, ct).ConfigureAwait(false);

        foreach (var route in routes)
        {
            var isConvergenceRoute = task.ForkGroupId.HasValue
                && task.ConvergenceTaskDefinitionId == route.NextTaskDefinitionId;

            if (isConvergenceRoute)
            {
                await HandleConvergenceAsync(task, route, actorId, now, ct).ConfigureAwait(false);
                continue;
            }

            var nextDef = route.NextTaskDefinition
                ?? throw new InvalidOperationException(
                    $"Route {route.Id} has no resolved target definition.");

            // A follow-on of a fork branch is still branch work, so it must not pick up a
            // mainline pre-assignment either -- the fork context propagates below.
            var assignment = await ResolveAssignmentAsync(
                nextDef,
                new WorkflowAssignment(task.AssignedToActorId, task.AssignedBranchKey),
                task.ToSnapshot(),
                ct,
                task.ForkGroupId.HasValue ? 0 : task.WorkflowRunId).ConfigureAwait(false);

            var next = await NewTaskAsync(task.WorkflowRunId, nextDef, assignment, actorId, now, ct).ConfigureAwait(false);

            // Propagate fork context to non-convergence follow-ons, so a branch that
            // routes through intermediate steps still converges at the end.
            if (task.ForkGroupId.HasValue)
            {
                next.ForkGroupId = task.ForkGroupId;
                next.ConvergenceTaskDefinitionId = task.ConvergenceTaskDefinitionId;
            }

            next.SubWorkflowInstanceId = task.SubWorkflowInstanceId;
            next.ParentTaskId = task.ParentTaskId;

            db.WorkflowTasks.Add(next);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            Log(next.Id, "Created", $"Follow-on from task {task.Id}", actorId, now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            await FireAsync(next, WorkflowEventKind.TaskCreated, actorId, ct: ct).ConfigureAwait(false);
            await SpawnAutomaticAsync(next, actorId, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleConvergenceAsync(
        WorkflowTask task,
        TaskRoute route,
        string actorId,
        DateTime now,
        CancellationToken ct)
    {
        // Count sibling branches still outstanding. Safe to read-then-act here only
        // because the whole operation runs in one transaction and the unique index on
        // ForkManifestId is the backstop if two transactions still interleave.
        var pending = await db.WorkflowTasks
            .Where(t => t.ForkGroupId == task.ForkGroupId
                     && t.Id != task.Id
                     && !t.IsForkOrigin
                     && t.Status != WorkflowTaskStatus.Completed
                     && t.Status != WorkflowTaskStatus.Cancelled
                     && t.Status != WorkflowTaskStatus.Forked)
            .CountAsync(ct).ConfigureAwait(false);

        if (pending > 0)
        {
            Log(task.Id, "AwaitingConvergence",
                $"Branch complete. Waiting for {pending} other branch(es).", actorId, now);
            return;
        }

        var manifest = await db.WorkflowForkManifests
            .Include(m => m.Entries)
            .SingleOrDefaultAsync(m => m.ForkGroupId == task.ForkGroupId, ct)
            .ConfigureAwait(false)
            ?? throw new WorkflowNotFoundException("Fork group", $"{task.ForkGroupId}");

        var alreadyConverged = await db.WorkflowTasks
            .AnyAsync(t => t.ForkManifestId == manifest.Id
                        && t.Status != WorkflowTaskStatus.Cancelled, ct)
            .ConfigureAwait(false);

        if (alreadyConverged)
        {
            Log(task.Id, "AwaitingConvergence", "Convergence task already exists.", actorId, now);
            return;
        }

        await RecordBranchOutcomesAsync(manifest, ct).ConfigureAwait(false);

        var originTask = await db.WorkflowTasks
            .FirstOrDefaultAsync(t => t.Id == manifest.OriginTaskId, ct).ConfigureAwait(false);

        var source = originTask ?? task;
        var convergenceDef = route.NextTaskDefinition
            ?? throw new InvalidOperationException($"Route {route.Id} has no target definition.");

        // The convergence task is mainline again -- the fork ends there -- so a
        // pre-assignment on it applies.
        var assignment = await ResolveAssignmentAsync(
            convergenceDef,
            new WorkflowAssignment(source.AssignedToActorId, source.AssignedBranchKey),
            source.ToSnapshot(),
            ct,
            task.WorkflowRunId).ConfigureAwait(false);

        var convergence = await NewTaskAsync(task.WorkflowRunId, convergenceDef, assignment, actorId, now, ct).ConfigureAwait(false);
        convergence.ForkGroupId = null;                     // the fork ends here
        convergence.ConvergenceTaskDefinitionId = null;
        convergence.ForkManifestId = manifest.Id;
        convergence.SubWorkflowInstanceId = task.SubWorkflowInstanceId;

        db.WorkflowTasks.Add(convergence);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        Log(convergence.Id, "ForkConverged",
            $"All {manifest.Entries.Count} branch(es) complete.", actorId, now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var branchContext = new BranchContext(
            manifest.ForkGroupId, manifest.Id, null,
            manifest.Entries.Select(e => e.BranchKey).ToList());

        await FireAsync(convergence, WorkflowEventKind.ForkConverged, actorId, branchContext, ct)
            .ConfigureAwait(false);
        await SpawnAutomaticAsync(convergence, actorId, ct).ConfigureAwait(false);

        await FireAsync(convergence, WorkflowEventKind.TaskCreated, actorId, ct: ct)
            .ConfigureAwait(false);
    }

    private async Task RecordBranchOutcomesAsync(ForkManifest manifest, CancellationToken ct)
    {
        // Two-step rather than GroupBy(...).Select(g => g.OrderBy(...).First()), which
        // EF often cannot translate (the original engine's review finding M10).
        var completed = await db.WorkflowTasks
            .Where(t => t.ForkGroupId == manifest.ForkGroupId
                     && !t.IsForkOrigin
                     && t.Status == WorkflowTaskStatus.Completed)
            .Select(t => new { t.AssignedBranchKey, t.OutcomeKey, t.Notes, t.CompletedDate })
            .ToListAsync(ct).ConfigureAwait(false);

        var latestPerBranch = completed
            .Where(t => t.AssignedBranchKey is not null)
            .GroupBy(t => t.AssignedBranchKey!)
            .Select(g => g.OrderByDescending(x => x.CompletedDate).First());

        foreach (var branch in latestPerBranch)
        {
            var entry = manifest.Entries.FirstOrDefault(e => e.BranchKey == branch.AssignedBranchKey);
            if (entry is null)
            {
                continue;
            }

            entry.BranchOutcomeKey = branch.OutcomeKey;
            entry.BranchNotes = branch.Notes;
            entry.Modified = DateTime.UtcNow;
        }
    }

    private async Task MaybeCompleteRunAsync(int runId, string actorId, DateTime now, CancellationToken ct)
    {
        var openTasks = await db.WorkflowTasks
            .CountAsync(t => t.WorkflowRunId == runId
                          && t.Status != WorkflowTaskStatus.Completed
                          && t.Status != WorkflowTaskStatus.Cancelled
                          && t.Status != WorkflowTaskStatus.Forked, ct)
            .ConfigureAwait(false);

        if (openTasks > 0)
        {
            return;
        }

        var run = await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == runId, ct).ConfigureAwait(false);
        if (run is null || run.Status != WorkflowRunStatus.Running)
        {
            return;
        }

        run.Status = WorkflowRunStatus.Completed;
        run.CompletedDate = now;
        run.ModifierId = actorId;
        run.Modified = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // RunCompleted is raised against the last task, since triggers are configured
        // per task definition and there is no run-level definition to hang them off.
        var lastTask = await db.WorkflowTasks
            .Where(t => t.WorkflowRunId == runId && t.Status == WorkflowTaskStatus.Completed)
            .OrderByDescending(t => t.CompletedDate).ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (lastTask is not null)
        {
            await FireAsync(lastTask, WorkflowEventKind.RunCompleted, actorId, ct: ct)
                .ConfigureAwait(false);
        }
    }

    // ───────────────────────────────── Helpers ─────────────────────────────────

    private async Task<WorkflowTask> LoadTaskAsync(int taskId, CancellationToken ct) =>
        await db.WorkflowTasks
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.OutgoingRoutes)
            .SingleOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false)
        ?? throw new WorkflowNotFoundException("Task", taskId.ToString());

    /// <summary>
    /// Asks the host whether this actor may do this, and throws
    /// <see cref="WorkflowAuthorizationException"/> if not.
    ///
    /// Must be called inside each mutating method's transaction, after the task is loaded
    /// and <b>before any validation</b>. The ordering is the point: told "task 42 is already
    /// completed", a denied actor has learned that task 42 exists and what became of it.
    ///
    /// <paramref name="task"/> is null only for <see cref="WorkflowOperation.StartRun"/>.
    /// </summary>
    private async Task AuthorizeAsync(
        WorkflowOperation operation,
        WorkflowTask? task,
        string actorId,
        WorkflowSubject? subject,
        int? workflowDefinitionId,
        CancellationToken ct)
    {
        // No policy registered: the library's pre-authorization behaviour. DI supplies
        // AllowAllAuthorizationPolicy, but the engine can also be constructed directly.
        if (authorizationPolicy is null)
        {
            return;
        }

        // The sweeper and the trigger dispatcher act under this id. The bypass lives here
        // rather than in every host's policy because a host that forgot it would break its
        // own deadline sweep, and the symptom would be tasks quietly not escalating.
        //
        // Hosts must never let this id arrive from an untrusted caller: it is checked
        // before the policy is consulted, so a request that supplies it is ungated.
        if (actorId == WorkflowActors.System)
        {
            return;
        }

        var context = new WorkflowAuthorizationContext(
            actorId,
            task?.ToSnapshot(),
            subject,
            workflowDefinitionId);

        WorkflowAuthorizationResult result;

        try
        {
            result = await authorizationPolicy
                .EvaluateAsync(operation, context, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException and not WorkflowAuthorizationException)
        {
            // Fail closed. See IWorkflowAuthorizationPolicy: this is the opposite of the
            // rule the resolver seams document, and deliberately so.
            //
            // Two exclusions, neither of which weakens that. Cancellation is not a policy
            // failure, and reporting it as a denial would tell a host mapping this to 403
            // that an aborted request was forbidden. A policy that throws
            // WorkflowAuthorizationException has already said precisely why, and wrapping
            // it would replace that reason with a vaguer one. Both still abort the
            // transaction, so nothing proceeds ungated either way.
            //
            // The cause lives in this log line and not on the exception: the Result
            // carries a stable message for the host to show a user, and the stack trace
            // is a server-side concern.
            logger.LogError(
                ex,
                "Authorization policy threw evaluating {Operation} for actor {ActorId}; denying.",
                operation,
                actorId);

            throw new WorkflowAuthorizationException(
                operation, actorId, "the authorization policy failed");
        }

        if (result is null || !result.IsAllowed)
        {
            throw new WorkflowAuthorizationException(
                operation, actorId, result?.Reason ?? "not permitted");
        }
    }

    private async Task ValidateCompletableAsync(
        WorkflowTask task, string? outcomeKey, CancellationToken ct)
    {
        if (task.Status == WorkflowTaskStatus.Completed)
        {
            throw new InvalidOperationException($"Task {task.Id} is already completed.");
        }

        if (task.Status == WorkflowTaskStatus.Cancelled)
        {
            throw new InvalidOperationException($"Task {task.Id} is cancelled.");
        }

        if (task.Status == WorkflowTaskStatus.Forked)
        {
            throw new InvalidOperationException($"Task {task.Id} was superseded by a fork.");
        }

        // Blocking children must be *finished*, not merely not-completed.
        // The original engine's finding H1: it tested `Status != Completed`, so a cancelled blocking
        // child blocked its parent forever with no way to clear it.
        //
        // A positive list, not a negative one -- and this is the second time that distinction
        // has cost something. The negative form read "not Completed and not Cancelled", which
        // silently admitted Forked: a blocking child superseded by a fork went on blocking its
        // parent for good, while the fork's own branches ran to completion and the screen
        // showed the chain as finished. The inbox query says it outright: "the negative form
        // silently admits any status added to the enum later, and Forked is exactly the value
        // somebody writing this by hand leaves out."
        //
        // Open means open. Anything else -- completed, cancelled, superseded by a fork -- is
        // a child nobody is waiting on.
        var blocking = await db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.ParentTaskId == task.Id
                     && (t.Status == WorkflowTaskStatus.NotStarted
                      || t.Status == WorkflowTaskStatus.InProgress))
            .ToListAsync(ct).ConfigureAwait(false);

        var names = blocking
            .Where(t => t.TaskDefinition?.IsBlocking == true)
            .Select(t => t.TaskDefinition!.DisplayName ?? t.TaskDefinition.TaskType?.DisplayName ?? $"#{t.Id}")
            .Distinct()
            .ToList();

        if (names.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot complete: blocking task(s) outstanding: {string.Join(", ", names)}.");
        }

        var blockingInstances = await BlockingInstanceNamesAsync(task.Id, ct).ConfigureAwait(false);

        if (blockingInstances.Count > 0)
        {
            throw new InvalidOperationException(
                "Cannot complete: blocking sub-workflow(s) still running: " +
                $"{string.Join(", ", blockingInstances)}.");
        }

        await ValidateOutcomeAsync(task, outcomeKey, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// An outcome must be one the task definition declares.
    ///
    /// Without this a caller can complete a task with any string at all: the task is
    /// marked done, no route matches, no follow-on task is created, and the run stalls
    /// silently with nothing to show for it. That is the same shape as review finding
    /// C3 — a caller supplying state the definition never sanctioned — and it is worth
    /// closing for the same reason.
    ///
    /// A task that declares no outcomes accepts any key. Terminal tasks are usually
    /// written that way: nothing routes onward from them, so there is nothing for an
    /// outcome to select.
    /// </summary>
    private async Task ValidateOutcomeAsync(WorkflowTask task, string? outcomeKey, CancellationToken ct)
    {
        var valid = await db.WorkflowTaskOutcomes
            .Where(o => o.TaskDefinitionId == task.TaskDefinitionId && !o.IsArchived)
            .Select(o => o.OutcomeKey)
            .ToListAsync(ct).ConfigureAwait(false);

        if (valid.Count == 0)
        {
            return;
        }

        if (!valid.Contains(outcomeKey, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{outcomeKey}' is not a valid outcome for this task. " +
                $"Expected one of: {string.Join(", ", valid)}.");
        }
    }

    private async Task<List<TaskRoute>> GetValidRoutesAsync(
        WorkflowTask task,
        string outcomeKey,
        CancellationToken ct)
    {
        var outcomeId = await ResolveOutcomeIdAsync(task.TaskDefinitionId, outcomeKey, ct)
            .ConfigureAwait(false);

        if (outcomeId is null)
        {
            return [];
        }

        var routes = await db.WorkflowTaskRoutes
            .Include(r => r.NextTaskDefinition!).ThenInclude(d => d.TaskType)
            .Where(r => r.TaskDefinitionId == task.TaskDefinitionId
                     && r.TaskOutcomeDefinitionId == outcomeId
                     && !r.IsArchived)
            .OrderBy(r => r.Order)
            .ToListAsync(ct).ConfigureAwait(false);

        if (routes.Count == 0)
        {
            return routes;
        }

        var variables = await GetVariablesAsync(task.WorkflowRunId, ct).ConfigureAwait(false);
        var snapshot = task.ToSnapshot();

        return routes
            .Where(r => r.IsDefault
                     || string.IsNullOrWhiteSpace(r.ConditionKey)
                     || EvaluateCondition(r.ConditionKey!, snapshot, variables))
            .ToList();
    }

    /// <summary>
    /// The id of the outcome a task declares under this key, or null if it declares none.
    ///
    /// <para>The one place a key becomes an id. Matching is <b>case-insensitive and done in
    /// memory</b>, deliberately: the same comparison in SQL follows the database's collation,
    /// so a deployment on a case-sensitive one would route differently from one on the
    /// default — and the validation that runs just before this already compares
    /// case-insensitively, so doing it any other way here would accept an outcome and then
    /// fail to route it. A task declares a handful of outcomes, so the read is cheap.</para>
    ///
    /// <para>Null means the caller named an outcome this task does not declare. Callers treat
    /// that as "no routes", which is what a string comparison produced before ids.</para>
    /// </summary>
    private async Task<int?> ResolveOutcomeIdAsync(
        int taskDefinitionId, string outcomeKey, CancellationToken ct)
    {
        var declared = await db.WorkflowTaskOutcomes
            .AsNoTracking()
            .Where(o => o.TaskDefinitionId == taskDefinitionId && !o.IsArchived)
            .Select(o => new { o.Id, o.OutcomeKey })
            .ToListAsync(ct).ConfigureAwait(false);

        return declared
            .FirstOrDefault(o => string.Equals(o.OutcomeKey, outcomeKey, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }

    private bool EvaluateCondition(
        string key,
        WorkflowTaskSnapshot task,
        IReadOnlyDictionary<string, string?> variables)
    {
        if (_conditions.TryGetValue(key, out var evaluator))
        {
            return evaluator.Evaluate(task, variables);
        }

        // Unknown condition means the workflow references an evaluator this deployment
        // does not have. Failing closed avoids silently taking a route nobody intended.
        logger.LogWarning(
            "Route condition '{ConditionKey}' has no registered evaluator; route skipped.", key);
        return false;
    }

    private async Task<IReadOnlyDictionary<string, string?>> GetVariablesAsync(
        int runId, CancellationToken ct)
    {
        var vars = await db.WorkflowVariables
            .Where(v => v.WorkflowRunId == runId && v.TaskId == null)
            .Select(v => new { v.Name, v.Value })
            .ToListAsync(ct).ConfigureAwait(false);

        return vars.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Who a step being created goes to: a standing pre-assignment if the run has one, then
    /// the step's assignment role, then whatever the previous step carried.
    /// </summary>
    /// <param name="runId">
    /// Which run — pre-assignment is per run. Zero means "do not look", which is what the
    /// fork path passes: branches are out of scope by decision, and a branch quietly taking
    /// the mainline's pre-assignment would put every section's copy on one person.
    /// </param>
    private async Task<WorkflowAssignment> ResolveAssignmentAsync(
        WorkflowTaskDefinition definition,
        WorkflowAssignment current,
        WorkflowTaskSnapshot? task,
        CancellationToken ct,
        int runId = 0)
    {
        if (runId != 0)
        {
            var promised = await db.WorkflowPreAssignments
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    p => p.WorkflowRunId == runId
                      && p.TaskDefinitionId == definition.Id
                      && !p.IsArchived, ct)
                .ConfigureAwait(false);

            if (promised is not null)
            {
                // The resolver is skipped rather than consulted and overridden. Calling it
                // would run host code -- often a database lookup -- for an answer already
                // decided, and a host watching its resolver would see work it has no say in.
                return new WorkflowAssignment(
                    promised.ActorId,
                    promised.BranchKey ?? current.BranchKey);
            }
        }

        if (string.IsNullOrWhiteSpace(definition.AssignmentRoleKey))
        {
            return current;
        }

        // Unlike the original engine (finding M8), a resolver failure is logged rather than silently
        // falling back, so a misrouted assignment is diagnosable.
        try
        {
            return await assignmentResolver
                .ResolveAsync(definition.AssignmentRoleKey, current, task ?? Placeholder(definition), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Assignment resolver failed for role '{RoleKey}' on definition {DefinitionId}; " +
                "falling back to the current assignment.",
                definition.AssignmentRoleKey, definition.Id);
            return current;
        }
    }

    private static WorkflowTaskSnapshot Placeholder(WorkflowTaskDefinition d) => new(
        0, 0, d.Id, d.TaskType?.Key ?? string.Empty,
        d.DisplayName ?? d.TaskType?.DisplayName ?? string.Empty,
        WorkflowTaskStatus.NotStarted, null, null, null, null, null,
        false, null, null, null, null);

    /// <summary>
    /// A run's subject, memoised. The engine is scoped, so this lives for one unit of
    /// work — and a run's subject is written once at creation and never mutated, so
    /// there is nothing for a stale entry to be stale about.
    ///
    /// It exists because the due-date funnel needs a subject and seven of the eight
    /// creation paths do not have the run loaded: LoadTaskAsync does not Include it.
    /// Threading a subject parameter through those seven call sites would reintroduce
    /// exactly the per-site burden the funnel exists to remove.
    /// </summary>
    private readonly Dictionary<int, WorkflowSubject> _subjectsByRun = [];

    private async Task<WorkflowSubject?> SubjectForRunAsync(int runId, CancellationToken ct)
    {
        if (_subjectsByRun.TryGetValue(runId, out var cached))
        {
            return cached;
        }

        // Projected as two scalars and rebuilt, not selected as the owned type itself —
        // the same shape WorkflowEngine.Inbox.cs uses.
        var row = await db.WorkflowRuns
            .AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Subject.SubjectType, r.Subject.SubjectId })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var subject = new WorkflowSubject(row.SubjectType, row.SubjectId);
        _subjectsByRun[runId] = subject;
        return subject;
    }

    /// <summary>
    /// The single place a <see cref="WorkflowTask"/> is constructed. All eight creation
    /// paths go through here, and five of them are fork- or sub-workflow-related — which
    /// is the argument for the funnel in one sentence. Anyone adding a deadline at "the
    /// place tasks are made" by hand would find three of them.
    /// </summary>
    private async Task<WorkflowTask> NewTaskAsync(
        int runId,
        WorkflowTaskDefinition definition,
        WorkflowAssignment assignment,
        string actorId,
        DateTime now,
        CancellationToken ct)
    {
        var task = new WorkflowTask
        {
            WorkflowRunId = runId,
            TaskDefinitionId = definition.Id,
            TaskDefinition = definition,
            Status = WorkflowTaskStatus.NotStarted,
            AssignedToActorId = assignment.ActorId,
            AssignedBranchKey = assignment.BranchKey,
            CreatorId = actorId,
            ModifierId = actorId,
            Created = now,
            Modified = now
        };

        task.DueDate = await ResolveDueDateAsync(runId, task, ct).ConfigureAwait(false);

        return task;
    }

    private async Task<DateTime?> ResolveDueDateAsync(
        int runId, WorkflowTask task, CancellationToken ct)
    {
        if (dueDateResolver is null)
        {
            return null;
        }

        var subject = await SubjectForRunAsync(runId, ct).ConfigureAwait(false);

        if (subject is null)
        {
            // Only reachable if the run row is not there yet. Every caller saves the run
            // before creating its first task, so this is a defect rather than a state.
            logger.LogWarning(
                "No run {RunId} when resolving a due date; leaving it null.", runId);
            return null;
        }

        try
        {
            // The snapshot's Id is 0 and its TaskTypeKey may be empty: the task is not
            // saved yet, and TaskDefinition.TaskType is not loaded on every path. A
            // resolver that needs either must key on the subject instead — which is what
            // a deadline belongs to anyway.
            return await dueDateResolver
                .ResolveAsync(subject, task.ToSnapshot(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Logged rather than silently swallowed, and never fatal: the same policy
            // ResolveAssignmentAsync has, for the same reason (the original engine's finding M8).
            logger.LogError(ex,
                "Due-date resolver failed for definition {DefinitionId} on run {RunId}; " +
                "leaving the due date null.",
                task.TaskDefinitionId, runId);
            return null;
        }
    }

    private void Log(int taskId, string action, string? note, string actorId, DateTime at) =>
        db.WorkflowTaskLogs.Add(new WorkflowTaskLog
        {
            TaskId = taskId,
            Action = action,
            Note = note,
            PerformedBy = actorId,
            PerformedAt = at,
            CreatorId = actorId,
            ModifierId = actorId,
            Created = at,
            Modified = at
        });

    private async Task<bool> IsRejectionOutcomeAsync(WorkflowTask task, string outcomeKey, CancellationToken ct)
    {
        // A convergence task's "rejection" outcome is whichever outcome routes backwards.
        // Rather than hardcode the word, treat any outcome whose route targets a task
        // that is not downstream-terminal as a rework path.
        var outcomeId = await ResolveOutcomeIdAsync(task.TaskDefinitionId, outcomeKey, ct)
            .ConfigureAwait(false);

        if (outcomeId is null)
        {
            return false;
        }

        var route = await db.WorkflowTaskRoutes
            .FirstOrDefaultAsync(r => r.TaskDefinitionId == task.TaskDefinitionId
                                   && r.TaskOutcomeDefinitionId == outcomeId
                                   && !r.IsArchived, ct).ConfigureAwait(false);

        return route is not null && route.IsReworkRoute;
    }
}
