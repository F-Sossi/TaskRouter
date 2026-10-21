using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;

using TaskRouter.EntityFrameworkCore.Fields;

namespace TaskRouter.EntityFrameworkCore.Triggers;

public interface IWorkflowTriggerDispatcher
{
    /// <summary>
    /// Runs the triggers configured for a task's definition and an event.
    /// In-transaction triggers execute now; after-commit and background triggers are
    /// written to the outbox inside the caller's transaction.
    /// </summary>
    Task DispatchAsync(
        WorkflowTask task,
        WorkflowEventKind eventKind,
        string actorId,
        string? customEventName = null,
        BranchContext? branch = null,
        CancellationToken ct = default);

    /// <summary>
    /// What the triggers for this task's definition and event would do, without doing it:
    /// the same triggers <see cref="DispatchAsync"/> would run, conditions applied, each
    /// described by <see cref="IWorkflowTrigger.DescribeActionAsync"/>. The task may be one
    /// that does not exist yet — a step the completion would create.
    /// </summary>
    Task<IReadOnlyList<PlannedAction>> PreviewAsync(
        WorkflowTask task,
        WorkflowEventKind eventKind,
        string actorId,
        TriggerPreview preview,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlannedAction>>([]);
}

public sealed class TriggerDispatcher(
    IWorkflowDbContext db,
    IWorkflowTriggerRegistry registry,
    IServiceProvider services,
    ILogger<TriggerDispatcher> logger,
    IWorkflowJobQueue? jobQueue = null,
    IWorkflowPostCommitActions? postCommit = null) : IWorkflowTriggerDispatcher
{
    public async Task DispatchAsync(
        WorkflowTask task,
        WorkflowEventKind eventKind,
        string actorId,
        string? customEventName = null,
        BranchContext? branch = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        var definitions = await db.WorkflowTriggerDefinitions
            .Where(t => t.TaskDefinitionId == task.TaskDefinitionId
                     && t.Event == eventKind
                     && t.IsActive
                     && !t.IsArchived)
            // Ordering matters as soon as one trigger writes a variable another reads.
            // The original engine had no OrderBy at all, so execution order was whatever the query returned.
            .OrderBy(t => t.Order).ThenBy(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        if (customEventName is not null)
        {
            definitions = definitions
                .Where(d => string.Equals(d.CustomEventName, customEventName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (definitions.Count == 0)
        {
            return;
        }

        // Built lazily: no point loading run state if nothing is configured.
        var context = await BuildContextAsync(task, eventKind, actorId, customEventName, branch, ct)
            .ConfigureAwait(false);

        foreach (var definition in definitions)
        {
            if (!ShouldRun(definition, context))
            {
                continue;
            }

            var key = IdempotencyKey(definition.Id, task.Id, eventKind);

            switch (definition.DispatchMode)
            {
                case TriggerDispatchMode.InTransaction:
                    await RunInlineAsync(definition, context, key, ct).ConfigureAwait(false);
                    break;

                case TriggerDispatchMode.AfterCommit:
                    Enqueue(definition, task, eventKind, actorId, customEventName, key);
                    break;

                case TriggerDispatchMode.Background:
                    Enqueue(definition, task, eventKind, actorId, customEventName, key);
                    SignalAfterCommit(definition, task, eventKind, key);
                    break;

                default:
                    logger.LogWarning("Unknown dispatch mode {Mode}.", definition.DispatchMode);
                    break;
            }
        }
    }

    public async Task<IReadOnlyList<PlannedAction>> PreviewAsync(
        WorkflowTask task,
        WorkflowEventKind eventKind,
        string actorId,
        TriggerPreview preview,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(preview);

        var definitions = await db.WorkflowTriggerDefinitions.AsNoTracking()
            .Where(t => t.TaskDefinitionId == task.TaskDefinitionId
                     && t.Event == eventKind
                     && t.IsActive
                     && !t.IsArchived)
            .OrderBy(t => t.Order).ThenBy(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        if (definitions.Count == 0)
        {
            return [];
        }

        var context = await BuildContextAsync(task, eventKind, actorId, null, null, ct, preview).ConfigureAwait(false);
        var step = task.TaskDefinition?.DisplayName ?? context.Task.DisplayName;
        var actions = new List<PlannedAction>();

        foreach (var definition in definitions)
        {
            if (!ShouldRun(definition, context) || !registry.TryGet(definition.TriggerKey, out var trigger))
            {
                continue;
            }

            string? said;

            try
            {
                said = await trigger.DescribeActionAsync(
                    With(context, definition), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A trigger that cannot describe itself is still going to run: name it.
                logger.LogWarning(ex, "Trigger {TriggerKey} could not describe what it would do.", definition.TriggerKey);
                said = null;
            }

            if (said is { Length: 0 })
            {
                continue;
            }

            actions.Add(new PlannedAction(
                step,
                TriggerEvents.Label(eventKind),
                said ?? registry.Descriptors.FirstOrDefault(d => d.Key == trigger.Key)?.DisplayName ?? trigger.Key));
        }

        return actions;
    }

    /// <summary>
    /// What separates Background from AfterCommit. Both write the same durable outbox
    /// row inside this transaction; a background trigger additionally tells a worker to
    /// pick it up now rather than at the next poll.
    ///
    /// Queued for after the commit, not done here: the outbox row does not exist to
    /// anyone else until the transaction commits, so a worker woken now would find
    /// nothing and go back to sleep — leaving the "prompt" trigger to wait for the
    /// timer, which is exactly what the signal was meant to avoid.
    ///
    /// With no job queue registered, Background behaves as AfterCommit: still durable,
    /// still retried, just not prompt.
    /// </summary>
    private void SignalAfterCommit(
        TriggerDefinition definition, WorkflowTask task, WorkflowEventKind eventKind, string key)
    {
        if (jobQueue is null)
        {
            return;
        }

        var job = new WorkflowJob(definition.TriggerKey, definition.Id, task.Id, eventKind, key);

        if (postCommit is null)
        {
            // No post-commit hook: signal now and accept that the worker may be early.
            // The message stays in the outbox either way, so the poll still gets it.
            _ = jobQueue.EnqueueAsync(job, CancellationToken.None);
            return;
        }

        postCommit.Add(ct => jobQueue.EnqueueAsync(job, ct));
    }

    internal static string IdempotencyKey(int definitionId, int taskId, WorkflowEventKind e) =>
        $"{definitionId}:{taskId}:{e}";

    private bool ShouldRun(TriggerDefinition definition, WorkflowTriggerContext context)
    {
        if (string.IsNullOrWhiteSpace(definition.Condition))
        {
            return true;
        }

        try
        {
            return TriggerConditionEvaluator.Evaluate(definition.Condition!, context);
        }
        catch (FormatException ex)
        {
            // A malformed condition must not silently fire the trigger.
            logger.LogError(ex,
                "Trigger {TriggerKey} has an invalid condition '{Condition}'; skipping.",
                definition.TriggerKey, definition.Condition);
            return false;
        }
    }

    private async Task RunInlineAsync(
        TriggerDefinition definition,
        WorkflowTriggerContext baseContext,
        string idempotencyKey,
        CancellationToken ct)
    {
        var execution = new TriggerExecution
        {
            TriggerDefinitionId = definition.Id,
            TaskId = baseContext.Task.Id,
            Event = baseContext.Event,
            IdempotencyKey = idempotencyKey,
            Status = TriggerExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            CreatorId = baseContext.ActorId,
            ModifierId = baseContext.ActorId
        };

        db.WorkflowTriggerExecutions.Add(execution);

        if (!registry.TryGet(definition.TriggerKey, out var trigger))
        {
            // A deployment missing a trigger degrades rather than failing the operation.
            execution.Status = TriggerExecutionStatus.Skipped;
            execution.Error = $"No implementation registered for trigger key '{definition.TriggerKey}'.";
            execution.CompletedAt = DateTime.UtcNow;

            logger.LogWarning(
                "No implementation for trigger key '{TriggerKey}' on definition {DefinitionId}.",
                definition.TriggerKey, definition.Id);
            return;
        }

        var context = With(baseContext, definition);

        try
        {
            await trigger.ExecuteAsync(context, ct).ConfigureAwait(false);

            execution.Status = TriggerExecutionStatus.Succeeded;
            execution.Detail = context.Report.Detail;
            execution.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // The recorded outcome is the real one. The original engine wrote "executed successfully"
            // into the audit log regardless of what happened (review finding H3).
            execution.Status = TriggerExecutionStatus.Failed;
            execution.Error = ex.Message;
            execution.Detail = context.Report.Detail;
            execution.CompletedAt = DateTime.UtcNow;

            logger.LogError(ex, "Trigger {TriggerKey} failed for task {TaskId}.",
                definition.TriggerKey, baseContext.Task.Id);

            // A failed field-update undo has already rolled the whole transaction back. Once
            // the transaction is dead, "continue" cannot mean anything, and rethrowing here
            // lets the caller see the real cause instead of SqlClient's complaint that the
            // transaction has completed.
            if (definition.FailurePolicy == TriggerFailurePolicy.FailOperation
                || ex is FieldUpdateUndoFailedException)
            {
                throw;
            }
        }
    }

    private void Enqueue(
        TriggerDefinition definition,
        WorkflowTask task,
        WorkflowEventKind eventKind,
        string actorId,
        string? customEventName,
        string idempotencyKey) =>
        db.WorkflowOutbox.Add(new WorkflowOutboxMessage
        {
            TriggerDefinitionId = definition.Id,
            TaskId = task.Id,
            Event = eventKind,
            CustomEventName = customEventName,
            IdempotencyKey = idempotencyKey,
            ActorId = actorId,
            DispatchMode = definition.DispatchMode,
            Status = OutboxStatus.Pending,
            NextAttemptAt = DateTime.UtcNow,
            CreatorId = actorId,
            ModifierId = actorId
        });

    private static WorkflowTriggerContext With(
        WorkflowTriggerContext context, TriggerDefinition definition) =>
        new()
        {
            Subject = context.Subject,
            Event = context.Event,
            CustomEventName = context.CustomEventName,
            Task = context.Task,
            Run = context.Run,
            Branch = context.Branch,
            Config = TriggerConfig.Parse(definition.Configuration),
            ActorId = context.ActorId,
            Variables = context.Variables,
            Actions = context.Actions,
            Services = context.Services,
            Preview = context.Preview
        };

    private async Task<WorkflowTriggerContext> BuildContextAsync(
        WorkflowTask task,
        WorkflowEventKind eventKind,
        string actorId,
        string? customEventName,
        BranchContext? branch,
        CancellationToken ct,
        TriggerPreview? preview = null)
    {
        var run = await db.WorkflowRuns
            .Include(r => r.DefinitionVersion)
            .SingleAsync(r => r.Id == task.WorkflowRunId, ct).ConfigureAwait(false);

        var tasks = await db.WorkflowTasks
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .Where(t => t.WorkflowRunId == task.WorkflowRunId)
            .ToListAsync(ct).ConfigureAwait(false);

        // Prefer the tracked instance so in-flight changes are visible to the trigger. Not
        // in a preview: there the task given is the one as it would be, which is the point.
        var self = preview is null ? tasks.FirstOrDefault(t => t.Id == task.Id) ?? task : task;

        return new WorkflowTriggerContext
        {
            Subject = run.Subject,
            Event = eventKind,
            CustomEventName = customEventName,
            Task = self.ToSnapshot(),
            Run = run.ToSnapshot(tasks),
            Branch = branch,
            Config = TriggerConfig.Empty,
            ActorId = actorId,
            Variables = new WorkflowVariables(db, task.WorkflowRunId, task.Id),
            Actions = new WorkflowActions(
                (IWorkflowEngine)services.GetService(typeof(IWorkflowEngine))!,
                db, task.WorkflowRunId, actorId),
            Services = services,
            Preview = preview
        };
    }
}
