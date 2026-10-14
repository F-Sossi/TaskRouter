using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// A terminal task that closes itself the moment it is reached.
///
/// <para>The seeded workflow's Close Document is written the old way — a person finishes it —
/// so each test switches it on in the database first, which is what a workflow authored
/// with the setting looks like to the engine. Enter Record rejects straight to it, so one
/// completion reaches the end.</para>
/// </summary>
[TestClass]
public class SelfCompletingTerminalTests
{
    private TestHost _host = null!;
    private int _workflowId;
    private int _closeDefinitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();

        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

        _closeDefinitionId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "Close Document"
                     && d.WorkflowDefinitionVersion!.WorkflowDefinitionId == _workflowId)
            .Select(d => d.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task SwitchOnAsync()
    {
        await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.Id == _closeDefinitionId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.CompletesAutomatically, true));

        // The engine shares this context, which still holds the seeded row from before the
        // update. A host's engine is scoped to a request and would read it fresh.
        _host.Db.ChangeTracker.Clear();
    }

    /// <summary>Starts a run and rejects its entry task, which routes to Close Document.</summary>
    private async Task<int> RunToTheEndAsync(string actor = "user-originator")
    {
        var subject = new WorkflowSubject(
            nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());

        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, actor)).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Rejected, actor)).Unwrap();

        return run.Id;
    }

    private Task<WorkflowTask> CloseTaskAsync(int runId) =>
        _host.Db.WorkflowTasks.AsNoTracking()
            .SingleAsync(t => t.WorkflowRunId == runId && t.TaskDefinitionId == _closeDefinitionId);

    [TestMethod]
    public async Task A_terminal_task_written_before_the_setting_still_waits_for_a_person()
    {
        var runId = await RunToTheEndAsync();

        var close = await CloseTaskAsync(runId);
        var run = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        Assert.AreEqual(WorkflowTaskStatus.NotStarted, close.Status);
        Assert.AreEqual(WorkflowRunStatus.Running, run.Status);
    }

    [TestMethod]
    public async Task Reaching_it_completes_it_and_the_run()
    {
        await SwitchOnAsync();

        var runId = await RunToTheEndAsync();

        var close = await CloseTaskAsync(runId);
        var run = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        Assert.AreEqual(WorkflowTaskStatus.Completed, close.Status);
        Assert.AreEqual(WorkflowRunStatus.Completed, run.Status);
    }

    [TestMethod]
    public async Task It_closes_with_its_first_outcome_credited_to_whoever_reached_it()
    {
        await SwitchOnAsync();

        // A second outcome ahead of the seeded one, so "first" means Order and not "only".
        _host.Db.WorkflowTaskOutcomes.Add(new TaskOutcomeDefinition
        {
            TaskDefinitionId = _closeDefinitionId,
            OutcomeKey = "archived",
            DisplayName = "Archived",
            Order = 0,
            CreatorId = "test",
            ModifierId = "test",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        });
        await _host.Db.SaveChangesAsync();

        var runId = await RunToTheEndAsync("user-originator");

        var close = await CloseTaskAsync(runId);

        Assert.AreEqual("archived", close.OutcomeKey);
        Assert.AreEqual("user-originator", close.AssignedToActorId);
        Assert.AreEqual("user-originator", close.ModifierId);

        var log = await _host.Db.WorkflowTaskLogs.AsNoTracking()
            .Where(l => l.TaskId == close.Id)
            .Select(l => new { l.Action, l.Note })
            .ToListAsync();

        Assert.IsTrue(
            log.Any(l => l.Action == "Completed" && l.Note!.Contains("automatically")),
            "The history has to say nobody pressed the button.");
        Assert.IsFalse(log.Any(l => l.Action == "Reassigned"),
            "Nobody stood in for anybody, so there is no hand-over to explain.");
    }

    [TestMethod]
    public async Task The_step_before_it_completes_before_it_does()
    {
        await SwitchOnAsync();

        var runId = await RunToTheEndAsync();

        var completed = await _host.Db.WorkflowTasks.AsNoTracking()
            .Where(t => t.WorkflowRunId == runId)
            .OrderBy(t => t.Id)
            .Select(t => new { t.TaskDefinitionId, t.CompletedDate })
            .ToListAsync();

        Assert.HasCount(2, completed);
        Assert.AreNotEqual(_closeDefinitionId, completed[0].TaskDefinitionId);
        Assert.IsTrue(completed[0].CompletedDate <= completed[1].CompletedDate);
    }

    [TestMethod]
    public async Task One_held_up_by_a_blocking_sub_workflow_closes_when_it_finishes()
    {
        // Not something a valid workflow can have any more -- a terminal task cannot have a
        // sub-workflow attached -- but one published before that rule can, and it must not
        // stay open for ever.
        await SwitchOnAsync();

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var versionId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.Id == _closeDefinitionId)
            .Select(d => d.WorkflowDefinitionVersionId)
            .SingleAsync();

        _host.Db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            SubWorkflowDefinitionId = subWorkflowId,
            WorkflowDefinitionVersionId = versionId,
            TaskDefinitionId = _closeDefinitionId,
            IsAutomatic = true,
            IsBlocking = true,
            CreatorId = "test",
            ModifierId = "test",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        });
        await _host.Db.SaveChangesAsync();

        var runId = await RunToTheEndAsync();
        var close = await CloseTaskAsync(runId);

        Assert.AreEqual(WorkflowTaskStatus.NotStarted, close.Status,
            "Blocked when it was reached, so it has to wait.");

        // Work the sub-workflow through to the end, one open task at a time.
        while (await _host.Db.WorkflowTasks.AsNoTracking()
                   .Where(t => t.WorkflowRunId == runId
                            && t.SubWorkflowInstanceId != null
                            && t.Status == WorkflowTaskStatus.NotStarted)
                   .FirstOrDefaultAsync() is { } step)
        {
            var outcome = (await _host.Engine.GetValidOutcomesAsync(step.Id)).Unwrap()[0];

            (await _host.Engine.CompleteTaskAsync(
                step.Id, outcome.OutcomeKey, step.AssignedToActorId ?? "user-originator")).Unwrap();
        }

        close = await CloseTaskAsync(runId);
        var run = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        Assert.AreEqual(WorkflowTaskStatus.Completed, close.Status);
        Assert.AreEqual(WorkflowRunStatus.Completed, run.Status);
    }

    [TestMethod]
    public async Task A_sub_workflow_cannot_be_started_on_a_terminal_task()
    {
        // Left waiting for a person, so there is an open terminal task to try it on.
        var runId = await RunToTheEndAsync();
        var close = await CloseTaskAsync(runId);

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var started = await _host.Engine.StartSubWorkflowAsync(
            close.Id, subWorkflowId, "user-originator");

        Assert.IsTrue(started.IsError);
        StringAssert.Contains(started.UnwrapError().Message, "terminal");

        Assert.IsEmpty((await _host.Engine.GetSubWorkflowOptionsAsync(close.Id)).Unwrap(),
            "Nothing should be offered that would be refused.");
    }

    [TestMethod]
    public async Task An_ad_hoc_task_cannot_be_added_to_a_terminal_task()
    {
        var runId = await RunToTheEndAsync();
        var close = await CloseTaskAsync(runId);

        var adHocDefinitionId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.DisplayName == "Ad-hoc Provide Input")
            .Select(d => d.Id)
            .SingleAsync();

        var added = await _host.Engine.AddAdHocTaskAsync(
            close.Id, adHocDefinitionId, "user-originator");

        Assert.IsTrue(added.IsError);
        StringAssert.Contains(added.UnwrapError().Message, "terminal");
    }
}
