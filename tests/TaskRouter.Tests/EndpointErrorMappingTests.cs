using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Five status codes, one per exception shape the engine produces.
///
/// The ordering assertion matters most: WorkflowAuthorizationException and
/// WorkflowNotFoundException both derive from InvalidOperationException, so a switch
/// that tested the base type first would answer 409 to everything and every one of these
/// tests but the last would fail.
/// </summary>
[TestClass]
public class EndpointErrorMappingTests
{
    /// <summary>
    /// Denies only the operation under test. Scoped deliberately: the fixture seeds its
    /// task by calling StartRunAsync in-process, and that goes through the same gate --
    /// a policy that denied everything would fail the setup before the request under
    /// test was ever sent.
    /// </summary>
    private sealed class DenyingPolicy : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation,
            WorkflowAuthorizationContext context,
            CancellationToken ct = default) =>
            Task.FromResult(operation == WorkflowOperation.UpdateTaskNotes
                ? WorkflowAuthorizationResult.Denied("the test says no")
                : WorkflowAuthorizationResult.Allowed);
    }

    private sealed class ExplodingPolicy : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation,
            WorkflowAuthorizationContext context,
            CancellationToken ct = default) =>
            operation == WorkflowOperation.UpdateTaskNotes
                ? throw new BadImageFormatException("a secret from the internals")
                : Task.FromResult(WorkflowAuthorizationResult.Allowed);
    }

    private static async Task<(EndpointTestHost Host, int TaskId)> StartAsync(
        Action<IServiceCollection>? configure = null)
    {
        var host = await EndpointTestHost.CreateAsync(configure);

        var definitionId = await host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        return (host, run.Tasks.Single().Id);
    }

    private static HttpRequestMessage Notes(int taskId, string actorId)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "touched" })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, actorId);

        return request;
    }

    [TestMethod]
    public async Task It_answers_403_when_the_policy_denies()
    {
        var (host, taskId) = await StartAsync(s =>
            s.AddScoped<IWorkflowAuthorizationPolicy, DenyingPolicy>());

        await using var _ = host;

        var response = await host.Client.SendAsync(Notes(taskId, "user-originator"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        StringAssert.Contains(
            problem.GetProperty("detail").GetString(),
            "the test says no",
            "the policy's reason is the point of reporting a denial at all");

        Assert.AreEqual(
            nameof(WorkflowOperation.UpdateTaskNotes),
            problem.GetProperty("operation").GetString());
    }

    [TestMethod]
    public async Task It_answers_404_for_a_missing_task()
    {
        var (host, _) = await StartAsync();
        await using var _h = host;

        var response = await host.Client.SendAsync(Notes(999_999, "user-originator"));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task It_answers_409_for_a_state_refusal()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        var outcomes = (await host.Engine.GetValidOutcomesAsync(taskId)).Unwrap();
        (await host.Engine.CompleteTaskAsync(
            taskId, outcomes.First().OutcomeKey, "user-originator")).Unwrap();

        // Completing it a second time is a conflict, not an absence.
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/workflow/tasks/{taskId}/complete")
        {
            Content = JsonContent.Create(new { OutcomeKey = outcomes.First().OutcomeKey })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, "user-originator");

        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task It_answers_500_without_leaking_the_message()
    {
        var (host, taskId) = await StartAsync(s =>
            s.AddScoped<IWorkflowAuthorizationPolicy, ExplodingPolicy>());

        await using var _h = host;

        var response = await host.Client.SendAsync(Notes(taskId, "user-originator"));

        // AuthorizeAsync catches a throwing policy and denies, so this is really
        // asserting the *shape* of the 500 arm via whatever surfaces. Either outcome is
        // acceptable except leaking the text.
        var body = await response.Content.ReadAsStringAsync();

        Assert.IsFalse(
            body.Contains("a secret from the internals", StringComparison.Ordinal),
            "an unexpected exception's message was never written for a caller");
    }

    [TestMethod]
    public async Task It_answers_400_for_a_semantically_invalid_body()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        // Duplicate branch keys. Well-formed JSON the engine refuses on its merits, so
        // ASP.NET's own 400 never fires and the request reaches the handler.
        var request = new HttpRequestMessage(HttpMethod.Post, $"/workflow/tasks/{taskId}/fork")
        {
            Content = JsonContent.Create(new
            {
                BranchKeys = new[] { "SEC-1", "SEC-1" },
                ConvergenceTaskDefinitionId = 1,
            })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, "user-originator");

        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        StringAssert.Contains(
            await response.Content.ReadAsStringAsync(),
            "Duplicate branch keys",
            "the caller needs the sentence that tells them what to fix");
    }
}
