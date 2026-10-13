using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

using MudBlazor.Services;

using TaskRouter.Blazor;
using TaskRouter.Core.Inbox;

namespace TaskRouter.Tests;

/// <summary>
/// The link an inbox row actually renders, in an app published under a sub-path.
///
/// <para>The first host built its links with a leading slash, and from
/// <c>https://server/app/MyTasks</c> every row went to <c>https://server/documents/42</c> —
/// the server's root, outside the app. Nothing showed in development, where the app sits at
/// the root and the two are the same place.</para>
/// </summary>
[TestClass]
public class InboxLinkTests
{
    [TestMethod]
    [DataRow("/documents/42?groupId=2")]
    [DataRow("documents/42?groupId=2")]
    [DataRow("/app/documents/42?groupId=2")]
    public async Task A_row_links_under_the_apps_base_however_the_host_wrote_it(string subjectUrl)
    {
        var html = await RenderInboxAsync("https://server/app/", subjectUrl);

        // Relative, so the browser resolves it against <base href="/app/">.
        StringAssert.Contains(html, "href=\"documents/42?groupId=2\"");
    }

    [TestMethod]
    public async Task A_link_out_of_the_app_is_left_alone()
    {
        var html = await RenderInboxAsync("https://server/app/", "https://elsewhere.example/42");

        StringAssert.Contains(html, "href=\"https://elsewhere.example/42\"");
    }

    private static async Task<string> RenderInboxAsync(string baseUri, string subjectUrl)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime, NoJsRuntime>();
        services.AddSingleton<NavigationManager>(new FixedNavigationManager(baseUri));
        services.AddSingleton<IWorkflowInboxClient>(new OneRowInbox(subjectUrl));

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<WorkflowInbox>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(WorkflowInbox.ActorId)] = "user-a",
                }));

            return output.ToHtmlString();
        });
    }

    private sealed class FixedNavigationManager : NavigationManager
    {
        public FixedNavigationManager(string baseUri) => Initialize(baseUri, baseUri + "MyTasks");
    }

    private sealed class OneRowInbox(string subjectUrl) : IWorkflowInboxClient
    {
        public Task<IReadOnlyList<InboxItem>> GetInboxAsync(
            string actorId, IReadOnlyList<string>? orgUnits = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InboxItem>>(
            [
                new InboxItem(
                    TaskId: 1,
                    TaskLabel: "Review",
                    WorkflowName: "Change request",
                    SubjectLabel: "CR-2026-0042",
                    SubjectSubtitle: null,
                    SubjectUrl: subjectUrl,
                    BranchKey: null,
                    IsUnclaimed: false,
                    IsBlocked: false,
                    Created: DateTime.UtcNow,
                    DueDate: null,
                    OverdueFiredAt: null),
            ]);

        public Task<IReadOnlyList<InboxOrgUnit>> GetOrgUnitsAsync(
            string actorId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InboxOrgUnit>>([]);
    }

    /// <summary>Static rendering never reaches the browser, so nothing here is called for
    /// a value that matters.</summary>
    private sealed class NoJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
