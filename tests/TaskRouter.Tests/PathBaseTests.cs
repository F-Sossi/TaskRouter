using Microsoft.EntityFrameworkCore;

using TaskRouter.Blazor.Http;
using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// The HTTP clients against an app published under a sub-path — <c>https://server/app/</c>
/// rather than <c>https://server/</c>.
///
/// <para>The WebAssembly template points the <c>HttpClient</c> at the host environment's base
/// address, which carries that sub-path. A request URL starting with <c>/</c> is
/// root-relative and replaces the base address's path entirely, so <c>/workflow/inbox/items</c>
/// went to <c>https://server/workflow/…</c> — past the proxy, which only forwards
/// <c>/app/*</c>. Every client worked in development, where the app sits at the root, and
/// none of them worked deployed.</para>
/// </summary>
[TestClass]
public class PathBaseTests
{
    private EndpointTestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await EndpointTestHost.CreateAsync(pathBase: "/app");

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task The_builder_client_stays_under_the_base_address()
    {
        // Throws on any status but 404, so a request that escaped the sub-path fails here.
        Assert.IsNotEmpty(await new HttpWorkflowBuilderClient(_host.Client).GetWorkflowsAsync());
    }

    [TestMethod]
    public async Task The_runner_client_stays_under_the_base_address()
    {
        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-a")).Unwrap();

        _host.Client.DefaultRequestHeaders.Add(EndpointTestHost.ActorHeader, "user-a");

        // The runner's reads degrade to null rather than throwing, so null is the failure.
        Assert.IsNotNull(
            await new HttpWorkflowRunnerClient(_host.Client).GetRunAsync(run.Id, "user-a"));
    }

    [TestMethod]
    public async Task The_inbox_client_stays_under_the_base_address()
    {
        _host.Client.DefaultRequestHeaders.Add(EndpointTestHost.ActorHeader, "user-a");

        // The inbox throws on failure, so reaching the assertion at all is the test.
        Assert.IsNotNull(await new HttpWorkflowInboxClient(_host.Client).GetInboxAsync("user-a"));
    }

    [TestMethod]
    public async Task A_prefix_without_a_leading_slash_is_the_same_prefix()
    {
        // "workflow" and "/workflow" must mean the same thing, so a host that wrote the
        // prefix either way is not punished for it.
        Assert.IsNotEmpty(
            await new HttpWorkflowBuilderClient(_host.Client, "workflow").GetWorkflowsAsync());
    }
}
