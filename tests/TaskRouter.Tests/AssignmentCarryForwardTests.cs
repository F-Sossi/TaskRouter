using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Who gets the next step when nobody has been named for it.
///
/// <para>Three things can decide a new task's assignee, in order: a pre-assignment for that
/// step, the host's resolver if the step carries an assignment role, or — if neither applies
/// — whatever the previous task was assigned to. That last fallback is the one this covers,
/// and <see cref="WorkflowDefinitionVersion.CarryAssignmentForward"/> is what makes it
/// optional.</para>
///
/// <para>It defaults to off, so an unroled step arrives unassigned and sits in its section's
/// unclaimed work until somebody takes it. Carrying the assignee forward silently hands the
/// next step to whoever happened to finish the last one, which is right for a workflow one
/// person shepherds end to end and wrong for most others — but it was the only behaviour
/// available, so it had to be the one a workflow opts into rather than the one it is stuck
/// with.</para>
///
/// <para><b>The section is kept either way.</b> Unassigned means no person, never no
/// section: the inbox only shows unclaimed work to people in the task's own org unit
/// (<c>WorkflowEngine.Inbox.cs</c>), so a task with neither would be invisible to everybody
/// and the work would simply stop.</para>
/// </summary>
[TestClass]
public class AssignmentCarryForwardTests
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

    /// <summary>
    /// Sets the flag on whichever version new runs start on.
    ///
    /// The seeded workflow is published, and publishing is what makes a version immutable —
    /// but this is a host-level policy switch rather than part of the routing graph, and the
    /// builder writes it the same way on a draft. Setting it directly here keeps the test
    /// about the engine's behaviour rather than about the builder's save path.
    /// </summary>
    private async Task SetCarryForwardAsync(bool value)
    {
        var version = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _workflowId && v.IsPublished && v.IsLatest)
            .SingleAsync();

        version.CarryAssignmentForward = value;
        await _host.Db.SaveChangesAsync();
    }

    /// <summary>
    /// Runs to "Close Document", which is the one step in the seeded workflow carrying no
    /// assignment role — so it is the step whose assignee this setting decides. Rejecting
    /// the entry task routes straight to it.
    /// </summary>
    /// <param name="startingActor">Who the run starts on. The entry task carries the
    /// originator role, which this demo resolves as "whoever it already was" — so this is
    /// also the person a carried-forward assignment would arrive from. Null leaves the
    /// chain with no actor at all, which is the right setup for asserting the default and
    /// the wrong one for asserting carry-forward: null carried forward is still null.</param>
    private async Task<(WorkflowTask Unroled, string? EntryActor)> AdvanceToUnroledTaskAsync(
        string? startingActor = null)
    {
        var run = (await _host.Engine.StartRunAsync(
            NewSubject(), _workflowId, "user-originator",
            new WorkflowAssignment(startingActor, "C100"))).Unwrap();

        var entry = run.Tasks.Single();

        (await _host.Engine.CompleteTaskAsync(
            entry.Id, DemoWorkflowSeeder.Outcomes.Rejected, "user-originator")).Unwrap();

        var unroled = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.WorkflowRunId == run.Id
                          && t.TaskDefinition!.DisplayName == "Close Document");

        var entryActor = await _host.Db.WorkflowTasks
            .Where(t => t.Id == entry.Id).Select(t => t.AssignedToActorId)
            .SingleAsync();

        return (unroled, entryActor);
    }

    [TestMethod]
    public async Task By_default_an_unroled_step_arrives_unassigned_but_keeps_its_section()
    {
        // Started with a named person precisely so there is somebody to carry: the
        // default has to drop a real assignee, not merely fail to invent one.
        var (closeDocument, entryActor) = await AdvanceToUnroledTaskAsync("user-lead-c100");

        Assert.IsNotNull(entryActor, "the step before must have had somebody on it");

        Assert.IsNull(closeDocument.AssignedToActorId,
            "the default is that nobody is handed work they did not ask for");

        Assert.AreEqual("C100", closeDocument.AssignedBranchKey,
            "the section must survive, or the inbox cannot show this to anyone and the "
            + "work is lost rather than merely unclaimed");
    }

    [TestMethod]
    public async Task Unassigned_work_is_offered_to_its_own_section()
    {
        // The other half of the default, and the reason dropping the section would be a
        // defect rather than a preference: this is how the work gets picked up at all.
        var (closeDocument, _) = await AdvanceToUnroledTaskAsync("user-lead-c100");

        var inbox = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-lead-c100", ["C100"])).Unwrap();

        var waiting = inbox.SingleOrDefault(t => t.TaskId == closeDocument.Id);

        Assert.IsNotNull(waiting,
            "a section member should see unclaimed work sitting in their section");
        Assert.IsTrue(waiting.IsUnclaimed);
    }

    [TestMethod]
    public async Task With_the_switch_on_the_previous_assignee_carries_forward()
    {
        await SetCarryForwardAsync(true);

        var (closeDocument, entryActor) = await AdvanceToUnroledTaskAsync("user-lead-c100");

        Assert.IsNotNull(entryActor);
        Assert.AreEqual(entryActor, closeDocument.AssignedToActorId,
            "with carry-forward on, the step follows whoever held the last one");
        Assert.AreEqual("C100", closeDocument.AssignedBranchKey);
    }

    [TestMethod]
    public async Task A_pre_assignment_still_wins_over_both()
    {
        // Pre-assignment is a decision somebody made deliberately about this run, so it
        // outranks a default about what to do when nobody decided.
        await SetCarryForwardAsync(false);

        var run = (await _host.Engine.StartRunAsync(
            NewSubject(), _workflowId, "user-originator",
            new WorkflowAssignment(null, "C100"))).Unwrap();

        var closeDef = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "Close Document"
                     && d.WorkflowDefinitionVersionId == run.WorkflowDefinitionVersionId)
            .SingleAsync();

        (await _host.Engine.PreAssignAsync(
            run.Id, closeDef.Id, new WorkflowAssignment("user-lead-c100", null),
            "user-originator")).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Rejected, "user-originator")).Unwrap();

        var closeDocument = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.WorkflowRunId == run.Id
                          && t.TaskDefinition!.DisplayName == "Close Document");

        Assert.AreEqual("user-lead-c100", closeDocument.AssignedToActorId,
            "a pre-assignment is an explicit decision and outranks the default");
    }

    [TestMethod]
    public async Task A_role_still_decides_where_one_is_set()
    {
        // The switch governs only the fallback. A step with a role goes to the host's
        // resolver whatever this is set to, or turning it off would quietly disable
        // role-based assignment throughout.
        await SetCarryForwardAsync(false);

        var run = (await _host.Engine.StartRunAsync(
            NewSubject(), _workflowId, "user-originator",
            new WorkflowAssignment(null, "C100"))).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.WorkflowRunId == run.Id
                          && t.TaskDefinition!.DisplayName == "Provide Input");

        Assert.AreEqual("user-lead-c100", provideInput.AssignedToActorId,
            "the section-lead role must still resolve with carry-forward off");
    }
}
