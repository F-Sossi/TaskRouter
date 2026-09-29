using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// The due-date seam and the funnel it is called from.
///
/// The funnel is the whole point: five of the eight paths that create a task are
/// fork- or sub-workflow-related, so anyone stamping a deadline at "the place tasks are
/// made" by hand finds three of them. Each test here is one of the eight.
/// </summary>
[TestClass]
public class DueDateTests
{
    /// <summary>A resolver that answers the same date for everything, so a test can
    /// assert on the value rather than on a computation.</summary>
    private sealed class FixedDueDateResolver(DateTime? due) : IWorkflowDueDateResolver
    {
        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) => Task.FromResult(due);
    }

    [TestMethod]
    public void The_default_registration_is_the_null_resolver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTaskRouter(includeBuiltInTriggers: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkflowDueDateResolver>();

        Assert.AreEqual("NullDueDateResolver", resolver.GetType().Name);
    }

    [TestMethod]
    public async Task The_null_resolver_answers_null()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTaskRouter(includeBuiltInTriggers: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkflowDueDateResolver>();

        var answer = await resolver.ResolveAsync(
            new WorkflowSubject("ChangeRequest", "1"),
            new WorkflowTaskSnapshot(0, 0, 0, "t", "T", WorkflowTaskStatus.NotStarted,
                null, null, null, null, null, false, null, null, null, null));

        Assert.IsNull(answer);
    }

    [TestMethod]
    public void AddDueDateResolver_replaces_the_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTaskRouter(includeBuiltInTriggers: false)
                .AddDueDateResolver<AlwaysTomorrow>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkflowDueDateResolver>();

        Assert.IsInstanceOfType<AlwaysTomorrow>(resolver);
    }

    private sealed class AlwaysTomorrow : IWorkflowDueDateResolver
    {
        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(DateTime.UtcNow.AddDays(1));
    }

    private static readonly DateTime Due = new(2026, 12, 25, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>A resolver that records what it was asked, and throws on demand.</summary>
    private sealed class RecordingResolver(DateTime? due, bool throws = false) : IWorkflowDueDateResolver
    {
        public List<WorkflowSubject> Asked { get; } = [];

        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default)
        {
            Asked.Add(subject);

            if (throws)
            {
                throw new InvalidOperationException("resolver is broken");
            }

            return Task.FromResult(due);
        }
    }

    private static async Task<int> MainlineDefinitionIdAsync(TestHost host) =>
        await host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

    [TestMethod]
    public async Task Run_start_stamps_the_entry_task()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var task = await host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        Assert.AreEqual(Due, task.DueDate);
    }

    [TestMethod]
    public async Task The_resolver_is_asked_about_the_runs_subject()
    {
        var resolver = new RecordingResolver(Due);
        await using var host = await TestHost.CreateAsync(dueDates: resolver);
        var definitionId = await MainlineDefinitionIdAsync(host);

        await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "42"), definitionId, "user-originator");

        var asked = resolver.Asked.Single();
        Assert.AreEqual("ChangeRequest", asked.SubjectType);
        Assert.AreEqual("42", asked.SubjectId);
    }

    [TestMethod]
    public async Task Advancing_on_completion_stamps_the_next_task()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();
        var entryId = run.Tasks.Single().Id;

        (await host.Engine.CompleteTaskAsync(entryId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var next = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Id != entryId)
            .SingleAsync();

        Assert.AreEqual(Due, next.DueDate);
    }

    [TestMethod]
    public async Task An_ad_hoc_task_is_stamped()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();
        var entryId = run.Tasks.Single().Id;

        var adHocDef = await host.Db.WorkflowTaskDefinitions
            .Where(d => d.IsAdHoc).Select(d => d.Id).FirstAsync();

        // Returns a WorkflowTaskSnapshot, not an id -- see IWorkflowEngine.cs:94.
        var created = (await host.Engine.AddAdHocTaskAsync(
            entryId, adHocDef, "user-originator")).Unwrap();

        var adHoc = await host.Db.WorkflowTasks.SingleAsync(t => t.Id == created.Id);

        Assert.AreEqual(Due, adHoc.DueDate);
    }

    [TestMethod]
    public async Task A_resolver_that_throws_leaves_a_null_date_and_creates_the_task()
    {
        var resolver = new RecordingResolver(Due, throws: true);
        await using var host = await TestHost.CreateAsync(dueDates: resolver);
        var definitionId = await MainlineDefinitionIdAsync(host);

        // Not Result.Failure: a broken deadline resolver is logged and treated as null,
        // never fatal -- the same policy IWorkflowAssignmentResolver has, for the same
        // reason (the original system review finding M8).
        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var task = await host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        Assert.IsNull(task.DueDate);
    }

    [TestMethod]
    public async Task No_resolver_means_no_date_and_no_error()
    {
        await using var host = await TestHost.CreateAsync();
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var task = await host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        Assert.IsNull(task.DueDate);
    }

    [TestMethod]
    public async Task The_subject_is_resolved_once_per_run_however_many_tasks_are_created()
    {
        var resolver = new RecordingResolver(Due);
        await using var host = await TestHost.CreateAsync(dueDates: resolver);
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        (await host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        // Two tasks created, so the resolver was asked twice -- but both answers describe
        // the same subject, which is the memoisation working. (The engine is scoped and
        // this test shares one, so the second call hits the cache.)
        Assert.HasCount(2, resolver.Asked);
        Assert.AreEqual(1, resolver.Asked.Select(s => s.SubjectId).Distinct().Count());
    }

    // ─── The five fork- and sub-workflow-related paths ───
    //
    // Setup follows ForkConvergenceTests: reach Provide Input, then fork it across
    // sections toward the PM Review convergence point.

    private static async Task<int> DefinitionIdAsync(TestHost host, string displayName) =>
        await host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == displayName).Select(d => d.Id).SingleAsync();

    private static async Task<(int RunId, int ProvideInputId)> ReachProvideInputAsync(TestHost host)
    {
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        (await host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var provideInputId = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .SingleAsync();

        return (run.Id, provideInputId);
    }

    [TestMethod]
    public async Task Fork_branches_are_stamped()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var (_, provideInputId) = await ReachProvideInputAsync(host);
        var convergenceDefId = await DefinitionIdAsync(host, "PM Review");

        var fork = (await host.Engine.ForkTaskAsync(
            provideInputId, ["C100", "C200"], convergenceDefId, "user-originator")).Unwrap();

        var branches = await host.Db.WorkflowTasks
            .Where(t => fork.Branches.Select(b => b.Id).Contains(t.Id))
            .ToListAsync();

        Assert.HasCount(2, branches);
        Assert.IsTrue(branches.All(b => b.DueDate == Due),
            "every branch of a fork goes through the funnel");
    }

    [TestMethod]
    public async Task A_branch_added_to_a_live_fork_is_stamped()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var (runId, provideInputId) = await ReachProvideInputAsync(host);
        var convergenceDefId = await DefinitionIdAsync(host, "PM Review");

        var fork = (await host.Engine.ForkTaskAsync(
            provideInputId, ["C100", "C200"], convergenceDefId, "user-originator")).Unwrap();

        (await host.Engine.AddBranchToForkAsync(
            fork.ForkGroupId, ["C300"], "user-originator")).Unwrap();

        var added = await host.Db.WorkflowTasks
            .SingleAsync(t => t.WorkflowRunId == runId && t.AssignedBranchKey == "C300");

        Assert.AreEqual(Due, added.DueDate);
    }

    [TestMethod]
    public async Task The_convergence_task_is_stamped()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var (runId, provideInputId) = await ReachProvideInputAsync(host);
        var convergenceDefId = await DefinitionIdAsync(host, "PM Review");

        var fork = (await host.Engine.ForkTaskAsync(
            provideInputId, ["C100", "C200"], convergenceDefId, "user-originator")).Unwrap();

        foreach (var branch in fork.Branches)
        {
            (await host.Engine.CompleteTaskAsync(
                branch.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c200")).Unwrap();
        }

        var convergence = await host.Db.WorkflowTasks
            .SingleAsync(t => t.WorkflowRunId == runId && t.ForkManifestId != null);

        Assert.AreEqual(Due, convergence.DueDate);
    }

    [TestMethod]
    public async Task Rework_tasks_from_selective_rejection_are_stamped()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var (runId, provideInputId) = await ReachProvideInputAsync(host);
        var convergenceDefId = await DefinitionIdAsync(host, "PM Review");

        var fork = (await host.Engine.ForkTaskAsync(
            provideInputId, ["C100", "C200"], convergenceDefId, "user-originator")).Unwrap();

        foreach (var branch in fork.Branches)
        {
            (await host.Engine.CompleteTaskAsync(
                branch.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-lead-c200")).Unwrap();
        }

        var convergence = await host.Db.WorkflowTasks
            .SingleAsync(t => t.WorkflowRunId == runId && t.ForkManifestId != null);

        var before = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == runId).Select(t => t.Id).ToListAsync();

        (await host.Engine.CompleteWithSelectiveRejectionAsync(
            convergence.Id,
            DemoWorkflowSeeder.Outcomes.Rejected,
            ["C100"],
            "user-division-head")).Unwrap();

        var rework = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == runId && !before.Contains(t.Id))
            .ToListAsync();

        Assert.IsGreaterThan(0, rework.Count,
            "selective rejection re-forks the rejected branches");
        Assert.IsTrue(rework.All(t => t.DueDate == Due));
    }

    [TestMethod]
    public async Task A_sub_workflow_entry_task_is_stamped()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var (_, parentTaskId) = await ReachProvideInputAsync(host);

        var subWorkflowId = await host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var instance = (await host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator")).Unwrap();

        var spawned = await host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId == instance.Id)
            .ToListAsync();

        Assert.IsGreaterThan(0, spawned.Count,
            "the sub-workflow's entry task is an ordinary task in the parent's run");
        Assert.IsTrue(spawned.All(t => t.DueDate == Due));
    }
}
