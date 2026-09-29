using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Exercises the the original system-shaped adapter layer end to end. These are the tests that
/// prove the integration guide actually works — if the original system's service interface can be
/// satisfied here, the swap is a wiring exercise.
/// </summary>
[TestClass]
public class DocumentTaskServiceTests
{
    private TestHost _host = null!;
    private DocumentTaskService _svc = null!;
    private int _documentId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();
        _svc = new DocumentTaskService(_host.Db, _host.Engine);

        var changeRequest = new ChangeRequest
        {
            Title = "Test ChangeRequest",
            DocNumber = "CR-0001",
            Originator = "user-originator",
            CreatorId = "user-originator"
        };

        _host.Db.Documents.Add(changeRequest);
        await _host.Db.SaveChangesAsync();
        _documentId = changeRequest.Id;

        foreach (var code in new[] { "C100", "C200", "C300" })
        {
            var section = await _host.Db.Sections.SingleAsync(s => s.Code == code);
            _host.Db.WorkItems.Add(new WorkItem
            {
                DocumentId = _documentId,
                AssignedSectionId = section.Id
            });
        }

        await _host.Db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<int> StartAsync()
    {
        var status = (await _svc.CreateInitialTasksAsync(
            _documentId, DemoDocumentType.ChangeRequest, "user-originator")).Unwrap();

        return status.Runs.Single().Tasks.Single().Id;
    }

    [TestMethod]
    public async Task Creating_initial_tasks_produces_a_document_level_status()
    {
        await StartAsync();

        var status = (await _svc.GetWorkflowStatusAsync(_documentId, DemoDocumentType.ChangeRequest)).Unwrap();

        Assert.AreEqual(_documentId, status.DocumentId);
        Assert.AreEqual("ChangeRequest", status.DocumentType);
        Assert.AreEqual(1, status.TotalTasks);
        Assert.AreEqual(0, status.CompletedTasks);
        Assert.AreEqual("Not Started", status.OverallStatus);
    }

    [TestMethod]
    public async Task Valid_outcomes_come_back_for_a_task()
    {
        var taskId = await StartAsync();

        var outcomes = (await _svc.GetValidOutcomesForTaskAsync(taskId)).Unwrap();

        CollectionAssert.AreEquivalent(
            new[] { "approved", "rejected" },
            outcomes.Select(o => o.OutcomeKey).ToArray());
    }

    [TestMethod]
    public async Task Task_logs_are_recorded_and_queryable()
    {
        var taskId = await StartAsync();
        (await _svc.CompleteTaskAsync(taskId, "approved", "user-originator", "looks fine")).Unwrap();

        var logs = (await _svc.GetLogsForTaskAsync(taskId)).Unwrap();

        Assert.IsGreaterThanOrEqualTo(2, logs.Count, "Expected at least a Created and a Completed entry.");
        Assert.IsTrue(logs.Any(l => l.Action == "Completed"));
    }

    [TestMethod]
    public async Task Notes_can_be_edited_without_completing_the_task()
    {
        var taskId = await StartAsync();

        (await _svc.UpdateTaskNotesAsync(taskId, "in progress note", "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual("in progress note", task.Notes);
        Assert.AreEqual(WorkflowTaskStatus.NotStarted, task.Status,
            "Editing notes must not advance the task.");
    }

    [TestMethod]
    public async Task Ad_hoc_task_types_are_listed_for_the_workflow()
    {
        var taskId = await StartAsync();

        var options = (await _svc.GetAdHocTaskTypesAsync(taskId)).Unwrap();

        Assert.IsGreaterThanOrEqualTo(3, options.Count, "Expected the seeded ad-hoc chain definitions.");
        Assert.IsTrue(options.Any(o => o.DisplayName == "Ad-hoc Provide Input" && o.IsBlocking));
    }

    [TestMethod]
    public async Task Progress_reporting_updates_the_hosts_work_items()
    {
        var taskId = await StartAsync();

        // The sink is host code; drive it directly, since the engine does not yet
        // compute progress (see STATE.md).
        var sink = new DemoProgressSink(_host.Db, NullLogger<DemoProgressSink>.Instance);
        await sink.ReportAsync(
            new WorkflowSubject("ChangeRequest", _documentId.ToString()), "C100", 42d, "user-section-lead");
        await _host.Db.SaveChangesAsync();

        var section = await _host.Db.Sections.SingleAsync(s => s.Code == "C100");
        var item = await _host.Db.WorkItems.AsNoTracking()
            .SingleAsync(w => w.DocumentId == _documentId && w.AssignedSectionId == section.Id);

        Assert.AreEqual(42d, item.CompletionPercentage);
        Assert.IsGreaterThan(0, taskId);
    }

    [TestMethod]
    public async Task Work_item_completion_percentage_is_clamped()
    {
        var sink = new DemoProgressSink(_host.Db, NullLogger<DemoProgressSink>.Instance);
        await sink.ReportAsync(
            new WorkflowSubject("ChangeRequest", _documentId.ToString()), "C100", 250d, "user-section-lead");
        await _host.Db.SaveChangesAsync();

        var section = await _host.Db.Sections.SingleAsync(s => s.Code == "C100");
        var item = await _host.Db.WorkItems.AsNoTracking()
            .SingleAsync(w => w.DocumentId == _documentId && w.AssignedSectionId == section.Id);

        Assert.AreEqual(100d, item.CompletionPercentage);
    }

    [TestMethod]
    public async Task Fork_context_counts_cancelled_branches_separately_from_completed()
    {
        var entry = await StartAsync();
        (await _svc.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input"
                          && t.Status == WorkflowTaskStatus.NotStarted);

        var convergenceDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "PM Review").Select(d => d.Id).SingleAsync();

        var fork = (await _svc.ForkTaskAsync(
            provideInput.Id, ["C100", "C200", "C300"], convergenceDefId, "user-originator")).Unwrap();

        (await _svc.CompleteTaskAsync(fork.Branches[0].Id, "approved", "system")).Unwrap();
        (await _svc.CancelTaskAsync(fork.Branches[1].Id, "system", "not applicable")).Unwrap();

        var ctx = (await _svc.GetForkContextAsync(fork.Branches[2].Id)).Unwrap();

        // the original system lumped cancelled in with completed, overstating progress.
        Assert.AreEqual(1, ctx.CompletedBranchCount);
        Assert.AreEqual(1, ctx.CancelledBranchCount);
        Assert.AreEqual(1, ctx.PendingBranchCount);
    }

    [TestMethod]
    public async Task Branches_can_be_added_to_an_open_fork()
    {
        var entry = await StartAsync();
        (await _svc.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input"
                          && t.Status == WorkflowTaskStatus.NotStarted);

        var convergenceDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "PM Review").Select(d => d.Id).SingleAsync();

        var fork = (await _svc.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], convergenceDefId, "user-originator")).Unwrap();

        var added = (await _svc.AddBranchToForkAsync(
            fork.ForkGroupId, ["C300"], "user-originator")).Unwrap();

        Assert.HasCount(1, added.NewBranches);
        Assert.AreEqual("C300", added.NewBranches[0].AssignedBranchKey);
        Assert.AreEqual(3, added.TotalBranchCount);
        Assert.AreEqual("user-lead-c300", added.NewBranches[0].AssignedToActorId);
    }

    [TestMethod]
    public async Task Branches_cannot_be_added_after_the_fork_has_converged()
    {
        var entry = await StartAsync();
        (await _svc.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input"
                          && t.Status == WorkflowTaskStatus.NotStarted);

        var convergenceDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "PM Review").Select(d => d.Id).SingleAsync();

        var fork = (await _svc.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], convergenceDefId, "user-originator")).Unwrap();

        foreach (var b in fork.Branches)
        {
            (await _svc.CompleteTaskAsync(b.Id, "approved", "system")).Unwrap();
        }

        // the original system finding H5: adding here would strand the new branch forever, because
        // convergence has already fired and will never re-evaluate.
        var result = await _svc.AddBranchToForkAsync(fork.ForkGroupId, ["C300"], "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "converged");
    }

    [TestMethod]
    public async Task Fork_manifest_records_each_branch_outcome()
    {
        var entry = await StartAsync();
        (await _svc.CompleteTaskAsync(entry, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input"
                          && t.Status == WorkflowTaskStatus.NotStarted);

        var convergenceDefId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "PM Review").Select(d => d.Id).SingleAsync();

        var fork = (await _svc.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], convergenceDefId, "user-originator")).Unwrap();

        (await _svc.CompleteTaskAsync(fork.Branches[0].Id, "approved", "system", "all good")).Unwrap();
        (await _svc.CompleteTaskAsync(fork.Branches[1].Id, "rejected", "system", "needs work")).Unwrap();

        var manifests = (await _svc.GetForkManifestsForDocumentAsync(
            _documentId, DemoDocumentType.ChangeRequest)).Unwrap();

        var manifest = manifests.Single();
        Assert.HasCount(2, manifest.Entries);

        var c100 = manifest.Entries.Single(e => e.BranchKey == "C100");
        Assert.AreEqual("approved", c100.BranchOutcomeKey);
        Assert.AreEqual("all good", c100.BranchNotes);
    }

    [TestMethod]
    public async Task Document_status_reaches_Complete_when_every_task_is_done()
    {
        var entry = await StartAsync();
        (await _svc.CompleteTaskAsync(entry, "rejected", "user-originator")).Unwrap();

        // The rejected route from Enter Record goes straight to Close Document.
        var close = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Close Document");

        (await _svc.CompleteTaskAsync(close.Id, "approved", "user-originator")).Unwrap();

        var status = (await _svc.GetWorkflowStatusAsync(_documentId, DemoDocumentType.ChangeRequest)).Unwrap();
        Assert.AreEqual("Complete", status.OverallStatus);
    }
}
