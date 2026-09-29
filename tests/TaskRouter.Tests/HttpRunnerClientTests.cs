using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Domain;

using TaskRouter.Blazor.Http;
using TaskRouter.Core.Inbox;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;

namespace TaskRouter.Tests;

/// <summary>
/// The runner and inbox clients over HTTP, driven against the real server rather than a
/// mocked <c>HttpMessageHandler</c>.
///
/// <para>That choice is the whole point. These classes exist to make calls that agree with
/// routes, and a mocked handler agrees with whatever the test author believed the routes
/// were — it would pass just as happily against a client posting to <c>/runner/complete</c>
/// when the server listens on <c>/workflow/runner/tasks/{id}/complete</c>.</para>
/// </summary>
[TestClass]
public class HttpRunnerClientTests
{
    private EndpointTestHost _host = null!;
    private HttpWorkflowRunnerClient _client = null!;
    private string _actor = null!;
    private int _runId;
    private int _taskId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await EndpointTestHost.CreateAsync();
        _client = new HttpWorkflowRunnerClient(_host.Client);

        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "500"), definitionId, "user-a")).Unwrap();

        var detail = (await _host.Engine.GetRunAsync(run.Id)).Unwrap();

        _runId = run.Id;
        _taskId = detail.Tasks.Single().Id;
        _actor = detail.Tasks.Single().AssignedToActorId!;

        // The endpoints take the actor from the principal, so the client's HttpClient has to
        // carry one. In a real WebAssembly host that is a cookie or a bearer token; here it
        // is the fixture's test header, set once for every request this client makes.
        _host.Client.DefaultRequestHeaders.Add(EndpointTestHost.ActorHeader, _actor);
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task A_run_comes_back_whole_over_HTTP()
    {
        var detail = await _client.GetRunAsync(_runId, _actor);

        Assert.IsNotNull(detail);
        Assert.AreEqual(_runId, detail.Run.Id);
        Assert.IsNotEmpty(detail.Tasks);
        Assert.AreEqual(_taskId, detail.Tasks[0].Id);
        Assert.AreEqual(_actor, detail.Tasks[0].AssignedToActorId,
            "The assignment did not survive the wire, so the run would render as unassigned.");
    }

    [TestMethod]
    public async Task Every_read_the_runner_renders_from_round_trips()
    {
        // Each is a separate route, and a client with one path wrong fails only on the
        // panel that uses it -- which is exactly the failure a mocked handler would hide.
        Assert.IsNotEmpty(await _client.GetRunsAsync("ChangeRequest", "500"));
        Assert.IsNotEmpty(await _client.GetOutcomesAsync(_taskId));
        Assert.IsNotNull(await _client.GetLogAsync(_taskId));
        Assert.IsNotNull(await _client.GetBranchOptionsAsync(_taskId));
        Assert.IsNotNull(await _client.GetConvergenceOptionsAsync(_taskId));
        Assert.IsNotNull(await _client.GetSubWorkflowOptionsAsync(_taskId));
        Assert.IsNotNull(await _client.GetAdHocOptionsAsync(_taskId));
        Assert.IsNotNull(await _client.GetSubWorkflowInstancesAsync(_runId));
        Assert.IsNotEmpty(await _client.GetActorsAsync());
        Assert.IsNotEmpty(await _client.GetOrgUnitsAsync());
    }

    [TestMethod]
    public async Task A_run_that_is_not_there_is_null_and_so_is_a_task_that_is_not_forked()
    {
        // Both are documented answers rather than errors, and the server says 404 for both.
        Assert.IsNull(await _client.GetRunAsync(999999, _actor));
        Assert.IsNull(await _client.GetForkInfoAsync(_taskId),
            "Most tasks are not forked. That is an answer, not a failure.");
    }

    [TestMethod]
    public async Task Completing_a_task_over_HTTP_actually_completes_it()
    {
        // The one that matters: serialization, routing and the endpoint contract together,
        // in the direction a person actually drives them.
        var outcomes = await _client.GetOutcomesAsync(_taskId);

        var result = await _client.CompleteAsync(_taskId, outcomes[0].OutcomeKey, _actor, "Done.");

        Assert.IsTrue(result.Success, result.Error);

        var after = (await _host.Engine.GetRunAsync(_runId)).Unwrap();

        Assert.AreEqual(
            WorkflowTaskStatus.Completed,
            after.Tasks.Single(t => t.Id == _taskId).Status,
            "The client reported success but nothing happened on the server.");
    }

    [TestMethod]
    public async Task A_refusal_arrives_as_a_failed_result_with_something_to_read()
    {
        var result = await _client.CompleteAsync(_taskId, "no-such-outcome", _actor, null);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error, "A refusal with no message tells the user nothing.");
    }

    [TestMethod]
    public async Task Notes_and_reassignment_go_over_the_wire_and_stick()
    {
        var actors = await _client.GetActorsAsync();
        var other = actors.First(a => a.ActorId != _actor);

        Assert.IsTrue((await _client.UpdateNotesAsync(_taskId, "Written remotely.", _actor)).Success);
        Assert.IsTrue((await _client.ReassignAsync(_taskId, other.ActorId, null, _actor, "Yours.")).Success);

        var detail = (await _client.GetRunAsync(_runId, _actor))!;
        var task = detail.Tasks.Single(t => t.Id == _taskId);

        Assert.AreEqual("Written remotely.", task.Notes);
        Assert.AreEqual(other.ActorId, task.AssignedToActorId,
            "Reassignment reported success but the task did not move.");
    }

    [TestMethod]
    public async Task A_transport_failure_is_a_failed_result_rather_than_an_exception()
    {
        // "The server said no" and "the server could not be reached" are the same thing to
        // whoever clicked the button, and the component already knows how to render one of
        // them. Throwing here would make every call site need a try/catch.
        var wrong = new HttpWorkflowRunnerClient(_host.Client, prefix: "/no-such-prefix");

        var result = await wrong.CompleteAsync(_taskId, "approved", _actor, null);

        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public async Task A_read_against_a_broken_route_degrades_rather_than_throwing()
    {
        // Matching EfWorkflowRunnerClient, whose reads flatten a failed Result to empty. A
        // component must not behave differently depending on which side of a wire the
        // engine is.
        var wrong = new HttpWorkflowRunnerClient(_host.Client, prefix: "/no-such-prefix");

        Assert.IsEmpty(await wrong.GetActorsAsync());
        Assert.IsNull(await wrong.GetRunAsync(_runId, _actor));
    }

    [TestMethod]
    public async Task A_test_run_can_be_started_and_discarded_over_HTTP()
    {
        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.IsPublished).Select(v => v.Id).FirstAsync();

        var started = await _client.StartTestRunAsync(versionId, _actor);

        Assert.IsTrue(started.Success, started.Error);
        Assert.IsGreaterThan(0, started.RunId);

        var deleted = await _client.DeleteTestRunAsync(started.RunId, _actor);

        Assert.IsTrue(deleted.Success, deleted.Error);

        var refused = await _client.DeleteTestRunAsync(_runId, _actor);

        Assert.IsFalse(refused.Success,
            "A real run must not be deletable. This is a hard delete and that guard is the "
            + "whole safety of the operation.");
    }

    [TestMethod]
    public async Task The_inbox_arrives_with_its_subjects_already_resolved()
    {
        var document = new ChangeRequest
        {
            Title = "Pump room rewire",
            DocNumber = "CR-2026-0300",
            Originator = "user-a",
        };

        _host.Db.Documents.Add(document);
        await _host.Db.SaveChangesAsync();

        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", document.Id.ToString()),
            definitionId, "user-a")).Unwrap();

        var actor = (await _host.Engine.GetRunAsync(run.Id)).Unwrap()
            .Tasks.Single().AssignedToActorId!;

        _host.Client.DefaultRequestHeaders.Remove(EndpointTestHost.ActorHeader);
        _host.Client.DefaultRequestHeaders.Add(EndpointTestHost.ActorHeader, actor);

        var inbox = new HttpWorkflowInboxClient(_host.Client);

        var items = await inbox.GetInboxAsync(actor);

        var row = items.Single(i => i.SubjectLabel == "CR-2026-0300");

        Assert.AreEqual("Pump room rewire", row.SubjectSubtitle);
        Assert.AreEqual($"/documents/{document.Id}", row.SubjectUrl,
            "A browser cannot resolve a subject itself, so if this is empty the inbox "
            + "renders rows nobody can click.");
    }

    [TestMethod]
    public async Task A_failing_inbox_request_throws_rather_than_reading_as_an_empty_inbox()
    {
        // The one client that must not degrade, and it must agree with the in-process one.
        // An empty inbox says "Nothing is waiting on you", which is the opposite of the
        // truth when the request simply failed.
        var wrong = new HttpWorkflowInboxClient(_host.Client, prefix: "/no-such-prefix");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => wrong.GetInboxAsync(_actor));
    }

    [TestMethod]
    public void Registration_resolves_both_clients()
    {
        var services = new ServiceCollection();

        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost") });
        services.AddTaskRouterRunnerHttpClient();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsInstanceOfType<HttpWorkflowRunnerClient>(
            scope.ServiceProvider.GetRequiredService<IWorkflowRunnerClient>());
        Assert.IsInstanceOfType<HttpWorkflowInboxClient>(
            scope.ServiceProvider.GetRequiredService<IWorkflowInboxClient>());
    }

    [TestMethod]
    public async Task A_run_can_be_cancelled_over_HTTP_and_reads_as_cancelled()
    {
        // The reason the operation exists: a manager starts the wrong workflow and needs the
        // work in flight to stop so a different one can start. Cancelling every task by hand
        // would leave the run reading as Completed.
        var result = await _client.CancelRunAsync(_runId, _actor, "Wrong workflow.");

        Assert.IsTrue(result.Success, result.Error);

        var runs = await _client.GetRunsAsync("ChangeRequest", "500");

        Assert.AreEqual(
            WorkflowRunStatus.Cancelled,
            runs.Single(r => r.Id == _runId).Status,
            "A cancelled run must not come back as Completed -- that is the whole point.");
    }

    [TestMethod]
    public async Task Cancelling_a_run_twice_comes_back_as_a_failed_result_not_an_exception()
    {
        Assert.IsTrue((await _client.CancelRunAsync(_runId, _actor, null)).Success);

        var second = await _client.CancelRunAsync(_runId, _actor, null);

        Assert.IsFalse(second.Success);
        Assert.IsNotNull(second.Error, "A refusal with no message tells the user nothing.");
    }
}
