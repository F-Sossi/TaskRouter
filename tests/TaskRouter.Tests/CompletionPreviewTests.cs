using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Fields;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;
using TaskRouter.EntityFrameworkCore.Fields;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// Before a step is completed, the runner asks what else the completion will do, and the
/// person confirms. The answer has to be what the completion really does — the same
/// triggers, conditions and next steps — and asking must change nothing.
/// </summary>
[TestClass]
public class CompletionPreviewTests
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

    [TestMethod]
    public async Task A_field_update_and_a_notification_to_the_next_assignee_are_both_listed()
    {
        var document = await NewChangeRequestAsync();
        var entry = await StartAsync(document);
        await AddTriggerAsync(entry, BuiltInTriggerKeys.UpdateFields, WorkflowEventKind.TaskCompleted, new()
        {
            ["target"] = "document",
            ["assignments"] = FieldAssignments.Serialize([new("Status", "OnHold")])
        });
        await AddTriggerAsync(entry, BuiltInTriggerKeys.Notify, WorkflowEventKind.TaskCompleted, new()
        {
            ["sendTo"] = "nextAssignee", ["subject"] = "Over to you", ["body"] = "b"
        });

        var actions = await PreviewAsync(entry, "approved");

        CollectionAssert.Contains(actions.Select(a => a.Description).ToList(),
            "Set Status → On Hold on this document");

        var notify = actions.Single(a => a.Description.StartsWith("Notify ", StringComparison.Ordinal));
        StringAssert.Contains(notify.Description, ": \"Over to you\"");
        Assert.AreEqual("Enter Record", notify.Step);
        Assert.AreEqual("Step is completed", notify.When);
    }

    [TestMethod]
    public async Task Asking_changes_nothing()
    {
        var document = await NewChangeRequestAsync();
        var entry = await StartAsync(document);
        await AddTriggerAsync(entry, BuiltInTriggerKeys.UpdateFields, WorkflowEventKind.TaskCompleted, new()
        {
            ["target"] = "document",
            ["assignments"] = FieldAssignments.Serialize([new("Status", "OnHold")])
        });
        var tasksBefore = await _host.Db.WorkflowTasks.CountAsync();

        _ = await PreviewAsync(entry, "approved");

        _host.Db.ChangeTracker.Clear();
        Assert.AreEqual(tasksBefore, await _host.Db.WorkflowTasks.CountAsync(), "No next step is created.");
        Assert.AreEqual(WorkflowTaskStatus.NotStarted,
            (await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == entry)).Status);
        Assert.AreEqual(DocumentStatus.Scheduled,
            (await _host.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id)).Status);
        Assert.IsFalse(await _host.Db.WorkflowTriggerExecutions.AnyAsync());
        Assert.IsEmpty(_host.Notifications.Delivered);
    }

    [TestMethod]
    public async Task The_next_steps_own_triggers_are_listed_and_bookkeeping_is_not()
    {
        // Provide Input sets a variable when completed, which nobody needs telling; PM
        // Review, which it leads to, notifies its assignee when reached.
        var provideInput = await ProvideInputAsync();

        var actions = await PreviewAsync(provideInput, "approved");

        Assert.IsTrue(actions.Any(a => a.Step == "PM Review" && a.When == "Step is reached"
                && a.Description.Contains("\"PM Review required\"", StringComparison.Ordinal)),
            string.Join(" | ", actions.Select(a => $"{a.Step}/{a.When}: {a.Description}")));
        Assert.IsFalse(actions.Any(a => a.Description.Contains("lastInputOutcome", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_trigger_whose_condition_the_outcome_does_not_meet_is_left_out()
    {
        // PM Review notifies "Work rejected" only when the outcome is rejected.
        var provideInput = await ProvideInputAsync();
        (await _host.Engine.CompleteTaskAsync(provideInput, "approved", "user-lead-c100")).Unwrap();
        var pmReview = await TaskIdAsync("PM Review");

        var approved = await PreviewAsync(pmReview, "approved");
        var rejected = await PreviewAsync(pmReview, "rejected");

        Assert.IsFalse(approved.Any(a => a.Description.Contains("Work rejected", StringComparison.Ordinal)));
        Assert.IsTrue(rejected.Any(a => a.Description.Contains("Work rejected", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Progress_reporting_is_listed()
    {
        var entry = await StartAsync(await NewChangeRequestAsync());

        CollectionAssert.Contains((await PreviewAsync(entry, "approved")).Select(a => a.Description).ToList(),
            "Update the progress percentage from the steps completed so far");
    }

    [TestMethod]
    public async Task A_step_with_no_automation_asks_nothing()
    {
        var entry = await StartAsync(await NewChangeRequestAsync());

        // The demo reports progress on every completion; without that, Enter Record and
        // Close, which "rejected" leads to, do nothing automatic.
        await _host.Db.WorkflowTriggerDefinitions
            .Where(d => d.TriggerKey == BuiltInTriggerKeys.ReportProgress)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsActive, false));

        Assert.IsEmpty(await PreviewAsync(entry, "rejected"));
    }

    [TestMethod]
    public async Task A_field_update_that_would_fail_says_so()
    {
        var entry = await StartAsync(await NewChangeRequestAsync());
        await AddTriggerAsync(entry, BuiltInTriggerKeys.UpdateFields, WorkflowEventKind.TaskCompleted, new()
        {
            ["target"] = "document",
            ["assignments"] = FieldAssignments.Serialize([new("NoSuchField", "x")])
        });

        var actions = await PreviewAsync(entry, "approved");

        Assert.IsTrue(actions.Any(a => a.Description.StartsWith("Update fields, which will fail: ", StringComparison.Ordinal)),
            string.Join(" | ", actions.Select(a => a.Description)));
    }

    [TestMethod]
    public async Task Somebody_who_may_not_complete_the_step_is_told_nothing()
    {
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, policy: new DenyCompletionPolicy());
        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();
        var entry = await StartAsync(await NewChangeRequestAsync());

        var result = await _host.Engine.GetCompletionPreviewAsync(entry, "approved", "user-originator");

        Assert.IsTrue(result.IsError, "The preview is authorized as the completion is.");
    }

    [TestMethod]
    public async Task Sending_branches_back_lists_what_that_path_raises_not_an_ordinary_completion()
    {
        var convergence = await ConvergenceAsync();
        await AddTriggerAsync(convergence, BuiltInTriggerKeys.Notify, WorkflowEventKind.SelectiveRejection, new()
        {
            ["sendTo"] = "nextAssignee", ["subject"] = "Rework", ["body"] = "b"
        });
        await AddTriggerAsync(convergence, BuiltInTriggerKeys.Notify, WorkflowEventKind.TaskCompleted, new()
        {
            ["sendTo"] = "starter", ["subject"] = "Signed off", ["body"] = "b"
        });

        var rejecting = (await _host.Engine.GetSelectiveRejectionPreviewAsync(
            convergence, "rejected", ["C100"], "user-division-head")).Unwrap();
        var approving = (await _host.Engine.GetSelectiveRejectionPreviewAsync(
            convergence, "approved", [], "user-division-head")).Unwrap();

        Assert.IsTrue(rejecting.Any(a => a.When == "Some branches are sent back"
                && a.Description.Contains("\"Rework\"", StringComparison.Ordinal)),
            string.Join(" | ", rejecting.Select(a => $"{a.Step}/{a.When}: {a.Description}")));

        // A straight approval finishes the convergence step like an ordinary completion,
        // so its "completed" triggers run. It used to route on without raising them.
        Assert.IsTrue(approving.Any(a => a.Step == "PM Review" && a.When == "Step is completed"
                && a.Description.Contains("\"Signed off\"", StringComparison.Ordinal)),
            string.Join(" | ", approving.Select(a => $"{a.Step}/{a.When}: {a.Description}")));
        Assert.IsFalse(approving.Any(a => a.When == "Some branches are sent back"));
    }

    // ───────────────────────────── Fixtures ─────────────────────────────

    private async Task<IReadOnlyList<PlannedAction>> PreviewAsync(int taskId, string outcome) =>
        (await _host.Engine.GetCompletionPreviewAsync(taskId, outcome, "user-originator")).Unwrap();

    private async Task<int> StartAsync(DocumentBase document)
    {
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest),
            document.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        return run.Tasks.Single().Id;
    }

    /// <summary>A run forked over C100 and C200 at Provide Input, both branches done: PM Review is waiting.</summary>
    private async Task<int> ConvergenceAsync()
    {
        var provideInput = await ProvideInputAsync();
        var pmReview = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "PM Review").Select(d => d.Id).SingleAsync();

        var fork = (await _host.Engine.ForkTaskAsync(provideInput, ["C100", "C200"], pmReview, "user-originator")).Unwrap();

        foreach (var branch in fork.Branches)
        {
            (await _host.Engine.CompleteTaskAsync(branch.Id, "approved", "user-lead-c100")).Unwrap();
        }

        return await _host.Db.WorkflowTasks.AsNoTracking()
            .Where(t => t.ForkManifestId != null && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .SingleAsync();
    }

    private sealed class DenyCompletionPolicy : TaskRouter.Core.Abstractions.IWorkflowAuthorizationPolicy
    {
        public Task<TaskRouter.Core.Abstractions.WorkflowAuthorizationResult> EvaluateAsync(
            TaskRouter.Core.Abstractions.WorkflowOperation operation,
            TaskRouter.Core.Abstractions.WorkflowAuthorizationContext context,
            CancellationToken ct = default) =>
            Task.FromResult(operation == TaskRouter.Core.Abstractions.WorkflowOperation.CompleteTask
                ? TaskRouter.Core.Abstractions.WorkflowAuthorizationResult.Denied("not yours")
                : TaskRouter.Core.Abstractions.WorkflowAuthorizationResult.Allowed);
    }

    private async Task<int> ProvideInputAsync()
    {
        var entry = await StartAsync(await NewChangeRequestAsync());
        (await _host.Engine.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();
        return await TaskIdAsync("Provide Input");
    }

    private async Task<int> TaskIdAsync(string step) =>
        await _host.Db.WorkflowTasks.AsNoTracking()
            .Where(t => t.TaskDefinition!.DisplayName == step && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .SingleAsync();

    private async Task AddTriggerAsync(
        int taskId, string key, WorkflowEventKind on, Dictionary<string, string?> configuration)
    {
        var definitionId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == taskId).Select(t => t.TaskDefinitionId).SingleAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = definitionId,
            TriggerKey = key,
            Event = on,
            DispatchMode = TriggerDispatchMode.InTransaction,
            FailurePolicy = TriggerFailurePolicy.FailOperation,
            Configuration = System.Text.Json.JsonSerializer.Serialize(configuration),
            CreatorId = "seed",
            ModifierId = "seed"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
    }

    private async Task<ChangeRequest> NewChangeRequestAsync()
    {
        var document = new ChangeRequest
        {
            Title = "Pump room rewire",
            DocNumber = $"CR-{Guid.NewGuid():N}"[..12],
            Originator = "user-originator",
            Status = DocumentStatus.Scheduled
        };

        _host.Db.ChangeRequests.Add(document);
        await _host.Db.SaveChangesAsync();
        return document;
    }
}
