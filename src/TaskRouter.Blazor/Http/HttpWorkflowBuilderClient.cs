using System.Net;
using System.Net.Http.Json;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Validation;

namespace TaskRouter.Blazor.Http;

/// <summary>
/// <see cref="IWorkflowBuilderClient"/> over HTTP, for a host that cannot run the engine in
/// process — a WebAssembly host, which has no DbContext because it runs in a browser.
///
/// <para>The server half is <c>MapTaskRouterEndpoints</c>, which mounts these routes under
/// its own prefix. <paramref name="prefix"/> below must be the same one: the two are a pair,
/// and a host that passes a prefix to one has to pass it to the other. The parameter is
/// named to match.</para>
///
/// <para><b>A Blazor Server host should not use this.</b> It can implement the seam against
/// the engine directly — <c>EfWorkflowBuilderClient</c> does — and routing those calls
/// through its own HTTP stack adds a network hop, a serialization pass and a second set of
/// failure modes to a method call.</para>
/// </summary>
public sealed class HttpWorkflowBuilderClient(HttpClient http, string prefix = "/workflow")
    : IWorkflowBuilderClient
{
    private readonly string _root = $"{prefix.TrimEnd('/')}/builder";

    public async Task<IReadOnlyList<WorkflowSummary>> GetWorkflowsAsync(CancellationToken ct = default) =>
        await GetAsync<List<WorkflowSummary>>($"{_root}/workflows", ct).ConfigureAwait(false);

    public async Task<WorkflowEditModel?> GetWorkflowAsync(int versionId, CancellationToken ct = default)
    {
        var response = await http
            .GetAsync($"{_root}/workflows/{versionId}", ct)
            .ConfigureAwait(false);

        // The one status with a meaning of its own. The interface returns a nullable
        // precisely so "no such version" is a value the builder can test rather than an
        // exception it has to catch, and the server answers 404 for exactly that.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // Everything else surfaces. A builder that renders an empty workflow because the
        // server faulted is worse than one that shows an error, because the author will
        // then save that emptiness over their real workflow.
        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<WorkflowEditModel>(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TaskTypeOption>> GetTaskTypesAsync(CancellationToken ct = default) =>
        await GetAsync<List<TaskTypeOption>>($"{_root}/task-types", ct).ConfigureAwait(false);

    public async Task<TaskTypeOption> CreateTaskTypeAsync(
        string key, string displayName, CancellationToken ct = default) =>
        await PostAsync<CreateTaskTypeBody, TaskTypeOption>(
            $"{_root}/task-types", new CreateTaskTypeBody(key, displayName), ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<TriggerDescriptor>> GetTriggerDescriptorsAsync(CancellationToken ct = default) =>
        await GetAsync<List<TriggerDescriptor>>($"{_root}/triggers", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetAssignmentRolesAsync(CancellationToken ct = default) =>
        await GetAsync<List<string>>($"{_root}/assignment-roles", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<OutcomeTypeOption>> GetOutcomeTypesAsync(
        CancellationToken ct = default) =>
        await GetAsync<List<OutcomeTypeOption>>($"{_root}/outcome-types", ct).ConfigureAwait(false);

    public async Task<OutcomeTypeOption> CreateOutcomeTypeAsync(
        string key, string displayName, CancellationToken ct = default) =>
        await PostAsync<CreateOutcomeTypeBody, OutcomeTypeOption>(
            $"{_root}/outcome-types", new CreateOutcomeTypeBody(key, displayName), ct)
            .ConfigureAwait(false);

    public async Task<TypeInventory> GetTypeInventoryAsync(CancellationToken ct = default) =>
        await GetAsync<TypeInventory>($"{_root}/type-inventory", ct).ConfigureAwait(false);

    public async Task RestoreTaskTypeAsync(int taskTypeId, CancellationToken ct = default) =>
        await PostAsync($"{_root}/task-types/{taskTypeId}/restore", ct).ConfigureAwait(false);

    public async Task RestoreOutcomeTypeAsync(int outcomeTypeId, CancellationToken ct = default) =>
        await PostAsync($"{_root}/outcome-types/{outcomeTypeId}/restore", ct).ConfigureAwait(false);

    public async Task ArchiveTaskTypeAsync(int taskTypeId, CancellationToken ct = default) =>
        await PostAsync($"{_root}/task-types/{taskTypeId}/archive", ct).ConfigureAwait(false);

    public async Task ArchiveOutcomeTypeAsync(int outcomeTypeId, CancellationToken ct = default) =>
        await PostAsync($"{_root}/outcome-types/{outcomeTypeId}/archive", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetSubjectTypesAsync(CancellationToken ct = default) =>
        await GetAsync<List<string>>($"{_root}/subject-types", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetRouteConditionsAsync(CancellationToken ct = default) =>
        await GetAsync<List<string>>($"{_root}/route-conditions", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SubWorkflowDefinitionOption>> GetSubWorkflowDefinitionsAsync(
        CancellationToken ct = default) =>
        await GetAsync<List<SubWorkflowDefinitionOption>>($"{_root}/sub-workflows", ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ValidationError>> ValidateAsync(
        WorkflowEditModel model, CancellationToken ct = default) =>
        await PostAsync<WorkflowEditModel, List<ValidationError>>(
            $"{_root}/validate", model, ct).ConfigureAwait(false);

    public async Task<SaveResult> SaveAsync(WorkflowEditModel model, CancellationToken ct = default) =>
        await PostAsync<WorkflowEditModel, SaveResult>($"{_root}/save", model, ct)
            .ConfigureAwait(false);

    public async Task<SaveResult> PublishAsync(WorkflowEditModel model, CancellationToken ct = default) =>
        await PostAsync<WorkflowEditModel, SaveResult>($"{_root}/publish", model, ct)
            .ConfigureAwait(false);

    public async Task<int> CreateDraftVersionAsync(int fromVersionId, CancellationToken ct = default)
    {
        var response = await http
            .PostAsync($"{_root}/workflows/{fromVersionId}/draft", content: null, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<int>(ct).ConfigureAwait(false);
    }

    public async Task<int> DuplicateWorkflowAsync(
        int fromVersionId, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            // Rejected here as well as server-side, so the WebAssembly host fails the same
            // way the in-process one does rather than on a 400 from somewhere else.
            throw new ArgumentException(
                "A duplicate needs a name of its own.", nameof(newName));
        }

        return await PostAsync<DuplicateWorkflowBody, int>(
            $"{_root}/workflows/{fromVersionId}/duplicate",
            new DuplicateWorkflowBody(newName), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A read that must produce a value.
    ///
    /// <para>The null-coalescing throw is not defensive noise. <c>ReadFromJsonAsync</c>
    /// returns null for a body that is literally <c>null</c>, and every one of these routes
    /// returns a list — so null here means the server said something this client does not
    /// understand, and returning an empty list instead would show the author an empty
    /// dropdown with nothing wrong on screen.</para>
    /// </summary>
    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false)
            ?? throw new HttpRequestException($"{url} returned a null body.");
    }

    /// <summary>A post with nothing to send and nothing to read back — archiving.</summary>
    private async Task PostAsync(string url, CancellationToken ct)
    {
        var response = await http.PostAsync(url, content: null, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    private async Task<TOut> PostAsync<TIn, TOut>(string url, TIn body, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TOut>(ct).ConfigureAwait(false)
            ?? throw new HttpRequestException($"{url} returned a null body.");
    }

    /// <summary>
    /// Mirrors <c>TaskRouter.AspNetCore.CreateTaskTypeRequest</c>. Declared again rather
    /// than referenced because this package targets the browser and must not depend on
    /// ASP.NET Core; the shape is two strings, and the round trip is covered by a test
    /// against the real server, which is what would catch them drifting apart.
    /// </summary>
    private sealed record CreateTaskTypeBody(string Key, string DisplayName);

    /// <inheritdoc cref="CreateTaskTypeBody"/>
    private sealed record CreateOutcomeTypeBody(string Key, string DisplayName);

    /// <inheritdoc cref="CreateTaskTypeBody"/>
    /// <remarks>The name travels in a body rather than the route: it is free text a person
    /// typed, so it would need escaping in a path and would show up in server logs.</remarks>
    private sealed record DuplicateWorkflowBody(string NewName);
}
