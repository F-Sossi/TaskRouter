using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Fields;
using TaskRouter.Core.Model;

using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// The <c>workflow.updateFields</c> trigger, end to end through the engine. The undo
/// scope it relies on is tested on its own in <see cref="UpdateFieldsTriggerTests"/>.
/// </summary>
[TestClass]
public class UpdateFieldsTriggerEndToEndTests
{
    private TestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await TestHost.CreateAsync(withTriggers: true);

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    // ───────────────────────────── The trigger ─────────────────────────────

    [TestMethod]
    public async Task Completing_a_step_puts_the_document_on_hold()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        Assert.AreEqual(DocumentStatus.OnHold, await StatusOfAsync(document.Id));

        var execution = await ExecutionAsync();
        Assert.AreEqual(TriggerExecutionStatus.Succeeded, execution.Status, execution.Error);
        Assert.AreEqual($"ChangeRequest {document.Id} Status: Scheduled → OnHold", execution.Detail);
    }

    [TestMethod]
    public async Task Several_fields_change_together_and_tokens_render()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document",
        [
            new("Status", "OnHold"),
            new("Comments", "Held by {actor}"),
            new("InternalDueDate", "{today+5d}")
        ]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();
        var stored = await _host.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);

        Assert.AreEqual(DocumentStatus.OnHold, stored.Status);
        Assert.AreEqual("Held by user-originator", stored.Comments);
        Assert.AreEqual(DateTime.UtcNow.Date.AddDays(5), stored.InternalDueDate);
    }

    [TestMethod]
    public async Task A_refused_transition_fails_the_completion_by_default()
    {
        var document = await NewChangeRequestAsync(DocumentStatus.Complete);
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "cannot move from Complete to OnHold");
        Assert.AreEqual(WorkflowTaskStatus.NotStarted, await TaskStatusAsync(taskId));
        Assert.AreEqual(DocumentStatus.Complete, await StatusOfAsync(document.Id));
    }

    [TestMethod]
    public async Task Under_log_and_continue_a_refusal_changes_nothing_and_the_step_completes()
    {
        var document = await NewChangeRequestAsync(DocumentStatus.Complete);
        var taskId = await StartWithTriggerAsync(document, "document",
            [new("Comments", "should not stick"), new("Status", "OnHold")],
            TriggerFailurePolicy.LogAndContinue);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();
        var stored = await _host.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);

        Assert.AreEqual(WorkflowTaskStatus.Completed, await TaskStatusAsync(taskId));
        Assert.IsNull(stored.Comments, "The first assignment must be undone with the second.");
        Assert.AreEqual(DocumentStatus.Complete, stored.Status);
        Assert.AreEqual(TriggerExecutionStatus.Failed, (await ExecutionAsync()).Status);
    }

    [TestMethod]
    public async Task Under_log_and_continue_a_refusal_records_what_was_undone()
    {
        var document = await NewChangeRequestAsync(DocumentStatus.Complete);
        var taskId = await StartWithTriggerAsync(document, "document",
            [new("Comments", "should not stick"), new("Status", "OnHold")],
            TriggerFailurePolicy.LogAndContinue);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        var execution = await ExecutionAsync();
        Assert.AreEqual(TriggerExecutionStatus.Failed, execution.Status);
        Assert.IsNotNull(execution.Detail, "The comment was written before the refusal, so the detail must say it was undone.");
        StringAssert.StartsWith(execution.Detail, "Undone: ");
        StringAssert.Contains(execution.Detail, "Comments: (empty) → should not stick");
    }

    [TestMethod]
    public async Task A_value_already_set_is_left_alone_and_reported()
    {
        var document = await NewChangeRequestAsync(DocumentStatus.OnHold);
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        Assert.AreEqual("Nothing to change.", (await ExecutionAsync()).Detail);
    }

    [TestMethod]
    public async Task A_change_request_only_field_works_on_a_change_request()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Urgency", nameof(LarUrgency.Priority))]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();
        Assert.AreEqual(LarUrgency.Priority,
            (await _host.Db.ChangeRequests.AsNoTracking().SingleAsync(c => c.Id == document.Id)).Urgency);
    }

    [TestMethod]
    public async Task A_field_this_subject_type_lacks_fails_with_a_sentence()
    {
        var drawing = new Drawing { Title = "Deck plan", DocNumber = $"DW-{Guid.NewGuid():N}"[..12] };
        _host.Db.Drawings.Add(drawing);
        await _host.Db.SaveChangesAsync();

        // "Drawing" is the subject type's name; DemoDocumentType calls it DW.
        var taskId = await StartWithTriggerAsync(drawing, "document", [new("Urgency", "Priority")],
            subjectType: "Drawing");

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "Drawing has no field 'Urgency' on 'This document'");
    }

    [TestMethod]
    public async Task All_work_items_on_the_document_are_updated()
    {
        var document = await NewChangeRequestAsync();
        await AddWorkItemsAsync(document, "C100", "C200");
        var taskId = await StartWithTriggerAsync(document, "allWorkItems", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        var states = await _host.Db.WorkItems.AsNoTracking()
            .Where(w => w.DocumentId == document.Id).Select(w => w.WorkItemState).ToListAsync();

        Assert.HasCount(2, states);
        Assert.IsTrue(states.All(s => s == WorkItemStatus.InProgress));
    }

    [TestMethod]
    public async Task The_branch_work_item_is_the_one_for_the_tasks_section()
    {
        var document = await NewChangeRequestAsync();
        await AddWorkItemsAsync(document, "C100", "C200");
        var taskId = await StartWithTriggerAsync(document, "branchWorkItem", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == taskId);
        task.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        var bySection = await _host.Db.WorkItems.AsNoTracking()
            .Where(w => w.DocumentId == document.Id)
            .Select(w => new { w.AssignedSection!.Code, w.WorkItemState })
            .ToDictionaryAsync(w => w.Code, w => w.WorkItemState);

        Assert.AreEqual(WorkItemStatus.InProgress, bySection["C200"]);
        Assert.AreEqual(WorkItemStatus.Estimated, bySection["C100"]);
    }

    [TestMethod]
    public async Task A_branch_target_with_no_branch_fails_saying_so()
    {
        var document = await NewChangeRequestAsync();
        await AddWorkItemsAsync(document, "C100");
        var taskId = await StartWithTriggerAsync(document, "branchWorkItem", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "There is no work item for this task's section to update");
    }

    [TestMethod]
    public async Task A_denied_actor_cannot_update_fields()
    {
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, policy: new DenyFieldsPolicy());

        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "fields are frozen");
        Assert.AreEqual(DocumentStatus.Scheduled, await StatusOfAsync(document.Id));
    }

    // ───────────────────────────── Fixtures ─────────────────────────────

    private async Task<int> StartWithTriggerAsync(
        DocumentBase document,
        string target,
        IReadOnlyList<FieldAssignment> assignments,
        TriggerFailurePolicy policy = TriggerFailurePolicy.FailOperation,
        string subjectType = "ChangeRequest")
    {
        var workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

        var subject = new WorkflowSubject(subjectType, document.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var run = (await _host.Engine.StartRunAsync(subject, workflowId, "user-originator")).Unwrap();
        var taskId = run.Tasks.Single().Id;

        var definitionId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == taskId).Select(t => t.TaskDefinitionId).SingleAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = definitionId,
            TriggerKey = BuiltInTriggerKeys.UpdateFields,
            Event = WorkflowEventKind.TaskCompleted,
            FailurePolicy = policy,
            Configuration = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["target"] = target,
                ["assignments"] = FieldAssignments.Serialize(assignments)
            }),
            CreatorId = "system",
            ModifierId = "system"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
        return taskId;
    }

    private async Task<ChangeRequest> NewChangeRequestAsync(DocumentStatus status = DocumentStatus.Scheduled)
    {
        var document = new ChangeRequest
        {
            Title = "Pump room rewire",
            DocNumber = $"CR-{Guid.NewGuid():N}"[..12],
            Originator = "user-originator",
            Status = status
        };

        _host.Db.ChangeRequests.Add(document);
        await _host.Db.SaveChangesAsync();
        return document;
    }

    private async Task AddWorkItemsAsync(DocumentBase document, params string[] sectionCodes)
    {
        foreach (var code in sectionCodes)
        {
            var sectionId = await _host.Db.Sections.Where(s => s.Code == code).Select(s => s.Id).SingleAsync();
            _host.Db.WorkItems.Add(new WorkItem { DocumentId = document.Id, AssignedSectionId = sectionId });
        }

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
    }

    private async Task<DocumentStatus> StatusOfAsync(int documentId)
    {
        _host.Db.ChangeTracker.Clear();
        return await _host.Db.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.Status).SingleAsync();
    }

    private async Task<WorkflowTaskStatus> TaskStatusAsync(int taskId) =>
        await _host.Db.WorkflowTasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.Status).SingleAsync();

    private async Task<TriggerExecution> ExecutionAsync() =>
        await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Join(_host.Db.WorkflowTriggerDefinitions.Where(d => d.TriggerKey == BuiltInTriggerKeys.UpdateFields),
                e => e.TriggerDefinitionId, d => d.Id, (e, _) => e)
            .SingleAsync();

    private sealed class DenyFieldsPolicy : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation, WorkflowAuthorizationContext context, CancellationToken ct = default) =>
            Task.FromResult(operation == WorkflowOperation.UpdateSubjectFields
                ? WorkflowAuthorizationResult.Denied("fields are frozen")
                : WorkflowAuthorizationResult.Allowed);
    }
}
