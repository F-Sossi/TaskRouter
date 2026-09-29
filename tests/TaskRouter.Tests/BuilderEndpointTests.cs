using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using DemoDocuments.Server.Data;

using TaskRouter.AspNetCore;
using TaskRouter.Core.Builder;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// The builder's operations over real HTTP.
///
/// <para>Driven through <see cref="EndpointTestHost"/>, the same in-process server the engine
/// endpoints are tested against, so a route that does not agree with its handler fails here
/// rather than in a browser.</para>
/// </summary>
[TestClass]
public class BuilderEndpointTests
{
    private EndpointTestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await EndpointTestHost.CreateAsync();

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task Listing_workflows_returns_the_seeded_one()
    {
        var response = await _host.Client.GetAsync("/workflow/builder/workflows");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var workflows = await response.Content.ReadFromJsonAsync<List<WorkflowSummary>>();

        Assert.IsNotNull(workflows);
        Assert.IsNotEmpty(workflows, "The demo seeds a workflow, so this is never empty.");
    }

    [TestMethod]
    public async Task A_workflow_comes_back_whole()
    {
        var summaries = (await _host.Client
            .GetFromJsonAsync<List<WorkflowSummary>>("/workflow/builder/workflows"))!;

        var model = await _host.Client
            .GetFromJsonAsync<WorkflowEditModel>($"/workflow/builder/workflows/{summaries[0].VersionId}");

        Assert.IsNotNull(model);
        Assert.IsNotEmpty(model.Tasks, "A workflow with no tasks came back, which is not one.");
        Assert.IsTrue(
            model.Tasks.Exists(t => t.Routes.Count > 0),
            "Routes are nested two levels down and are the first thing a shallow "
            + "serialization would lose.");
    }

    [TestMethod]
    public async Task A_workflow_that_does_not_exist_is_404_not_an_empty_one()
    {
        var response = await _host.Client.GetAsync("/workflow/builder/workflows/999999");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode,
            "200 with a null body cannot be told apart from a workflow with nothing in it.");
    }

    [TestMethod]
    public async Task Validation_errors_are_a_body_not_a_fault()
    {
        // An empty workflow fails WF_EMPTY. That is a legitimate answer to a legitimate
        // question -- the builder asks precisely so it can show the author what is wrong --
        // and a 500 would leave them with a broken workflow and nothing to read.
        var empty = new WorkflowEditModel { Name = "Nothing", Tasks = [] };

        var response = await _host.Client.PostAsJsonAsync("/workflow/builder/validate", empty);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var errors = await response.Content
            .ReadFromJsonAsync<List<TaskRouter.Core.Validation.ValidationError>>();

        Assert.IsNotNull(errors);
        Assert.IsNotEmpty(errors);
    }

    [TestMethod]
    public async Task The_subject_types_an_author_may_choose_reach_the_builder()
    {
        // Without these the field is free text, and a subject type typed one character wrong
        // produces a workflow that is never offered on any document -- with nothing on screen
        // to say why. The fixture configures two.
        var types = await _host.Client
            .GetFromJsonAsync<List<string>>("/workflow/builder/subject-types");

        Assert.IsNotNull(types);
        CollectionAssert.Contains(types, "ChangeRequest");
    }

    [TestMethod]
    public async Task The_reference_data_a_builder_renders_from_is_all_reachable()
    {
        // Six dropdowns. Each is a separate route and a separate chance to be wrong.
        foreach (var route in new[]
        {
            "/workflow/builder/task-types",
            "/workflow/builder/triggers",
            "/workflow/builder/assignment-roles",
            "/workflow/builder/route-conditions",
            "/workflow/builder/subject-types",
            "/workflow/builder/sub-workflows",
        })
        {
            var response = await _host.Client.GetAsync(route);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{route} did not answer.");
        }
    }

    [TestMethod]
    public async Task A_task_type_can_be_created()
    {
        var key = $"created-{Guid.NewGuid():N}";

        var response = await _host.Client.PostAsJsonAsync(
            "/workflow/builder/task-types", new CreateTaskTypeRequest(key, "Created Type"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<TaskTypeOption>();

        Assert.IsNotNull(created);
        Assert.AreEqual(key, created.Key);
        Assert.IsGreaterThan(0, created.Id, "A created type without an id was not persisted.");
    }

    [TestMethod]
    public async Task A_draft_version_can_be_created_from_a_published_one()
    {
        var summaries = (await _host.Client
            .GetFromJsonAsync<List<WorkflowSummary>>("/workflow/builder/workflows"))!;

        var response = await _host.Client.PostAsync(
            $"/workflow/builder/workflows/{summaries[0].VersionId}/draft", content: null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var draftId = await response.Content.ReadFromJsonAsync<int>();

        Assert.AreNotEqual(summaries[0].VersionId, draftId,
            "A draft that is the same version is not a draft.");
    }

    [TestMethod]
    public async Task A_workflow_fetched_over_HTTP_can_be_saved_back_over_HTTP()
    {
        // The one that matters: serialization, routing and the endpoint contract together, in
        // the direction a person actually drives them. Everything above tests one hop.
        var summaries = (await _host.Client
            .GetFromJsonAsync<List<WorkflowSummary>>("/workflow/builder/workflows"))!;

        var draftId = await (await _host.Client.PostAsync(
            $"/workflow/builder/workflows/{summaries[0].VersionId}/draft", content: null))
            .Content.ReadFromJsonAsync<int>();

        var model = (await _host.Client
            .GetFromJsonAsync<WorkflowEditModel>($"/workflow/builder/workflows/{draftId}"))!;

        var response = await _host.Client.PostAsJsonAsync("/workflow/builder/save", model);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<SaveResult>();

        Assert.IsNotNull(result);
        Assert.IsEmpty(result.Errors,
            "A workflow that came out of the builder went back in and was refused: "
            + string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    [TestMethod]
    public async Task A_host_that_never_adopted_the_builder_still_starts()
    {
        // MapTaskRouterEndpoints maps the builder routes unconditionally, so every host
        // that upgrades gets them whether or not it called AddWorkflowBuilder(). This is
        // the test that upgrading does not break such a host -- and it is not
        // hypothetical: without [FromServices] on the handlers, minimal APIs infer the
        // unregistered IWorkflowBuilderClient as a *body* parameter, a GET may not have
        // one, and the host fails to start with an error that names neither the builder
        // nor the route. Deleting those attributes fails this test, which is the point.
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

        // 401 rather than 500: the inbox route reached its handler and refused the
        // request for having no actor, which it can only do if routing is intact.
        var response = await client.GetAsync("/workflow/inbox");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode,
            "A host with no builder client should serve its task endpoints normally.");

        await app.StopAsync();
    }

    [TestMethod]
    public async Task The_builder_group_can_carry_a_stricter_policy_than_the_rest()
    {
        // Editing a definition changes how every future run of that workflow behaves, which
        // is a good deal more privileged than completing one task -- so a host has to be
        // able to gate the two differently. Before this hook existed MapTaskRouterEndpoints
        // returned one group and the only choice was to gate all of it the same way.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddDbContext<DemoDbContext>(o => o.UseSqlServer("Server=none"));
        builder.Services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<DemoDbContext>());
        builder.Services.AddTaskRouter();
        builder.Services.AddTaskRouterEndpoints();

        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NobodyHandler>("Test", null);
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("Authoring", policy => policy.RequireClaim("may-author"));

        await using var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapTaskRouterEndpoints(
            configureBuilderGroup: group => group.RequireAuthorization("Authoring"));

        await app.StartAsync();

        using var client = app.GetTestClient();

        Assert.AreEqual(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/workflow/builder/workflows")).StatusCode,
            "A caller without the authoring claim must not reach the builder.");

        // And the rest of the group is untouched: 401 means the inbox route reached its
        // handler and refused for having no actor, rather than being blocked by the
        // builder's policy.
        Assert.AreEqual(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/workflow/inbox")).StatusCode,
            "The builder's policy must not leak onto the task endpoints.");

        await app.StopAsync();
    }

    /// <summary>Authenticates every request as somebody holding no claims at all.</summary>
    private sealed class NobodyHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([], "Test")), "Test")));
    }
}
