using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// After-commit triggers are allowed to write to the host's tables, which raises a
/// question the engine has to answer: when do those writes commit, and what happens to
/// the batch when one of them fails?
///
/// The answer is a scope and a transaction per message. These tests pin it, because the
/// failure it prevents is quiet: without isolation every trigger's changes pile onto the
/// processor's own context, so one bad change fails the save for messages that had
/// nothing to do with it, and a partial failure leaves some of them applied.
/// </summary>
[TestClass]
public class OutboxIsolationTests
{
    /// <summary>Writes to a host table, then succeeds. Stands in for a trigger that
    /// updates a document or a work item.</summary>
    private sealed class WritesThenSucceedsTrigger(DemoDbContext db) : IWorkflowTrigger
    {
        public const string TriggerKey = "test.writesThenSucceeds";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() => new(
            Key, "Writes then succeeds", "Test trigger.",
            [WorkflowEventKind.TaskCompleted], []);

        public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            var doc = await db.Documents.SingleAsync(d => d.DocNumber == "OUTBOX-1", ct);
            doc.Title = "written by a trigger that succeeded";
        }
    }

    /// <summary>Writes to a host table, then throws.</summary>
    private sealed class WritesThenThrowsTrigger(DemoDbContext db) : IWorkflowTrigger
    {
        public const string TriggerKey = "test.writesThenThrows";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() => new(
            Key, "Writes then throws", "Test trigger.",
            [WorkflowEventKind.TaskCompleted], []);

        public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            var doc = await db.Documents.SingleAsync(d => d.DocNumber == "OUTBOX-2", ct);
            doc.Title = "written by a trigger that then failed";

            await db.SaveChangesAsync(ct);   // the write really is in the database now

            throw new InvalidOperationException("deliberate failure");
        }
    }

    private TestHost _host = null!;
    private int _runId;
    private List<int> _triggerDefinitionIds = [];

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync(withTriggers: true, configure: services =>
        {
            services.AddScoped<IWorkflowTrigger, WritesThenSucceedsTrigger>();
            services.AddScoped<IWorkflowTrigger, WritesThenThrowsTrigger>();

            // The signal and job queue without the hosted service: a background drain
            // running alongside would race every assertion here.
            services.AddSingleton<IWorkflowOutboxSignal, WorkflowOutboxSignal>();
            services.AddSingleton<IWorkflowJobQueue, SignallingJobQueue>();
        });

        foreach (var number in new[] { "OUTBOX-1", "OUTBOX-2" })
        {
            _host.Db.Documents.Add(new ChangeRequest
            {
                Title = "untouched", DocNumber = number, Originator = "u", CreatorId = "u"
            });
        }

        await _host.Db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Attaches both triggers to the entry task, after-commit, and completes it.
    ///
    /// The seeded workflow has triggers of its own — an in-transaction progress report
    /// on this very task — so everything asserted below is scoped to the definitions
    /// created here.
    /// </summary>
    private async Task<int> RunWithBothTriggersAsync()
    {
        var entryDefinitionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.IsPublished && v.IsLatest && !v.WorkflowDefinition!.IsSubWorkflow)
            .Select(v => v.EntryTaskDefinitionId!.Value)
            .SingleAsync();

        foreach (var key in new[] { WritesThenSucceedsTrigger.TriggerKey, WritesThenThrowsTrigger.TriggerKey })
        {
            _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
            {
                TaskDefinitionId = entryDefinitionId,
                TriggerKey = key,
                Event = WorkflowEventKind.TaskCompleted,
                DispatchMode = TriggerDispatchMode.AfterCommit,
                FailurePolicy = TriggerFailurePolicy.LogAndContinue,
                CreatorId = "system",
                ModifierId = "system"
            });
        }

        await _host.Db.SaveChangesAsync();

        _triggerDefinitionIds = await _host.Db.WorkflowTriggerDefinitions
            .Where(t => t.TriggerKey.StartsWith("test."))
            .Select(t => t.Id)
            .ToListAsync();

        var definitionId = await _host.Db.WorkflowDefinitions.Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        _runId = run.Id;
        var entryTaskId = run.Tasks.Single().Id;

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        return entryTaskId;
    }

    [TestMethod]
    public async Task A_failing_trigger_does_not_discard_what_another_trigger_wrote()
    {
        await RunWithBothTriggersAsync();

        // Both messages are queued and neither has run: the engine transaction has
        // committed, but the outbox has not been drained.
        Assert.AreEqual(2, await _host.Db.WorkflowOutbox
            .CountAsync(m => m.Status == OutboxStatus.Pending
                          && _triggerDefinitionIds.Contains(m.TriggerDefinitionId)));

        await _host.Outbox.ProcessPendingAsync();

        // Read through a fresh context: the changes were made on other scopes' contexts.
        await using var scope = _host.Provider!.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DemoDbContext>();

        var succeeded = await db.Documents.AsNoTracking().SingleAsync(d => d.DocNumber == "OUTBOX-1");
        var failed = await db.Documents.AsNoTracking().SingleAsync(d => d.DocNumber == "OUTBOX-2");

        Assert.AreEqual("written by a trigger that succeeded", succeeded.Title,
            "a failing message discarded the work of one that had succeeded");

        Assert.AreEqual("untouched", failed.Title,
            "a failed trigger's write was left behind instead of rolled back");
    }

    [TestMethod]
    public async Task Each_message_records_its_own_outcome()
    {
        await RunWithBothTriggersAsync();
        await _host.Outbox.ProcessPendingAsync();

        var messages = await _host.Db.WorkflowOutbox.AsNoTracking()
            .Where(m => _triggerDefinitionIds.Contains(m.TriggerDefinitionId))
            .ToListAsync();

        Assert.AreEqual(1, messages.Count(m => m.Status == OutboxStatus.Succeeded));

        // The failure is retried rather than abandoned on the first attempt, and the
        // record of it survives the rollback of the work itself.
        var failing = messages.Single(m => m.Status == OutboxStatus.Pending);
        Assert.AreEqual(1, failing.Attempts);
        StringAssert.Contains(failing.Error!, "deliberate failure");
        Assert.IsTrue(failing.NextAttemptAt > DateTime.UtcNow, "a retry should be scheduled");

        var executions = await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Where(e => _triggerDefinitionIds.Contains(e.TriggerDefinitionId))
            .ToListAsync();

        Assert.AreEqual(1, executions.Count(e => e.Status == TriggerExecutionStatus.Succeeded));
        Assert.AreEqual(1, executions.Count(e => e.Status == TriggerExecutionStatus.Failed));
    }

    [TestMethod]
    public async Task A_retry_does_not_apply_the_work_twice()
    {
        await RunWithBothTriggersAsync();

        await _host.Outbox.ProcessPendingAsync();

        // Nothing is due: the succeeded message is finished, and the failed one is
        // waiting out its backoff.
        var second = await _host.Outbox.ProcessPendingAsync();

        Assert.AreEqual(0, second, "a settled message was leased for a second attempt");
    }

    // ─────────────────────── Background versus after-commit ───────────────────────

    [TestMethod]
    public async Task A_background_trigger_signals_the_drain_only_after_the_commit()
    {
        var signal = _host.Provider!.GetRequiredService<IWorkflowOutboxSignal>();

        var entryDefinitionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.IsPublished && v.IsLatest && !v.WorkflowDefinition!.IsSubWorkflow)
            .Select(v => v.EntryTaskDefinitionId!.Value)
            .SingleAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefinitionId,
            TriggerKey = WritesThenSucceedsTrigger.TriggerKey,
            Event = WorkflowEventKind.TaskCompleted,
            DispatchMode = TriggerDispatchMode.Background,
            CreatorId = "system",
            ModifierId = "system"
        });

        await _host.Db.SaveChangesAsync();

        var definitionId = await _host.Db.WorkflowDefinitions.Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "2"), definitionId, "user-originator")).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, "approved", "user-originator")).Unwrap();

        // Signalled: a waiter returns immediately rather than sitting out the timeout.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        waited.Stop();

        Assert.IsLessThan(TimeSpan.FromSeconds(4), waited.Elapsed,
            "a background trigger should have woken the drain");

        // Durability is not traded away for promptness: the row is in the outbox too.
        var backgroundDefinitionId = await _host.Db.WorkflowTriggerDefinitions
            .Where(t => t.TriggerKey == WritesThenSucceedsTrigger.TriggerKey)
            .Select(t => t.Id)
            .SingleAsync();

        Assert.AreEqual(1, await _host.Db.WorkflowOutbox
            .CountAsync(m => m.Status == OutboxStatus.Pending
                          && m.TriggerDefinitionId == backgroundDefinitionId));
    }

    [TestMethod]
    public async Task An_after_commit_trigger_does_not_signal()
    {
        var signal = _host.Provider!.GetRequiredService<IWorkflowOutboxSignal>();

        await RunWithBothTriggersAsync();   // both are AfterCommit

        var waited = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        waited.Stop();

        // It waits out the timeout: after-commit is durable and retried, not prompt.
        // That difference is the only thing separating the two modes.
        Assert.IsGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250), waited.Elapsed);
    }
}
