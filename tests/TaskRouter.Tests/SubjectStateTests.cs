using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// Reading where many subjects have got to, in one query.
///
/// <para>Every other read is single-entity, so a host building a list of documents made one
/// call per row and, in practice, gave up and queried the tables directly. See
/// <c>docs/superpowers/specs/2026-08-31-bulk-subject-reads.md</c>.</para>
///
/// <para>The interesting half is forks. An inbox wants the branches, because each person needs
/// their own; a document list wants the origin, because the row should read "Provide Input"
/// once rather than three times differing only by section. Those are different questions and
/// <see cref="ForkView"/> is which one is being asked.</para>
/// </summary>
[TestClass]
public class SubjectStateTests
{
    private TestHost _host = null!;
    private int _workflowId;
    private int _provideInputDefId;
    private int _pmReviewDefId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();

        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

        _provideInputDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "Provide Input")
            .Select(d => d.Id)
            .SingleAsync();

        _pmReviewDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "PM Review")
            .Select(d => d.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private static WorkflowSubject Subject(string id) =>
        new(nameof(DemoDocumentType.ChangeRequest), id);

    /// <summary>Starts a run and returns its subject and entry task id.</summary>
    private async Task<(WorkflowSubject Subject, int EntryTaskId)> StartAsync(string id)
    {
        var subject = Subject(id);
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        return (subject, run.Tasks.Single().Id);
    }

    /// <summary>Drives a run to its forkable step and forks it into three sections.</summary>
    private async Task<WorkflowSubject> ForkedRunAsync(string id)
    {
        var (subject, entryTaskId) = await StartAsync(id);

        (await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Where(t => t.TaskDefinitionId == _provideInputDefId
                     && t.Status == WorkflowTaskStatus.NotStarted)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        _ = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200", "C300"], _pmReviewDefId, "user-originator")).Unwrap();

        return subject;
    }

    [TestMethod]
    public async Task Every_subject_asked_about_comes_back_with_its_current_task()
    {
        var (first, _) = await StartAsync("doc-1");
        var (second, _) = await StartAsync("doc-2");

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync([first, second])).Unwrap();

        Assert.HasCount(2, state);
        Assert.AreEqual("Enter Record", state[first].Single().CurrentTasks.Single().Task.DisplayName);
        Assert.AreEqual("Enter Record", state[second].Single().CurrentTasks.Single().Task.DisplayName);
    }

    [TestMethod]
    public async Task A_subject_with_nothing_running_is_absent_rather_than_empty()
    {
        var (started, _) = await StartAsync("doc-1");
        var never = Subject("doc-never-started");

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync([started, never])).Unwrap();

        Assert.IsTrue(state.ContainsKey(started));
        Assert.IsFalse(state.ContainsKey(never),
            "Absent, not present-and-empty, so a caller can tell 'nothing open' from 'never asked'.");
    }

    [TestMethod]
    public async Task A_subject_not_asked_about_is_absent_even_though_it_is_running()
    {
        var (asked, _) = await StartAsync("doc-1");
        _ = await StartAsync("doc-2");

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync([asked])).Unwrap();

        Assert.HasCount(1, state, "The subject filter has to actually bind.");
    }

    [TestMethod]
    public async Task The_run_is_named_so_concurrent_workflows_can_be_told_apart()
    {
        var (subject, _) = await StartAsync("doc-1");

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync([subject])).Unwrap();
        var workflow = state[subject].Single();

        Assert.AreEqual(DemoWorkflowSeeder.ChangeRequestWorkflowName, workflow.WorkflowName,
            "A tab with no label is not a tab.");
        Assert.AreEqual(1, workflow.Version);
        Assert.AreEqual(WorkflowRunStatus.Running, workflow.Status);
    }

    [TestMethod]
    public async Task Branches_view_returns_each_branch_and_not_the_origin()
    {
        var subject = await ForkedRunAsync("doc-1");

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync(
            [subject], ForkView.Branches)).Unwrap();

        var tasks = state[subject].Single().CurrentTasks;

        CollectionAssert.AreEquivalent(
            new[] { "C100", "C200", "C300" },
            tasks.Select(t => t.Task.AssignedBranchKey).ToArray());

        Assert.IsFalse(tasks.Any(t => t.Task.IsForkOrigin), "The origin is not actionable work.");
        Assert.IsTrue(tasks.All(t => t.OpenBranchCount == 0),
            "Branches come back individually; counting them is the caller's own Count.");
    }

    [TestMethod]
    public async Task Origin_view_returns_the_origin_once_with_its_open_branch_count()
    {
        var subject = await ForkedRunAsync("doc-1");

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync(
            [subject], ForkView.Origin)).Unwrap();

        var current = state[subject].Single().CurrentTasks.Single();

        Assert.IsTrue(current.Task.IsForkOrigin,
            "A document list says 'Provide Input' once, not once per section.");
        Assert.AreEqual(WorkflowTaskStatus.Forked, current.Task.Status);
        Assert.AreEqual(3, current.OpenBranchCount);
    }

    [TestMethod]
    public async Task The_branch_count_is_of_open_branches_not_the_forks_original_width()
    {
        var subject = await ForkedRunAsync("doc-1");

        var c100 = await _host.Db.WorkflowTasks
            .Where(t => t.AssignedBranchKey == "C100" && t.Status == WorkflowTaskStatus.NotStarted)
            .SingleAsync();

        (await _host.Engine.CompleteTaskAsync(
            c100.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c100")).Unwrap();

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync(
            [subject], ForkView.Origin)).Unwrap();

        var origin = state[subject].Single().CurrentTasks.Single(t => t.Task.IsForkOrigin);

        Assert.AreEqual(2, origin.OpenBranchCount,
            "Two branches are still going. Three is the fork's width, which is history.");
    }

    [TestMethod]
    public async Task The_origins_assignee_is_returned_unchanged()
    {
        var subject = await ForkedRunAsync("doc-1");

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync(
            [subject], ForkView.Origin)).Unwrap();

        var origin = state[subject].Single().CurrentTasks.Single();
        var stored = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == origin.Task.Id);

        Assert.AreEqual(stored.AssignedToActorId, origin.Task.AssignedToActorId,
            "Whoever held the step before it forked. Not nulled -- the host renders what it needs.");
    }

    [TestMethod]
    public async Task A_finished_fork_is_history_and_its_origin_stops_being_the_position()
    {
        // The assertion that stops ForkView.Origin degenerating into "always show origins".
        // Once every branch is done the fork is over, and the position is whatever it
        // converged into -- not the origin, which would otherwise sit in the grid forever.
        var subject = await ForkedRunAsync("doc-1");

        var branches = await _host.Db.WorkflowTasks
            .Where(t => t.TaskDefinitionId == _provideInputDefId
                     && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .ToListAsync();

        Assert.HasCount(3, branches);

        foreach (var branchId in branches)
        {
            (await _host.Engine.CompleteTaskAsync(
                branchId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();
        }

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync(
            [subject], ForkView.Origin)).Unwrap();

        var tasks = state[subject].Single().CurrentTasks;

        Assert.IsFalse(tasks.Any(t => t.Task.IsForkOrigin),
            "Every branch finished, so the fork is history rather than position.");
        Assert.IsTrue(tasks.Any(t => t.Task.DisplayName == "PM Review"),
            "The convergence it produced is the position now.");
    }

    [TestMethod]
    public async Task An_ordinary_task_reports_no_branches_under_either_view()
    {
        var (subject, _) = await StartAsync("doc-1");

        foreach (var view in new[] { ForkView.Branches, ForkView.Origin })
        {
            var state = (await _host.Engine.GetCurrentStateForSubjectsAsync([subject], view)).Unwrap();

            Assert.AreEqual(0, state[subject].Single().CurrentTasks.Single().OpenBranchCount,
                $"{view}: an unforked step has no branches.");
        }
    }

    [TestMethod]
    public async Task Completed_work_is_not_a_current_position()
    {
        var (subject, entryTaskId) = await StartAsync("doc-1");

        (await _host.Engine.CompleteTaskAsync(
            entryTaskId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var state = (await _host.Engine.GetCurrentStateForSubjectsAsync([subject])).Unwrap();
        var tasks = state[subject].Single().CurrentTasks;

        Assert.IsFalse(tasks.Any(t => t.Task.Id == entryTaskId),
            "The completed step is history, not position.");
        Assert.IsTrue(tasks.Any(t => t.Task.DisplayName == "Provide Input"),
            "What it routed to is the position.");
    }
}
