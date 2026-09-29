using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Characterization tests for fork, convergence and selective rejection — the code
/// path that had zero coverage in the original system and contained two race conditions.
/// </summary>
[TestClass]
public class ForkConvergenceTests
{
    private TestHost _host = null!;
    private int _workflowId;
    private int _pmReviewDefId;
    private int _provideInputDefId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();

        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

        _pmReviewDefId = await DefinitionIdAsync("PM Review");
        _provideInputDefId = await DefinitionIdAsync("Provide Input");
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<int> DefinitionIdAsync(string displayName) =>
        await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == displayName)
            .Select(d => d.Id)
            .SingleAsync();

    private async Task<int> StartRunAndReachProvideInputAsync()
    {
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();

        var entryTaskId = run.Tasks.Single().Id;
        (await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        return run.Id;
    }

    private async Task<WorkflowTask> OpenTaskAsync(int runId, string displayName) =>
        await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.WorkflowRunId == runId
                     && t.TaskDefinition!.DisplayName == displayName
                     && t.Status == WorkflowTaskStatus.NotStarted)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

    [TestMethod]
    public async Task Fork_creates_one_branch_per_key_and_marks_origin_forked()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var fork = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200", "C300"], _pmReviewDefId, "user-originator")).Unwrap();

        Assert.HasCount(3, fork.Branches);
        CollectionAssert.AreEquivalent(
            new[] { "C100", "C200", "C300" },
            fork.Branches.Select(b => b.AssignedBranchKey).ToArray());

        var origin = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == provideInput.Id);
        Assert.AreEqual(WorkflowTaskStatus.Forked, origin.Status);
        Assert.IsTrue(origin.IsForkOrigin);

        // Each branch is assigned to its section's Section Lead by the host resolver.
        var c100 = fork.Branches.Single(b => b.AssignedBranchKey == "C100");
        Assert.AreEqual("user-lead-c100", c100.AssignedToActorId);
    }

    [TestMethod]
    public async Task Convergence_does_not_fire_until_every_branch_completes()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var fork = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], _pmReviewDefId, "user-originator")).Unwrap();

        // First branch completes — no convergence task yet.
        (await _host.Engine.CompleteTaskAsync(
            fork.Branches[0].Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c100")).Unwrap();

        Assert.AreEqual(0, await ConvergenceTaskCountAsync(runId),
            "Convergence fired while a branch was still outstanding.");

        // Second branch completes — convergence appears exactly once.
        (await _host.Engine.CompleteTaskAsync(
            fork.Branches[1].Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c200")).Unwrap();

        Assert.AreEqual(1, await ConvergenceTaskCountAsync(runId),
            "Expected exactly one convergence task after the last branch completed.");
    }

    private async Task<int> ConvergenceTaskCountAsync(int runId) =>
        await _host.Db.WorkflowTasks
            .CountAsync(t => t.WorkflowRunId == runId
                          && t.ForkManifestId != null
                          && t.Status != WorkflowTaskStatus.Cancelled);

    [TestMethod]
    public async Task Convergence_task_is_created_only_once_even_if_routing_runs_again()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var fork = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], _pmReviewDefId, "user-originator")).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            fork.Branches[0].Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c100")).Unwrap();
        (await _host.Engine.CompleteTaskAsync(
            fork.Branches[1].Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c200")).Unwrap();

        // Completing an already-completed branch must fail rather than converge twice.
        var second = await _host.Engine.CompleteTaskAsync(
            fork.Branches[1].Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c200");

        Assert.IsTrue(second.IsError, "Re-completing a completed task should fail.");
        Assert.AreEqual(1, await ConvergenceTaskCountAsync(runId));
    }

    [TestMethod]
    public async Task Selective_rejection_re_forks_only_the_rejected_branches()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var fork = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200", "C300"], _pmReviewDefId, "user-originator")).Unwrap();

        foreach (var branch in fork.Branches)
        {
            (await _host.Engine.CompleteTaskAsync(
                branch.Id, DemoWorkflowSeeder.Outcomes.Approved, "system")).Unwrap();
        }

        var convergence = await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == runId && t.ForkManifestId != null)
            .SingleAsync();

        (await _host.Engine.CompleteWithSelectiveRejectionAsync(
            convergence.Id,
            DemoWorkflowSeeder.Outcomes.Rejected,
            ["C100", "C300"],
            "user-division-head")).Unwrap();

        var rework = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.WorkflowRunId == runId
                     && t.Status == WorkflowTaskStatus.NotStarted
                     && t.ForkGroupId != null)
            .ToListAsync();

        Assert.HasCount(2, rework, "Only the rejected branches should be re-forked.");
        CollectionAssert.AreEquivalent(
            new[] { "C100", "C300" },
            rework.Select(t => t.AssignedBranchKey).ToArray());

        // C200 was approved and must not be reworked.
        Assert.IsFalse(rework.Any(t => t.AssignedBranchKey == "C200"));
    }

    [TestMethod]
    public async Task Selective_rejection_with_no_rejected_branches_completes_normally()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var fork = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], _pmReviewDefId, "user-originator")).Unwrap();

        foreach (var branch in fork.Branches)
        {
            (await _host.Engine.CompleteTaskAsync(
                branch.Id, DemoWorkflowSeeder.Outcomes.Approved, "system")).Unwrap();
        }

        var convergence = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.WorkflowRunId == runId && t.ForkManifestId != null);

        // In the original system this path delegated to CompleteTaskAsync, which then refused the
        // rejection outcome and told the user to use selective rejection — advice they
        // could not act on. Here it simply completes.
        var result = await _host.Engine.CompleteWithSelectiveRejectionAsync(
            convergence.Id, DemoWorkflowSeeder.Outcomes.Approved, [], "user-division-head");

        Assert.IsTrue(result.IsOk, result.IsError ? result.UnwrapError().Message : null);

        var reloaded = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == convergence.Id);
        Assert.AreEqual(WorkflowTaskStatus.Completed, reloaded.Status);
    }

    [TestMethod]
    public async Task Selective_rejection_rejects_unknown_branch_keys()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var fork = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], _pmReviewDefId, "user-originator")).Unwrap();

        foreach (var branch in fork.Branches)
        {
            (await _host.Engine.CompleteTaskAsync(
                branch.Id, DemoWorkflowSeeder.Outcomes.Approved, "system")).Unwrap();
        }

        var convergence = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.WorkflowRunId == runId && t.ForkManifestId != null);

        var result = await _host.Engine.CompleteWithSelectiveRejectionAsync(
            convergence.Id, DemoWorkflowSeeder.Outcomes.Rejected, ["C999"], "user-division-head");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "C999");
    }

    [TestMethod]
    public async Task Fork_requires_at_least_two_branches()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var result = await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100"], _pmReviewDefId, "user-originator");

        Assert.IsTrue(result.IsError);
    }

    [TestMethod]
    public async Task Fork_rejects_duplicate_branch_keys()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var result = await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C100"], _pmReviewDefId, "user-originator");

        Assert.IsTrue(result.IsError);
    }

    [TestMethod]
    public async Task Cannot_fork_a_task_that_is_already_forked()
    {
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], _pmReviewDefId, "user-originator")).Unwrap();

        var again = await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], _pmReviewDefId, "user-originator");

        Assert.IsTrue(again.IsError);
    }

    [TestMethod]
    public async Task A_forks_pending_count_means_steps_left_not_branches_left()
    {
        // Reported as "after the convergence, all approved, the sub-workflow still blocks the
        // parent". Not a defect: a fork spans every step between the forked task and the
        // convergence point, so each branch works through its own chain first and the fork is
        // outstanding until the last of them arrives. The counts say so, but "3 done, 3
        // outstanding" reads as a contradiction unless you know they count different things.
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var fork = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200", "C300"], _pmReviewDefId, "user-originator")).Unwrap();

        foreach (var branch in fork.Branches)
        {
            (await _host.Engine.CompleteTaskAsync(
                branch.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();
        }

        var context = (await _host.Engine.GetForkContextAsync(fork.Branches[0].Id)).Unwrap();

        Assert.AreEqual(3, context.CompletedBranchCount, "Every branch was completed.");

        // The invariant that matters, whatever this fixture's graph looks like: the fork is
        // converged exactly when nothing carrying it is still open.
        var converged = await _host.Db.WorkflowTasks
            .AnyAsync(t => t.WorkflowRunId == runId && t.ForkManifestId != null);

        Assert.AreEqual(context.PendingBranchCount == 0, converged,
            "Convergence happens exactly when no step of the fork is left open.");
    }

    [TestMethod]
    public async Task A_blocking_child_superseded_by_a_fork_does_not_block_its_parent()
    {
        // Reported from a real screen: every branch of a fork inside a delegated chain was
        // complete, the chain itself showed "finished", and the parent still refused with
        // "Cannot complete: blocking task(s) outstanding: <the step>" -- naming the forked
        // origin, which had been superseded half an hour earlier.
        //
        // The guard listed the statuses that mean finished as "not Completed and not
        // Cancelled", and Forked is a third. Every sibling predicate in the engine already
        // had all three; this one was written before Forked existed and never learned about
        // it. The inbox's own query carries a comment predicting exactly this: "a positive
        // status list, not a negative one ... Forked is exactly the value somebody writing
        // this by hand leaves out."
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var adHocDefId = await DefinitionIdAsync("Ad-hoc Provide Input");

        var child = (await _host.Engine.AddAdHocTaskAsync(
            provideInput.Id, adHocDefId, "user-originator")).Unwrap();

        // The status is set directly because the demo's blocking child is not itself
        // forkable, and the rule under test is about what the status *means*: a task
        // superseded by a fork is finished, however it got that way. ForkTaskAsync sets
        // exactly this on an origin.
        var childRow = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == child.Id);
        childRow.Status = WorkflowTaskStatus.Forked;
        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        var result = await _host.Engine.CompleteTaskAsync(
            provideInput.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator");

        Assert.IsTrue(result.IsOk,
            $"A superseded child blocked its parent: {(result.IsError ? result.UnwrapError().Message : string.Empty)}");
    }

    [TestMethod]
    public async Task A_blocking_child_that_is_still_open_does_block_its_parent()
    {
        // The other half, so the fix above cannot be "stop checking".
        var runId = await StartRunAndReachProvideInputAsync();
        var provideInput = await OpenTaskAsync(runId, "Provide Input");

        var adHocDefId = await DefinitionIdAsync("Ad-hoc Provide Input");

        _ = (await _host.Engine.AddAdHocTaskAsync(
            provideInput.Id, adHocDefId, "user-originator")).Unwrap();

        var result = await _host.Engine.CompleteTaskAsync(
            provideInput.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator");

        Assert.IsTrue(result.IsError, "An open blocking child must still stop its parent.");
        StringAssert.Contains(result.UnwrapError().Message, "blocking task(s) outstanding");
    }
}
