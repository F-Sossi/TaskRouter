using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TaskRouter.AspNetCore;

/// <summary>
/// The one place an actor id enters the endpoint layer.
///
/// Every handler that resolves an actor calls <see cref="Resolve"/> and none touches
/// the accessor directly — that includes read handlers, wherever they resolve one, not
/// only writes — so the reserved-prefix check below cannot be forgotten by a route
/// added later. That is the whole reason this exists rather than the check living in
/// <see cref="ClaimsActorAccessor"/>: a host may replace the accessor, and the guard
/// must outlive the replacement.
/// </summary>
internal static class WorkflowEndpointActor
{
    /// <summary>
    /// Ids the engine reserves for itself. <c>WorkflowActors.System</c> short-circuits
    /// the authorization gate <i>before</i> the policy is consulted — which is what lets
    /// the deadline sweeper act without every host policy having to allow it, and what
    /// makes the id a root switch if it ever arrives from outside.
    ///
    /// The whole prefix is refused rather than the one id in use today, so a future
    /// reserved id is covered the day it is introduced rather than the day someone
    /// remembers this file.
    ///
    /// Matched case-insensitively. The engine's own gate compares this id with ordinal
    /// <c>==</c>, but every other view of an actor id is SQL-mediated — assignment
    /// matching, the <c>CreatorId</c>/<c>ModifierId</c> audit columns — and the
    /// database's collation compares case-insensitively. An ordinal guard here would
    /// leave "WORKFLOW:SYSTEM" free to impersonate the engine in every audit trail with
    /// no local signal that anything was wrong, and no warning if the engine's own
    /// comparison is ever relaxed to match.
    /// </summary>
    private const string ReservedPrefix = "workflow:";

    /// <summary>
    /// Resolves the actor, or returns the response that refuses the request.
    ///
    /// Null means <paramref name="actorId"/> is usable. Returning the failure rather
    /// than a bool keeps the null-state analysable at the call site, and matches the
    /// idiom the demo host already uses for its own guard.
    /// </summary>
    internal static IResult? Resolve(
        HttpContext http,
        IWorkflowEndpointActorAccessor accessor,
        out string actorId)
    {
        actorId = string.Empty;

        var candidate = accessor.GetActorId(http)?.Trim();

        if (string.IsNullOrEmpty(candidate))
        {
            return Results.Problem(
                detail: "The request carries no workflow actor.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Not authenticated");
        }

        if (candidate.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // The caller here *is* authenticated -- they are only claiming an id they
            // may not have -- so this stays 403, not 401. Truncated because the claim
            // is attacker-influenced and an unbounded value (a CRLF, say) could forge
            // lines in a console or file log sink; the response detail below is fine
            // unbounded because ProblemDetails is JSON-encoded.
            http.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(WorkflowEndpointActor))
                .LogWarning(
                    "Refused a request whose actor id '{ActorId}' uses the reserved '{Prefix}' prefix.",
                    Truncate(candidate, 64),
                    ReservedPrefix);

            return Results.Problem(
                detail: $"Actor id '{candidate}' is reserved for the engine.",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Reserved actor id");
        }

        actorId = candidate;

        return null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
}
