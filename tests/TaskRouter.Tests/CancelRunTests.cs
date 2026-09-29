using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Abandoning a run.
///
/// <para>The case this exists for: a manager started the wrong workflow, or a section changed
/// its process, and the work in flight has to stop so a different one can start. Before this
/// the only route was cancelling each open task by hand, which left the run marked
/// <b>Completed</b> — indistinguishable in every report from work that actually finished.</para>
/// </summary>
[TestClass]
public class CancelRunTests
{
    private TestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await TestHost.CreateAsync();

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<WorkflowRunSnapshot> StartAsync(string subjectId = "1")
    {
        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        return (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), definitionId, "user-a")).Unwrap();
    }

    [TestMethod]
    public async Task A_cancelled_run_reads_as_cancelled_and_not_as_completed()
    {
        // The whole point. "Completed" is what the old workaround produced, and it is a
        // different claim about the work -- one a report cannot tell from a real finish.
        var run = await StartAsync();

        (await _host.Engine.CancelRunAsync(run.Id, "user-a", "Wrong workflow.")).Unwrap();

        var after = (await _host.Engine.GetRunAsync(run.Id)).Unwrap();

        Assert.AreEqual(WorkflowRunStatus.Cancelled, after.Status);
    }

    [TestMethod]
    public async Task Every_open_task_is_cancelled_with_it()
    {
        // Left open they would sit in somebody's inbox forever, pointing at a run that is
        // over -- which is exactly what starting a replacement workflow alongside would do.
        var run = await StartAsync("2");

        (await _host.Engine.CancelRunAsync(run.Id, "user-a", null)).Unwrap();

        var after = (await _host.Engine.GetRunAsync(run.Id)).Unwrap();

        Assert.IsEmpty(
            after.Tasks.Where(t => t.Status is WorkflowTaskStatus.NotStarted
                                             or WorkflowTaskStatus.InProgress),
            "An open task on a cancelled run is work nobody can finish and nothing will close.");
    }

    [TestMethod]
    public async Task Work_already_done_keeps_its_outcome()
    {
        // Cancelling ends what is outstanding; it does not rewrite history. The audit trail
        // has to still show what was decided before somebody changed course.
        var run = await StartAsync("3");
        var detail = (await _host.Engine.GetRunAsync(run.Id)).Unwrap();
        var first = detail.Tasks.Single();

        var outcome = (await _host.Engine.GetValidOutcomesAsync(first.Id)).Unwrap().First();
        (await _host.Engine.CompleteTaskAsync(first.Id, outcome.OutcomeKey, "user-a")).Unwrap();

        (await _host.Engine.CancelRunAsync(run.Id, "user-a", "Changed our minds.")).Unwrap();

        var after = (await _host.Engine.GetRunAsync(run.Id)).Unwrap();
        var completed = after.Tasks.Single(t => t.Id == first.Id);

        Assert.AreEqual(WorkflowTaskStatus.Completed, completed.Status);
        Assert.AreEqual(outcome.OutcomeKey, completed.OutcomeKey);
    }

    [TestMethod]
    public async Task Cancelling_twice_is_refused_rather_than_silently_repeated()
    {
        var run = await StartAsync("4");

        (await _host.Engine.CancelRunAsync(run.Id, "user-a", null)).Unwrap();

        var second = await _host.Engine.CancelRunAsync(run.Id, "user-a", null);

        Assert.IsTrue(second.IsError,
            "A second cancel would re-stamp the run's modifier and log a cancellation that "
            + "did not happen.");
    }

    [TestMethod]
    public async Task A_finished_run_cannot_be_cancelled()
    {
        // Reached through the old workaround, deliberately: cancelling every open task by
        // hand is what people did before this operation existed, and it marks the run
        // Completed. So this both proves the guard and pins the behaviour that motivated
        // the whole change -- an abandoned run reading as a finished one.
        var run = await StartAsync("5");
        var detail = (await _host.Engine.GetRunAsync(run.Id)).Unwrap();

        foreach (var task in detail.Tasks.Where(
                     t => t.Status is WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress))
        {
            (await _host.Engine.CancelTaskAsync(task.Id, "user-a")).Unwrap();
        }

        var finished = (await _host.Engine.GetRunAsync(run.Id)).Unwrap();

        Assert.AreEqual(
            WorkflowRunStatus.Completed, finished.Status,
            "Cancelling every task still marks the run Completed. That is the behaviour "
            + "CancelRunAsync exists to avoid, and if it ever changes this test should be "
            + "the thing that says so.");

        Assert.IsTrue(
            (await _host.Engine.CancelRunAsync(run.Id, "user-a", null)).IsError,
            "Marking a finished run Cancelled would erase the fact that it finished.");
    }

    [TestMethod]
    public async Task The_reason_is_kept_where_somebody_will_find_it()
    {
        // "Why did this stop?" is the first question anyone asks of a cancelled run.
        var run = await StartAsync("6");
        var taskId = (await _host.Engine.GetRunAsync(run.Id)).Unwrap().Tasks.Single().Id;

        (await _host.Engine.CancelRunAsync(run.Id, "user-a", "Superseded by the replacement process.")).Unwrap();

        var log = (await _host.Engine.GetTaskLogsAsync(taskId)).Unwrap();

        Assert.IsTrue(
            log.Any(e => e.Note?.Contains("replacement process", StringComparison.Ordinal) == true),
            "The reason was not recorded against anything a person would look at.");
    }

    [TestMethod]
    public async Task A_replacement_workflow_can_start_on_the_same_subject()
    {
        // The reason the operation exists. Concurrent runs were always allowed, so this
        // works either way -- the point is that the abandoned one is visibly abandoned.
        var run = await StartAsync("7");
        (await _host.Engine.CancelRunAsync(run.Id, "user-a", "Wrong variant.")).Unwrap();

        var replacement = await StartAsync("7");

        Assert.AreNotEqual(run.Id, replacement.Id);

        var runs = (await _host.Engine.GetRunsForSubjectAsync(
            new WorkflowSubject("ChangeRequest", "7"))).Unwrap();

        Assert.AreEqual(
            WorkflowRunStatus.Cancelled,
            runs.Single(r => r.Id == run.Id).Status);
        Assert.AreEqual(
            WorkflowRunStatus.Running,
            runs.Single(r => r.Id == replacement.Id).Status);
    }
}
