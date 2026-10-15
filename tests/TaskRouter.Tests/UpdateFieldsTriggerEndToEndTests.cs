using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Data;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Fields;
using TaskRouter.Core.Model;

using TaskRouter.EntityFrameworkCore.Fields;
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
        Assert.AreEqual($"document {document.Id} Status: Scheduled → OnHold", execution.Detail);
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

        await SetBranchAsync(taskId, "C200");

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
        StringAssert.Contains(result.UnwrapError().Message, "'Work item for this task's section' found nothing to update.");
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

    [TestMethod]
    public async Task A_subject_that_is_not_a_document_fails_saying_so()
    {
        var taskId = await StartWithTriggerAsync("999999999", "document", [new("Status", "OnHold")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "'This document' found nothing to update.");
    }

    [TestMethod]
    public async Task A_one_target_matching_two_records_fails_saying_so()
    {
        // The demo allows one work item per section, so its branch target cannot match two.
        // The test catalog's "oneItem" is a One target over every work item on the document.
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, configure: s => s.AddSingleton(TestCatalog([])));

        var document = await NewChangeRequestAsync();
        await AddWorkItemsAsync(document, "C100", "C200");
        var taskId = await StartWithTriggerAsync(document, "oneItem", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message,
            "'One work item' matched more than one record; it must match exactly one.");
    }

    [TestMethod]
    public async Task A_many_target_with_no_records_has_nothing_to_change()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "allWorkItems", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        var execution = await ExecutionAsync();
        Assert.AreEqual(TriggerExecutionStatus.Succeeded, execution.Status, execution.Error);
        Assert.AreEqual("Nothing to change.", execution.Detail);
    }

    [TestMethod]
    public async Task An_unknown_target_fails_with_a_sentence()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "nope", [new("Status", "OnHold")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message,
            "This step updates 'nope', which ChangeRequest does not have.");
    }

    [TestMethod]
    public async Task A_missing_target_fails_with_a_sentence()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithConfigurationAsync(IdOf(document), new Dictionary<string, string?>
        {
            ["assignments"] = FieldAssignments.Serialize([new("Status", "OnHold")])
        });

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "This step's field update has no target configured.");
    }

    [TestMethod]
    public async Task No_fields_fails_with_a_sentence()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", []);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "This step's field update has no fields configured.");
    }

    [TestMethod]
    public async Task An_unparseable_value_fails_naming_the_field()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("InternalDueDate", "abc")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "Internal due date: 'abc' is not");
    }

    [TestMethod]
    public async Task A_record_of_the_wrong_type_is_a_misconfiguration_said_plainly()
    {
        // A change-request workflow pointed at a drawing: the "document" target finds a
        // Drawing, which has no Urgency.
        var drawing = new Drawing { Title = "Deck plan", DocNumber = $"DW-{Guid.NewGuid():N}"[..12] };
        _host.Db.Drawings.Add(drawing);
        await _host.Db.SaveChangesAsync();

        var taskId = await StartWithTriggerAsync(drawing, "document", [new("Urgency", "Priority")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message,
            "This step's field update is misconfigured: 'This document' found a record without 'Urgency'. "
            + "Ask whoever maintains this workflow.");
    }

    [TestMethod]
    public async Task OnWritten_runs_on_a_changed_record_and_not_on_an_unchanged_one()
    {
        // A test catalog whose hook records what it ran on: the demo's own hook touches
        // WorkItem.Modified, which the seeded workflow's progress reporting also touches.
        var written = new List<int>();
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, configure: s => s.AddSingleton(TestCatalog(written)));

        var document = await NewChangeRequestAsync();
        var c100 = await _host.Db.Sections.Where(s => s.Code == "C100").Select(s => s.Id).SingleAsync();
        var c200 = await _host.Db.Sections.Where(s => s.Code == "C200").Select(s => s.Id).SingleAsync();

        var changing = new WorkItem { DocumentId = document.Id, AssignedSectionId = c100 };
        var unchanged = new WorkItem
        {
            DocumentId = document.Id, AssignedSectionId = c200, WorkItemState = WorkItemStatus.InProgress
        };
        _host.Db.WorkItems.AddRange(changing, unchanged);
        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        var taskId = await StartWithTriggerAsync(document, "items", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        CollectionAssert.AreEqual(new[] { changing.Id }, written.ToArray(),
            "OnWritten must run on the item whose state changed, and only on it.");
    }

    [TestMethod]
    public async Task The_system_actor_is_not_checked_by_the_policy()
    {
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, policy: new DenyFieldsPolicy());

        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", WorkflowActors.System)).Unwrap();

        Assert.AreEqual(DocumentStatus.OnHold, await StatusOfAsync(document.Id));
    }

    [TestMethod]
    public async Task The_policy_is_told_the_subject_target_and_fields()
    {
        var policy = new CapturingPolicy();
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, policy: policy);

        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        var seen = policy.FieldUpdates.Single();
        Assert.AreEqual("user-originator", seen.ActorId);
        Assert.AreEqual(new WorkflowSubject("ChangeRequest", IdOf(document)), seen.Subject);
        Assert.IsNotNull(seen.Task);
        Assert.AreEqual("document", seen.FieldTarget);
        CollectionAssert.AreEqual(new[] { "Status" }, seen.Fields.ToArray());
    }

    [TestMethod]
    public async Task A_policy_that_throws_denies_the_update()
    {
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, policy: new ThrowingFieldsPolicy());

        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
        StringAssert.Contains(result.UnwrapError().Message, "the authorization policy failed");
        Assert.AreEqual(DocumentStatus.Scheduled, await StatusOfAsync(document.Id));
    }

    [TestMethod]
    public async Task After_commit_the_update_lands_when_the_outbox_drains()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")],
            dispatch: TriggerDispatchMode.AfterCommit);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();
        Assert.AreEqual(DocumentStatus.Scheduled, await StatusOfAsync(document.Id), "Not before the outbox runs.");

        await _host.Outbox.ProcessPendingAsync();

        Assert.AreEqual(DocumentStatus.OnHold, await StatusOfAsync(document.Id));
        var execution = await ExecutionAsync();
        Assert.AreEqual(TriggerExecutionStatus.Succeeded, execution.Status, execution.Error);
    }

    [TestMethod]
    public async Task A_resolver_that_does_not_track_still_has_its_writes_saved()
    {
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, configure: s => s.AddSingleton(TestCatalog([])));

        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "untracked", [new("Comments", "landed")]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();
        Assert.AreEqual("landed",
            (await _host.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id)).Comments);
    }

    [TestMethod]
    public async Task A_field_update_sees_records_added_earlier_in_the_same_event()
    {
        // An earlier trigger adds a work item without saving it. The update's scope saves
        // pending work before the target resolves, so the new item is found and updated too.
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(
            withTriggers: true,
            configure: s => s.AddScoped<IWorkflowTrigger, AddWorkItemTrigger>());

        var document = await NewChangeRequestAsync();
        await AddWorkItemsAsync(document, "C100");
        var taskId = await StartWithTriggerAsync(document, "allWorkItems", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        var update = await _host.Db.WorkflowTriggerDefinitions.SingleAsync(d => d.TriggerKey == BuiltInTriggerKeys.UpdateFields);
        update.Order = 1;
        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = update.TaskDefinitionId,
            TriggerKey = AddWorkItemTrigger.TriggerKey,
            Event = WorkflowEventKind.TaskCompleted,
            Order = 0,
            FailurePolicy = TriggerFailurePolicy.FailOperation,
            CreatorId = "system",
            ModifierId = "system"
        });
        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();
        var bySection = await _host.Db.WorkItems.AsNoTracking()
            .Where(w => w.DocumentId == document.Id)
            .Select(w => new { w.AssignedSection!.Code, w.WorkItemState })
            .ToDictionaryAsync(w => w.Code, w => w.WorkItemState);

        Assert.HasCount(2, bySection);
        Assert.AreEqual(WorkItemStatus.InProgress, bySection["C100"], "The work item that already existed.");
        Assert.AreEqual(WorkItemStatus.InProgress, bySection["C200"], "The work item added earlier in the same event.");
    }

    // ───────────────────────────── Fixtures ─────────────────────────────

    /// <summary>
    /// A test catalog: a target whose resolver asks for untracked entities, which writes
    /// must survive; a One target that can match several work items; and a work-item
    /// target whose OnWritten hook records each record it ran on.
    /// </summary>
    private static WorkflowFieldCatalog TestCatalog(List<int> written)
    {
        var fields = new WorkflowFieldsBuilder();
        fields.ForSubjectType("ChangeRequest")
            .Target<DocumentBase>("untracked", "This document, untracked", FieldCardinality.One, context =>
            {
                var id = int.Parse(context.Subject.SubjectId, System.Globalization.CultureInfo.InvariantCulture);
                return context.Db<DemoDbContext>().Documents.AsNoTracking().Where(d => d.Id == id);
            })
                .Field(d => d.Comments, "Comments")
            .Target<WorkItem>("oneItem", "One work item", FieldCardinality.One, context =>
            {
                var id = int.Parse(context.Subject.SubjectId, System.Globalization.CultureInfo.InvariantCulture);
                return context.Db<DemoDbContext>().WorkItems.Where(w => w.DocumentId == id);
            })
                .Field(w => w.WorkItemState, "State")
            .Target<WorkItem>("items", "Work items", FieldCardinality.Many, context =>
            {
                var id = int.Parse(context.Subject.SubjectId, System.Globalization.CultureInfo.InvariantCulture);
                return context.Db<DemoDbContext>().WorkItems.Where(w => w.DocumentId == id);
            })
                .Field(w => w.WorkItemState, "State")
                .OnWritten((item, _, _) =>
                {
                    written.Add(item.Id);
                    return Task.CompletedTask;
                });
        return fields.Build();
    }

    private static string IdOf(DocumentBase document) =>
        document.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task SetBranchAsync(int taskId, string branchKey)
    {
        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == taskId);
        task.AssignedBranchKey = branchKey;
        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
    }

    private Task<int> StartWithTriggerAsync(
        DocumentBase document,
        string target,
        IReadOnlyList<FieldAssignment> assignments,
        TriggerFailurePolicy policy = TriggerFailurePolicy.FailOperation,
        string subjectType = "ChangeRequest",
        TriggerDispatchMode dispatch = TriggerDispatchMode.InTransaction) =>
        StartWithTriggerAsync(IdOf(document), target, assignments, policy, subjectType, dispatch);

    private Task<int> StartWithTriggerAsync(
        string subjectId,
        string target,
        IReadOnlyList<FieldAssignment> assignments,
        TriggerFailurePolicy policy = TriggerFailurePolicy.FailOperation,
        string subjectType = "ChangeRequest",
        TriggerDispatchMode dispatch = TriggerDispatchMode.InTransaction) =>
        StartWithConfigurationAsync(subjectId, new Dictionary<string, string?>
        {
            ["target"] = target,
            ["assignments"] = FieldAssignments.Serialize(assignments)
        }, policy, subjectType, dispatch);

    private async Task<int> StartWithConfigurationAsync(
        string subjectId,
        Dictionary<string, string?> configuration,
        TriggerFailurePolicy policy = TriggerFailurePolicy.FailOperation,
        string subjectType = "ChangeRequest",
        TriggerDispatchMode dispatch = TriggerDispatchMode.InTransaction)
    {
        var workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

        var subject = new WorkflowSubject(subjectType, subjectId);
        var run = (await _host.Engine.StartRunAsync(subject, workflowId, "user-originator")).Unwrap();
        var taskId = run.Tasks.Single().Id;

        var definitionId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == taskId).Select(t => t.TaskDefinitionId).SingleAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = definitionId,
            TriggerKey = BuiltInTriggerKeys.UpdateFields,
            Event = WorkflowEventKind.TaskCompleted,
            DispatchMode = dispatch,
            FailurePolicy = policy,
            Configuration = System.Text.Json.JsonSerializer.Serialize(configuration),
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

    /// <summary>Adds a C200 work item to the run's document, and leaves it unsaved.</summary>
    private sealed class AddWorkItemTrigger(DemoDbContext db) : IWorkflowTrigger
    {
        public const string TriggerKey = "test.add-work-item";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() =>
            new(Key, "Add work item", "Adds a work item without saving it.", Enum.GetValues<WorkflowEventKind>(), []);

        public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            var sectionId = await db.Sections.Where(s => s.Code == "C200").Select(s => s.Id).SingleAsync(ct);
            db.WorkItems.Add(new WorkItem
            {
                DocumentId = int.Parse(context.Subject.SubjectId, System.Globalization.CultureInfo.InvariantCulture),
                AssignedSectionId = sectionId
            });
        }
    }

    private sealed class ThrowingFieldsPolicy : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation, WorkflowAuthorizationContext context, CancellationToken ct = default) =>
            operation == WorkflowOperation.UpdateSubjectFields
                ? throw new InvalidOperationException("the policy's database is down")
                : Task.FromResult(WorkflowAuthorizationResult.Allowed);
    }

    private sealed class CapturingPolicy : IWorkflowAuthorizationPolicy
    {
        public List<WorkflowAuthorizationContext> FieldUpdates { get; } = [];

        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation, WorkflowAuthorizationContext context, CancellationToken ct = default)
        {
            if (operation == WorkflowOperation.UpdateSubjectFields)
            {
                FieldUpdates.Add(context);
            }

            return Task.FromResult(WorkflowAuthorizationResult.Allowed);
        }
    }
}
