using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using TaskRouter.Core.Inbox;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.AspNetCore;

/// <summary>
/// "My open tasks" — the query a task-driven system lives on.
///
/// Returns the engine's <c>InboxTaskSnapshot</c>, not the <c>InboxItem</c> the demo's UI
/// consumes: resolving "ChangeRequest:42" into a document number and a URL is host knowledge this
/// package does not have. A host that wants labelled rows implements
/// <c>IWorkflowInboxClient</c> over this.
/// </summary>
internal static class InboxEndpoints
{
    internal static void MapInboxEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/inbox", async (
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            IWorkflowEndpointBranchKeyResolver branchKeys,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            // The one read that resolves an actor: an inbox with no subject is not a
            // question. This is not a gate on reading -- it is what the query is about.
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            var keys = await branchKeys.GetBranchKeysAsync(http, actorId, ct);

            return mapper.Map(await engine.GetOpenTasksForActorAsync(actorId, keys, ct));
        });

        // The same inbox, resolved. The route above returns the engine's rows with their
        // subjects still opaque -- honest, and enough for a host that knows its own
        // documents. This one returns InboxItem, which is what WorkflowInbox.razor renders:
        // subjects turned into labels and links by the host's IWorkflowSubjectResolver.
        //
        // Both are kept. A host with the engine in process wants the raw one; a WebAssembly
        // host cannot resolve subjects in a browser and needs this.
        group.MapGet("/inbox/items", async (
            HttpContext http,
            [FromQuery] string[]? orgUnits,
            [FromServices] IWorkflowInboxClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            // ?orgUnits= is honoured here, and deliberately is not on the raw /inbox route
            // above.
            //
            // The original rule was that no route takes branch keys, because guessing them
            // would enumerate another unit's unclaimed work. What changed is the use case:
            // work handed to a section by a sub-workflow waits there unclaimed, and somebody
            // covering for that section had no way to see it. The client checks what is
            // asked for against the host's own directory, so a guessed key matches nothing;
            // a host wanting per-person scoping on top implements IWorkflowInboxClient.
            //
            // Nothing asked for still means the actor's own units, so the unscoped request
            // behaves exactly as it did.
            var units = orgUnits is null or { Length: 0 } ? null : orgUnits;

            return Results.Ok(
                await client.GetInboxAsync(actorId, units, ct).ConfigureAwait(false));
        });

        // What the picker above offers, and which of them are the actor's own.
        group.MapGet("/inbox/org-units", async (
            HttpContext http,
            [FromServices] IWorkflowInboxClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client.GetOrgUnitsAsync(actorId, ct).ConfigureAwait(false));
        });
    }
}
