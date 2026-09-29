using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Runner;

/// <summary>
/// The directory a host gets if it registers none: empty answers, and one log line saying so.
///
/// <para>Empty rather than throwing, because the runner must still render. A host that has
/// not wired a directory should get a run view with an empty reassign picker, not an error
/// page — the same reasoning that makes every resolver seam fail soft.</para>
///
/// <para>And one log line rather than silence, because an empty picker looks exactly like a
/// host with nobody in it, and those two are worth telling apart. Once per process, following
/// <c>AllowAllAuthorizationPolicy</c>.</para>
/// </summary>
internal sealed class NullActorResolver(ILogger<NullActorResolver> logger) : IWorkflowActorResolver
{
    private static int _warned;

    private void WarnOnce()
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "No IWorkflowActorResolver is registered: actor names are blank, and the "
                + "reassign and fork pickers are empty. Register one with "
                + "AddTaskRouter().AddActorResolver<T>() to change this.");
        }
    }

    public Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct = default)
    {
        WarnOnce();
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default)
    {
        WarnOnce();
        return Task.FromResult<IReadOnlyList<ActorOption>>([]);
    }

    public Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(CancellationToken ct = default)
    {
        WarnOnce();
        return Task.FromResult<IReadOnlyList<BranchOption>>([]);
    }

    public Task<IReadOnlyList<string>> GetOrgUnitsForAsync(
        string actorId, CancellationToken ct = default)
    {
        WarnOnce();

        // Empty means "nothing unclaimed is yours", which is honest for a host that has
        // not described its org chart. Work assigned by name still reaches the inbox.
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task<WorkflowAssignment> GetAssignmentForAsync(
        string? actorId, CancellationToken ct = default)
    {
        WarnOnce();

        // Not Unassigned: the actor is known, only their org unit is not. Discarding the
        // actor here would silently unassign work on every path that routes through the
        // directory, which is a good deal worse than a missing branch key.
        return Task.FromResult(string.IsNullOrWhiteSpace(actorId)
            ? WorkflowAssignment.Unassigned
            : new WorkflowAssignment(actorId, null));
    }
}

/// <summary>
/// The subject resolver a host gets if it registers none: nothing resolved, and one log line.
///
/// <para>An empty dictionary is the documented "could not resolve" answer for every subject,
/// so inbox rows fall back to their raw keys and render unclickable. That is a degraded inbox
/// rather than a broken one, and it is visibly degraded — a row reading
/// <c>"ChangeRequest:42"</c> tells whoever sees it exactly what is missing.</para>
/// </summary>
internal sealed class NullSubjectResolver(ILogger<NullSubjectResolver> logger) : IWorkflowSubjectResolver
{
    private static int _warned;

    public Task<IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor>> ResolveAsync(
        IReadOnlyList<WorkflowSubject> subjects, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "No IWorkflowSubjectResolver is registered: inbox rows show raw subject keys "
                + "such as 'ChangeRequest:42' and are not clickable. Register one with "
                + "AddTaskRouter().AddSubjectResolver<T>() to change this.");
        }

        return Task.FromResult<IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor>>(
            new Dictionary<WorkflowSubject, SubjectDescriptor>());
    }
}
