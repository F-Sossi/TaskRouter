using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// The overdue sweep.
///
/// Its sibling is <see cref="ReminderTests"/>, and the two differ in exactly one way
/// worth remembering: a reminder needs a lead time configured on the task definition, and
/// an escalation needs nothing at all. Overdue is a fact about a task, not a feature
/// somebody enables, so <see cref="It_fires_for_a_task_with_no_reminder_configuration"/>
/// is the test that pins the whole design decision down.
/// </summary>
[TestClass]
public class EscalationTests
{
    /// <summary>
    /// Answers a date the test controls, can be moved between sweeps, and can be made to
    /// throw — the sweep must treat a failing resolver as a skipped task, never a failed
    /// pass.
    /// </summary>
    private sealed class MutableDueDateResolver : IWorkflowDueDateResolver
    {
        public DateTime? Due { get; set; }
        public bool ShouldThrow { get; set; }

        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) =>
            ShouldThrow
                ? Task.FromException<DateTime?>(new InvalidOperationException("resolver down"))
                : Task.FromResult(Due);
    }

    /// <summary>
    /// Throws from <c>ExecuteAsync</c>. Seeded with
    /// <see cref="TriggerFailurePolicy.FailOperation"/>, which is what makes the
    /// dispatcher rethrow instead of logging and carrying on — the one shape the sweep
    /// has to contain itself.
    /// </summary>
    private sealed class ThrowingTrigger : IWorkflowTrigger
    {
        public const string TriggerKey = "test.throws";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() => new(
            Key, "Throws", "Test trigger.", [WorkflowEventKind.TaskOverdue], []);

        public Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default) =>
            throw new InvalidOperationException("deliberate trigger failure");
    }

    private MutableDueDateResolver _dueDates = null!;
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _dueDates = new MutableDueDateResolver { Due = DateTime.UtcNow.AddDays(-1) };

        // Registered for every test in the class; nothing seeded references the key, so
        // it is inert until a test seeds a definition pointing at it.
        _host = await TestHost.CreateAsync(
            withTriggers: true,
            configure: services => services.AddScoped<IWorkflowTrigger, ThrowingTrigger>(),
            dueDates: _dueDates);

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Starts a run and attaches a notify trigger for TaskOverdue to the entry task's
    /// definition, so a dispatch is observable.
    ///
    /// Note what it does <b>not</b> do: it sets no ReminderLeadTimeMinutes. The seeded
    /// entry task ("Enter Record") has none, which is exactly the condition the overdue
    /// sweep must fire under and the reminder sweep must not.
    /// </summary>
    private async Task<WorkflowTask> OverdueTaskAsync(bool isTest = false)
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

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = task.TaskDefinitionId,
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = WorkflowEventKind.TaskOverdue,
            // InTransaction rather than the notify trigger's AfterCommit default, so the
            // notification lands in DemoNotificationSink by the time ProcessDueAsync
            // returns. Same reasoning as ReminderTests.
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Configuration = """{"subject":"Overdue","body":"This is late."}""",
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

    private async Task<DateTime?> OverdueFiredAtAsync(int taskId) =>
        await _host.Db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.OverdueFiredAt)
            .SingleAsync();

    private async Task<DateTime?> DueDateAsync(int taskId) =>
        await _host.Db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.DueDate)
            .SingleAsync();

    [TestMethod]
    public async Task It_fires_when_the_deadline_has_passed()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        var fired = await _host.Deadlines!.ProcessDueAsync();

        Assert.AreEqual(1, fired);
        Assert.IsNotNull(await OverdueFiredAtAsync(task.Id));
        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_fires_for_a_task_with_no_reminder_configuration()
    {
        // The seeded "Enter Record" definition has no ReminderLeadTimeMinutes, so the
        // reminder sweep will never look at it. This is the no-opt-in rule: overdue is a
        // fact about the task, not a feature somebody enabled.
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        var definition = await _host.Db.WorkflowTaskDefinitions
            .AsNoTracking().SingleAsync(d => d.Id == task.TaskDefinitionId);

        Assert.IsNull(definition.ReminderLeadTimeMinutes, "the fixture's premise");

        Assert.AreEqual(1, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNotNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_does_not_fire_before_the_deadline()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(30);
        var task = await OverdueTaskAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_does_not_fire_for_a_task_with_no_due_date()
    {
        _dueDates.Due = null;
        var task = await OverdueTaskAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_fires_once()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        Assert.AreEqual(1, await _host.Deadlines!.ProcessDueAsync());
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());

        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_completed_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        await _host.Deadlines!.ProcessDueAsync();

        // Asserted on this task rather than the pass total: completing it creates the
        // follow-on Provide Input task, which is itself overdue against this resolver and
        // legitimately fires. Same trap ReminderTests documents.
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
        Assert.AreEqual(0, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_cancelled_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        (await _host.Engine.CancelTaskAsync(task.Id, "user-originator", "not needed")).Unwrap();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_forked_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        // Set directly rather than by forking: this is a test of the candidate query's
        // status list, and Forked is the member a hand-written predicate forgets — a
        // superseded ghost that can never be completed and must never be escalated.
        var tracked = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        tracked.Status = WorkflowTaskStatus.Forked;
        await _host.Db.SaveChangesAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_an_archived_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        var tracked = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        tracked.IsArchived = true;
        await _host.Db.SaveChangesAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_task_in_a_test_run()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync(isTest: true);

        // The reason this predicate is in the library and not in each host: an admin
        // trying a workflow out in the builder must not escalate to a real supervisor.
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task A_deadline_moved_later_repairs_the_column_and_does_not_fire()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        // Extended before the sweep ever ran. Firing here would tell a supervisor about a
        // deadline that no longer exists, which is worse than not firing at all.
        var extended = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        _dueDates.Due = extended;

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));

        // Re-resolving repairs the cached column as a side effect, which is what keeps
        // the inbox's Due column honest without a second sweep.
        Assert.AreEqual(extended, await DueDateAsync(task.Id));
    }

    [TestMethod]
    public async Task A_resolver_that_throws_skips_the_task_without_failing_the_pass()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        _dueDates.ShouldThrow = true;

        // Not an exception: the failure policy for a host resolver is logged-and-skipped,
        // matching IWorkflowDueDateResolver's documented contract.
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));

        // And it recovers on the next pass rather than being poisoned.
        _dueDates.ShouldThrow = false;

        Assert.AreEqual(1, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNotNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task Two_concurrent_sweeps_produce_one_dispatch()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

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
    public async Task The_dispatch_is_attributed_to_the_system_actor()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        await _host.Deadlines!.ProcessDueAsync();

        var execution = await _host.Db.WorkflowTriggerExecutions
            .AsNoTracking()
            .Where(e => e.TaskId == task.Id && e.Event == WorkflowEventKind.TaskOverdue)
            .SingleAsync();

        // TriggerExecution has no ActorId column: WorkflowEntity's CreatorId is where the
        // dispatching actor lands (TriggerDispatcher.cs:172). Nobody acted, so it must not
        // be the assignee.
        Assert.AreEqual(WorkflowActors.System, execution.CreatorId);
    }

    [TestMethod]
    public async Task A_reminder_and_an_escalation_both_fire_in_one_pass()
    {
        // One task, both halves due: a lead time wide enough that it is inside the
        // reminder window, and a deadline already in the past.
        _dueDates.Due = DateTime.UtcNow.AddHours(-1);

        var task = await OverdueTaskAsync();

        var definition = await _host.Db.WorkflowTaskDefinitions
            .SingleAsync(d => d.Id == task.TaskDefinitionId);

        definition.ReminderLeadTimeMinutes = 2880;

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = task.TaskDefinitionId,
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = WorkflowEventKind.TaskDueSoon,
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

        // Both sweeps run in the same pass and both claim their own column. A task that
        // is already late is also, trivially, inside its lead window — the two are not
        // exclusive and neither should suppress the other.
        Assert.AreEqual(2, await _host.Deadlines!.ProcessDueAsync());

        Assert.IsNotNull(await OverdueFiredAtAsync(task.Id));

        var reminded = await _host.Db.WorkflowTasks
            .AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.ReminderSentAt).SingleAsync();

        Assert.IsNotNull(reminded);
        Assert.AreEqual(2, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task A_task_with_no_cached_due_date_never_escalates()
    {
        // Null at creation, and the seeded entry task carries no reminder lead time — so
        // this task is in neither candidate set and nothing will ever re-resolve its date.
        _dueDates.Due = null;
        var task = await OverdueTaskAsync();

        Assert.IsNull(await DueDateAsync(task.Id), "the fixture's premise: nothing was cached");

        // The deadline is real now, and long past. A caller would reasonably expect an
        // escalation.
        _dueDates.Due = DateTime.UtcNow.AddDays(-30);

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));

        // Pinning the limitation, not endorsing it. The overdue query narrows on the
        // cached column to keep its candidate set shrinking, and a task that was never
        // dated has no way back into that set. Documented on OverdueCandidatesAsync; if
        // a repair pass is ever added, this test should start failing and be rewritten
        // to assert the escalation fires.
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
    }

    [TestMethod]
    public async Task A_trigger_that_throws_does_not_stop_the_rest_of_the_pass()
    {
        // The blast radius this protects is the whole sweep, not one task.
        //
        // A trigger with FailurePolicy.FailOperation rethrows out of the dispatcher, and
        // "the operation" it means is the one somebody invoked — except nobody invoked
        // this one. Uncaught, the exception would leave ProcessDueAsync entirely: every
        // later candidate in *both* halves skipped, the hosted service backed off for the
        // error interval, and the same trigger failing again next pass. One misconfigured
        // trigger on one task definition would stall every reminder and every escalation
        // in the system, indefinitely, and the only symptom would be silence.
        //
        // Its sibling A_resolver_that_throws_skips_the_task_without_failing_the_pass
        // covers the other try/catch in SweepAsync — the due-date resolver's — not this
        // one.
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);

        // Two runs, so two open tasks sharing one task definition. Triggers are attached
        // to definitions, so the branch key is the only thing a Condition can tell them
        // apart by.
        var first = await OverdueEntryTaskAsync("2", "C100");
        var second = await OverdueEntryTaskAsync("3", "C200");

        // Lower id first: the candidate query orders by id, so this is the arrangement
        // where the failure happens *before* the task that must still fire. The reverse
        // ordering would pass even without the containment.
        Assert.IsLessThan(second.Id, first.Id, "the fixture's premise: the thrower goes first");

        Seed(first.TaskDefinitionId, ThrowingTrigger.TriggerKey, condition: "task.branch == 'C100'",
            configuration: null, failurePolicy: TriggerFailurePolicy.FailOperation, order: 1);

        Seed(first.TaskDefinitionId, BuiltInTriggerKeys.Notify, condition: "task.branch == 'C200'",
            configuration: """{"subject":"Overdue","body":"This is late."}""",
            failurePolicy: TriggerFailurePolicy.LogAndContinue, order: 2);

        await _host.Db.SaveChangesAsync();

        // Contained, so the pass completes and reports the one event it did fire.
        var fired = await _host.Deadlines!.ProcessDueAsync();

        Assert.AreEqual(1, fired);

        // The point of the test: the second task was reached at all.
        Assert.IsNotNull(await OverdueFiredAtAsync(second.Id));
        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == second.Id));

        // And the first was not silently consumed. Its claim was written inside the same
        // transaction as the dispatch, so it rolled back with it — the task stays a
        // candidate and gets another chance next pass, which is what makes a
        // fixed-and-redeployed trigger recover on its own.
        Assert.IsNull(await OverdueFiredAtAsync(first.Id));
    }

    /// <summary>
    /// Starts a run and returns its overdue entry task, stamped with a branch key so a
    /// trigger Condition can name it. Unlike <see cref="OverdueTaskAsync"/> it attaches
    /// no trigger of its own — the caller seeds exactly what it wants to observe.
    /// </summary>
    private async Task<WorkflowTask> OverdueEntryTaskAsync(string subjectId, string branchKey)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), _definitionId, "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        task.AssignedBranchKey = branchKey;
        await _host.Db.SaveChangesAsync();

        return task;
    }

    /// <summary>
    /// Attaches a TaskOverdue trigger to a task definition. InTransaction rather than the
    /// after-commit default for the same reason the rest of this file uses it: the effect
    /// has to have happened by the time ProcessDueAsync returns.
    /// </summary>
    private void Seed(
        int taskDefinitionId,
        string triggerKey,
        string condition,
        string? configuration,
        TriggerFailurePolicy failurePolicy,
        int order) =>
        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = taskDefinitionId,
            TriggerKey = triggerKey,
            Event = WorkflowEventKind.TaskOverdue,
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Condition = condition,
            Configuration = configuration,
            FailurePolicy = failurePolicy,
            Order = order,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });
}
