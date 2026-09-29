using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Triggers;

public interface IWorkflowOutboxProcessor
{
    /// <summary>Processes due outbox messages. Returns how many were attempted.</summary>
    Task<int> ProcessPendingAsync(int maxMessages = 50, CancellationToken ct = default);
}

/// <summary>
/// Drains the outbox after the engine transaction has committed.
///
/// Claim-by-update with a lease, at-least-once delivery, exponential backoff. Exactly
/// once is not attempted — consumers deduplicate on the idempotency key, which is
/// recorded on <see cref="TriggerExecution"/>.
///
/// <para><b>Each message runs in its own scope and its own transaction.</b> An
/// after-commit trigger is allowed to write to the host's tables, and without isolation
/// those writes accumulate on the processor's own context: one bad change fails the save
/// for every message in the batch, and a partial failure leaves some of them applied.
/// A scope per message makes the contract statable — a trigger's writes commit together
/// or not at all, and one failing message cannot affect its neighbours.</para>
///
/// <para>The processor's own bookkeeping — message status, attempt counts,
/// <see cref="TriggerExecution"/> — stays on the outer context deliberately. It has to
/// survive the rollback of a failed trigger, which is the whole point of recording the
/// failure.</para>
/// </summary>
public sealed class OutboxProcessor(
    IWorkflowDbContext db,
    IWorkflowTriggerRegistry registry,
    IServiceProvider services,
    ILogger<OutboxProcessor> logger) : IWorkflowOutboxProcessor
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<int> ProcessPendingAsync(int maxMessages = 50, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var due = await db.WorkflowOutbox
            .Where(m => m.Status == OutboxStatus.Pending
                     && m.NextAttemptAt <= now
                     && (m.LeasedUntil == null || m.LeasedUntil < now))
            .OrderBy(m => m.NextAttemptAt).ThenBy(m => m.Id)
            .Take(maxMessages)
            .ToListAsync(ct).ConfigureAwait(false);

        if (due.Count == 0)
        {
            return 0;
        }

        // Claim before doing any work, so a concurrent worker skips these rows.
        var leaseId = Guid.NewGuid();
        foreach (var message in due)
        {
            message.LeaseId = leaseId;
            message.LeasedUntil = now.Add(LeaseDuration);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var attempted = 0;

        foreach (var message in due)
        {
            await ProcessOneAsync(message, ct).ConfigureAwait(false);
            attempted++;

            // Saved per message rather than per batch: the outcome of one must be
            // durable before the next runs, or a crash mid-batch loses the record of
            // work that already happened.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return attempted;
    }

    private async Task ProcessOneAsync(WorkflowOutboxMessage message, CancellationToken ct)
    {
        message.Attempts++;

        var execution = await db.WorkflowTriggerExecutions
            .SingleOrDefaultAsync(e => e.IdempotencyKey == message.IdempotencyKey, ct)
            .ConfigureAwait(false);

        if (execution is null)
        {
            execution = new TriggerExecution
            {
                TriggerDefinitionId = message.TriggerDefinitionId,
                TaskId = message.TaskId,
                Event = message.Event,
                IdempotencyKey = message.IdempotencyKey,
                StartedAt = DateTime.UtcNow,
                CreatorId = message.ActorId,
                ModifierId = message.ActorId
            };

            db.WorkflowTriggerExecutions.Add(execution);
        }

        execution.Attempt = message.Attempts;

        var definition = await db.WorkflowTriggerDefinitions
            .SingleOrDefaultAsync(d => d.Id == message.TriggerDefinitionId, ct).ConfigureAwait(false);

        if (definition is null || !registry.TryGet(definition.TriggerKey, out var trigger))
        {
            Abandon(message, execution,
                $"No implementation registered for trigger definition {message.TriggerDefinitionId}.");
            return;
        }

        try
        {
            await ExecuteIsolatedAsync(message, definition, ct).ConfigureAwait(false);

            message.Status = OutboxStatus.Succeeded;
            message.ProcessedAt = DateTime.UtcNow;
            message.Error = null;
            message.LeaseId = null;
            message.LeasedUntil = null;

            execution.Status = TriggerExecutionStatus.Succeeded;
            execution.CompletedAt = DateTime.UtcNow;
            execution.Error = null;
        }
        catch (Exception ex)
        {
            execution.Status = TriggerExecutionStatus.Failed;
            execution.Error = ex.Message;
            execution.CompletedAt = DateTime.UtcNow;

            message.Error = ex.Message;
            message.LeaseId = null;
            message.LeasedUntil = null;

            if (message.Attempts >= MaxAttempts)
            {
                message.Status = OutboxStatus.Abandoned;
                message.ProcessedAt = DateTime.UtcNow;

                logger.LogError(ex,
                    "Outbox message {MessageId} ({TriggerKey}) abandoned after {Attempts} attempts.",
                    message.Id, definition.TriggerKey, message.Attempts);
            }
            else
            {
                // Exponential backoff: 2s, 4s, 8s, 16s.
                var delay = TimeSpan.FromSeconds(Math.Pow(2, message.Attempts));
                message.NextAttemptAt = DateTime.UtcNow.Add(delay);

                logger.LogWarning(ex,
                    "Outbox message {MessageId} ({TriggerKey}) failed, attempt {Attempts}; retrying in {Delay}.",
                    message.Id, definition.TriggerKey, message.Attempts, delay);
            }
        }
    }

    /// <summary>
    /// Runs one trigger with its own <see cref="IWorkflowDbContext"/> and its own
    /// transaction, so whatever it writes to the host's tables is atomic with itself and
    /// isolated from every other message in the batch.
    ///
    /// Falls back to the processor's own context when no scope factory is reachable —
    /// a host that constructed this by hand rather than through DI. That is the old
    /// behaviour, and it is the caller's choice rather than a silent downgrade: without
    /// a container there is no second scope to be had.
    /// </summary>
    private async Task ExecuteIsolatedAsync(
        WorkflowOutboxMessage message, TriggerDefinition definition, CancellationToken ct)
    {
        var scopeFactory = services.GetService<IServiceScopeFactory>();

        if (scopeFactory is null)
        {
            var inlineContext = await BuildContextAsync(db, services, message, definition, ct)
                .ConfigureAwait(false);

            var inlineTrigger = Resolve(services, definition.TriggerKey);
            await inlineTrigger.ExecuteAsync(inlineContext, ct).ConfigureAwait(false);
            return;
        }

        using var scope = scopeFactory.CreateScope();

        var scopedDb = scope.ServiceProvider.GetRequiredService<IWorkflowDbContext>();
        var scopedTrigger = Resolve(scope.ServiceProvider, definition.TriggerKey);

        // Through WorkflowTransaction, not BeginTransaction: a provider configured with
        // EnableRetryOnFailure refuses a user-initiated transaction outside its execution
        // strategy, and any host that expects to survive a transient network blip has it on.
        await WorkflowTransaction.ExecuteAsync(scopedDb, async token =>
        {
            var context = await BuildContextAsync(scopedDb, scope.ServiceProvider, message, definition, token)
                .ConfigureAwait(false);

            await scopedTrigger.ExecuteAsync(context, token).ConfigureAwait(false);

            // The trigger is not required to save: anything it changed on the scoped
            // context is written by the transaction helper, which is what makes "its
            // writes commit together" true.
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the trigger from the given provider. Trigger implementations are scoped,
    /// so the one that runs must come from the scope its DbContext came from.
    /// </summary>
    private IWorkflowTrigger Resolve(IServiceProvider provider, string triggerKey)
    {
        var scoped = provider.GetServices<IWorkflowTrigger>()
            .LastOrDefault(t => string.Equals(t.Key, triggerKey, StringComparison.OrdinalIgnoreCase));

        if (scoped is not null)
        {
            return scoped;
        }

        // A host that registered the registry but not the services it was built from.
        return registry.TryGet(triggerKey, out var fromRegistry)
            ? fromRegistry
            : throw new InvalidOperationException($"No implementation registered for '{triggerKey}'.");
    }

    private static void Abandon(WorkflowOutboxMessage message, TriggerExecution execution, string error)
    {
        message.Status = OutboxStatus.Abandoned;
        message.Error = error;
        message.ProcessedAt = DateTime.UtcNow;
        message.LeaseId = null;
        message.LeasedUntil = null;

        execution.Status = TriggerExecutionStatus.Skipped;
        execution.Error = error;
        execution.CompletedAt = DateTime.UtcNow;
    }

    private static async Task<WorkflowTriggerContext> BuildContextAsync(
        IWorkflowDbContext db,
        IServiceProvider services,
        WorkflowOutboxMessage message,
        TriggerDefinition definition,
        CancellationToken ct)
    {
        var task = await db.WorkflowTasks
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .SingleAsync(t => t.Id == message.TaskId, ct).ConfigureAwait(false);

        var run = await db.WorkflowRuns
            .Include(r => r.DefinitionVersion)
            .SingleAsync(r => r.Id == task.WorkflowRunId, ct).ConfigureAwait(false);

        var tasks = await db.WorkflowTasks
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .Where(t => t.WorkflowRunId == task.WorkflowRunId)
            .ToListAsync(ct).ConfigureAwait(false);

        return new WorkflowTriggerContext
        {
            Subject = run.Subject,
            Event = message.Event,
            CustomEventName = message.CustomEventName,
            Task = task.ToSnapshot(),
            Run = run.ToSnapshot(tasks),
            Branch = task.ForkGroupId is null
                ? null
                : new BranchContext(task.ForkGroupId, task.ForkManifestId, task.AssignedBranchKey, []),
            Config = TriggerConfig.Parse(definition.Configuration),
            ActorId = message.ActorId,
            Variables = new WorkflowVariables(db, task.WorkflowRunId, task.Id),
            Actions = new WorkflowActions(
                (IWorkflowEngine)services.GetService(typeof(IWorkflowEngine))!,
                db, task.WorkflowRunId, message.ActorId),
            Services = services
        };
    }
}
