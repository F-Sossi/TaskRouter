using Microsoft.AspNetCore.Http;

namespace TaskRouter.AspNetCore;

/// <summary>
/// Which org units the current actor belongs to, as opaque branch keys.
///
/// <c>IWorkflowEngine.GetOpenTasksForActorAsync</c> takes these alongside the actor and
/// documents why: the engine has no user model and no directory, and a membership seam
/// inside it would be a third way to ask a question the host can already answer.
///
/// The endpoint asks this rather than the caller. Accepting <c>?branchKeys=</c> would let
/// anyone enumerate another org unit's unclaimed work by guessing keys — an inbox that
/// answers questions about other people's inboxes.
/// </summary>
public interface IWorkflowEndpointBranchKeyResolver
{
    Task<IReadOnlyList<string>> GetBranchKeysAsync(
        HttpContext context, string actorId, CancellationToken ct);
}

/// <summary>
/// The default: no keys.
///
/// The inbox then shows only work assigned to the actor by name, and unclaimed branch
/// work is invisible. That is the engine's documented predicate rather than a failure —
/// both halves need something to match on — but it is a puzzling first experience, so a
/// host that has an org model should implement the seam.
/// </summary>
internal sealed class NoBranchKeysResolver : IWorkflowEndpointBranchKeyResolver
{
    public Task<IReadOnlyList<string>> GetBranchKeysAsync(
        HttpContext context, string actorId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
