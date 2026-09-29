using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TaskRouter.AspNetCore;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Where the actor comes from, and what happens when it is missing or forged.
///
/// <see cref="It_never_reaches_the_engine_with_a_reserved_actor_id"/> is the one that
/// matters: WorkflowActors.System short-circuits the authorization gate before the
/// policy is consulted, so an actor id starting "workflow:" is a root switch. The demo
/// guards it on every route; here it is guarded once, on the only path an actor arrives
/// through.
/// </summary>
[TestClass]
public class EndpointActorTests
{
    private EndpointTestHost _host = null!;
    private int _taskId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await EndpointTestHost.CreateAsync();

        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        _taskId = run.Tasks.Single().Id;
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private HttpRequestMessage Notes(string? actorId)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{_taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "touched" })
        };

        if (actorId is not null)
        {
            request.Headers.Add(EndpointTestHost.ActorHeader, actorId);
        }

        return request;
    }

    [TestMethod]
    public async Task It_refuses_a_request_with_no_principal()
    {
        var response = await _host.Client.SendAsync(Notes(actorId: null));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task It_accepts_a_request_carrying_an_actor()
    {
        var response = await _host.Client.SendAsync(Notes("user-originator"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task It_refuses_a_reserved_actor_id()
    {
        var response = await _host.Client.SendAsync(Notes(WorkflowActors.System));

        // 403, not 401 -- the caller here is authenticated. They are only claiming an
        // id they may not have, which is an authorization refusal, not "who are you".
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task It_refuses_any_reserved_prefix_not_just_the_system_id()
    {
        var response = await _host.Client.SendAsync(Notes("workflow:something-invented"));

        Assert.AreEqual(
            HttpStatusCode.Forbidden,
            response.StatusCode,
            "the prefix is reserved, not just the one id in use today");
    }

    [TestMethod]
    public async Task It_refuses_a_case_variant_of_the_reserved_prefix()
    {
        // The database's collation is case-insensitive -- AssignedToActorId matching and
        // the CreatorId/ModifierId audit columns all compare this way -- so a guard that
        // only matched "workflow:" ordinally would let "WORKFLOW:SYSTEM" through to
        // impersonate the engine in every audit trail that reads the actor id back out.
        var response = await _host.Client.SendAsync(Notes("WORKFLOW:SYSTEM"));

        Assert.AreEqual(
            HttpStatusCode.Forbidden,
            response.StatusCode,
            "the prefix match must not be case-sensitive");
    }

    [TestMethod]
    public async Task It_never_reaches_the_engine_with_a_reserved_actor_id()
    {
        var response = await _host.Client.SendAsync(Notes(WorkflowActors.System));

        // Asserted here too, not just the DB state below -- otherwise this test would
        // pass for the wrong reason whenever the route is missing, which is exactly
        // what happened before Task 6 mapped it.
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == _taskId);

        Assert.IsNull(task.Notes, "the guard must refuse before the engine writes anything");
    }

    [TestMethod]
    public async Task It_ignores_an_actor_id_in_the_body()
    {
        // The contract has no ActorId. A body that carries one anyway must change
        // nothing -- the property is not bound, so this is really asserting the shape
        // of the request record.
        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{_taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "from body", ActorId = WorkflowActors.System })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, "user-originator");

        var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == _taskId);
        Assert.AreEqual("user-originator", task.ModifierId, "the body must not choose the actor");
    }

    [TestMethod]
    public async Task It_refuses_a_reserved_actor_id_even_if_the_accessor_is_replaced()
    {
        // The whole reason the guard lives in WorkflowEndpointActor rather than in
        // ClaimsActorAccessor is that a host can replace the accessor (it is
        // TryAddSingleton) without losing the guard. This is the test that would catch
        // the guard quietly migrating into the default accessor instead.
        await using var host = await EndpointTestHost.CreateAsync(
            services => services.AddSingleton<IWorkflowEndpointActorAccessor, RogueAccessor>());

        var definitionId = await host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var taskId = run.Tasks.Single().Id;

        // No actor header at all -- RogueAccessor ignores the request and always
        // answers WorkflowActors.System, which is the point.
        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "touched" })
        };

        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task It_honours_a_custom_actor_claim_type()
    {
        await using var host = await EndpointTestHost.CreateAsync(services =>
            services.AddSingleton(new WorkflowEndpointOptions { ActorClaimType = "custom-actor-claim" }));

        var definitionId = await host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var taskId = run.Tasks.Single().Id;

        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "touched" })
        };
        request.Headers.Add(EndpointTestHost.ActorHeader, "user-originator");
        request.Headers.Add(EndpointTestHost.ActorClaimTypeHeader, "custom-actor-claim");

        var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task It_refuses_an_authenticated_principal_with_no_actor_claim()
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/workflow/tasks/{_taskId}/notes")
        {
            Content = JsonContent.Create(new { Notes = "touched" })
        };
        request.Headers.Add(EndpointTestHost.AuthenticatedNoClaimHeader, "true");

        var response = await _host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Always answers the reserved system id, unconditionally -- stands in for a host
    /// accessor that got something badly wrong, so the chokepoint guard is what has to
    /// catch it rather than the accessor policing itself.
    /// </summary>
    private sealed class RogueAccessor : IWorkflowEndpointActorAccessor
    {
        public string? GetActorId(HttpContext context) => WorkflowActors.System;
    }
}
