using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// Who a notification goes to is picked by role — this step's assignee, the next step's,
/// whoever started the run, whoever acted, or a named person — and every execution records
/// who it went to, or why it went to nobody.
/// </summary>
[TestClass]
public class NotifyRecipientTests
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
    public async Task The_person_assigned_the_next_step_is_notified_when_this_one_completes()
    {
        var entry = await StartWithNotifyAsync(WorkflowEventKind.TaskCompleted, """{"sendTo":"nextAssignee"}""");

        (await _host.Engine.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        var next = await _host.Db.WorkflowTasks.AsNoTracking()
            .Include(t => t.TaskDefinition)
            .SingleAsync(t => t.TaskDefinition!.DisplayName == "Provide Input");
        Assert.IsNotNull(next.AssignedToActorId, "The demo assigns Provide Input to the section lead.");

        Assert.AreEqual(next.AssignedToActorId, Delivered().Single().RecipientActorId);
        StringAssert.StartsWith((await ExecutionAsync()).Detail, "Sent to ");
        StringAssert.Contains((await ExecutionAsync()).Detail, next.AssignedToActorId);
    }

    [TestMethod]
    public async Task A_next_step_nobody_is_assigned_is_recorded_rather_than_dropped()
    {
        var entry = await StartWithNotifyAsync(WorkflowEventKind.TaskCompleted, """{"sendTo":"nextAssignee"}""");
        await UnassignNextStepsAsync();

        (await _host.Engine.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        Assert.IsEmpty(Delivered());
        var execution = await ExecutionAsync();
        Assert.AreEqual(TriggerExecutionStatus.Succeeded, execution.Status, execution.Error);
        Assert.AreEqual("No one to notify: 'Provide Input', which is not assigned to anyone.", execution.Detail);
    }

    [TestMethod]
    public async Task An_unassigned_step_has_no_one_to_notify_and_says_so()
    {
        // Cancelled rather than completed: completing an unassigned step assigns it to
        // whoever completes it, so it is never unassigned by the time the trigger runs.
        var entry = await StartWithNotifyAsync(WorkflowEventKind.TaskCancelled, """{"sendTo":"assignee"}""");
        await _host.Db.WorkflowTasks.Where(t => t.Id == entry)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssignedToActorId, (string?)null));

        (await _host.Engine.CancelTaskAsync(entry, "user-originator")).Unwrap();

        Assert.IsEmpty(Delivered());
        Assert.AreEqual("No one to notify: this step is not assigned to anyone.", (await ExecutionAsync()).Detail);
    }

    [TestMethod]
    public async Task Next_step_on_an_event_other_than_completion_says_why_nobody_was_told()
    {
        var entry = await StartWithNotifyAsync(WorkflowEventKind.TaskCancelled, """{"sendTo":"nextAssignee"}""");

        (await _host.Engine.CancelTaskAsync(entry, "user-originator")).Unwrap();

        Assert.IsEmpty(Delivered());
        Assert.AreEqual(
            "No one to notify: \"Person assigned the next step\" only works when a step is completed.",
            (await ExecutionAsync()).Detail);
    }

    [TestMethod]
    public async Task The_person_who_started_the_workflow_is_notified()
    {
        var entry = await StartWithNotifyAsync(WorkflowEventKind.TaskCompleted, """{"sendTo":"starter"}""");

        (await _host.Engine.CompleteTaskAsync(entry, "approved", "user-lead-c100")).Unwrap();

        Assert.AreEqual("user-originator", Delivered().Single().RecipientActorId);
    }

    [TestMethod]
    public async Task The_person_who_acted_is_notified()
    {
        var entry = await StartWithNotifyAsync(WorkflowEventKind.TaskCompleted, """{"sendTo":"actor"}""");

        (await _host.Engine.CompleteTaskAsync(entry, "approved", "user-lead-c100")).Unwrap();

        Assert.AreEqual("user-lead-c100", Delivered().Single().RecipientActorId);
    }

    [TestMethod]
    public async Task A_specific_person_is_notified_and_named_in_the_execution()
    {
        var entry = await StartWithNotifyAsync(WorkflowEventKind.TaskCompleted,
            """{"sendTo":"person","recipient":"user-lead-c200"}""");

        (await _host.Engine.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        Assert.AreEqual("user-lead-c200", Delivered().Single().RecipientActorId);
        StringAssert.Contains((await ExecutionAsync()).Detail, "(user-lead-c200)",
            "The directory knows the person, so the execution names them.");
    }

    [TestMethod]
    public async Task A_trigger_saved_before_send_to_existed_still_reaches_its_recipient()
    {
        var entry = await StartWithNotifyAsync(WorkflowEventKind.TaskCompleted, """{"recipient":"user-lead-c200"}""");

        (await _host.Engine.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        Assert.AreEqual("user-lead-c200", Delivered().Single().RecipientActorId);
    }

    [TestMethod]
    public void A_specific_person_must_be_picked_but_only_when_that_is_the_choice()
    {
        var descriptor = new NotifyTrigger().Describe();

        CollectionAssert.Contains(
            TriggerConfig.Validate(descriptor, """{"sendTo":"person","subject":"s","body":"b"}""").ToList(),
            "'Person' is required.");
        Assert.IsEmpty(TriggerConfig.Validate(descriptor, """{"sendTo":"nextAssignee","subject":"s","body":"b"}"""));
        Assert.IsNotEmpty(TriggerConfig.Validate(descriptor, """{"sendTo":"everyone","subject":"s","body":"b"}"""));
    }

    [TestMethod]
    public void The_person_parameter_is_shown_only_for_a_specific_person()
    {
        var parameters = new NotifyTrigger().Describe().Parameters;
        var person = parameters.Single(p => p.Name == "recipient");

        Assert.IsFalse(person.AppliesTo(new Dictionary<string, string?>(), parameters), "Defaults to the assignee.");
        Assert.IsFalse(person.AppliesTo(new Dictionary<string, string?> { ["sendTo"] = "nextAssignee" }, parameters));
        Assert.IsTrue(person.AppliesTo(new Dictionary<string, string?> { ["sendTo"] = "person" }, parameters));
    }

    // ───────────────────────────── Fixtures ─────────────────────────────

    /// <summary>
    /// Starts a run and puts an in-transaction notify trigger on its entry step. In the
    /// transaction rather than after commit, so what it did is recorded by the time the
    /// operation returns.
    /// </summary>
    private async Task<int> StartWithNotifyAsync(WorkflowEventKind on, string recipientJson)
    {
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        var entry = run.Tasks.Single();

        // Merge the recipient settings with a subject and body.
        var configuration = recipientJson.TrimEnd('}') + ""","subject":"Over to you","body":"Task {task.id}"}""";

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entry.TaskDefinitionId,
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = on,
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Configuration = configuration,
            Order = 99,
            CreatorId = "seed",
            ModifierId = "seed"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
        return entry.Id;
    }

    /// <summary>Takes the role off Provide Input, so the step it creates lands on nobody.</summary>
    private async Task UnassignNextStepsAsync()
    {
        await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "Provide Input")
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AssignmentRoleKey, (string?)null));
    }

    private IReadOnlyList<WorkflowNotification> Delivered() =>
        [.. _host.Notifications.Delivered.Where(n => n.Subject_ == "Over to you")];

    private async Task<TriggerExecution> ExecutionAsync() =>
        await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Join(_host.Db.WorkflowTriggerDefinitions.Where(d => d.Order == 99),
                e => e.TriggerDefinitionId, d => d.Id, (e, _) => e)
            .SingleAsync();
}
