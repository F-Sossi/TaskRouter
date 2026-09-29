using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Every "no row with this id" refusal is its own exception type, so a host can map it
/// to 404 without reading the message. The messages themselves are unchanged, which is
/// why no existing test needed touching.
/// </summary>
[TestClass]
public class NotFoundExceptionTests
{
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();
        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task It_still_derives_from_InvalidOperationException()
    {
        // The whole point of deriving: a host catching the old type keeps working.
        var ex = new WorkflowNotFoundException("Task", "99");

        Assert.IsInstanceOfType<InvalidOperationException>(ex);
        Assert.AreEqual("Task 99 not found.", ex.Message);
        Assert.AreEqual("Task", ex.EntityKind);
        Assert.AreEqual("99", ex.Id);
    }

    [TestMethod]
    public async Task It_reports_a_missing_task_as_not_found()
    {
        var result = await _host.Engine.CompleteTaskAsync(999_999, "approve", "user-x");

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_a_missing_run_as_not_found()
    {
        var result = await _host.Engine.GetRunAsync(999_999);

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_a_missing_fork_manifest_as_not_found()
    {
        var result = await _host.Engine.GetForkManifestAsync(999_999);

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_a_missing_task_as_not_found_from_the_outcomes_read()
    {
        // GetValidOutcomesAsync infers absence from a projected TaskDefinitionId of 0,
        // not from a null row -- the only converted site that works that way.
        var result = await _host.Engine.GetValidOutcomesAsync(999_999);

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_an_unknown_fork_group_as_not_found()
    {
        var result = await _host.Engine.AddBranchToForkAsync(
            Guid.NewGuid(), ["SEC-1"], "user-x");

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_a_missing_sub_workflow_instance_as_not_found()
    {
        var result = await _host.Engine.CancelSubWorkflowAsync(999_999, "user-x");

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<WorkflowNotFoundException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_leaves_state_refusals_as_plain_invalid_operations()
    {
        // The definition exists; it has no published version. That is a state refusal,
        // not an absence, and must NOT convert -- this is the line the conversion draws.
        var draft = new WorkflowDefinition { Name = "Never published" };
        _host.Db.WorkflowDefinitions.Add(draft);
        await _host.Db.SaveChangesAsync();

        var result = await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), draft.Id, "user-x");

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<InvalidOperationException>(result.UnwrapError());
        Assert.IsNotInstanceOfType<WorkflowNotFoundException>(
            result.UnwrapError(),
            "an unpublished definition exists -- it is a conflict, not a 404");
    }

    [TestMethod]
    public async Task It_leaves_a_draft_version_as_a_plain_invalid_operation()
    {
        // The sharpest instance of the rule: two arms of the same method, five lines
        // apart. A version id with no row converts (WorkflowEngine.cs ~122); a version
        // id that resolves but is still a draft does not (~126, "is a draft") -- this
        // pins the second arm against the first.
        var draftVersion = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = _definitionId,
            Version = 999,
            IsPublished = false,
        };
        _host.Db.WorkflowDefinitionVersions.Add(draftVersion);
        await _host.Db.SaveChangesAsync();

        var result = await _host.Engine.StartRunOnVersionAsync(
            new WorkflowSubject("ChangeRequest", "3"), draftVersion.Id, "user-x", isTest: false);

        Assert.IsTrue(result.IsError);
        Assert.IsInstanceOfType<InvalidOperationException>(result.UnwrapError());
        Assert.IsNotInstanceOfType<WorkflowNotFoundException>(
            result.UnwrapError(),
            "the version row exists and is a draft -- it is a conflict, not a 404");
    }

    [TestMethod]
    public async Task It_reports_a_started_task_normally()
    {
        // Guards the conversion from over-reaching: a real task still completes.
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "2"), _definitionId, "user-originator")).Unwrap();

        var outcomes = (await _host.Engine.GetValidOutcomesAsync(run.Tasks.Single().Id)).Unwrap();
        var result = await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, outcomes.First().OutcomeKey, "user-originator");

        Assert.IsTrue(result.IsOk, result.IsError ? result.UnwrapError().Message : "");
    }
}
