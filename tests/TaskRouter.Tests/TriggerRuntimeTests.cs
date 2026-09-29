using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// Trigger runtime: dispatch modes, the outbox, execution records, conditions and
/// ordering. The dispatch-mode behaviour is the part that matters most — running an
/// after-commit trigger inline (the original system's only mode) means a side effect survives a
/// rolled-back transaction.
/// </summary>
[TestClass]
public class TriggerRuntimeTests
{
    private TestHost _host = null!;
    private int _workflowId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync(withTriggers: true);

        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<(int RunId, int EntryTaskId)> StartAsync()
    {
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        return (run.Id, run.Tasks.Single().Id);
    }

    /// <summary>
    /// A trigger must be able to tell an abandoned chain from a finished one. Cancellation
    /// used to fire nothing at all, so a trigger watching for delegated work ending simply
    /// never ran for the cancelled case — it saw the sub-workflow as still open forever.
    ///
    /// Lives here rather than in SubWorkflowTests because that class builds its host
    /// without the trigger subsystem, so FireAsync is a no-op there.
    /// </summary>
    [TestMethod]
    public async Task Cancelling_a_sub_workflow_fires_a_trigger_event()
    {
        var (_, entryTaskId) = await StartAsync();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var parentTaskId = await _host.Db.WorkflowTasks
            .Where(t => t.Id != entryTaskId && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .FirstAsync();

        var parentDefId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == parentTaskId).Select(t => t.TaskDefinitionId).SingleAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = parentDefId,
            TriggerKey = BuiltInTriggerKeys.SetVariable,
            Event = WorkflowEventKind.SubWorkflowCancelled,
            Configuration = """{"name":"abandoned","value":"yes"}""",
            CreatorId = "system",
            ModifierId = "system"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator")).Unwrap();

        (await _host.Engine.CancelSubWorkflowAsync(instance.Id, "user-originator")).Unwrap();

        var executions = await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Where(e => e.Event == WorkflowEventKind.SubWorkflowCancelled)
            .ToListAsync();

        Assert.HasCount(1, executions);
        Assert.AreEqual(TriggerExecutionStatus.Succeeded, executions[0].Status,
            executions[0].Error);
    }

    [TestMethod]
    public async Task In_transaction_trigger_runs_and_records_a_successful_execution()
    {
        var (_, entryTaskId) = await StartAsync();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var executions = await _host.Db.WorkflowTriggerExecutions
            .AsNoTracking().Where(e => e.TaskId == entryTaskId).ToListAsync();

        Assert.IsNotEmpty(executions, "Expected the progress trigger to have run.");
        Assert.IsTrue(executions.All(e => e.Status == TriggerExecutionStatus.Succeeded),
            "Every execution should have succeeded: " +
            string.Join("; ", executions.Select(e => e.Error)));
    }

    [TestMethod]
    public async Task Progress_trigger_updates_the_hosts_work_items()
    {
        

        var changeRequest = new ChangeRequest
        {
            Title = "Progress ChangeRequest", DocNumber = "CR-9001",
            Originator = "user-originator", CreatorId = "user-originator"
        };
        _host.Db.Documents.Add(changeRequest);
        await _host.Db.SaveChangesAsync();

        var section = await _host.Db.Sections.SingleAsync(s => s.Code == "C100");
        _host.Db.WorkItems.Add(new WorkItem { DocumentId = changeRequest.Id, AssignedSectionId = section.Id });
        await _host.Db.SaveChangesAsync();

        var subject = new WorkflowSubject(
            nameof(DemoDocumentType.ChangeRequest), changeRequest.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        var entry = run.Tasks.Single().Id;

        (await _host.Engine.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        var item = await _host.Db.WorkItems.AsNoTracking()
            .SingleAsync(w => w.DocumentId == changeRequest.Id);

        // One of two actionable tasks complete.
        Assert.IsGreaterThan(0d, item.CompletionPercentage,
            "The progress trigger should have reported a non-zero percentage.");
    }

    [TestMethod]
    public async Task Set_variable_trigger_writes_a_run_variable_with_template_substitution()
    {
        var (runId, entryTaskId) = await StartAsync();
        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input");

        (await _host.Engine.CompleteTaskAsync(provideInput.Id, "rejected", "user-lead-c100")).Unwrap();

        var variable = await _host.Db.WorkflowVariables.AsNoTracking()
            .SingleOrDefaultAsync(v => v.WorkflowRunId == runId && v.Name == "lastInputOutcome");

        Assert.IsNotNull(variable);
        Assert.AreEqual("rejected", variable.Value, "The {task.outcome} token should have been rendered.");
    }

    [TestMethod]
    public async Task After_commit_trigger_is_queued_to_the_outbox_not_run_inline()
    {
        var (_, entryTaskId) = await StartAsync();
        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        // Completing Enter Record creates PM Review's predecessor; drive through to
        // the point the after-commit notify trigger is configured (PM Review created).
        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input");

        (await _host.Engine.CompleteTaskAsync(provideInput.Id, "approved", "user-lead-c100")).Unwrap();

        var pending = await _host.Db.WorkflowOutbox.AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending).ToListAsync();

        Assert.IsNotEmpty(pending, "The after-commit notify trigger should be queued.");

        // Crucially it has NOT been delivered yet.
        Assert.IsEmpty(_host.Notifications.Delivered,
            "An after-commit trigger must not deliver before the outbox is processed.");
    }

    [TestMethod]
    public async Task Outbox_processing_delivers_the_notification_and_marks_it_succeeded()
    {
        var (_, entryTaskId) = await StartAsync();
        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input");

        (await _host.Engine.CompleteTaskAsync(provideInput.Id, "approved", "user-lead-c100")).Unwrap();

        var processed = await _host.Outbox.ProcessPendingAsync();

        Assert.IsGreaterThan(0, processed);
        Assert.IsNotEmpty(_host.Notifications.Delivered,
            "The notification should have been delivered once the outbox ran.");

        var messages = await _host.Db.WorkflowOutbox.AsNoTracking().ToListAsync();
        Assert.IsTrue(messages.All(m => m.Status == OutboxStatus.Succeeded),
            "Every message should be marked succeeded: " +
            string.Join("; ", messages.Select(m => m.Error)));
    }

    [TestMethod]
    public async Task Outbox_processing_is_idempotent_across_repeated_runs()
    {
        var (_, entryTaskId) = await StartAsync();
        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input");

        (await _host.Engine.CompleteTaskAsync(provideInput.Id, "approved", "user-lead-c100")).Unwrap();

        await _host.Outbox.ProcessPendingAsync();
        var deliveredAfterFirst = _host.Notifications.Delivered.Count;

        // A second sweep must find nothing pending and deliver nothing further.
        var second = await _host.Outbox.ProcessPendingAsync();

        Assert.AreEqual(0, second);
        Assert.HasCount(deliveredAfterFirst, _host.Notifications.Delivered);
    }

    [TestMethod]
    public async Task Conditional_trigger_does_not_fire_when_the_condition_is_false()
    {
        // The seeded rejection notice on PM Review has condition
        // "task.outcome == 'rejected'". Approving must not fire it.
        var (_, entryTaskId) = await StartAsync();
        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input");
        (await _host.Engine.CompleteTaskAsync(provideInput.Id, "approved", "user-lead-c100")).Unwrap();

        var pmReview = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "PM Review");

        (await _host.Engine.CompleteTaskAsync(pmReview.Id, "approved", "user-division-head")).Unwrap();
        await _host.Outbox.ProcessPendingAsync();

        Assert.IsFalse(
            _host.Notifications.Delivered.Any(n => n.Subject_ == "Work rejected"),
            "The rejection notice must not fire on an approval.");
    }

    [TestMethod]
    public async Task Unknown_trigger_key_is_skipped_rather_than_failing_the_operation()
    {
        var (_, entryTaskId) = await StartAsync();

        var entryDefId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == entryTaskId).Select(t => t.TaskDefinitionId).SingleAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefId,
            TriggerKey = "nobody.implements.this",
            Event = WorkflowEventKind.TaskCompleted,
            CreatorId = "system",
            ModifierId = "system"
        });
        await _host.Db.SaveChangesAsync();

        var result = await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator");

        Assert.IsTrue(result.IsOk,
            "A missing trigger implementation must degrade, not fail the operation.");

        var skipped = await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Status == TriggerExecutionStatus.Skipped);

        Assert.IsNotNull(skipped);
        StringAssert.Contains(skipped.Error, "nobody.implements.this");
    }

    [TestMethod]
    public async Task Failing_trigger_with_FailOperation_rolls_the_whole_operation_back()
    {
        var (_, entryTaskId) = await StartAsync();

        var entryDefId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == entryTaskId).Select(t => t.TaskDefinitionId).SingleAsync();

        // Webhook pointing nowhere, marked critical.
        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefId,
            TriggerKey = BuiltInTriggerKeys.SetVariable,
            Event = WorkflowEventKind.TaskCompleted,
            Configuration = """{"value":"no name supplied"}""",   // 'name' is required
            DispatchMode = TriggerDispatchMode.InTransaction,
            FailurePolicy = TriggerFailurePolicy.FailOperation,
            CreatorId = "system",
            ModifierId = "system"
        });
        await _host.Db.SaveChangesAsync();

        var result = await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError, "FailOperation should surface the trigger failure.");

        // And the task must not be completed, because the transaction rolled back.
        _host.Db.ChangeTracker.Clear();
        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == entryTaskId);
        Assert.AreEqual(WorkflowTaskStatus.NotStarted, task.Status,
            "A FailOperation trigger failure must roll back the task completion.");
    }

    [TestMethod]
    public async Task Failing_trigger_with_LogAndContinue_leaves_the_operation_committed()
    {
        var (_, entryTaskId) = await StartAsync();

        var entryDefId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == entryTaskId).Select(t => t.TaskDefinitionId).SingleAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefId,
            TriggerKey = BuiltInTriggerKeys.SetVariable,
            Event = WorkflowEventKind.TaskCompleted,
            Configuration = """{"value":"no name supplied"}""",
            DispatchMode = TriggerDispatchMode.InTransaction,
            FailurePolicy = TriggerFailurePolicy.LogAndContinue,
            CreatorId = "system",
            ModifierId = "system"
        });
        await _host.Db.SaveChangesAsync();

        var result = await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator");
        Assert.IsTrue(result.IsOk);

        _host.Db.ChangeTracker.Clear();
        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == entryTaskId);
        Assert.AreEqual(WorkflowTaskStatus.Completed, task.Status);

        // The recorded outcome is the real one, unlike the original system which logged
        // "executed successfully" regardless (review finding H3).
        var failed = await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Status == TriggerExecutionStatus.Failed);

        Assert.IsNotNull(failed, "The failure must be recorded, not reported as success.");
        Assert.IsNotNull(failed.Error);
    }
}
