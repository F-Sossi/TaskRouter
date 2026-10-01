using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;
using TaskRouter.EntityFrameworkCore.Builder;
using TaskRouter.EntityFrameworkCore.Runner;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// The runner's server half: the same component drives the builder's test pane and a
/// host's document page, so what sits behind <see cref="IWorkflowRunnerClient"/> has to
/// hold up for both.
///
/// The interesting cases are the test-run rules. Testing a draft means starting a run on
/// a version that was never published, which the ordinary entry point refuses on
/// purpose — so the narrow hole that allows it needs to stay narrow.
/// </summary>
[TestClass]
public class RunnerClientTests
{
    private TestHost _host = null!;
    private EfWorkflowRunnerClient _client = null!;
    private EfWorkflowBuilderClient _builder = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();

        _client = new EfWorkflowRunnerClient(
            _host.Db, _host.Engine, new DemoDirectory(_host.Db), new AllowAll());

        _builder = new EfWorkflowBuilderClient(
            _host.Db,
            // The full set the demo seeder actually uses, DemoEscalationTrigger included.
            // A narrower registry makes the builder reject the *seeded* graph with
            // TRIGGER_UNKNOWN -- the validator working correctly on a fixture that lied.
            new WorkflowTriggerRegistry(
            [
                new SetVariableTrigger(),
                new ReportProgressTrigger(),
                new NotifyTrigger(),
                new WebhookTrigger(),
                new DemoEscalationTrigger(_host.Db, NullLogger<DemoEscalationTrigger>.Instance)
            ]),
            [new RequiresReviewCondition()],
            new WorkflowBuilderOptions
            {
                AssignmentRoles =
                    [DemoRoles.SectionLead, DemoRoles.DivisionHead, DemoRoles.Originator],
            },
            new TestEditorActorAccessor());
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<int> PublishedVersionIdAsync() =>
        await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.IsPublished && v.IsLatest && !v.WorkflowDefinition!.IsSubWorkflow).Select(v => v.Id).SingleAsync();

    // ─────────────────────────────── Test runs ───────────────────────────────

    [TestMethod]
    public async Task A_draft_can_be_tried_out_before_it_is_published()
    {
        var draftId = await _builder.CreateDraftVersionAsync(await PublishedVersionIdAsync());

        var draft = await _host.Db.WorkflowDefinitionVersions
            .AsNoTracking().SingleAsync(v => v.Id == draftId);
        Assert.IsFalse(draft.IsPublished, "the fixture is meant to produce a draft");

        var result = await _client.StartTestRunAsync(draftId, "tester");

        Assert.IsTrue(result.Success, result.Error);

        var run = await _host.Db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == result.RunId);

        Assert.IsTrue(run.IsTest);
        Assert.AreEqual(draftId, run.WorkflowDefinitionVersionId,
            "a test run must use the version being edited, not the published one");
    }

    [TestMethod]
    public async Task A_real_run_may_not_start_on_an_unpublished_version()
    {
        var draftId = await _builder.CreateDraftVersionAsync(await PublishedVersionIdAsync());

        var result = await _host.Engine.StartRunOnVersionAsync(
            new WorkflowSubject("ChangeRequest", "1"), draftId, "user-a", isTest: false);

        Assert.IsTrue(result.IsError, "starting real work on an unapproved draft must be refused");
        StringAssert.Contains(result.UnwrapError().Message, "draft");
    }

    [TestMethod]
    public async Task A_draft_that_has_been_tried_out_can_still_be_saved_and_published()
    {
        // Reported from the builder as "it will not let me publish". Trying a draft out
        // leaves a test run pinned to it, and that run's tasks point at the draft's task
        // definitions -- the very rows a re-save deletes before rewriting the graph. So the
        // act of testing a draft made it unpublishable, with a 500 as the only signal.
        var draftId = await _builder.CreateDraftVersionAsync(await PublishedVersionIdAsync());

        var started = await _client.StartTestRunAsync(draftId, "tester");
        Assert.IsTrue(started.Success, started.Error);

        Assert.IsTrue(
            await _host.Db.WorkflowTasks.AnyAsync(t => t.WorkflowRunId == started.RunId),
            "the test run must have produced a task for this to be the case under test");

        var model = await _builder.GetWorkflowAsync(draftId)
            ?? throw new InvalidOperationException("the draft vanished");

        var saved = await _builder.SaveAsync(model);
        Assert.IsTrue(saved.Success, string.Join("; ", saved.Errors.Select(e => e.Code)));

        model.VersionId = saved.VersionId;
        var published = await _builder.PublishAsync(model);
        Assert.IsTrue(published.Success, string.Join("; ", published.Errors.Select(e => e.Code)));

        // The stale run goes with the graph it was exercising: its task definitions no
        // longer exist, so there is nothing left for it to mean.
        Assert.IsFalse(
            await _host.Db.WorkflowRuns.AnyAsync(r => r.Id == started.RunId),
            "the test run should have been discarded, not left dangling");
    }

    [TestMethod]
    public async Task An_ordinary_run_is_not_marked_as_a_test()
    {
        var definitionId = await _host.Db.WorkflowDefinitions.Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "5"), definitionId, "user-a")).Unwrap();

        Assert.IsFalse(run.IsTest);

        var stored = await _host.Db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        Assert.IsFalse(stored.IsTest);
    }

    [TestMethod]
    public async Task A_test_run_can_be_discarded_and_a_real_one_cannot()
    {
        var versionId = await PublishedVersionIdAsync();
        var definitionId = await _host.Db.WorkflowDefinitions.Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var test = await _client.StartTestRunAsync(versionId, "tester");
        Assert.IsTrue(test.Success, test.Error);

        var real = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "7"), definitionId, "user-a")).Unwrap();

        // The guard is the whole point: this is a hard delete.
        var refused = await _client.DeleteTestRunAsync(real.Id, "tester");
        Assert.IsFalse(refused.Success);
        Assert.IsTrue(await _host.Db.WorkflowRuns.AnyAsync(r => r.Id == real.Id));

        var discarded = await _client.DeleteTestRunAsync(test.RunId, "tester");
        Assert.IsTrue(discarded.Success, discarded.Error);

        Assert.IsFalse(await _host.Db.WorkflowRuns.AnyAsync(r => r.Id == test.RunId));
        Assert.IsFalse(await _host.Db.WorkflowTasks.AnyAsync(t => t.WorkflowRunId == test.RunId),
            "discarding a test run must not leave its tasks behind");
    }

    // ─────────────────────────── Driving a run ───────────────────────────

    [TestMethod]
    public async Task A_workflow_can_be_walked_from_entry_to_completion()
    {
        var versionId = await PublishedVersionIdAsync();
        var started = await _client.StartTestRunAsync(versionId, "tester");
        Assert.IsTrue(started.Success, started.Error);

        var detail = await _client.GetRunAsync(started.RunId, "tester");
        Assert.IsNotNull(detail);

        var entry = detail.Tasks.Single();
        Assert.AreEqual("Enter Record", entry.Label);
        Assert.IsTrue(entry.CanComplete);

        // Outcomes come from the task definition, never from a fixed list.
        var outcomes = await _client.GetOutcomesAsync(entry.Id);
        CollectionAssert.AreEquivalent(
            new[] { "approved", "rejected" }, outcomes.Select(o => o.OutcomeKey).ToList());

        var completed = await _client.CompleteAsync(entry.Id, "approved", "tester", "looks fine");
        Assert.IsTrue(completed.Success, completed.Error);

        // Completing routes onward, which is why the runner reloads rather than
        // patching what it already has.
        detail = await _client.GetRunAsync(started.RunId, "tester");
        Assert.IsNotNull(detail);
        Assert.HasCount(2, detail.Tasks);

        var next = detail.Tasks.Single(t => t.CanComplete);
        Assert.AreEqual("Provide Input", next.Label);

        Assert.AreEqual("approved", detail.Tasks.Single(t => t.Label == "Enter Record").OutcomeKey);
        Assert.IsGreaterThan(0, detail.Run.PercentComplete);
    }

    /// <summary>
    /// Catches a dropped mapping line at either hop between the column and the view:
    /// WorkflowTask -> SnapshotMapper.ToSnapshot -> RunTaskView. Either drop compiles
    /// fine and leaves the escalation chip permanently invisible, since a missing
    /// assignment just leaves the property at its default rather than failing anything.
    /// </summary>
    [TestMethod]
    public async Task An_escalated_tasks_stamp_survives_to_the_runner_view()
    {
        var started = await _client.StartTestRunAsync(await PublishedVersionIdAsync(), "tester");
        var entry = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();

        var stamped = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == entry.Id);
        task.OverdueFiredAt = stamped;
        await _host.Db.SaveChangesAsync();

        var detail = (await _client.GetRunAsync(started.RunId, "tester"))!;

        Assert.AreEqual(stamped, detail.Tasks.Single(t => t.Id == entry.Id).OverdueFiredAt);
    }

    [TestMethod]
    public async Task The_log_records_what_happened_to_a_task()
    {
        var started = await _client.StartTestRunAsync(await PublishedVersionIdAsync(), "tester");
        var detail = (await _client.GetRunAsync(started.RunId, "tester"))!;
        var entry = detail.Tasks.Single();

        await _client.CompleteAsync(entry.Id, "approved", "user-lead-c100", null);

        var log = await _client.GetLogAsync(entry.Id);

        Assert.IsNotEmpty(log);
        Assert.IsTrue(log.Any(e => e.Action.Contains("Created", StringComparison.OrdinalIgnoreCase)));

        // Actor ids are opaque to the engine; the host resolves the name.
        Assert.IsTrue(log.Any(e => e.ActorDisplayName is not null),
            "the runner shows names, so at least one known actor should resolve");
    }

    [TestMethod]
    public async Task Notes_cancel_and_reassign_all_go_through_the_engine()
    {
        var started = await _client.StartTestRunAsync(await PublishedVersionIdAsync(), "tester");
        var entry = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();

        Assert.IsTrue((await _client.UpdateNotesAsync(entry.Id, "a note", "tester")).Success);
        Assert.IsTrue((await _client.ReassignAsync(
            entry.Id, "user-lead-c100", "C100", "tester", "over to you")).Success);

        var afterReassign = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();

        Assert.AreEqual("a note", afterReassign.Notes);
        Assert.AreEqual("user-lead-c100", afterReassign.AssignedToActorId);
        Assert.AreEqual("C100", afterReassign.AssignedBranchKey);
        Assert.IsNotNull(afterReassign.AssignedToDisplayName);

        Assert.IsTrue((await _client.CancelAsync(entry.Id, "tester", "not needed")).Success);

        var afterCancel = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();

        Assert.AreEqual(WorkflowTaskStatus.Cancelled, afterCancel.Status);
        Assert.IsFalse(afterCancel.CanComplete, "a cancelled task must not offer actions");
    }

    [TestMethod]
    public async Task An_ad_hoc_task_can_be_added_from_the_options_the_run_pinned()
    {
        var started = await _client.StartTestRunAsync(await PublishedVersionIdAsync(), "tester");
        var entry = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();

        var options = await _client.GetAdHocOptionsAsync(entry.Id);

        Assert.IsNotEmpty(options);
        Assert.IsTrue(options.Any(o => o.Label == "Ad-hoc Provide Input"));

        var added = await _client.AddAdHocAsync(
            entry.Id, options.First(o => o.Label == "Ad-hoc Provide Input").TaskDefinitionId,
            "tester", "please look at this");

        Assert.IsTrue(added.Success, added.Error);

        var detail = (await _client.GetRunAsync(started.RunId, "tester"))!;
        var adHoc = detail.Tasks.Single(t => t.Label == "Ad-hoc Provide Input");

        Assert.IsTrue(adHoc.IsAdHoc);
        Assert.AreEqual(entry.Id, adHoc.ParentTaskId);
    }

    [TestMethod]
    public async Task An_error_from_the_engine_becomes_a_message_rather_than_an_exception()
    {
        var started = await _client.StartTestRunAsync(await PublishedVersionIdAsync(), "tester");
        var entry = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();

        // "approve-ish" is not an outcome this task defines.
        var result = await _client.CompleteAsync(entry.Id, "approve-ish", "tester", null);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
    }

    // ─────────────────────────── Runs by subject ───────────────────────────

    [TestMethod]
    public async Task Runs_are_found_by_subject_which_is_how_a_document_page_finds_its_own()
    {
        var definitionId = await _host.Db.WorkflowDefinitions.Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        await _host.Engine.StartRunAsync(new WorkflowSubject("ChangeRequest", "42"), definitionId, "user-a");
        await _host.Engine.StartRunAsync(new WorkflowSubject("ChangeRequest", "43"), definitionId, "user-a");

        var forOne = await _client.GetRunsAsync("ChangeRequest", "42");

        Assert.HasCount(1, forOne);
        Assert.AreEqual("42", forOne[0].SubjectId);
        Assert.AreEqual(DemoWorkflowSeeder.ChangeRequestWorkflowName, forOne[0].WorkflowName);
        Assert.AreEqual(1, forOne[0].OpenTaskCount);
        Assert.IsFalse(forOne[0].IsTest);

        Assert.IsEmpty(await _client.GetRunsAsync("ChangeRequest", "999"));
    }

    // ─────────────────────── Fork and selective rejection ───────────────────────

    /// <summary>Drives the run to Provide Input, which is the forkable task.</summary>
    private async Task<(int RunId, RunTaskView Forkable)> AtForkPointAsync()
    {
        var started = await _client.StartTestRunAsync(await PublishedVersionIdAsync(), "tester");
        var entry = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();

        await _client.CompleteAsync(entry.Id, "approved", "tester", null);

        var forkable = (await _client.GetRunAsync(started.RunId, "tester"))!
            .Tasks.Single(t => t.Label == "Provide Input");

        return (started.RunId, forkable);
    }

    [TestMethod]
    public async Task The_runner_only_offers_a_fork_where_the_workflow_allows_one()
    {
        var started = await _client.StartTestRunAsync(await PublishedVersionIdAsync(), "tester");
        var entry = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();

        Assert.IsFalse(entry.IsForkable, "Enter Record is not declared forkable");

        var (_, forkable) = await AtForkPointAsync();
        Assert.IsTrue(forkable.IsForkable);
    }

    [TestMethod]
    public async Task Branch_and_convergence_options_come_from_the_host_and_the_pinned_version()
    {
        var (_, forkable) = await AtForkPointAsync();

        var branches = await _client.GetBranchOptionsAsync(forkable.Id);
        var convergences = await _client.GetConvergenceOptionsAsync(forkable.Id);

        // Branch keys are the host's org units; the engine never names them.
        CollectionAssert.IsSubsetOf(
            new[] { "C100", "C200", "C300" }, branches.Select(b => b.Key).ToList());

        // A convergence task from another version would be refused by the engine, so it
        // must not be offered. Declared convergence points sort first.
        Assert.IsNotEmpty(convergences);
        Assert.IsTrue(convergences[0].IsDeclaredConvergencePoint);
        Assert.AreEqual("PM Review", convergences[0].Label);
        Assert.IsFalse(convergences.Any(c => c.TaskDefinitionId == forkable.TaskDefinitionId),
            "a task cannot converge at itself");
    }

    [TestMethod]
    public async Task Forking_creates_one_branch_per_unit_and_supersedes_the_origin()
    {
        var (runId, forkable) = await AtForkPointAsync();
        var convergence = (await _client.GetConvergenceOptionsAsync(forkable.Id))
            .First(c => c.IsDeclaredConvergencePoint);

        var result = await _client.ForkAsync(
            forkable.Id, ["C100", "C200", "C300"], convergence.TaskDefinitionId,
            "tester", "split by section");

        Assert.IsTrue(result.Success, result.Error);

        var detail = (await _client.GetRunAsync(runId, "tester"))!;
        var branches = detail.Tasks.Where(t => t.AssignedBranchKey is not null).ToList();

        Assert.HasCount(3, branches);
        Assert.IsTrue(branches.All(b => b.CanComplete));

        // The origin is superseded rather than completed: it did not produce an outcome.
        var origin = detail.Tasks.Single(t => t.Id == forkable.Id);
        Assert.AreEqual(WorkflowTaskStatus.Forked, origin.Status);
        Assert.IsTrue(origin.IsForkOrigin);
        Assert.IsFalse(origin.CanComplete);

        var fork = await _client.GetForkInfoAsync(branches[0].Id);
        Assert.IsNotNull(fork);
        Assert.AreEqual(3, fork.PendingBranchCount);
        Assert.AreEqual(0, fork.CompletedBranchCount);
        Assert.HasCount(3, fork.Branches);
    }

    [TestMethod]
    public async Task A_unit_already_branched_on_is_not_offered_again()
    {
        var (runId, forkable) = await AtForkPointAsync();
        var convergence = (await _client.GetConvergenceOptionsAsync(forkable.Id))
            .First(c => c.IsDeclaredConvergencePoint);

        await _client.ForkAsync(forkable.Id, ["C100", "C200"], convergence.TaskDefinitionId, "tester", null);

        var branch = (await _client.GetRunAsync(runId, "tester"))!.Tasks.First(t => t.AssignedBranchKey == "C100");
        var remaining = await _client.GetBranchOptionsAsync(branch.Id);

        // The engine rejects duplicate branch keys, so offering them would only produce
        // an error the user could not have predicted.
        Assert.IsFalse(remaining.Any(b => b.Key is "C100" or "C200"));
        Assert.IsTrue(remaining.Any(b => b.Key == "C300"));
    }

    [TestMethod]
    public async Task Branches_can_be_added_to_a_fork_that_has_not_converged()
    {
        var (runId, forkable) = await AtForkPointAsync();
        var convergence = (await _client.GetConvergenceOptionsAsync(forkable.Id))
            .First(c => c.IsDeclaredConvergencePoint);

        var forked = await _client.ForkAsync(
            forkable.Id, ["C100", "C200"], convergence.TaskDefinitionId, "tester", null);
        Assert.IsTrue(forked.Success, forked.Error);

        var branch = (await _client.GetRunAsync(runId, "tester"))!.Tasks.First(t => t.AssignedBranchKey == "C100");
        var fork = (await _client.GetForkInfoAsync(branch.Id))!;

        // Adding one at a time is fine; it is only creating a fork that needs two.
        var added = await _client.AddBranchesAsync(
            fork.ForkGroupId!.Value, ["C300"], "tester", "this one turned out to be involved");

        Assert.IsTrue(added.Success, added.Error);

        var after = (await _client.GetForkInfoAsync(branch.Id))!;
        Assert.AreEqual(3, after.PendingBranchCount);
    }

    [TestMethod]
    public async Task A_fork_of_one_branch_is_refused()
    {
        var (_, forkable) = await AtForkPointAsync();
        var convergence = (await _client.GetConvergenceOptionsAsync(forkable.Id))
            .First(c => c.IsDeclaredConvergencePoint);

        // A fork of one is the task itself, plus a convergence step for nothing. The
        // fork dialog enforces the same rule so a user never sees this message.
        var result = await _client.ForkAsync(
            forkable.Id, ["C100"], convergence.TaskDefinitionId, "tester", null);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "at least two");
    }

    [TestMethod]
    public async Task A_convergence_task_appears_only_once_every_branch_is_finished()
    {
        var (runId, forkable) = await AtForkPointAsync();
        var convergence = (await _client.GetConvergenceOptionsAsync(forkable.Id))
            .First(c => c.IsDeclaredConvergencePoint);

        await _client.ForkAsync(forkable.Id, ["C100", "C200"], convergence.TaskDefinitionId, "tester", null);

        var branches = (await _client.GetRunAsync(runId, "tester"))!
            .Tasks.Where(t => t.AssignedBranchKey is not null).ToList();

        await _client.CompleteAsync(branches[0].Id, "approved", "tester", "first done");

        Assert.IsFalse((await _client.GetRunAsync(runId, "tester"))!.Tasks.Any(t => t.Label == "PM Review"),
            "convergence must wait for every branch");

        await _client.CompleteAsync(branches[1].Id, "approved", "tester", "second done");

        var pmReview = (await _client.GetRunAsync(runId, "tester"))!.Tasks.SingleOrDefault(t => t.Label == "PM Review");

        Assert.IsNotNull(pmReview);
        Assert.IsTrue(pmReview.CanComplete);
        Assert.IsTrue(pmReview.IsConvergencePoint);
    }

    [TestMethod]
    public async Task The_rejection_outcome_is_flagged_so_the_runner_does_not_offer_a_refused_button()
    {
        var pmReview = await AtConvergenceAsync();

        var outcomes = await _client.GetOutcomesAsync(pmReview.Id);

        // "rejected" routes backwards on PM Review, and the engine refuses to take it
        // through the ordinary completion path.
        Assert.IsTrue(outcomes.Single(o => o.OutcomeKey == "rejected").IsRework);
        Assert.IsFalse(outcomes.Single(o => o.OutcomeKey == "approved").IsRework);

        var refused = await _client.CompleteAsync(pmReview.Id, "rejected", "tester", null);

        Assert.IsFalse(refused.Success);
        StringAssert.Contains(refused.Error!, "selective rejection");
    }

    [TestMethod]
    public async Task Rejecting_some_branches_re_forks_only_those()
    {
        var pmReview = await AtConvergenceAsync();

        // The convergence view reads from the manifest: the branch tasks are closed by
        // now, and this is the record of what each one decided.
        var fork = (await _client.GetForkInfoAsync(pmReview.Id))!;
        Assert.IsTrue(fork.IsConvergenceTask);
        CollectionAssert.AreEquivalent(
            new[] { "C100", "C200", "C300" }, fork.Branches.Select(b => b.BranchKey).ToList());

        var result = await _client.CompleteWithSelectiveRejectionAsync(
            pmReview.Id, "rejected", ["C100", "C300"], "tester", "these two need more work");

        Assert.IsTrue(result.Success, result.Error);

        var detail = (await _client.GetRunAsync(_runId, "tester"))!;
        var reworking = detail.Tasks
            .Where(t => t.CanComplete && t.AssignedBranchKey is not null)
            .Select(t => t.AssignedBranchKey)
            .ToList();

        // Only the rejected branches come back. C200 was accepted and is left alone,
        // which is the entire reason selective rejection exists.
        CollectionAssert.AreEquivalent(new[] { "C100", "C300" }, reworking!);
    }

    [TestMethod]
    public async Task Rejecting_nothing_accepts_every_branch_and_completes_normally()
    {
        var pmReview = await AtConvergenceAsync();

        var result = await _client.CompleteWithSelectiveRejectionAsync(
            pmReview.Id, "approved", [], "tester", null);

        Assert.IsTrue(result.Success, result.Error);

        var detail = (await _client.GetRunAsync(_runId, "tester"))!;

        Assert.AreEqual(WorkflowTaskStatus.Completed,
            detail.Tasks.Single(t => t.Id == pmReview.Id).Status);
        Assert.IsTrue(detail.Tasks.Any(t => t.Label == "Close Document"));
    }

    private int _runId;

    /// <summary>Forks across three sections, finishes every branch, and returns the
    /// convergence task that results.</summary>
    private async Task<RunTaskView> AtConvergenceAsync()
    {
        var (runId, forkable) = await AtForkPointAsync();
        _runId = runId;

        var convergence = (await _client.GetConvergenceOptionsAsync(forkable.Id))
            .First(c => c.IsDeclaredConvergencePoint);

        await _client.ForkAsync(
            forkable.Id, ["C100", "C200", "C300"], convergence.TaskDefinitionId, "tester", null);

        foreach (var branch in (await _client.GetRunAsync(runId, "tester"))!
                     .Tasks.Where(t => t.AssignedBranchKey is not null).ToList())
        {
            await _client.CompleteAsync(branch.Id, "approved", "tester", $"{branch.AssignedBranchKey} done");
        }

        return (await _client.GetRunAsync(runId, "tester"))!.Tasks.Single(t => t.Label == "PM Review");
    }

    // ─────────────────────────── Delegated sub-workflows ───────────────────────────

    private int _delegationRunId;

    /// <summary>Starts a run and drives it to the task the sub-workflow attaches to.</summary>
    private async Task<int> ParentTaskIdAsync()
    {
        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "delegation"), definitionId, "user-originator")).Unwrap();

        _delegationRunId = run.Id;

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, "approved", "user-originator")).Unwrap();

        return await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .SingleAsync();
    }

    /// <summary>
    /// The delegation feature walked end to end through the host the runner actually
    /// calls, rather than through the engine directly.
    ///
    /// This is the layer the click-through would exercise: the dialog hands the client an
    /// actor id and nothing else, and the directory is what turns that into an actor and
    /// a section together. Delegating to Drew Novak (Mechanical) must put the Section Lead review
    /// on Sam Section Lead — C200's — and never on the originator's.
    /// </summary>
    [TestMethod]
    public async Task Delegating_through_the_runner_client_moves_the_whole_chain_to_the_delegatees_section()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var started = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator", "user-worker-c200", null, "over to you");

        Assert.IsTrue(started.Success, started.Error);

        // 1. Get Info is assigned to Drew Novak, who was named. It declares no role, so
        //    nothing resolves on top of him.
        var detail = await _client.GetRunAsync(_delegationRunId, "tester");
        var getInfo = detail!.Tasks.Single(t => t.Label == "Technical Review — Get Info");

        Assert.AreEqual("user-worker-c200", getInfo.AssignedToActorId);
        Assert.AreEqual("Drew Novak (Mechanical)", getInfo.AssignedToDisplayName);
        Assert.AreEqual("C200", getInfo.AssignedBranchKey);

        // 2. The parent is held by the blocking instance and offers no way to complete.
        var parent = detail.Tasks.Single(t => t.Id == parentTaskId);

        Assert.AreEqual(1, parent.BlockingSubWorkflowCount);

        var blocked = await _client.CompleteAsync(parentTaskId, "approved", "user-originator", null);

        Assert.IsFalse(blocked.Success, "a blocking sub-workflow must hold the parent");

        // 3. Completing Get Info sends the review to C200's Section Lead, not the delegator's.
        var infoDone = await _client.CompleteAsync(getInfo.Id, "approved", "user-worker-c200", null);

        Assert.IsTrue(infoDone.Success, infoDone.Error);

        detail = await _client.GetRunAsync(_delegationRunId, "tester");
        var sectionLead = detail!.Tasks.Single(t => t.Label == "Technical Review — Section Lead");

        Assert.AreEqual("user-lead-c200", sectionLead.AssignedToActorId);
        Assert.AreEqual("Sam Section Lead (Mechanical)", sectionLead.AssignedToDisplayName);
        Assert.AreEqual("C200", sectionLead.AssignedBranchKey);

        // 4. And on to the branch head, still carrying C200.
        var sectionLeadDone = await _client.CompleteAsync(sectionLead.Id, "approved", "user-lead-c200", null);

        Assert.IsTrue(sectionLeadDone.Success, sectionLeadDone.Error);

        detail = await _client.GetRunAsync(_delegationRunId, "tester");
        var branch = detail!.Tasks.Single(t => t.Label == "Technical Review — Division");

        Assert.AreEqual("user-division-head", branch.AssignedToActorId);
        Assert.AreEqual("C200", branch.AssignedBranchKey);

        // 5. Finishing the chain gives the parent back its Complete button.
        var branchDone = await _client.CompleteAsync(branch.Id, "approved", "user-division-head", null);

        Assert.IsTrue(branchDone.Success, branchDone.Error);

        detail = await _client.GetRunAsync(_delegationRunId, "tester");
        parent = detail!.Tasks.Single(t => t.Id == parentTaskId);

        Assert.AreEqual(0, parent.BlockingSubWorkflowCount);
        Assert.IsTrue(parent.CanComplete);

        var released = await _client.CompleteAsync(parentTaskId, "approved", "user-originator", null);

        Assert.IsTrue(released.Success, released.Error);
    }

    [TestMethod]
    public async Task A_blocked_task_reports_its_blocking_instances()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator")).Unwrap();

        var detail = await _client.GetRunAsync(_delegationRunId, "tester");
        var parent = detail!.Tasks.Single(t => t.Id == parentTaskId);

        Assert.AreEqual(1, parent.BlockingSubWorkflowCount);
        Assert.AreEqual(1, parent.RunningSubWorkflowCount);
    }

    [TestMethod]
    public async Task A_non_blocking_instance_is_running_but_not_blocking()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        await _host.Db.WorkflowSubWorkflowAttachments
            .Where(a => a.SubWorkflowDefinitionId == subWorkflowId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsBlocking, false));

        _host.Db.ChangeTracker.Clear();

        var parentTaskId = await ParentTaskIdAsync();

        (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator")).Unwrap();

        var detail = await _client.GetRunAsync(_delegationRunId, "tester");
        var parent = detail!.Tasks.Single(t => t.Id == parentTaskId);

        Assert.AreEqual(0, parent.BlockingSubWorkflowCount);
        Assert.AreEqual(1, parent.RunningSubWorkflowCount);
    }

    [TestMethod]
    public async Task A_version_wide_attachment_is_offered_in_the_runner()
    {
        var parentTaskId = await ParentTaskIdAsync();

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        // A task the sub-workflow is not attached to by name.
        var otherTask = await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == _delegationRunId && t.Id != parentTaskId)
            .Select(t => new { t.Id, t.TaskDefinitionId })
            .FirstAsync();

        var versionId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.Id == otherTask.TaskDefinitionId)
            .Select(d => d.WorkflowDefinitionVersionId)
            .SingleAsync();

        _host.Db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            SubWorkflowDefinitionId = subWorkflowId,
            WorkflowDefinitionVersionId = versionId,
            TaskDefinitionId = null,
            IsBlocking = true,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        var detail = await _client.GetRunAsync(_delegationRunId, "tester");

        Assert.IsTrue(detail!.Tasks.Single(t => t.Id == otherTask.Id).HasSubWorkflowOptions,
            "A version-wide attachment must be offered on a task it does not name.");
    }

    [TestMethod]
    public async Task Delegating_through_the_client_fills_in_the_persons_section()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var result = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator", "user-worker-c200", null, "please review");

        Assert.IsTrue(result.Success, result.Error);

        var entry = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId != null)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        // The caller named a person; the host supplied the section. Setting the actor
        // without the section is what misroutes the Section Lead lookup downstream.
        Assert.AreEqual("C200", entry.AssignedBranchKey);

        // Not the actor: the sub-workflow's entry task is section-review, which carries the
        // section-lead role key, so the assignment resolver moves it off the named person and on
        // to C200's Section Lead. Task 13 puts a role-less get-info step in front, after which
        // asserting on the actor becomes meaningful.
    }

    [TestMethod]
    public async Task Delegating_to_somebody_with_no_section_still_works()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var result = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator", "user-unassigned", null, null);

        Assert.IsTrue(result.Success, result.Error);
    }

    [TestMethod]
    public async Task Delegating_to_a_section_puts_the_chain_in_that_section()
    {
        // The reason the org unit is now chosen rather than derived. Every role inside a
        // sub-workflow resolves against the unit the chain belongs to, so handing one to a
        // section is handing it to that section's people -- and until the unit could be
        // named, the only way to reach a section was to name somebody who happened to be in
        // it.
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var result = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignToActorId: null, assignToBranchKey: "C200", notes: "over to C200");

        Assert.IsTrue(result.Success, result.Error);

        var entry = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId != null)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        Assert.AreEqual("C200", entry.AssignedBranchKey,
            "The chain belongs to the section that was chosen.");
    }

    [TestMethod]
    public async Task A_section_named_outright_beats_the_persons_own()
    {
        // Both given, and they disagree: somebody in C200 is being asked to do work that
        // belongs to another section. The explicit choice wins -- deriving the unit from the
        // person is the fallback for when nobody said, not an override.
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var elsewhere = (await new DemoDirectory(_host.Db).GetBranchOptionsAsync())
            .First(o => o.Key != "C200").Key;

        var result = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignToActorId: "user-worker-c200", assignToBranchKey: elsewhere, notes: null);

        Assert.IsTrue(result.Success, result.Error);

        var entry = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId != null)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        Assert.AreEqual(elsewhere, entry.AssignedBranchKey);
        Assert.AreNotEqual("C200", entry.AssignedBranchKey,
            "The person's own section overrode the one that was chosen.");
    }

    [TestMethod]
    public async Task Naming_neither_still_inherits_the_parent()
    {
        // What starting a sub-workflow meant before anything could be chosen, and what an
        // untouched dialog must still mean.
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var parentBranch = await _host.Db.WorkflowTasks
            .Where(t => t.Id == parentTaskId)
            .Select(t => t.AssignedBranchKey)
            .SingleAsync();

        var result = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignToActorId: null, assignToBranchKey: null, notes: null);

        Assert.IsTrue(result.Success, result.Error);

        var entry = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId != null)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        Assert.AreEqual(parentBranch, entry.AssignedBranchKey);
    }

    [TestMethod]
    public async Task A_test_run_with_a_delegated_chain_can_still_be_discarded()
    {
        // Reported as a 500 from the builder's test pane. Discarding a test run cascades to
        // its tasks, and a sub-workflow instance points at the task it hangs off with
        // DeleteBehavior.Restrict -- so the moment somebody tried a delegation in the test
        // pane, that run became undeletable and the only signal was a 500.
        var started = await _client.StartTestRunAsync(await PublishedVersionIdAsync(), "tester");

        var entry = (await _client.GetRunAsync(started.RunId, "tester"))!.Tasks.Single();
        await _client.CompleteAsync(entry.Id, "approved", "tester", null);

        var parent = (await _client.GetRunAsync(started.RunId, "tester"))!
            .Tasks.First(t => t.HasSubWorkflowOptions);

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var delegated = await _client.StartSubWorkflowAsync(
            parent.Id, subWorkflowId, "tester", null, null, null);

        Assert.IsTrue(delegated.Success, delegated.Error);

        var discarded = await _client.DeleteTestRunAsync(started.RunId, "tester");

        Assert.IsTrue(discarded.Success, discarded.Error);

        Assert.IsFalse(
            await _host.Db.WorkflowRuns.AnyAsync(r => r.Id == started.RunId),
            "The run survived, so the test pane leaves rows behind for every delegation.");
        Assert.IsFalse(
            await _host.Db.WorkflowSubWorkflowInstances.AnyAsync(i => i.WorkflowRunId == started.RunId),
            "Its instances outlived it.");
    }

    [TestMethod]
    public async Task A_delegated_task_says_which_chain_it_belongs_to()
    {
        // A sub-workflow's tasks live in the same run as the task that spawned them, so
        // without this the runner cannot tell delegated work from the workflow's own steps
        // and lists them intermixed -- which is what it did.
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var started = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator", null, "C200", null);

        Assert.IsTrue(started.Success, started.Error);

        var detail = (await _client.GetRunAsync(_delegationRunId, "tester"))!;

        var parent = detail.Tasks.Single(t => t.Id == parentTaskId);

        Assert.IsNull(parent.SubWorkflowInstanceId,
            "The task that delegated is not itself delegated work.");

        var delegated = detail.Tasks.Where(t => t.SubWorkflowInstanceId is not null).ToList();

        Assert.IsNotEmpty(delegated, "The chain's tasks are in this run and must be findable.");

        var instances = await _client.GetSubWorkflowInstancesAsync(_delegationRunId);

        CollectionAssert.IsSubsetOf(
            delegated.Select(t => t.SubWorkflowInstanceId!.Value).Distinct().ToList(),
            instances.Select(i => i.Id).ToList(),
            "A task pointing at an instance the run does not list cannot be grouped.");
    }

    [TestMethod]
    public async Task A_task_inside_a_sub_workflow_converges_on_its_own_version()
    {
        // The fork inside a sub-workflow could never succeed. Both of these pickers took the
        // version from the *run*, and a run is pinned to the mainline version -- so a task
        // belonging to a sub-workflow was offered the mainline's task definitions, and the
        // engine then refused the fork with "Convergence task must belong to the same
        // workflow version as the forked task". Every choice on the screen was invalid.
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var started = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator", null, "C200", null);

        Assert.IsTrue(started.Success, started.Error);

        var delegated = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId != null)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        var subWorkflowVersionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == subWorkflowId)
            .Select(v => v.Id)
            .FirstAsync();

        var options = await _client.GetConvergenceOptionsAsync(delegated.Id);

        Assert.IsNotEmpty(options, "A sub-workflow with one step cannot be forked at all.");

        var offeredVersions = await _host.Db.WorkflowTaskDefinitions
            .Where(d => options.Select(o => o.TaskDefinitionId).Contains(d.Id))
            .Select(d => d.WorkflowDefinitionVersionId)
            .Distinct()
            .ToListAsync();

        CollectionAssert.AreEquivalent(
            new[] { subWorkflowVersionId }, offeredVersions,
            "The picker offered task definitions from another version, so forking a "
            + "delegated task can only ever be refused.");
    }

    [TestMethod]
    public async Task Ad_hoc_options_inside_a_sub_workflow_come_from_its_own_version()
    {
        // The same mistake, one method along.
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        _ = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator", null, "C200", null);

        var delegated = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId != null)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        var subWorkflowVersionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == subWorkflowId)
            .Select(v => v.Id)
            .FirstAsync();

        var options = await _client.GetAdHocOptionsAsync(delegated.Id);

        foreach (var option in options)
        {
            var versionId = await _host.Db.WorkflowTaskDefinitions
                .Where(d => d.Id == option.TaskDefinitionId)
                .Select(d => d.WorkflowDefinitionVersionId)
                .SingleAsync();

            Assert.AreEqual(subWorkflowVersionId, versionId,
                $"'{option.Label}' belongs to another version and cannot be added here.");
        }
    }

    [TestMethod]
    public async Task Cancelling_a_blocking_instance_unblocks_the_task()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator")).Unwrap();

        var cancelled = await _client.CancelSubWorkflowAsync(
            instance.Id, "user-originator", "not needed");

        Assert.IsTrue(cancelled.Success, cancelled.Error);

        var detail = await _client.GetRunAsync(_delegationRunId, "tester");
        var parent = detail!.Tasks.Single(t => t.Id == parentTaskId);

        Assert.AreEqual(0, parent.BlockingSubWorkflowCount);
    }

    /// <summary>Allow-everything double. This class is testing the runner, not
    /// authorization, so every actor id passed to <c>GetRunAsync</c> above is a
    /// placeholder -- <see cref="AuthorizationTests"/> and <see cref="DemoAuthorizationTests"/>
    /// cover what the policy itself decides.</summary>
    private sealed class AllowAll : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation,
            WorkflowAuthorizationContext context,
            CancellationToken ct = default) =>
            Task.FromResult(WorkflowAuthorizationResult.Allowed);
    }

    [TestMethod]
    public async Task The_org_unit_picker_is_every_unit_the_host_has()
    {
        // Distinct from GetBranchOptionsAsync, which is scoped to a task and removes the
        // units a fork has already branched on. Reassigning a task to a unit is legal
        // whether or not some other branch is also there, so filtering by fork state here
        // would hide valid choices -- and this is the only list a reassign picker has.
        var fromDirectory = await new DemoDirectory(_host.Db).GetBranchOptionsAsync();
        var fromClient = await _client.GetOrgUnitsAsync();

        Assert.IsNotEmpty(fromClient, "An empty picker means the directory was not asked.");
        CollectionAssert.AreEquivalent(
            fromDirectory.Select(o => o.Key).ToList(),
            fromClient.Select(o => o.Key).ToList(),
            "Something is filtering the org units that should not be.");
    }

    [TestMethod]
    public async Task The_org_unit_picker_still_offers_a_unit_a_fork_has_taken()
    {
        // The specific difference from the fork picker, pinned: after forking across every
        // unit, the fork picker is empty and the reassign picker is not.
        var (_, forkable) = await AtForkPointAsync();

        var units = await _client.GetBranchOptionsAsync(forkable.Id);
        var convergence = (await _client.GetConvergenceOptionsAsync(forkable.Id))[0];

        var forked = await _client.ForkAsync(
            forkable.Id,
            [.. units.Select(u => u.Key)],
            convergence.TaskDefinitionId,
            "tester",
            null);

        Assert.IsTrue(forked.Success, forked.Error);

        Assert.IsEmpty(
            await _client.GetBranchOptionsAsync(forkable.Id),
            "Every unit is branched, so the fork picker has nothing left to offer.");

        Assert.IsNotEmpty(
            await _client.GetOrgUnitsAsync(),
            "The reassign picker is not about fork state and must still list them.");
    }

    [TestMethod]
    public async Task The_reassign_picker_is_the_hosts_directory_and_nothing_else()
    {
        // The demo used to query its own People table here. Now the library asks the
        // directory, and the engine adds no filtering of its own -- so a host restricting
        // who may be assigned to does it in one place and gets the whole application.
        var fromDirectory = await new DemoDirectory(_host.Db).GetActorsAsync();
        var fromClient = await _client.GetActorsAsync();

        Assert.IsNotEmpty(fromClient, "An empty picker means the directory was not asked.");
        Assert.HasCount(
            fromDirectory.Count, fromClient,
            "The client returned a different number of actors than the directory offered, "
            + "so something is filtering the list that should not be.");
        CollectionAssert.AreEquivalent(
            fromDirectory.Select(a => a.ActorId).ToList(),
            fromClient.Select(a => a.ActorId).ToList());
    }
}
