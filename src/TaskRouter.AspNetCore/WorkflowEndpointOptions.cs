using System.Security.Claims;

namespace TaskRouter.AspNetCore;

/// <summary>
/// What the endpoint layer needs to know that is not a seam.
/// </summary>
public sealed class WorkflowEndpointOptions
{
    /// <summary>
    /// The claim the default actor accessor reads the actor id from.
    ///
    /// <see cref="ClaimTypes.NameIdentifier"/> because it is what the common
    /// authentication handlers populate. A host whose tokens carry the id elsewhere
    /// changes this rather than implementing the seam.
    /// </summary>
    public string ActorClaimType { get; set; } = ClaimTypes.NameIdentifier;
}
