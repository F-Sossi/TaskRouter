using Microsoft.Extensions.DependencyInjection;

using TaskRouter.Blazor.Http;
using TaskRouter.Core.Builder;

namespace TaskRouter.Tests;

/// <summary>
/// The builder client over HTTP, driven against the real server rather than a mocked
/// <c>HttpMessageHandler</c>.
///
/// <para>That choice is the whole point of these tests. This class exists to make twelve
/// calls that agree with twelve routes, and a mocked handler agrees with whatever the test
/// author believed the routes were — it would pass just as happily against a client that
/// posts to <c>/builder/save</c> when the server listens on <c>/workflow/builder/save</c>.
/// Every failure this class can have is a disagreement with the server, so the server is
/// what it is tested against.</para>
/// </summary>
[TestClass]
public class HttpBuilderClientTests
{
    private EndpointTestHost _host = null!;
    private HttpWorkflowBuilderClient _client = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await EndpointTestHost.CreateAsync();
        _client = new HttpWorkflowBuilderClient(_host.Client);
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task Every_read_the_builder_renders_from_round_trips()
    {
        // Six dropdowns and a list. Each is a separate route, and a client that has one
        // path wrong fails only on the screen that uses it.
        Assert.IsNotEmpty(await _client.GetWorkflowsAsync());
        Assert.IsNotEmpty(await _client.GetTaskTypesAsync());
        Assert.IsNotEmpty(await _client.GetTriggerDescriptorsAsync());
        Assert.IsNotEmpty(await _client.GetAssignmentRolesAsync(),
            "The fixture configures two roles, so an empty list means they did not arrive.");
        Assert.IsNotNull(await _client.GetOutcomeTypesAsync(),
            "The catalogue may be empty -- these are rows -- but the route has to answer.");
        Assert.IsNotEmpty(await _client.GetRouteConditionsAsync(),
            "RequiresReviewCondition is registered, so this is never empty here.");
        Assert.IsNotNull(await _client.GetSubWorkflowDefinitionsAsync());
    }

    [TestMethod]
    public async Task A_workflow_comes_back_whole()
    {
        var summaries = await _client.GetWorkflowsAsync();
        var model = await _client.GetWorkflowAsync(summaries[0].VersionId);

        Assert.IsNotNull(model);
        Assert.IsNotEmpty(model.Tasks);
        Assert.IsTrue(
            model.Tasks.Exists(t => t.Routes.Count > 0),
            "Routes are nested two levels down and are the first thing a shallow "
            + "deserialization would lose.");
    }

    [TestMethod]
    public async Task A_workflow_that_is_not_there_is_null_not_an_exception()
    {
        // The interface returns WorkflowEditModel? precisely so this is answerable, and
        // the server says 404. Throwing here would make "no such version" an error the
        // builder has to catch rather than a value it can test.
        Assert.IsNull(await _client.GetWorkflowAsync(999999));
    }

    [TestMethod]
    public async Task A_server_failure_is_not_disguised_as_an_empty_answer()
    {
        // 404 is the one status with a meaning; everything else must surface. A builder
        // that renders an empty workflow because the server faulted is worse than one
        // that shows an error, because the author will then save that emptiness over
        // their real workflow.
        var broken = new HttpWorkflowBuilderClient(_host.Client, prefix: "/no-such-prefix");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => broken.GetWorkflowsAsync());
    }

    [TestMethod]
    public async Task Validation_errors_arrive_as_errors_not_as_a_fault()
    {
        var errors = await _client.ValidateAsync(new WorkflowEditModel
        {
            Name = "Nothing",
            Tasks = [],
        });

        Assert.IsNotEmpty(errors, "An empty workflow fails WF_EMPTY.");
    }

    [TestMethod]
    public async Task A_task_type_can_be_created()
    {
        var key = $"http-{Guid.NewGuid():N}";
        var created = await _client.CreateTaskTypeAsync(key, "Created Over HTTP");

        Assert.AreEqual(key, created.Key);
        Assert.IsGreaterThan(0, created.Id, "A type with no id was never persisted.");
    }

    [TestMethod]
    public async Task A_workflow_survives_a_fetch_edit_save_round_trip()
    {
        // The one that matters. Serialization, routing and the endpoint contract
        // together, in the direction a person actually drives them: open a workflow,
        // change something, save it, and see the change still there.
        var summaries = await _client.GetWorkflowsAsync();
        var draftId = await _client.CreateDraftVersionAsync(summaries[0].VersionId);

        Assert.AreNotEqual(summaries[0].VersionId, draftId,
            "A draft that is the same version is not a draft.");

        var model = (await _client.GetWorkflowAsync(draftId))!;
        var renamed = $"Renamed {Guid.NewGuid():N}";
        model.Tasks[0].DisplayName = renamed;

        var result = await _client.SaveAsync(model);

        Assert.IsEmpty(result.Errors,
            "A workflow that came out of the builder went back in and was refused: "
            + string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
        Assert.IsTrue(result.Success);

        var reloaded = (await _client.GetWorkflowAsync(draftId))!;

        Assert.AreEqual(renamed, reloaded.Tasks[0].DisplayName,
            "The save returned success but the edit did not survive the round trip.");
    }

    [TestMethod]
    public async Task A_workflow_can_be_duplicated_over_HTTP()
    {
        // This is what keeps DuplicateWorkflowBody and DuplicateWorkflowRequest honest.
        // They are declared separately so the Blazor package need not reference
        // ASP.NET Core, and nothing but a round trip against the real server would catch
        // them drifting apart -- a renamed property would simply deserialise as null.
        var summaries = await _client.GetWorkflowsAsync();
        var source = summaries[0];

        var name = $"Duplicated {Guid.NewGuid():N}";
        var copyId = await _client.DuplicateWorkflowAsync(source.VersionId, name);

        var copy = (await _client.GetWorkflowAsync(copyId))!;

        Assert.AreEqual(name, copy.Name, "the name did not survive the body round trip");
        Assert.AreNotEqual(source.DefinitionId, copy.DefinitionId,
            "a duplicate is a new workflow, not a new version");
        Assert.IsFalse(copy.IsPublished);
        Assert.IsNotEmpty(copy.Tasks, "the graph did not come across");
    }

    [TestMethod]
    public async Task A_draft_can_be_published_over_HTTP()
    {
        var summaries = await _client.GetWorkflowsAsync();
        var draftId = await _client.CreateDraftVersionAsync(summaries[0].VersionId);
        var model = (await _client.GetWorkflowAsync(draftId))!;

        var result = await _client.PublishAsync(model);

        Assert.IsTrue(result.Success,
            "Publish refused a draft the builder itself produced: "
            + string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));

        var published = (await _client.GetWorkflowAsync(draftId))!;

        Assert.IsTrue(published.IsPublished,
            "Publish reported success but the version came back a draft.");
    }

    [TestMethod]
    public void Registration_over_the_hosts_own_HttpClient_resolves()
    {
        // The registration is three lines and every way it can be wrong -- a lifetime that
        // does not fit, a dependency the host has not registered -- shows up only when
        // something asks for the service.
        var services = new ServiceCollection();
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("http://localhost") });
        services.AddTaskRouterBuilderHttpClient();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsInstanceOfType<HttpWorkflowBuilderClient>(
            scope.ServiceProvider.GetRequiredService<IWorkflowBuilderClient>());
    }

    [TestMethod]
    public void Registration_with_a_base_address_leaves_an_existing_HttpClient_alone()
    {
        // TryAddSingleton, so the convenience overload cannot quietly replace a host's own
        // client along with its handlers and headers. A host that registered one and then
        // called this overload would otherwise lose it without a word.
        var mine = new HttpClient { BaseAddress = new Uri("http://mine.example") };

        var services = new ServiceCollection();
        services.AddSingleton(mine);
        services.AddTaskRouterBuilderHttpClient(new Uri("http://theirs.example"));

        using var provider = services.BuildServiceProvider(validateScopes: true);

        Assert.AreSame(mine, provider.GetRequiredService<HttpClient>());

        mine.Dispose();
    }
}