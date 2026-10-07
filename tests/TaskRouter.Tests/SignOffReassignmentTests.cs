using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Who a completed task says did it.
///
/// <para>Anybody permitted may sign a task off, not only the person it names — covering for
/// somebody on leave is ordinary. But the task went on carrying the name it was assigned to,
/// so a screen listing completed work credited the wrong person, and the only record of who
/// actually did it was <c>ModifierId</c> and the log.</para>
///
/// <para>So completing a task hands it to whoever completed it. The assignment on a finished
/// task is not a plan any more — nobody is going to do it next — and the useful question it
/// can answer is "who did this".</para>
/// </summary>
[TestClass]
public class SignOffReassignmentTests
{
    private TestHost _host = null!;
    private int _workflowId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();
        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<WorkflowTask> EntryTaskAssignedToAsync(string actorId)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString()),
            _workflowId, "user-originator",
            new WorkflowAssignment(actorId, "C100"))).Unwrap();

        return await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);
    }

    [TestMethod]
    public async Task Signing_off_somebody_elses_task_hands_it_to_the_signer()
    {
        var task = await EntryTaskAssignedToAsync("user-lead-c100");
        Assert.AreEqual("user-lead-c100", task.AssignedToActorId, "the fixture must start assigned");

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-division-head")).Unwrap();

        var completed = await _host.Db.WorkflowTasks
            .AsNoTracking().SingleAsync(t => t.Id == task.Id);

        Assert.AreEqual("user-division-head", completed.AssignedToActorId,
            "a completed task should name who did it, not who was nominated");
        Assert.AreEqual(WorkflowTaskStatus.Completed, completed.Status);
    }

    [TestMethod]
    public async Task The_section_is_left_alone()
    {
        // Only the person changes. The org unit is what the work belonged to, and a
        // stand-in signing off does not move the work to their section -- every role key
        // downstream resolves against it.
        var task = await EntryTaskAssignedToAsync("user-lead-c100");

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-division-head")).Unwrap();

        var completed = await _host.Db.WorkflowTasks
            .AsNoTracking().SingleAsync(t => t.Id == task.Id);

        Assert.AreEqual("C100", completed.AssignedBranchKey);
    }

    [TestMethod]
    public async Task The_handover_is_recorded_rather_than_silently_overwriting()
    {
        // The name that was there is not simply lost: the log says who it was taken from,
        // because "why does this say I did it" needs an answer.
        var task = await EntryTaskAssignedToAsync("user-lead-c100");

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-division-head")).Unwrap();

        var logs = await _host.Db.WorkflowTaskLogs
            .AsNoTracking().Where(l => l.TaskId == task.Id).ToListAsync();

        Assert.IsTrue(
            logs.Any(l => (l.Note ?? string.Empty).Contains("user-lead-c100", StringComparison.Ordinal)),
            "the previous assignee should appear in the log: "
            + string.Join(" | ", logs.Select(l => $"{l.Action}: {l.Note}")));
    }

    [TestMethod]
    public async Task Signing_off_your_own_task_changes_nothing()
    {
        var task = await EntryTaskAssignedToAsync("user-lead-c100");

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c100")).Unwrap();

        var completed = await _host.Db.WorkflowTasks
            .AsNoTracking().SingleAsync(t => t.Id == task.Id);

        Assert.AreEqual("user-lead-c100", completed.AssignedToActorId);

        var logs = await _host.Db.WorkflowTaskLogs
            .AsNoTracking().Where(l => l.TaskId == task.Id).ToListAsync();

        Assert.IsFalse(
            logs.Any(l => l.Action.Contains("Reassigned", StringComparison.Ordinal)),
            "no handover happened, so nothing should claim one did");
    }

    [TestMethod]
    public async Task An_unclaimed_task_is_claimed_by_whoever_completes_it()
    {
        // The case the new assignment default makes common: a step with no role arrives
        // unassigned, somebody in the section picks it up and signs it off, and the record
        // should then say who that was instead of staying blank.
        var task = await EntryTaskAssignedToAsync("user-lead-c100");
        task.AssignedToActorId = null;
        await _host.Db.SaveChangesAsync();

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-division-head")).Unwrap();

        var completed = await _host.Db.WorkflowTasks
            .AsNoTracking().SingleAsync(t => t.Id == task.Id);

        Assert.AreEqual("user-division-head", completed.AssignedToActorId);
    }
}
