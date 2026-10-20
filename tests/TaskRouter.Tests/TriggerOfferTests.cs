using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using DemoDocuments.Server.Workflow;

using TaskRouter.Blazor;
using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Builder;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// The builder offers only trigger options that can run: an event this host raises and
/// that is raised against the step, and a dispatch mode something here drains. What it
/// does not offer, the publish check refuses, because a workflow can reach the builder
/// from somewhere other than its own pickers.
/// </summary>
[TestClass]
public class TriggerOfferTests
{
    private TestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await TestHost.CreateAsync();

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>A builder client on a host whose wiring is <paramref name="configure"/>.</summary>
    private EfWorkflowBuilderClient Client(
        Action<IServiceCollection>? configure = null, WorkflowBuilderOptions? options = null)
    {
        var services = new ServiceCollection();
        configure?.Invoke(services);

        return new EfWorkflowBuilderClient(
            _host.Db,
            new WorkflowTriggerRegistry(
            [
                new SetVariableTrigger(),
                new ReportProgressTrigger(),
                new NotifyTrigger(),
                new WebhookTrigger(),
                new DemoEscalationTrigger(_host.Db, NullLogger<DemoEscalationTrigger>.Instance)
            ]),
            [new RequiresReviewCondition()],
            options ?? new WorkflowBuilderOptions(),
            new TestEditorActorAccessor(),
            services: services.BuildServiceProvider());
    }

    private static void RunsEverything(IServiceCollection services) => services
        .AddSingleton(new WorkflowOutboxOptions())
        .AddSingleton(new WorkflowDeadlineOptions());

    private async Task<WorkflowEditModel> SeededAsync(IWorkflowBuilderClient client)
    {
        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.IsPublished && v.IsLatest && !v.WorkflowDefinition!.IsSubWorkflow)
            .Select(v => v.Id)
            .SingleAsync();

        return (await client.GetWorkflowAsync(versionId))!;
    }

    private static TriggerDescriptor Notify(IReadOnlyList<TriggerDescriptor> descriptors) =>
        descriptors.Single(d => d.Key == BuiltInTriggerKeys.Notify);

    [TestMethod]
    public async Task Without_the_outbox_only_in_transaction_dispatch_is_offered()
    {
        var notify = Notify(await Client().GetTriggerDescriptorsAsync());

        CollectionAssert.AreEqual(new[] { TriggerDispatchMode.InTransaction }, notify.DispatchModes!.ToArray());
        // Its own default is after commit, which nothing here would ever send.
        Assert.AreEqual(TriggerDispatchMode.InTransaction, notify.DefaultDispatch);
    }

    [TestMethod]
    public async Task With_the_outbox_every_dispatch_mode_and_the_triggers_own_default_are_kept()
    {
        var notify = Notify(await Client(RunsEverything).GetTriggerDescriptorsAsync());

        Assert.AreEqual(3, notify.DispatchModes!.Count);
        Assert.AreEqual(TriggerDispatchMode.AfterCommit, notify.DefaultDispatch);
    }

    [TestMethod]
    public async Task Deadline_events_are_offered_only_where_the_sweep_runs()
    {
        var without = Notify(await Client().GetTriggerDescriptorsAsync()).SupportedEvents;
        var with = Notify(await Client(RunsEverything).GetTriggerDescriptorsAsync()).SupportedEvents;

        CollectionAssert.DoesNotContain(without.ToList(), WorkflowEventKind.TaskOverdue);
        CollectionAssert.DoesNotContain(without.ToList(), WorkflowEventKind.TaskDueSoon);
        CollectionAssert.Contains(with.ToList(), WorkflowEventKind.TaskOverdue);
        CollectionAssert.Contains(with.ToList(), WorkflowEventKind.TaskDueSoon);
    }

    [TestMethod]
    public async Task Custom_events_are_offered_only_when_the_host_says_it_raises_them()
    {
        var off = Notify(await Client(RunsEverything).GetTriggerDescriptorsAsync()).SupportedEvents;
        var on = Notify(await Client(RunsEverything, new WorkflowBuilderOptions { OfferCustomEvents = true })
            .GetTriggerDescriptorsAsync()).SupportedEvents;

        CollectionAssert.DoesNotContain(off.ToList(), WorkflowEventKind.Custom);
        CollectionAssert.Contains(on.ToList(), WorkflowEventKind.Custom);
    }

    [TestMethod]
    public async Task The_seeded_workflow_publishes_on_a_host_that_runs_everything()
    {
        var client = Client(RunsEverything);

        var errors = await client.ValidateAsync(await SeededAsync(client));

        Assert.IsFalse(errors.Any(e => e.Code.StartsWith("TRIGGER_NEVER", StringComparison.Ordinal)),
            string.Join("; ", errors.Select(e => e.Message)));
    }

    [TestMethod]
    public async Task On_a_host_without_the_outbox_or_sweep_the_triggers_that_would_never_run_are_named()
    {
        // The seeded workflow notifies after commit and escalates when Provide Input is
        // overdue. On a host with neither the outbox nor the sweep, none of those would ever
        // happen, and before this check the workflow published without a word.
        var client = Client();

        var errors = await client.ValidateAsync(await SeededAsync(client));

        Assert.IsTrue(errors.Any(e => e.Code == "TRIGGER_NEVER_RUNS" && e.Message.Contains("Send Notification")),
            string.Join("; ", errors.Select(e => e.Message)));
        Assert.IsTrue(errors.Any(e => e.Code == "TRIGGER_NEVER_FIRES" && e.Message.Contains("Step is overdue")),
            string.Join("; ", errors.Select(e => e.Message)));
    }

    [TestMethod]
    public async Task Workflow_starts_on_a_step_other_than_the_first_is_refused_unless_inactive()
    {
        var client = Client(RunsEverything);
        var model = await SeededAsync(client);
        var later = model.Tasks.First(t => t.LocalId != model.EntryTaskLocalId);

        var trigger = new TriggerEditModel
        {
            TriggerKey = BuiltInTriggerKeys.SetVariable,
            Event = WorkflowEventKind.RunStarted,
            Configuration = { ["name"] = "x", ["value"] = "y" }
        };
        later.Triggers.Add(trigger);

        var errors = await client.ValidateAsync(model);
        Assert.IsTrue(errors.Any(e => e.Code == "TRIGGER_NEVER_FIRES" && e.Message.Contains("first step")),
            string.Join("; ", errors.Select(e => e.Message)));

        trigger.IsActive = false;
        errors = await client.ValidateAsync(model);
        Assert.IsFalse(errors.Any(e => e.Code == "TRIGGER_NEVER_FIRES"));
    }

    [TestMethod]
    public async Task Notifying_the_next_steps_assignee_is_refused_on_anything_but_completion()
    {
        var client = Client(RunsEverything);
        var model = await SeededAsync(client);
        var entry = model.Tasks.Single(t => t.LocalId == model.EntryTaskLocalId);

        var trigger = new TriggerEditModel
        {
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = WorkflowEventKind.TaskCreated,
            DispatchMode = TriggerDispatchMode.AfterCommit,
            Configuration = { ["sendTo"] = "nextAssignee", ["subject"] = "s", ["body"] = "b" }
        };
        entry.Triggers.Add(trigger);

        var errors = await client.ValidateAsync(model);
        Assert.IsTrue(errors.Any(e => e.Code == "TRIGGER_CHOICE_EVENT"
                && e.Message.Contains("\"Send to: Person assigned the next step\" only works on \"Step is completed\"")),
            string.Join("; ", errors.Select(e => e.Message)));

        trigger.Event = WorkflowEventKind.TaskCompleted;
        errors = await client.ValidateAsync(model);
        Assert.IsFalse(errors.Any(e => e.Code == "TRIGGER_CHOICE_EVENT"));
    }

    [TestMethod]
    public void Which_events_can_fire_follows_the_steps_shape()
    {
        var first = new TaskEditModel { TaskTypeKey = "first" };
        var plain = new TaskEditModel { TaskTypeKey = "plain" };
        var fork = new TaskEditModel { TaskTypeKey = "fork", IsForkable = true };
        var merge = new TaskEditModel { TaskTypeKey = "merge", IsConvergencePoint = true };
        var reminded = new TaskEditModel { TaskTypeKey = "reminded", ReminderLeadTimeMinutes = 1440 };
        var workflow = new WorkflowEditModel
        {
            EntryTaskLocalId = first.LocalId,
            Tasks = [first, plain, fork, merge, reminded],
            SubWorkflows = [new SubWorkflowAttachmentEditModel { TaskLocalId = fork.LocalId }]
        };

        string? Why(WorkflowEventKind kind, TaskEditModel task) => TriggerEvents.WhyNever(kind, task, workflow);

        Assert.IsNull(Why(WorkflowEventKind.RunStarted, first));
        Assert.IsNotNull(Why(WorkflowEventKind.RunStarted, plain));
        Assert.IsNull(Why(WorkflowEventKind.TaskForked, fork));
        Assert.IsNotNull(Why(WorkflowEventKind.BranchAdded, plain));
        Assert.IsNull(Why(WorkflowEventKind.ForkConverged, merge));
        Assert.IsNotNull(Why(WorkflowEventKind.SelectiveRejection, fork));
        Assert.IsNull(Why(WorkflowEventKind.SubWorkflowCompleted, fork));
        Assert.IsNotNull(Why(WorkflowEventKind.SubWorkflowStarted, plain));
        Assert.IsNull(Why(WorkflowEventKind.TaskDueSoon, reminded));
        Assert.IsNotNull(Why(WorkflowEventKind.TaskDueSoon, plain));

        // Events every step can raise are never refused.
        foreach (var kind in new[] { WorkflowEventKind.TaskCreated, WorkflowEventKind.TaskCompleted,
                     WorkflowEventKind.TaskCancelled, WorkflowEventKind.TaskOverdue, WorkflowEventKind.RunCompleted })
        {
            Assert.IsNull(Why(kind, plain), kind.ToString());
        }
    }

    [TestMethod]
    public void Every_event_has_a_label_and_a_description()
    {
        foreach (var kind in Enum.GetValues<WorkflowEventKind>())
        {
            Assert.AreNotEqual(kind.ToString(), TriggerEvents.Label(kind), $"{kind} has no label");
            Assert.IsFalse(string.IsNullOrWhiteSpace(TriggerEvents.Description(kind)), $"{kind} has no description");
        }
    }

    [TestMethod]
    public void Choosing_an_action_lands_on_step_is_completed_when_the_first_event_cannot_fire_here()
    {
        var descriptor = new TriggerDescriptor(
            "workflow.setVariable", "Set", "Sets.",
            [WorkflowEventKind.RunStarted, WorkflowEventKind.TaskCreated, WorkflowEventKind.TaskCompleted],
            []);
        var edit = new TriggerEditModel { Event = WorkflowEventKind.ForkConverged };

        TriggerSelection.Apply(edit, descriptor, [WorkflowEventKind.TaskCreated, WorkflowEventKind.TaskCompleted]);

        Assert.AreEqual(WorkflowEventKind.TaskCompleted, edit.Event);
    }
}
