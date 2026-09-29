using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Microsoft.AspNetCore.Http;

using TaskRouter.AspNetCore;
using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// The inbox route and the seam it needs.
///
/// The engine's GetOpenTasksForActorAsync takes branch keys alongside the actor because
/// org membership is host knowledge. The route takes neither from the caller: accepting
/// ?branchKeys= would let anyone enumerate another org unit's unclaimed work by guessing.
/// </summary>
[TestClass]
public class EndpointInboxTests
{
    private sealed class FixedBranchKeys(params string[] keys) : IWorkflowEndpointBranchKeyResolver
    {
        public Task<IReadOnlyList<string>> GetBranchKeysAsync(
            HttpContext context, string actorId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(keys);
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

    [TestMethod]
    public async Task It_requires_an_actor()
    {
        var (host, _) = await StartAsync();
        await using var _h = host;

        var response = await host.Client.GetAsync("/workflow/inbox");

        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            response.StatusCode,
            "an inbox with no actor is a question with no subject");
    }

    [TestMethod]
    public async Task It_returns_work_assigned_directly_to_the_actor()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        (await host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment("user-alice", null), "user-manager")).Unwrap();

        var response = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/workflow/inbox", "user-alice"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), taskId.ToString());
    }

    [TestMethod]
    public async Task It_returns_unclaimed_branch_work_when_a_resolver_supplies_the_key()
    {
        var (host, taskId) = await StartAsync(s =>
            s.AddScoped<IWorkflowEndpointBranchKeyResolver>(_ => new FixedBranchKeys("SEC-1")));

        await using var _h = host;

        (await host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(null, "SEC-1"), "user-manager")).Unwrap();

        var response = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/workflow/inbox", "user-bob"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), taskId.ToString());
    }

    [TestMethod]
    public async Task It_returns_no_branch_work_without_a_resolver()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        (await host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(null, "SEC-1"), "user-manager")).Unwrap();

        var response = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/workflow/inbox", "user-bob"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsFalse(
            (await response.Content.ReadAsStringAsync()).Contains(taskId.ToString(), StringComparison.Ordinal),
            "the default resolver supplies no keys, so branch work is invisible -- "
            + "that is the documented predicate, not a bug");
    }

    [TestMethod]
    public async Task It_ignores_branch_keys_supplied_by_the_caller()
    {
        var (host, taskId) = await StartAsync();
        await using var _h = host;

        (await host.Engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(null, "SEC-1"), "user-manager")).Unwrap();

        var response = await host.Client.SendAsync(
            host.Request(HttpMethod.Get, "/workflow/inbox?branchKeys=SEC-1", "user-bob"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        Assert.IsFalse(
            (await response.Content.ReadAsStringAsync()).Contains(taskId.ToString(), StringComparison.Ordinal),
            "a caller must not be able to widen their own inbox");
    }
}
