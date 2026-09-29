using System.Net.Http.Json;

using TaskRouter.Core.Inbox;

namespace TaskRouter.Blazor.Http;

/// <summary>
/// <see cref="IWorkflowInboxClient"/> over HTTP, for a WebAssembly host.
///
/// <para>Points at <c>/inbox/items</c> rather than <c>/inbox</c>: the latter returns the
/// engine's rows with their subjects still opaque, and a browser has no way to turn
/// <c>ChangeRequest:42</c> into a document number and a link. The server resolves them
/// through its <c>IWorkflowSubjectResolver</c>.</para>
///
/// <para><b>Failures throw here, unlike the runner's reads.</b> An empty inbox renders as
/// "Nothing is waiting on you", so returning an empty list when the request failed would tell
/// the user the opposite of the truth. <c>EfWorkflowInboxClient</c> makes exactly the same
/// choice for the same reason, and the two must agree — a component cannot behave differently
/// depending on which side of a wire the engine is.</para>
/// </summary>
public sealed class HttpWorkflowInboxClient(HttpClient http, string prefix = "/workflow")
    : IWorkflowInboxClient
{
    private readonly string _url = $"{prefix.TrimEnd('/')}/inbox/items";

    private readonly string _unitsUrl = $"{prefix.TrimEnd('/')}/inbox/org-units";

    public async Task<IReadOnlyList<InboxItem>> GetInboxAsync(
        string actorId, IReadOnlyList<string>? orgUnits = null, CancellationToken ct = default)
    {
        // Repeated ?orgUnits= rather than one comma-joined value: a branch key is an opaque
        // host id and may contain anything, a comma included.
        var url = orgUnits is null or { Count: 0 }
            ? _url
            : _url + "?" + string.Join(
                "&", orgUnits.Select(unit => "orgUnits=" + Uri.EscapeDataString(unit)));

        var response = await http.GetAsync(url, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<InboxItem>>(ct).ConfigureAwait(false)
            ?? throw new HttpRequestException($"{url} returned a null body.");
    }

    public async Task<IReadOnlyList<InboxOrgUnit>> GetOrgUnitsAsync(
        string actorId, CancellationToken ct = default)
    {
        var response = await http.GetAsync(_unitsUrl, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content
                   .ReadFromJsonAsync<List<InboxOrgUnit>>(ct).ConfigureAwait(false)
               ?? throw new HttpRequestException($"{_unitsUrl} returned a null body.");
    }
}
