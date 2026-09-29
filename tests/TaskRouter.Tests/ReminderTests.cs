using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// The reminder sweep.
///
/// The claim happens before the dispatch, not after, so the failure mode is a missed
/// reminder rather than a duplicated one. That is the right way round: a person nudged
/// twice for the same task stops trusting the nudges.
/// </summary>
[TestClass]
public class ReminderTests
{
    /// <summary>Answers a date the test controls, and can be moved between sweeps.</summary>
    private sealed class MutableDueDateResolver : IWorkflowDueDateResolver
    {
        public DateTime? Due { get; set; }

        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) => Task.FromResult(Due);
    }

    private MutableDueDateResolver _dueDates = null!;
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _dueDates = new MutableDueDateResolver { Due = DateTime.UtcNow.AddHours(1) };

        _host = await TestHost.CreateAsync(withTriggers: true, dueDates: _dueDates);

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Starts a run, gives the entry task's definition a lead time, and attaches a
    /// notify trigger for TaskDueSoon so a dispatch is observable.
    /// </summary>
    private async Task<WorkflowTask> ArmedTaskAsync(int leadMinutes = 120, bool isTest = false)
    {
        var run = isTest
            ? (await _host.Engine.StartRunOnVersionAsync(
                new WorkflowSubject("ChangeRequest", "1"),
                await LatestVersionIdAsync(),
                "user-originator",
                isTest: true)).Unwrap()
            : (await _host.Engine.StartRunAsync(
                new WorkflowSubject("ChangeRequest", "1"), _definitionId, "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        var definition = await _host.Db.WorkflowTaskDefinitions
            .SingleAsync(d => d.Id == task.TaskDefinitionId);

        definition.ReminderLeadTimeMinutes = leadMinutes;

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = definition.Id,
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = WorkflowEventKind.TaskDueSoon,
            // InTransaction rather than the notify trigger's AfterCommit default, so the
            // notification lands in DemoNotificationSink by the time ProcessDueAsync
            // returns. Asserting on after-commit delivery would mean draining the outbox
            // too, which is OutboxIsolationTests' job, not this file's.
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Configuration = """{"subject":"Due soon","body":"This is due soon."}""",
            Order = 1,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();

        return task;
    }

    private async Task<int> LatestVersionIdAsync() =>
        await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _definitionId)
            .OrderByDescending(v => v.Version)
            .Select(v => v.Id)
            .FirstAsync();

    private async Task<DateTime?> ReminderSentAtAsync(int taskId) =>
        await _host.Db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.ReminderSentAt)
            .SingleAsync();

    [TestMethod]
    public async Task It_fires_inside_the_lead_window()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        var fired = await _host.Deadlines!.ProcessDueAsync();

        Assert.AreEqual(1, fired);
        Assert.IsNotNull(await ReminderSentAtAsync(task.Id));
        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_does_not_fire_outside_the_lead_window()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        var fired = await _host.Deadlines!.ProcessDueAsync();

        Assert.AreEqual(0, fired);
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_fires_once()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        Assert.AreEqual(1, await _host.Deadlines!.ProcessDueAsync());
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());

        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task Two_concurrent_sweeps_produce_one_dispatch()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        // Two scopes, two processors, two DbContexts — the shape a web farm has. One
        // shared context would prove nothing: it serialises the two claims for free.
        using var a = _host.Provider!.CreateScope();
        using var b = _host.Provider!.CreateScope();

        var results = await Task.WhenAll(
            a.ServiceProvider.GetRequiredService<IWorkflowDeadlineProcessor>().ProcessDueAsync(),
            b.ServiceProvider.GetRequiredService<IWorkflowDeadlineProcessor>().ProcessDueAsync());

        Assert.AreEqual(1, results.Sum(), "the conditional claim lets exactly one through");
        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_completed_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        await _host.Deadlines!.ProcessDueAsync();

        // Asserted on this task rather than on the pass's total. Completing it creates the
        // follow-on Provide Input task, whose seeded definition does carry a lead time, so
        // the sweep legitimately fires for that one — and a total of 0 would be asserting
        // something this test does not mean.
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
        Assert.AreEqual(0, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_cancelled_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        (await _host.Engine.CancelTaskAsync(task.Id, "user-originator", "not needed")).Unwrap();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
    }

    [TestMethod]
    public async Task It_skips_an_archived_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        var tracked = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        tracked.IsArchived = true;
        await _host.Db.SaveChangesAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
    }

    [TestMethod]
    public async Task It_skips_a_task_in_a_test_run()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync(isTest: true);

        // The reason this predicate is in the library and not in each host: an admin
        // trying a workflow out in the builder must not page real people.
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_definition_with_no_lead_time()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), _definitionId, "user-originator")).Unwrap();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await ReminderSentAtAsync(run.Tasks.Single().Id));
    }

    [TestMethod]
    public async Task It_skips_a_task_with_no_due_date()
    {
        _dueDates.Due = null;
        var task = await ArmedTaskAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task A_deadline_moved_later_defers_the_nudge()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        // Extended before the sweep ever ran. A date stamped at creation would fire here.
        _dueDates.Due = DateTime.UtcNow.AddDays(10);

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task A_deadline_moved_earlier_brings_the_nudge_forward()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());

        // This is why the candidate query does not filter on DueDate: a task pulled
        // forward would never enter a set narrowed to "already inside its window".
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);

        Assert.AreEqual(1, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNotNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task The_re_resolved_date_is_written_back()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        var moved = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        _dueDates.Due = moved;

        await _host.Deadlines!.ProcessDueAsync();

        var stored = await _host.Db.WorkflowTasks
            .AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.DueDate).SingleAsync();

        // Repairing the cached date is a side effect of re-resolving, and it is what
        // keeps the inbox's Due column honest without a second sweep.
        Assert.AreEqual(moved, stored);
    }

    [TestMethod]
    public async Task The_dispatch_is_attributed_to_the_system_actor()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        await _host.Deadlines!.ProcessDueAsync();

        var execution = await _host.Db.WorkflowTriggerExecutions
            .AsNoTracking()
            .Where(e => e.TaskId == task.Id && e.Event == WorkflowEventKind.TaskDueSoon)
            .SingleAsync();

        // TriggerExecution has no ActorId column: WorkflowEntity's CreatorId is where
        // the dispatching actor lands (TriggerDispatcher.cs:172).
        Assert.AreEqual(WorkflowActors.System, execution.CreatorId);
    }
}
