using Microsoft.AspNetCore.Http;

namespace TaskRouter.AspNetCore;

/// <summary>
/// Who is making this request.
///
/// The engine takes an <c>actorId</c> on every mutating method and the authorization
/// policy decides with it, so this is the single most security-relevant thing the
/// endpoint layer does. It reads an identity; it never establishes one — authentication
/// stays the host's, and a host that maps these endpoints without a scheme gets 401 on
/// every mutation, which is the right answer rather than a gap.
/// </summary>
public interface IWorkflowEndpointActorAccessor
{
    /// <summary>
    /// The actor id, or null when the request carries none — which endpoints answer 401.
    /// </summary>
    string? GetActorId(HttpContext context);
}

/// <summary>
/// The default: a claim off the authenticated principal.
///
/// Never the body, the query string or a header. A caller may assert what to do and
/// never who is doing it, and because the request records have no <c>ActorId</c> at all,
/// that is structural rather than a rule each handler has to remember.
///
/// A <c>ClaimsPrincipal</c> can carry several <c>ClaimsIdentity</c> instances at once —
/// multi-scheme authentication and claims-enrichment middleware both add identities
/// that are not the one that authenticated the request. Only identities where
/// <c>IsAuthenticated</c> is true are consulted here, and consulted for both the gate
/// and the value: an unauthenticated secondary identity carrying an actor claim must
/// not be able to speak for the request.
/// </summary>
internal sealed class ClaimsActorAccessor(WorkflowEndpointOptions options)
    : IWorkflowEndpointActorAccessor
{
    /// <summary>
    /// Ranges the gate and the lookup over the same set of identities deliberately.
    /// Checking <c>context.User.Identity</c> (the single primary identity) and then
    /// searching <c>context.User.FindFirst</c> (every identity's claims, authenticated
    /// or not) would let a claim on an unauthenticated secondary identity pass as
    /// though it came from the one that actually authenticated — and the converse
    /// misfires closed just as easily.
    /// </summary>
    public string? GetActorId(HttpContext context) =>
        context.User.Identities
            .Where(identity => identity.IsAuthenticated)
            .Select(identity => identity.FindFirst(options.ActorClaimType)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
