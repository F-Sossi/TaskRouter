using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Regression tests for defects found in the the original system engine review. Each one fails
/// against the original behaviour.
/// </summary>
[TestClass]
public class RegressionTests
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

    private async Task<(int RunId, int EntryTaskId)> StartRunAsync()
    {
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        return (run.Id, run.Tasks.Single().Id);
    }

    /// <summary>
    /// the original system finding H1. ValidateBlockingAdHocTasksAsync filtered on
    /// `Status != Completed`, so a *cancelled* blocking child blocked its parent
    /// permanently, with no way to clear it.
    /// </summary>
    [TestMethod]
    public async Task Cancelled_blocking_child_does_not_block_its_parent()
    {
        var (_, entryTaskId) = await StartRunAsync();

        var adHocDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "Ad-hoc Provide Input")
            .Select(d => d.Id)
            .SingleAsync();

        var adHoc = (await _host.Engine.AddAdHocTaskAsync(
            entryTaskId, adHocDefId, "user-originator")).Unwrap();

        // While it is open, the parent is blocked.
        var blocked = await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator");
        Assert.IsTrue(blocked.IsError, "An open blocking child should block the parent.");

        // Cancelling it must release the parent.
        (await _host.Engine.CancelTaskAsync(adHoc.Id, "user-originator", "not needed")).Unwrap();

        var afterCancel = await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator");

        Assert.IsTrue(afterCancel.IsOk,
            "A cancelled blocking child must not block the parent. " +
            (afterCancel.IsError ? afterCancel.UnwrapError().Message : string.Empty));
    }

    /// <summary>
    /// the original system finding C3. UpdateTaskAsync assigned a client-supplied Status directly,
    /// completing tasks without routing, triggers or blocking validation. The engine
    /// API here exposes no way to set Status — only outcomes — so the hole is closed
    /// by construction. This test pins that surface.
    /// </summary>
    [TestMethod]
    public void Engine_exposes_no_way_to_set_task_status_directly()
    {
        var methods = typeof(TaskRouter.EntityFrameworkCore.IWorkflowEngine).GetMethods();

        var takesStatus = methods.Any(m =>
            m.GetParameters().Any(p => p.ParameterType == typeof(WorkflowTaskStatus)
                                    || p.ParameterType == typeof(WorkflowTaskStatus?)));

        Assert.IsFalse(takesStatus,
            "No engine operation may accept a caller-supplied task status.");
    }

    /// <summary>
    /// The core unblock for sub-workflows: routes target definition ids, so one
    /// workflow may contain the same task type more than once. the original system resolved routes
    /// via SingleOrDefault on (workflow, task type) and would have thrown here.
    /// </summary>
    [TestMethod]
    public async Task Same_task_type_can_appear_twice_in_one_workflow()
    {
        var provideInputDefs = await _host.Db.WorkflowTaskDefinitions
            .Include(d => d.TaskType)
            .Where(d => d.TaskType!.Key == "provide-input")
            .ToListAsync();

        Assert.HasCount(2, provideInputDefs,
            "The seeded workflow should contain two 'provide-input' definitions.");

        // And they route differently, which is the point.
        var mainline = provideInputDefs.Single(d => d.DisplayName == "Provide Input");
        var adHoc = provideInputDefs.Single(d => d.DisplayName == "Ad-hoc Provide Input");

        var mainlineTargets = await _host.Db.WorkflowTaskRoutes
            .Where(r => r.TaskDefinitionId == mainline.Id)
            .Select(r => r.NextTaskDefinitionId).Distinct().ToListAsync();

        var adHocTargets = await _host.Db.WorkflowTaskRoutes
            .Where(r => r.TaskDefinitionId == adHoc.Id)
            .Select(r => r.NextTaskDefinitionId).Distinct().ToListAsync();

        CollectionAssert.AreNotEquivalent(mainlineTargets, adHocTargets);
    }

    /// <summary>
    /// An ad-hoc task can carry a follow-on chain — the sub-workflow shape the feature
    /// request asked for. the original system forbade this outright (validator error ADHOC_HAS_EDGES).
    /// </summary>
    [TestMethod]
    public async Task Adhoc_task_routes_to_its_own_follow_on_chain()
    {
        var (_, entryTaskId) = await StartRunAsync();

        var adHocDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "Ad-hoc Provide Input").Select(d => d.Id).SingleAsync();

        var adHoc = (await _host.Engine.AddAdHocTaskAsync(
            entryTaskId, adHocDefId, "user-originator")).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            adHoc.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var followOn = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.ParentTaskId == entryTaskId && t.Id != adHoc.Id)
            .ToListAsync();

        Assert.HasCount(1, followOn, "Completing the ad-hoc task should create its follow-on.");
        Assert.AreEqual("Ad-hoc Section Review", followOn[0].TaskDefinition!.DisplayName);
    }

    /// <summary>
    /// Runs pin to a definition version, so editing a workflow cannot reroute work that
    /// is already in flight. the original system had no versioning: CreateOrUpdateWorkflowAsync merged
    /// task definitions in place on the rows live tasks referenced.
    /// </summary>
    [TestMethod]
    public async Task Run_is_pinned_to_the_version_it_started_on()
    {
        var (runId, _) = await StartRunAsync();

        var run = await _host.Db.WorkflowRuns.SingleAsync(r => r.Id == runId);
        var originalVersionId = run.WorkflowDefinitionVersionId;

        // Publish a new version of the same workflow.
        var old = await _host.Db.WorkflowDefinitionVersions.SingleAsync(v => v.Id == originalVersionId);
        old.IsLatest = false;

        _host.Db.WorkflowDefinitionVersions.Add(new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = old.WorkflowDefinitionId,
            Version = old.Version + 1,
            IsPublished = true,
            IsLatest = true,
            PublishedAt = DateTime.UtcNow,
            CreatorId = "system",
            ModifierId = "system"
        });
        await _host.Db.SaveChangesAsync();

        var reloaded = await _host.Db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
        Assert.AreEqual(originalVersionId, reloaded.WorkflowDefinitionVersionId,
            "An in-flight run must stay on the version it started with.");
    }

    [TestMethod]
    public async Task Completing_a_task_twice_fails()
    {
        var (_, entryTaskId) = await StartRunAsync();

        (await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var again = await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator");

        Assert.IsTrue(again.IsError);
    }

    [TestMethod]
    public async Task Completing_the_entry_task_creates_the_routed_follow_on()
    {
        var (runId, entryTaskId) = await StartRunAsync();

        (await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var tasks = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.WorkflowRunId == runId)
            .ToListAsync();

        Assert.HasCount(2, tasks);
        Assert.IsTrue(tasks.Any(t => t.TaskDefinition!.DisplayName == "Provide Input"
                                  && t.Status == WorkflowTaskStatus.NotStarted));
    }

    [TestMethod]
    public async Task An_outcome_the_task_never_declared_is_refused()
    {
        var (runId, entryTaskId) = await StartRunAsync();

        var result = await _host.Engine.CompleteTaskAsync(
            entryTaskId, "approve-ish", "user-originator");

        Assert.IsTrue(result.IsError,
            "a caller-supplied outcome must be checked against the definition");

        StringAssert.Contains(result.UnwrapError().Message, "not a valid outcome");

        // The point of refusing: accepting it would mark the task done, match no route,
        // create nothing, and leave the run stalled with no indication why.
        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == entryTaskId);

        Assert.AreEqual(WorkflowTaskStatus.NotStarted, task.Status);
        Assert.HasCount(1, await _host.Db.WorkflowTasks.Where(t => t.WorkflowRunId == runId).ToListAsync());
    }

    [TestMethod]
    public async Task A_task_that_declares_no_outcomes_accepts_any_key()
    {
        var (runId, entryTaskId) = await StartRunAsync();

        (await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Rejected, "user-originator")).Unwrap();

        // Enter Record rejects straight to Close Document, which is terminal and
        // declares no outcomes -- nothing routes onward, so there is nothing to select.
        var close = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .SingleAsync(t => t.WorkflowRunId == runId
                           && t.TaskDefinition!.DisplayName == "Close Document");

        var result = await _host.Engine.CompleteTaskAsync(close.Id, "done", "user-originator");

        Assert.IsTrue(result.IsOk, result.IsError ? result.UnwrapError().Message : null);
    }

    [TestMethod]
    public async Task Completing_with_a_differently_cased_outcome_still_routes()
    {
        // Routing used to be a string comparison in SQL, so whether "APPROVED" matched the
        // declared "approved" was decided by the database's collation -- the default matched,
        // a case-sensitive one would not, and the same comparison in a host's route condition
        // was ordinal either way. A route points at its outcome by id now, and the key is
        // resolved to that id case-insensitively, which is how completion already validated
        // it. One rule, everywhere.
        var (runId, entryTaskId) = await StartRunAsync();

        var declared = (await _host.Engine.GetValidOutcomesAsync(entryTaskId)).Unwrap();
        var shouted = declared[0].OutcomeKey.ToUpperInvariant();

        Assert.AreNotEqual(declared[0].OutcomeKey, shouted,
            "The fixture's outcome keys are lower-case; this test is pointless otherwise.");

        (await _host.Engine.CompleteTaskAsync(entryTaskId, shouted, "user-originator")).Unwrap();

        var run = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        Assert.IsTrue(
            run.Tasks.Any(t => t.Id != entryTaskId),
            "The outcome was accepted, so the route it names has to have fired. A task that "
            + "completes and routes nowhere is a workflow that stops dead.");
    }

    [TestMethod]
    public async Task An_outcome_the_task_does_not_declare_routes_nowhere()
    {
        // The other half: resolving an unknown key gives no outcome id, so no route matches.
        // This is what a string comparison did too -- worth pinning, because the id lookup
        // made it a deliberate decision rather than a happy accident of "no rows matched".
        var (runId, entryTaskId) = await StartRunAsync();

        var result = await _host.Engine.CompleteTaskAsync(
            entryTaskId, "an-outcome-nobody-declared", "user-originator");

        Assert.IsTrue(result.IsError, "Completion validates the outcome before routing it.");

        var run = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        Assert.HasCount(1, run.Tasks, "Nothing was routed to, and the entry task is untouched.");
        Assert.AreEqual(WorkflowTaskStatus.NotStarted, run.Tasks[0].Status);
    }
}
