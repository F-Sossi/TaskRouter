using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// The demo host's escalation, which is the worked example the engine deliberately does
/// not provide. The engine raises TaskOverdue and stops; who sits above a late person is
/// host knowledge, and this is a host walking its own org chart to find out.
/// </summary>
[TestClass]
public class DemoEscalationTests
{
    private sealed class FixedDueDateResolver : IWorkflowDueDateResolver
    {
        public DateTime? Due { get; set; } = DateTime.UtcNow.AddDays(-1);

        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) => Task.FromResult(Due);
    }

    private TestHost _host = null!;

    // No `configure` hook registering the trigger: TestHost registers it for every
    // fixture, because the seeder puts a demo.escalate trigger on Provide Input and the
    // builder rejects a workflow whose trigger keys are not in the registry. Registering
    // it a second time here would trip WorkflowTriggerRegistry's duplicate-key guard.
    [TestInitialize]
    public async Task Setup() =>
        _host = await TestHost.CreateAsync(
            withTriggers: true,
            dueDates: new FixedDueDateResolver());

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Runs one task through the trigger directly, with the given branch key and
    /// assignee, and returns who the notification went to.
    /// </summary>
    private async Task<string?> EscalateAsync(string? branchKey, string? assignee)
    {
        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        task.AssignedBranchKey = branchKey;
        task.AssignedToActorId = assignee;
        task.DueDate = DateTime.UtcNow.AddDays(-1);
        await _host.Db.SaveChangesAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = task.TaskDefinitionId,
            TriggerKey = DemoEscalationTrigger.TriggerKey,
            Event = WorkflowEventKind.TaskOverdue,
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Configuration = """{"subject":"Overdue","body":"Task {task.id} is late."}""",
            Order = 1,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();

        await _host.Deadlines!.ProcessDueAsync();

        return _host.Notifications.Delivered
            .Where(n => n.TaskId == task.Id)
            .Select(n => n.RecipientActorId)
            .SingleOrDefault();
    }

    [TestMethod]
    public async Task It_escalates_to_the_sections_section_lead()
    {
        // C100's Section Lead, per DemoWorkflowSeeder's org seed.
        Assert.AreEqual("user-lead-c100", await EscalateAsync("C100", "user-engineer"));
    }

    [TestMethod]
    public async Task It_escalates_past_a_section_lead_who_is_the_late_one()
    {
        // Escalating to the person who is already late is not an escalation.
        Assert.AreEqual("user-division-head", await EscalateAsync("C100", "user-lead-c100"));
    }

    [TestMethod]
    public async Task It_sends_nothing_when_the_assignee_is_already_the_branch_head()
    {
        // The top of the two-rung rule: nobody sits above the branch head. Without the
        // guard this falls into the Section Lead branch and escalates down the org chart, to
        // somebody with no authority over the person who is late.
        Assert.IsNull(await EscalateAsync("C100", "user-division-head"));
    }

    [TestMethod]
    public async Task It_sends_nothing_when_the_task_has_no_branch_key()
    {
        // A task with no branch key has no org position, so there is nobody above it.
        // Silence beats guessing at a recipient.
        Assert.IsNull(await EscalateAsync(null, "user-engineer"));
    }

    [TestMethod]
    public async Task It_sends_nothing_when_the_section_code_matches_nothing()
    {
        // The second of two ways to have no supervisor: the branch key is present but
        // names no seeded section. That is a data fault, not a missing assignment, and
        // silence beats guessing at a recipient here too.
        Assert.IsNull(await EscalateAsync("C999", "user-engineer"));
    }
}
