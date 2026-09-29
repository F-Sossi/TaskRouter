using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// The <c>reportProgress</c> trigger's scopes.
///
/// <c>run</c> and <c>branch</c> each report one number. That is enough until a fork
/// converges: at that moment every branch has its own progress, and a host storing
/// per-branch figures needs all of them, not the run's average. The integration spike hit
/// this — the system it replaces updated each participating section's row with that
/// section's own number when a convergence task completed, and no combination of the
/// existing scopes can express it.
///
/// <c>all-branches</c> is the answer: one call per branch, each with its own key and its
/// own figure. Additive rather than a signature change, so a sink written against the
/// existing scopes needs no edit.
/// </summary>
[TestClass]
public class ProgressScopeTests
{
    private sealed class RecordingProgressSink : IWorkflowProgressSink
    {
        public List<(string? BranchKey, double Percent)> Reports { get; } = [];

        public Task ReportAsync(
            WorkflowSubject subject,
            string? branchKey,
            double percentComplete,
            string actorId,
            CancellationToken ct = default)
        {
            lock (Reports) { Reports.Add((branchKey, percentComplete)); }
            return Task.CompletedTask;
        }
    }

    private TestHost _host = null!;
    private RecordingProgressSink _progress = null!;
    private int _workflowId;
    private int _pmReviewDefId;

    [TestInitialize]
    public async Task Setup()
    {
        _progress = new RecordingProgressSink();

        _host = await TestHost.CreateAsync(
            withTriggers: true,
            configure: services => services.AddSingleton<IWorkflowProgressSink>(_progress));

        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

        _pmReviewDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "PM Review")
            .Select(d => d.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>Points the seeded Provide Input progress trigger at a different scope.</summary>
    private async Task UseScopeAsync(string scope)
    {
        var trigger = await _host.Db.WorkflowTriggerDefinitions
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TriggerKey == BuiltInTriggerKeys.ReportProgress
                          && t.TaskDefinition!.DisplayName == "Provide Input");

        trigger.Configuration = $$"""{"scope":"{{scope}}"}""";
        await _host.Db.SaveChangesAsync();
    }

    private async Task<int> ForkedRunAsync()
    {
        var subject = new WorkflowSubject(
            nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());

        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.WorkflowRunId == run.Id
                     && t.TaskDefinition!.DisplayName == "Provide Input"
                     && t.Status == WorkflowTaskStatus.NotStarted)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        _ = (await _host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200", "C300"], _pmReviewDefId, "user-originator")).Unwrap();

        return run.Id;
    }

    [TestMethod]
    public async Task All_branches_reports_once_per_branch()
    {
        var runId = await ForkedRunAsync();
        await UseScopeAsync("all-branches");

        _progress.Reports.Clear();

        var c100 = await _host.Db.WorkflowTasks
            .FirstAsync(t => t.WorkflowRunId == runId && t.AssignedBranchKey == "C100");

        (await _host.Engine.CompleteTaskAsync(
            c100.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c100")).Unwrap();

        CollectionAssert.AreEquivalent(
            new[] { "C100", "C200", "C300" },
            _progress.Reports.Select(r => r.BranchKey).ToArray(),
            "Every branch gets its own report, not just the one that completed.");
    }

    [TestMethod]
    public async Task All_branches_gives_each_branch_its_own_figure()
    {
        var runId = await ForkedRunAsync();
        await UseScopeAsync("all-branches");

        _progress.Reports.Clear();

        var c100 = await _host.Db.WorkflowTasks
            .FirstAsync(t => t.WorkflowRunId == runId && t.AssignedBranchKey == "C100");

        (await _host.Engine.CompleteTaskAsync(
            c100.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c100")).Unwrap();

        var completed = _progress.Reports.Single(r => r.BranchKey == "C100");
        var untouched = _progress.Reports.Where(r => r.BranchKey != "C100").ToList();

        Assert.IsGreaterThan(untouched.Max(r => r.Percent), completed.Percent,
            "The branch that finished work is further along than the ones that did not. "
            + "One number for all three would be the run average, which is the bug.");
    }

    [TestMethod]
    public async Task The_existing_scopes_still_report_exactly_once()
    {
        var runId = await ForkedRunAsync();
        await UseScopeAsync("run");

        _progress.Reports.Clear();

        var c100 = await _host.Db.WorkflowTasks
            .FirstAsync(t => t.WorkflowRunId == runId && t.AssignedBranchKey == "C100");

        (await _host.Engine.CompleteTaskAsync(
            c100.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c100")).Unwrap();

        Assert.HasCount(1, _progress.Reports);
        Assert.IsNull(_progress.Reports[0].BranchKey);
    }

    [TestMethod]
    public async Task Progress_is_a_percentage_not_a_fraction()
    {
        // The unit the runner renders. It read the value as a 0-1 fraction and formatted it
        // with "P0", which multiplies by a hundred again: a run eighty per cent done showed
        // "8,000%". The value was never wrong -- the contract was never stated.
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var started = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            started.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var run = (await _host.Engine.GetRunAsync(started.Id)).Unwrap();
        var percent = TaskRouter.EntityFrameworkCore.WorkflowProgress.Calculate(run, null);

        Assert.IsGreaterThan(0d, percent, "One task is complete, so this is not zero.");
        Assert.IsLessThanOrEqualTo(100d, percent,
            "0-100, not 0-1. Anything that renders this multiplies by a hundred at most once.");
    }
}
