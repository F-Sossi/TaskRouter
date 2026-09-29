using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using Microsoft.Extensions.Logging.Abstractions;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Runner;

namespace TaskRouter.Tests;

/// <summary>
/// Naming who will handle a step before the step exists.
///
/// <para>The defect these guard against is "the right answer for the wrong reason": a step
/// landing on the pre-assigned person because the role happened to resolve to them anyway.
/// The fixture's roles resolve to section leads, and the people named here are deliberately
/// not those, so a passing assertion means the pre-assignment was read.</para>
/// </summary>
[TestClass]
public class PreAssignmentTests
{
    private TestHost _host = null!;
    private int _workflowId;

    /// <summary>This class is about pre-assignment, not authorization.</summary>
    private sealed class PermitAll : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation,
            WorkflowAuthorizationContext context,
            CancellationToken ct = default) =>
            Task.FromResult(WorkflowAuthorizationResult.Allowed);
    }

    /// <summary>Records every role the assignment resolver was asked about.</summary>
    private sealed class RecordingResolver(IWorkflowAssignmentResolver inner) : IWorkflowAssignmentResolver
    {
        public List<string> Roles { get; } = [];

        public async Task<WorkflowAssignment> ResolveAsync(
            string? roleKey,
            WorkflowAssignment current,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default)
        {
            lock (Roles) { Roles.Add(roleKey ?? "(none)"); }
            return await inner.ResolveAsync(roleKey, current, task, ct);
        }
    }

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

    private async Task<int> StartRunAsync()
    {
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        return run.Id;
    }

    private async Task<int> DefinitionIdAsync(string displayName) =>
        await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == displayName)
            .Select(d => d.Id)
            .SingleAsync();

    private async Task<WorkflowTask> OpenTaskAsync(int runId, string displayName) =>
        await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.WorkflowRunId == runId
                     && t.TaskDefinition!.DisplayName == displayName
                     && t.Status == WorkflowTaskStatus.NotStarted)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

    /// <summary>
    /// Drives the run until the named step exists, approving whatever is open.
    ///
    /// <para>The mainline is Enter Record → Provide Input → PM Review → Close Document.
    /// <b>Provide Input is the forkable one</b>, so it is not a candidate for pre-assignment
    /// and these tests use the steps either side of it: PM Review declares a role, Close
    /// Document declares none.</para>
    /// </summary>
    private async Task DriveToAsync(int runId, string displayName)
    {
        for (var step = 0; step < 5; step++)
        {
            var existing = await _host.Db.WorkflowTasks
                .Include(t => t.TaskDefinition)
                .AnyAsync(t => t.WorkflowRunId == runId
                            && t.TaskDefinition!.DisplayName == displayName);

            if (existing)
            {
                return;
            }

            var open = await _host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId && t.Status == WorkflowTaskStatus.NotStarted)
                .OrderBy(t => t.Id)
                .FirstAsync();

            (await _host.Engine.CompleteTaskAsync(
                open.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();
        }

        Assert.Fail($"The run never reached '{displayName}'.");
    }

    [TestMethod]
    public async Task A_pre_assigned_step_goes_to_that_person_when_it_is_created()
    {
        var runId = await StartRunAsync();
        var pmReviewId = await DefinitionIdAsync("PM Review");

        (await _host.Engine.PreAssignAsync(
            runId, pmReviewId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        await DriveToAsync(runId, "PM Review");

        var created = await OpenTaskAsync(runId, "PM Review");

        Assert.AreEqual("user-worker-c100", created.AssignedToActorId);
    }

    [TestMethod]
    public async Task The_assignment_resolver_is_not_called_for_a_pre_assigned_step()
    {
        // "The right answer for the wrong reason" is the likely defect, so this asserts the
        // mechanism rather than the outcome. Consulting the resolver and discarding its
        // answer would run host code -- often a database lookup -- for a decision already
        // made, and a host watching its own resolver would see work it has no say in.
        RecordingResolver? recording = null;

        await using var host = await TestHost.CreateAsync(
            assignmentResolver: db =>
                recording = new RecordingResolver(
                    new DemoAssignmentResolver(db, NullLogger<DemoAssignmentResolver>.Instance)));

        Assert.IsNotNull(recording);

        var workflowId = await host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id).SingleAsync();

        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await host.Engine.StartRunAsync(subject, workflowId, "user-originator")).Unwrap();

        var pmReviewId = await host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "PM Review").Select(d => d.Id).SingleAsync();

        (await host.Engine.PreAssignAsync(
            run.Id, pmReviewId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        // Drive to the step before the pre-assigned one, then clear -- the steps in
        // between resolve normally and their roles are not what this is about.
        for (var step = 0; step < 4; step++)
        {
            var reached = await host.Db.WorkflowTasks
                .Include(t => t.TaskDefinition)
                .AnyAsync(t => t.WorkflowRunId == run.Id && t.TaskDefinition!.DisplayName == "PM Review");

            if (reached)
            {
                break;
            }

            recording.Roles.Clear();

            var open = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == run.Id && t.Status == WorkflowTaskStatus.NotStarted)
                .OrderBy(t => t.Id)
                .FirstAsync();

            (await host.Engine.CompleteTaskAsync(
                open.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();
        }

        Assert.IsEmpty(recording.Roles,
            "Creating the pre-assigned step asked the resolver about: "
            + string.Join(", ", recording.Roles));
    }

    [TestMethod]
    public async Task A_pre_assignment_beats_the_steps_role()
    {
        // Provide Input declares a role, so this proves the pre-assignment overrides rather
        // than merely filling a gap. The role resolves to a section lead; this person is not
        // one, so the two answers cannot be confused.
        var runId = await StartRunAsync();
        var pmReviewId = await DefinitionIdAsync("PM Review");

        var roleKey = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.Id == pmReviewId).Select(d => d.AssignmentRoleKey).SingleAsync();

        Assert.IsNotNull(roleKey, "This test is pointless on a step with no role.");

        (await _host.Engine.PreAssignAsync(
            runId, pmReviewId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        await DriveToAsync(runId, "PM Review");

        var created = await OpenTaskAsync(runId, "PM Review");

        Assert.AreEqual("user-worker-c100", created.AssignedToActorId);
        Assert.AreNotEqual("user-head-engineering", created.AssignedToActorId,
            "That is what the role would have produced.");
    }

    [TestMethod]
    public async Task A_step_with_no_role_can_be_pre_assigned()
    {
        // The case a host could not reach at all, and the reason this is in the engine.
        // ResolveAssignmentAsync returns before calling the resolver when a step declares no
        // role, so a host table consulted from IWorkflowAssignmentResolver is never asked.
        // Close Document is that step.
        var runId = await StartRunAsync();
        var closeId = await DefinitionIdAsync("Close Document");

        var roleKey = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.Id == closeId).Select(d => d.AssignmentRoleKey).SingleAsync();

        Assert.IsNull(roleKey, "This test is about a step with no role; that one has one.");

        (await _host.Engine.PreAssignAsync(
            runId, closeId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        await DriveToAsync(runId, "Close Document");

        var created = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.WorkflowRunId == runId && t.TaskDefinition!.DisplayName == "Close Document")
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        Assert.AreEqual("user-worker-c100", created.AssignedToActorId,
            "Without a role there is nothing for a host seam to hook, so the step would "
            + "otherwise inherit whoever held the previous one.");
    }

    [TestMethod]
    public async Task Removing_it_puts_the_step_back_on_its_role()
    {
        var runId = await StartRunAsync();
        var pmReviewId = await DefinitionIdAsync("PM Review");

        (await _host.Engine.PreAssignAsync(
            runId, pmReviewId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        (await _host.Engine.RemovePreAssignmentAsync(
            runId, pmReviewId, "user-originator")).Unwrap();

        await DriveToAsync(runId, "PM Review");

        var created = await OpenTaskAsync(runId, "PM Review");

        Assert.AreNotEqual("user-worker-c100", created.AssignedToActorId,
            "The pre-assignment was removed, so the role decides again.");
    }

    [TestMethod]
    public async Task Pre_assigning_twice_leaves_one_row_and_the_later_person()
    {
        var runId = await StartRunAsync();
        var pmReviewId = await DefinitionIdAsync("PM Review");

        (await _host.Engine.PreAssignAsync(
            runId, pmReviewId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        (await _host.Engine.PreAssignAsync(
            runId, pmReviewId, new WorkflowAssignment("user-worker-c200", null),
            "user-originator")).Unwrap();

        var standing = (await _host.Engine.GetPreAssignmentsForRunAsync(runId)).Unwrap();

        Assert.HasCount(1, standing, "Setting it again replaces it; that is what changing it means.");
        Assert.AreEqual("user-worker-c200", standing[0].ActorId);
    }

    [TestMethod]
    public async Task A_step_of_another_version_is_refused()
    {
        // A pre-assignment for a step this run can never reach is a mistake worth reporting:
        // the manager would otherwise watch the work go elsewhere with nothing to explain it.
        var runId = await StartRunAsync();

        var foreignDefinitionId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.WorkflowDefinitionVersion!.WorkflowDefinition!.IsSubWorkflow)
            .Select(d => d.Id)
            .FirstAsync();

        var result = await _host.Engine.PreAssignAsync(
            runId, foreignDefinitionId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "another version");
    }

    [TestMethod]
    public async Task A_forkable_step_is_refused()
    {
        // Out of scope by decision -- one named person cannot hold every unit's copy. A
        // manager who pre-assigns one and sees nothing happen has been told nothing.
        var runId = await StartRunAsync();

        var forkable = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.IsForkable && !d.WorkflowDefinitionVersion!.WorkflowDefinition!.IsSubWorkflow)
            .Select(d => d.Id)
            .FirstAsync();

        var result = await _host.Engine.PreAssignAsync(
            runId, forkable, new WorkflowAssignment("user-worker-c100", null), "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "forkable");
    }

    [TestMethod]
    public async Task A_step_that_has_already_started_is_refused()
    {
        // That is a reassignment, and ReassignTaskAsync is the operation for it -- with its
        // own permission. Two ways to do one thing, gated differently, is how they drift.
        var runId = await StartRunAsync();

        var entry = await OpenTaskAsync(runId, "Enter Record");

        var result = await _host.Engine.PreAssignAsync(
            runId, entry.TaskDefinitionId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "Reassign");
    }

    [TestMethod]
    public async Task Promised_work_is_listed_until_the_task_exists()
    {
        var runId = await StartRunAsync();
        var pmReviewId = await DefinitionIdAsync("PM Review");

        (await _host.Engine.PreAssignAsync(
            runId, pmReviewId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        var promised = (await _host.Engine.GetUpcomingTasksForActorAsync("user-worker-c100")).Unwrap();

        Assert.IsTrue(promised.Any(p => p.WorkflowRunId == runId && p.TaskDefinitionId == pmReviewId),
            "Work somebody is promised is invisible to them otherwise -- it is not in any inbox.");

        await DriveToAsync(runId, "PM Review");

        var afterCreation = (await _host.Engine.GetUpcomingTasksForActorAsync("user-worker-c100")).Unwrap();

        Assert.IsFalse(afterCreation.Any(p => p.WorkflowRunId == runId && p.TaskDefinitionId == pmReviewId),
            "The task exists now, so it is in the inbox. Showing both double-counts it.");
    }

    [TestMethod]
    public async Task A_pre_assignment_stands_for_the_run_so_rework_goes_to_the_same_person()
    {
        // Not a token that gets used up: the row is a standing instruction about this run.
        var runId = await StartRunAsync();
        var pmReviewId = await DefinitionIdAsync("PM Review");

        (await _host.Engine.PreAssignAsync(
            runId, pmReviewId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        await DriveToAsync(runId, "PM Review");

        var first = await OpenTaskAsync(runId, "PM Review");
        Assert.AreEqual("user-worker-c100", first.AssignedToActorId);

        // Complete it and drive back round to the same step, however the fixture gets there.
        (await _host.Engine.CompleteTaskAsync(
            first.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-worker-c100")).Unwrap();

        var standing = (await _host.Engine.GetPreAssignmentsForRunAsync(runId)).Unwrap();

        Assert.HasCount(1, standing,
            "Completing the step must not consume the instruction -- a step re-created by "
            + "rework goes to the same person again.");
    }

    [TestMethod]
    public async Task The_planning_view_lines_up_steps_tasks_and_promises()
    {
        // What the screen renders: every step of the workflow, including the ones the run
        // has not reached -- those have no task, which is exactly why definitions rather
        // than tasks are the spine of this view.
        var runId = await StartRunAsync();
        var pmReviewId = await DefinitionIdAsync("PM Review");

        (await _host.Engine.PreAssignAsync(
            runId, pmReviewId, new WorkflowAssignment("user-worker-c100", null),
            "user-originator")).Unwrap();

        var client = new EfWorkflowRunnerClient(
            _host.Db, _host.Engine, new DemoDirectory(_host.Db), new PermitAll());

        var steps = await client.GetPlannedStepsAsync(runId);

        Assert.IsNotEmpty(steps);

        var entry = steps.Single(s => s.Label == "Enter Record");
        Assert.IsNotNull(entry.Status, "The entry task exists, so it has a status.");
        Assert.IsFalse(entry.CanPreAssign, "It has already started -- that is a reassignment.");

        var pmReview = steps.Single(s => s.Label == "PM Review");
        Assert.IsNull(pmReview.Status, "The run has not reached it.");
        Assert.AreEqual("user-worker-c100", pmReview.PreAssignedActorId);
        Assert.IsNotNull(pmReview.PreAssignedDisplayName,
            "A screen shows a name; the engine stores an id.");

        var forkable = steps.Single(s => s.Label == "Provide Input");
        Assert.IsFalse(forkable.CanPreAssign,
            "Forkable, so each unit gets its own copy and the screen must not offer it.");
    }
}
