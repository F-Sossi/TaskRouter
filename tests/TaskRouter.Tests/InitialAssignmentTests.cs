using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// A run can be started already belonging to an org unit.
///
/// <para>Before this, nothing could put a branch key on a run. <c>ForkTaskAsync</c> sets one
/// per branch and <c>StartSubWorkflowAsync</c> takes one to delegate a chain elsewhere, but
/// the path that <i>creates</i> a run had no way to say which org unit the work belongs to —
/// so a workflow that never forked never acquired one, and every role key resolved against
/// nothing and silently kept the current assignment.</para>
///
/// <para>The integration spike found this the first time a real workflow ran: a host whose
/// documents are owned by an org unit could not get role-based assignment to work at all, and
/// not on the entry task under any workaround, because that call passes a placeholder snapshot
/// carrying run id 0.</para>
/// </summary>
[TestClass]
public class InitialAssignmentTests
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

    private WorkflowSubject NewSubject() =>
        new(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());

    [TestMethod]
    public async Task A_run_started_without_an_assignment_behaves_as_before()
    {
        var run = (await _host.Engine.StartRunAsync(
            NewSubject(), _workflowId, "user-originator")).Unwrap();

        var entry = run.Tasks.Single();

        Assert.IsNull(entry.AssignedBranchKey,
            "The default is unchanged: no org unit unless the caller supplies one.");
    }

    [TestMethod]
    public async Task An_initial_assignment_puts_the_org_unit_on_the_entry_task()
    {
        var run = (await _host.Engine.StartRunAsync(
            NewSubject(), _workflowId, "user-originator",
            new WorkflowAssignment(null, "C100"))).Unwrap();

        var entry = run.Tasks.Single();

        Assert.AreEqual("C100", entry.AssignedBranchKey);
    }

    [TestMethod]
    public async Task The_org_unit_is_inherited_by_routed_tasks_so_roles_resolve()
    {
        // This is the whole point. Provide Input carries the section-lead role, and the
        // resolver looks a section up by the branch key it is handed. With nothing on the
        // run, that lookup has nothing to find and the assignment silently stands still.
        var run = (await _host.Engine.StartRunAsync(
            NewSubject(), _workflowId, "user-originator",
            new WorkflowAssignment(null, "C100"))).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.WorkflowRunId == run.Id
                          && t.TaskDefinition!.DisplayName == "Provide Input");

        Assert.AreEqual("C100", provideInput.AssignedBranchKey,
            "The org unit flows down the chain as each task's starting assignment.");

        Assert.AreEqual("user-lead-c100", provideInput.AssignedToActorId,
            "And the section-lead role finally has a section to resolve against.");
    }

    [TestMethod]
    public async Task An_initial_actor_is_used_where_the_entry_task_has_no_role()
    {
        // A host may want to name the person as well as the org unit. Enter Record carries
        // the originator role in this workflow, so the resolver has the final say -- but the
        // assignment supplied is what it starts from, not the actor who called.
        var run = (await _host.Engine.StartRunAsync(
            NewSubject(), _workflowId, "user-originator",
            new WorkflowAssignment("user-lead-c100", "C100"))).Unwrap();

        var entry = run.Tasks.Single();

        Assert.AreEqual("C100", entry.AssignedBranchKey);
        Assert.IsNotNull(entry.AssignedToActorId);
    }
}
