using System.Net;
using System.Net.Http.Json;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Runner;

namespace TaskRouter.Blazor.Http;

/// <summary>
/// <see cref="IWorkflowRunnerClient"/> over HTTP, for a host that cannot run the engine in
/// process — a WebAssembly host, which has no DbContext because it runs in a browser.
///
/// <para>The server half is <c>MapTaskRouterEndpoints</c>. <paramref name="prefix"/> must be
/// the prefix that host passed to it: the two are a pair, and a host that changed one has to
/// change the other. The parameter is named to match.</para>
///
/// <para><b>Requests are relative to the <c>HttpClient</c>'s base address</b>, whatever the
/// prefix's leading slash says. A root-relative URL replaces the base address's path, so an
/// app published under a sub-path — <c>https://server/app/</c> — sent every call to
/// <c>https://server/workflow/…</c>, outside the app. Where the app lives is the base
/// address's business, and that is configured per environment already.</para>
///
/// <para><b>A Blazor Server host should not use this.</b> It can implement the seam against
/// the engine directly — <c>EfWorkflowRunnerClient</c> does — and routing those calls through
/// its own HTTP stack adds a network hop, a serialization pass and a second set of failure
/// modes to a method call.</para>
///
/// <para><b>Reads return empty on failure; writes return the failure.</b> That mirrors
/// <c>EfWorkflowRunnerClient</c> deliberately, so the components behave the same either side
/// of the wire — a panel that degrades in one hosting model and throws in the other is worse
/// than either. The exception is the two nullable reads, where null already means something.</para>
/// </summary>
public sealed class HttpWorkflowRunnerClient(HttpClient http, string prefix = "/workflow")
    : IWorkflowRunnerClient
{
    private readonly string _root = $"{prefix.Trim('/')}/runner";

    // ───────────────────────────────── Reads ─────────────────────────────────

    public async Task<IReadOnlyList<RunView>> GetRunsAsync(
        string subjectType, string subjectId, CancellationToken ct = default) =>
        await ListAsync<RunView>(
            $"{_root}/runs?subjectType={Uri.EscapeDataString(subjectType)}"
            + $"&subjectId={Uri.EscapeDataString(subjectId)}", ct).ConfigureAwait(false);

    public async Task<RunDetail?> GetRunAsync(
        int runId, string actorId, CancellationToken ct = default) =>
        await NullableAsync<RunDetail>($"{_root}/runs/{runId}", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<OutcomeOption>> GetOutcomesAsync(
        int taskId, CancellationToken ct = default) =>
        await ListAsync<OutcomeOption>($"{_root}/tasks/{taskId}/outcomes", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<LogEntryView>> GetLogAsync(
        int taskId, CancellationToken ct = default) =>
        await ListAsync<LogEntryView>($"{_root}/tasks/{taskId}/log", ct).ConfigureAwait(false);

    public async Task<ForkInfo?> GetForkInfoAsync(int taskId, CancellationToken ct = default) =>
        await NullableAsync<ForkInfo>($"{_root}/tasks/{taskId}/fork", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(
        int taskId, CancellationToken ct = default) =>
        await ListAsync<BranchOption>($"{_root}/tasks/{taskId}/branch-options", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ConvergenceOption>> GetConvergenceOptionsAsync(
        int taskId, CancellationToken ct = default) =>
        await ListAsync<ConvergenceOption>(
            $"{_root}/tasks/{taskId}/convergence-options", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SubWorkflowOptionView>> GetSubWorkflowOptionsAsync(
        int taskId, CancellationToken ct = default) =>
        await ListAsync<SubWorkflowOptionView>(
            $"{_root}/tasks/{taskId}/sub-workflow-options", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<AdHocOption>> GetAdHocOptionsAsync(
        int taskId, CancellationToken ct = default) =>
        await ListAsync<AdHocOption>($"{_root}/tasks/{taskId}/ad-hoc-options", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<SubWorkflowInstanceView>> GetSubWorkflowInstancesAsync(
        int runId, CancellationToken ct = default) =>
        await ListAsync<SubWorkflowInstanceView>(
            $"{_root}/runs/{runId}/sub-workflow-instances", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default) =>
        await ListAsync<ActorOption>($"{_root}/actors", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PlannedStepView>> GetPlannedStepsAsync(
        int runId, CancellationToken ct = default) =>
        await ListAsync<PlannedStepView>($"{_root}/runs/{runId}/planned-steps", ct).ConfigureAwait(false);

    public async Task<RunnerResult> PreAssignAsync(
        int runId, int taskDefinitionId, string assignToActorId, string? branchKey,
        string actorId, CancellationToken ct = default) =>
        await PostAsync(
            $"{_root}/runs/{runId}/pre-assignments/{taskDefinitionId}",
            new { actorId = assignToActorId, branchKey },
            ct).ConfigureAwait(false);

    public async Task<RunnerResult> RemovePreAssignmentAsync(
        int runId, int taskDefinitionId, string actorId, CancellationToken ct = default) =>
        await DeleteAsync($"{_root}/runs/{runId}/pre-assignments/{taskDefinitionId}", ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<BranchOption>> GetOrgUnitsAsync(CancellationToken ct = default) =>
        await ListAsync<BranchOption>($"{_root}/org-units", ct).ConfigureAwait(false);

    // ───────────────────────────────── Writes ─────────────────────────────────

    public async Task<RunnerResult> CompleteAsync(
        int taskId, string outcomeKey, string actorId, string? notes, CancellationToken ct = default) =>
        await PostAsync($"{_root}/tasks/{taskId}/complete", new { outcomeKey, notes }, ct)
            .ConfigureAwait(false);

    public async Task<RunnerResult> CancelAsync(
        int taskId, string actorId, string? note, CancellationToken ct = default) =>
        await PostAsync($"{_root}/tasks/{taskId}/cancel", new { note }, ct).ConfigureAwait(false);

    public async Task<RunnerResult> ReassignAsync(
        int taskId, string? toActorId, string? toBranchKey, string actorId, string? note,
        CancellationToken ct = default) =>
        await PostAsync(
            $"{_root}/tasks/{taskId}/reassign",
            new { actorId = toActorId, branchKey = toBranchKey, note },
            ct).ConfigureAwait(false);

    public async Task<RunnerResult> UpdateNotesAsync(
        int taskId, string? notes, string actorId, CancellationToken ct = default) =>
        await PostAsync($"{_root}/tasks/{taskId}/notes", new { notes }, ct).ConfigureAwait(false);

    public async Task<RunnerResult> AddAdHocAsync(
        int parentTaskId, int taskDefinitionId, string actorId, string? notes,
        CancellationToken ct = default) =>
        await PostAsync(
            $"{_root}/tasks/{parentTaskId}/ad-hoc", new { taskDefinitionId, notes }, ct)
            .ConfigureAwait(false);

    public async Task<RunnerResult> ForkAsync(
        int taskId, IReadOnlyList<string> branchKeys, int convergenceTaskDefinitionId,
        string actorId, string? notes, CancellationToken ct = default) =>
        await PostAsync(
            $"{_root}/tasks/{taskId}/fork",
            new { branchKeys, convergenceTaskDefinitionId, notes },
            ct).ConfigureAwait(false);

    public async Task<RunnerResult> AddBranchesAsync(
        Guid forkGroupId, IReadOnlyList<string> branchKeys, string actorId, string? notes,
        CancellationToken ct = default) =>
        await PostAsync(
            $"{_root}/forks/{forkGroupId}/branches", new { branchKeys, notes }, ct)
            .ConfigureAwait(false);

    public async Task<RunnerResult> CompleteWithSelectiveRejectionAsync(
        int convergenceTaskId, string outcomeKey, IReadOnlyList<string> rejectedBranchKeys,
        string actorId, string? notes, CancellationToken ct = default) =>
        await PostAsync(
            $"{_root}/tasks/{convergenceTaskId}/complete-selective",
            new { outcomeKey, rejectedBranchKeys, notes },
            ct).ConfigureAwait(false);

    public async Task<RunnerResult> StartSubWorkflowAsync(
        int parentTaskId, int subWorkflowDefinitionId, string actorId,
        string? assignToActorId, string? assignToBranchKey, string? notes,
        CancellationToken ct = default) =>
        await PostAsync(
            $"{_root}/tasks/{parentTaskId}/sub-workflows",
            new
            {
                subWorkflowDefinitionId,
                assignedActorId = assignToActorId,
                assignedBranchKey = assignToBranchKey,
                notes,
            },
            ct).ConfigureAwait(false);

    public async Task<RunnerResult> CancelRunAsync(
        int runId, string actorId, string? reason, CancellationToken ct = default) =>
        await PostAsync($"{_root}/runs/{runId}/cancel", new { reason }, ct).ConfigureAwait(false);

    public async Task<RunnerResult> CancelSubWorkflowAsync(
        int instanceId, string actorId, string? reason, CancellationToken ct = default) =>
        await PostAsync(
            $"{_root}/sub-workflow-instances/{instanceId}/cancel", new { reason }, ct)
            .ConfigureAwait(false);

    public async Task<TestRunResult> StartTestRunAsync(
        int workflowDefinitionVersionId, string actorId, CancellationToken ct = default)
    {
        var response = await http
            .PostAsync($"{_root}/test-runs/{workflowDefinitionVersionId}", content: null, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return TestRunResult.Failed(await DescribeAsync(response).ConfigureAwait(false));
        }

        return await response.Content.ReadFromJsonAsync<TestRunResult>(ct).ConfigureAwait(false)
            ?? TestRunResult.Failed("The server returned an empty response.");
    }

    public async Task<RunnerResult> DeleteTestRunAsync(
        int runId, string actorId, CancellationToken ct = default)
    {
        var response = await http
            .DeleteAsync($"{_root}/test-runs/{runId}", ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return RunnerResult.Failed(await DescribeAsync(response).ConfigureAwait(false));
        }

        return await response.Content.ReadFromJsonAsync<RunnerResult>(ct).ConfigureAwait(false)
            ?? RunnerResult.Failed("The server returned an empty response.");
    }

    // ───────────────────────────────── Plumbing ─────────────────────────────────

    /// <summary>
    /// A read that degrades to empty, matching <c>EfWorkflowRunnerClient</c>'s reads.
    ///
    /// <para>A run view missing one panel is more useful than an error page, and the
    /// in-process client already made that trade — a component must not behave differently
    /// depending on which side of a wire the engine is.</para>
    /// </summary>
    private async Task<IReadOnlyList<T>> ListAsync<T>(string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        return await response.Content.ReadFromJsonAsync<List<T>>(ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>
    /// A read where null is already an answer: no such run, or a task that is not forked.
    /// The server says 404 for both, and every other failure is also null — the caller has
    /// nowhere to put an error, and the in-process client returns null for these too.
    /// </summary>
    private async Task<T?> NullableAsync<T>(string url, CancellationToken ct) where T : class
    {
        var response = await http.GetAsync(url, ct).ConfigureAwait(false);

        return response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode
            ? null
            : await response.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A write. A transport failure becomes a failed <see cref="RunnerResult"/> rather than
    /// an exception, because that is exactly what the component already knows how to show —
    /// and "the server said no" and "the server could not be reached" are the same thing to
    /// whoever clicked the button.
    /// </summary>
    /// <summary>A delete that returns a RunnerResult, like the posts around it.</summary>
    private async Task<RunnerResult> DeleteAsync(string url, CancellationToken ct)
    {
        var response = await http.DeleteAsync(url, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return RunnerResult.Failed(await DescribeAsync(response).ConfigureAwait(false));
        }

        return await response.Content.ReadFromJsonAsync<RunnerResult>(ct).ConfigureAwait(false)
            ?? RunnerResult.Failed("The server returned an empty response.");
    }

    private async Task<RunnerResult> PostAsync<TBody>(string url, TBody body, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(url, body, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return RunnerResult.Failed(await DescribeAsync(response).ConfigureAwait(false));
        }

        return await response.Content.ReadFromJsonAsync<RunnerResult>(ct).ConfigureAwait(false)
            ?? RunnerResult.Failed("The server returned an empty response.");
    }

    /// <summary>Something a person can read, rather than a bare status code.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage response) =>
        response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "You are not signed in.",
            HttpStatusCode.Forbidden => "You are not allowed to do that.",
            HttpStatusCode.NotFound => "That is no longer there.",
            _ => $"The server refused the request ({(int)response.StatusCode} "
                 + $"{response.ReasonPhrase}).",
        };
}
