using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using DemoDocuments.Server.Data;

using TaskRouter.AspNetCore;
using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Inbox;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// The runner's and inbox's operations over real HTTP, through the same in-process server the
/// engine endpoints use — so a route that does not agree with its handler fails here rather
/// than in a browser.
/// </summary>
[TestClass]
public class RunnerEndpointTests
{
    private EndpointTestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await EndpointTestHost.CreateAsync();

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>Starts a run and returns it, with the actor its entry task went to.</summary>
    private async Task<(int RunId, string ActorId)> StartRunAsync(string subjectId)
    {
        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), definitionId, "user-a")).Unwrap();

        var detail = (await _host.Engine.GetRunAsync(run.Id)).Unwrap();

        return (run.Id, detail.Tasks.Single().AssignedToActorId!);
    }

    [TestMethod]
    public async Task A_runs_detail_comes_back_whole()
    {
        var (runId, actor) = await StartRunAsync("101");

        var response = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, $"/workflow/runner/runs/{runId}", actor));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var detail = await response.Content.ReadFromJsonAsync<RunDetail>();

        Assert.IsNotNull(detail);
        Assert.AreEqual(runId, detail.Run.Id);
        Assert.IsNotEmpty(detail.Tasks, "A run with no tasks came back, which is not one.");
    }

    [TestMethod]
    public async Task A_run_detail_needs_an_actor_because_CanComplete_is_about_a_person()
    {
        var (runId, _) = await StartRunAsync("102");

        var response = await _host.Client.GetAsync($"/workflow/runner/runs/{runId}");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "Without an actor there is no answer to give: CanComplete is evaluated for a "
            + "specific person, and a caller must not be able to ask what somebody else "
            + "could do.");
    }

    [TestMethod]
    public async Task A_run_that_does_not_exist_is_404_not_an_empty_one()
    {
        var response = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, "/workflow/runner/runs/999999", "user-a"));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Runs_are_found_by_subject_which_is_how_a_document_page_finds_its_own()
    {
        await StartRunAsync("103");

        var runs = await _host.Client.GetFromJsonAsync<List<RunView>>(
            "/workflow/runner/runs?subjectType=ChangeRequest&subjectId=103");

        Assert.IsNotNull(runs);
        Assert.IsNotEmpty(runs);
        Assert.AreEqual("103", runs[0].SubjectId);
    }

    [TestMethod]
    public async Task Every_read_the_runner_renders_from_is_reachable()
    {
        var (runId, actor) = await StartRunAsync("104");
        var detail = (await _host.Engine.GetRunAsync(runId)).Unwrap();
        var taskId = detail.Tasks.Single().Id;

        // Each is a separate route and a separate chance to be wrong, and a client with one
        // path wrong fails only on the panel that uses it.
        foreach (var route in new[]
        {
            $"/workflow/runner/tasks/{taskId}/outcomes",
            $"/workflow/runner/tasks/{taskId}/log",
            $"/workflow/runner/tasks/{taskId}/branch-options",
            $"/workflow/runner/tasks/{taskId}/convergence-options",
            $"/workflow/runner/tasks/{taskId}/sub-workflow-options",
            $"/workflow/runner/tasks/{taskId}/ad-hoc-options",
            $"/workflow/runner/runs/{runId}/sub-workflow-instances",
            "/workflow/runner/actors",
        })
        {
            var response = await _host.Client.SendAsync(
                _host.Request(HttpMethod.Get, route, actor));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{route} did not answer.");
        }
    }

    [TestMethod]
    public async Task A_task_that_is_not_forked_reports_no_fork_rather_than_faulting()
    {
        var (runId, actor) = await StartRunAsync("105");
        var detail = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        var response = await _host.Client.SendAsync(_host.Request(
            HttpMethod.Get, $"/workflow/runner/tasks/{detail.Tasks.Single().Id}/fork", actor));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode,
            "Most tasks are not forked, so this is an ordinary answer and not a fault.");
    }

    [TestMethod]
    public async Task A_task_can_be_completed_over_HTTP()
    {
        var (runId, actor) = await StartRunAsync("106");
        var detail = (await _host.Engine.GetRunAsync(runId)).Unwrap();
        var taskId = detail.Tasks.Single().Id;

        var outcomes = await _host.Client.GetFromJsonAsync<List<OutcomeOption>>(
            $"/workflow/runner/tasks/{taskId}/outcomes");

        var request = _host.Request(HttpMethod.Post, $"/workflow/runner/tasks/{taskId}/complete", actor);
        request.Content = JsonContent.Create(new CompleteTaskRequest(outcomes![0].OutcomeKey, "Done."));

        var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RunnerResult>();

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Success, result.Error);

        var after = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        Assert.AreEqual(
            WorkflowTaskStatus.Completed,
            after.Tasks.Single(t => t.Id == taskId).Status,
            "The route answered success but the task did not actually complete.");
    }

    [TestMethod]
    public async Task A_write_without_an_actor_is_refused_before_it_reaches_the_engine()
    {
        var (runId, _) = await StartRunAsync("107");
        var detail = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        var response = await _host.Client.PostAsJsonAsync(
            $"/workflow/runner/tasks/{detail.Tasks.Single().Id}/cancel",
            new CancelTaskRequest("no reason"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "A caller asserts what to do, never who is doing it.");
    }

    [TestMethod]
    public async Task A_refusal_from_the_engine_is_a_result_not_a_fault()
    {
        // Completing with an outcome the definition does not offer. The component has to
        // render the message; a 500 would leave a user with a failed action and nothing to
        // read, and no way to tell it apart from the server being broken.
        var (runId, actor) = await StartRunAsync("108");
        var detail = (await _host.Engine.GetRunAsync(runId)).Unwrap();

        var request = _host.Request(
            HttpMethod.Post,
            $"/workflow/runner/tasks/{detail.Tasks.Single().Id}/complete",
            actor);
        request.Content = JsonContent.Create(new CompleteTaskRequest("no-such-outcome"));

        var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RunnerResult>();

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Success);
        Assert.IsNotNull(result.Error, "A refusal with no message tells the user nothing.");
    }

    [TestMethod]
    public async Task The_resolved_inbox_comes_back_as_items_with_labels()
    {
        var document = new DemoDocuments.Server.Domain.ChangeRequest
        {
            Title = "Pump room rewire",
            DocNumber = "CR-2026-0200",
            Originator = "user-a",
        };

        _host.Db.Documents.Add(document);
        await _host.Db.SaveChangesAsync();

        var (_, actor) = await StartRunAsync(document.Id.ToString());

        var response = await _host.Client.SendAsync(
            _host.Request(HttpMethod.Get, "/workflow/inbox/items", actor));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var items = await response.Content.ReadFromJsonAsync<List<InboxItem>>();

        Assert.IsNotNull(items);

        var row = items.Single(i => i.SubjectLabel == "CR-2026-0200");

        Assert.AreEqual($"documents/{document.Id}", row.SubjectUrl,
            "The whole point of this route over /workflow/inbox is that subjects arrive "
            + "resolved -- a WebAssembly host cannot resolve them itself.");
    }

    [TestMethod]
    public async Task A_host_that_never_adopted_the_runner_still_starts()
    {
        // MapTaskRouterEndpoints maps the runner routes unconditionally, so every host that
        // upgrades gets them whether or not it called AddWorkflowRunner(). Without
        // [FromServices] on the handlers, minimal APIs infer the unregistered
        // IWorkflowRunnerClient as a *body* parameter, a GET may not have one, and the host
        // fails to start with an error that names neither the runner nor the route.
        // Deleting those attributes fails this test, which is the point of it.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        // Never connected to. Nothing below reaches the database.
        builder.Services.AddDbContext<DemoDbContext>(o => o.UseSqlServer("Server=none"));
        builder.Services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<DemoDbContext>());
        builder.Services.AddTaskRouter();
        builder.Services.AddTaskRouterEndpoints();

        await using var app = builder.Build();
        app.MapTaskRouterEndpoints();

        await app.StartAsync();

        using var client = app.GetTestClient();

        // 401 rather than 500: the inbox route reached its handler and refused the request
        // for having no actor, which it can only do if routing is intact.
        var response = await client.GetAsync("/workflow/inbox");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "A host with no runner client should serve its task endpoints normally.");

        await app.StopAsync();
    }
}
