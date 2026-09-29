using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// One request per route. The route table is where a copy-paste slip hides — a handler
/// wired to the wrong engine method still compiles — and only a call per route finds it.
/// </summary>
[TestClass]
public class EndpointRouteTests
{
    private EndpointTestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await EndpointTestHost.CreateAsync();
        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<int> StartedTaskAsync(string subjectId = "1")
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), _definitionId, "user-originator")).Unwrap();

        return run.Tasks.Single().Id;
    }

    private async Task<string> ValidOutcomeAsync(int taskId) =>
        (await _host.Engine.GetValidOutcomesAsync(taskId)).Unwrap().First().OutcomeKey;

    private async Task<HttpResponseMessage> PostAsync(string url, object body, string actor = "user-originator")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, actor);

        return await _host.Client.SendAsync(request);
    }

    [TestMethod]
    public async Task It_completes_a_task()
    {
        var taskId = await StartedTaskAsync();

        var response = await PostAsync(
            $"/workflow/tasks/{taskId}/complete",
            new { OutcomeKey = await ValidOutcomeAsync(taskId), Notes = "done" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual(WorkflowTaskStatus.Completed, task.Status,
            "the route must actually have moved the engine, not just answered 200");
    }

    [TestMethod]
    public async Task It_cancels_a_task()
    {
        var taskId = await StartedTaskAsync("2");

        var response = await PostAsync($"/workflow/tasks/{taskId}/cancel", new { Note = "not needed" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual(WorkflowTaskStatus.Cancelled, task.Status);
    }

    [TestMethod]
    public async Task It_reassigns_a_task_to_the_target_not_the_caller()
    {
        var taskId = await StartedTaskAsync("3");

        var response = await PostAsync(
            $"/workflow/tasks/{taskId}/reassign",
            new { ActorId = "user-target", BranchKey = (string?)null, Note = "yours" },
            actor: "user-manager");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual("user-target", task.AssignedToActorId, "the body names the target");
        Assert.AreEqual("user-manager", task.ModifierId, "the principal names the caller");
    }

    [TestMethod]
    public async Task It_updates_notes()
    {
        var taskId = await StartedTaskAsync("4");

        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "a note" })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, "user-originator");

        var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
        Assert.AreEqual("a note", task.Notes);
    }

    [TestMethod]
    public async Task It_starts_a_run()
    {
        var response = await PostAsync(
            "/workflow/runs",
            new { SubjectType = "ChangeRequest", SubjectId = "90", WorkflowDefinitionId = _definitionId });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var started = await _host.Db.WorkflowRuns.AsNoTracking()
            .AnyAsync(r => r.Subject.SubjectType == "ChangeRequest" && r.Subject.SubjectId == "90");

        Assert.IsTrue(started, "the route must have created a run");
    }

    [TestMethod]
    public async Task It_starts_a_run_on_a_named_version()
    {
        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _definitionId && v.IsLatest)
            .Select(v => v.Id)
            .SingleAsync();

        var response = await PostAsync(
            "/workflow/runs/version",
            new { SubjectType = "ChangeRequest", SubjectId = "91", WorkflowDefinitionVersionId = versionId });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task It_answers_404_for_a_fork_group_that_does_not_exist()
    {
        var response = await PostAsync(
            $"/workflow/forks/{Guid.NewGuid()}/branches",
            new { BranchKeys = new[] { "SEC-1" } });

        Assert.AreEqual(
            HttpStatusCode.NotFound,
            response.StatusCode,
            "the :guid constraint matched, so this reached the engine and came back absent");
    }

    [TestMethod]
    public async Task It_answers_404_for_a_sub_workflow_instance_that_does_not_exist()
    {
        var response = await PostAsync(
            "/workflow/sub-workflow-instances/999999/cancel", new { Reason = "no" });

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task It_reads_a_run()
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "80"), _definitionId, "user-originator")).Unwrap();

        var response = await _host.Client.GetAsync($"/workflow/runs/{run.Id}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "\"subjectId\":\"80\"");
    }

    [TestMethod]
    public async Task It_reads_runs_for_a_subject_from_the_query_string()
    {
        (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "81"), _definitionId, "user-originator")).Unwrap();

        var response = await _host.Client.GetAsync("/workflow/runs?subjectType=ChangeRequest&subjectId=81");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "\"subjectId\":\"81\"");
    }

    [TestMethod]
    public async Task It_reads_the_valid_outcomes_for_a_task()
    {
        var taskId = await StartedTaskAsync("82");

        var response = await _host.Client.GetAsync($"/workflow/tasks/{taskId}/outcomes");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        // Longer than "[]", which is what an empty outcome list would serialize to.
        Assert.IsGreaterThan(
            2, (await response.Content.ReadAsStringAsync()).Length, "outcomes were expected");
    }

    [TestMethod]
    public async Task It_reads_task_logs_children_and_fork_context()
    {
        var taskId = await StartedTaskAsync("83");

        foreach (var suffix in new[] { "logs", "children", "fork-context", "sub-workflow-options" })
        {
            var response = await _host.Client.GetAsync($"/workflow/tasks/{taskId}/{suffix}");

            Assert.AreEqual(
                HttpStatusCode.OK, response.StatusCode, $"GET tasks/{{id}}/{suffix} failed");
        }
    }

    [TestMethod]
    public async Task It_reads_a_run_s_fork_manifests_and_sub_workflow_instances()
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "84"), _definitionId, "user-originator")).Unwrap();

        foreach (var suffix in new[] { "fork-manifests", "sub-workflow-instances" })
        {
            var response = await _host.Client.GetAsync($"/workflow/runs/{run.Id}/{suffix}");

            Assert.AreEqual(
                HttpStatusCode.OK, response.StatusCode, $"GET runs/{{id}}/{suffix} failed");
        }
    }

    [TestMethod]
    public async Task It_answers_404_reading_a_run_that_does_not_exist()
    {
        var response = await _host.Client.GetAsync("/workflow/runs/999999");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task It_does_not_require_an_actor_to_read()
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "85"), _definitionId, "user-originator")).Unwrap();

        // No X-Test-Actor header at all. Reads are ungated on purpose; if that ever
        // changes, this test is the one that should fail and force the conversation.
        var response = await _host.Client.GetAsync($"/workflow/runs/{run.Id}");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
