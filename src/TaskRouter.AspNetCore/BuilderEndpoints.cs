using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using TaskRouter.Core.Builder;

namespace TaskRouter.AspNetCore;

/// <summary>
/// The builder's operations over HTTP, for a host that cannot run
/// <see cref="IWorkflowBuilderClient"/> in process — a WebAssembly host, which has no
/// DbContext because it runs in a browser.
///
/// <para><b>Results are results, not faults.</b> Validation errors and a failed save come back
/// as 200 with a body, because a builder's whole job is to show an author what is wrong with
/// their workflow. A 500 there would leave them with a broken workflow and no idea why.</para>
///
/// <para><b><c>[FromServices]</c> is load-bearing, not decoration.</b> Without it, minimal APIs
/// infer an unregistered complex type as a <i>body</i> parameter — and a GET may not have one,
/// so a host that mapped these endpoints without calling <c>AddWorkflowBuilder()</c> would fail
/// to start at all, with an error naming neither the builder nor the route. Marking the
/// parameter explicitly means an absent registration surfaces as a failure on a builder route,
/// which is what it should be: the engine endpoints are useful on their own, and upgrading must
/// not break a host that never adopted the builder. There is a test for exactly that.</para>
///
/// <para>Like the engine endpoints, this deliberately does not call
/// <c>RequireAuthorization</c>. The host applies its own policy to the returned group. Worth a
/// thought when it does: editing a workflow definition changes how every future run of it
/// behaves, which is a good deal more privileged than completing one task.</para>
/// </summary>
internal static class BuilderEndpoints
{
    internal static RouteGroupBuilder MapBuilderEndpoints(this RouteGroupBuilder group)
    {
        var builder = group.MapGroup("/builder");

        builder.MapGet("/workflows", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetWorkflowsAsync(ct).ConfigureAwait(false)));

        builder.MapGet("/workflows/{versionId:int}", async (
            int versionId, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
        {
            var model = await client.GetWorkflowAsync(versionId, ct).ConfigureAwait(false);

            // 404 rather than 200 with a null body: a builder cannot tell "no such workflow"
            // from "a workflow with nothing in it" otherwise, and the second is a thing that
            // legitimately exists.
            return model is null ? Results.NotFound() : Results.Ok(model);
        });

        builder.MapGet("/task-types", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetTaskTypesAsync(ct).ConfigureAwait(false)));

        builder.MapPost("/task-types", async (
            CreateTaskTypeRequest body, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client
                .CreateTaskTypeAsync(body.Key, body.DisplayName, ct)
                .ConfigureAwait(false)));

        builder.MapGet("/triggers", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetTriggerDescriptorsAsync(ct).ConfigureAwait(false)));

        builder.MapGet("/assignment-roles", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetAssignmentRolesAsync(ct).ConfigureAwait(false)));

        builder.MapGet("/outcome-types", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetOutcomeTypesAsync(ct).ConfigureAwait(false)));

        builder.MapPost("/outcome-types", async (
            CreateOutcomeTypeRequest body, [FromServices] IWorkflowBuilderClient client,
            CancellationToken ct) =>
            Results.Ok(await client
                .CreateOutcomeTypeAsync(body.Key, body.DisplayName, ct)
                .ConfigureAwait(false)));

        builder.MapGet("/type-inventory", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetTypeInventoryAsync(ct).ConfigureAwait(false)));

        builder.MapPost("/task-types/{taskTypeId:int}/restore", async (
            int taskTypeId, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
        {
            await client.RestoreTaskTypeAsync(taskTypeId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        builder.MapPost("/outcome-types/{outcomeTypeId:int}/restore", async (
            int outcomeTypeId, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
        {
            await client.RestoreOutcomeTypeAsync(outcomeTypeId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        builder.MapPost("/task-types/{taskTypeId:int}/archive", async (
            int taskTypeId, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
        {
            await client.ArchiveTaskTypeAsync(taskTypeId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        builder.MapPost("/outcome-types/{outcomeTypeId:int}/archive", async (
            int outcomeTypeId, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
        {
            await client.ArchiveOutcomeTypeAsync(outcomeTypeId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        builder.MapGet("/subject-types", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetSubjectTypesAsync(ct).ConfigureAwait(false)));

        builder.MapGet("/route-conditions", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetRouteConditionsAsync(ct).ConfigureAwait(false)));

        builder.MapGet("/sub-workflows", async (
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetSubWorkflowDefinitionsAsync(ct).ConfigureAwait(false)));

        builder.MapPost("/validate", async (
            WorkflowEditModel body, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.ValidateAsync(body, ct).ConfigureAwait(false)));

        builder.MapPost("/save", async (
            WorkflowEditModel body, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.SaveAsync(body, ct).ConfigureAwait(false)));

        builder.MapPost("/publish", async (
            WorkflowEditModel body, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.PublishAsync(body, ct).ConfigureAwait(false)));

        builder.MapPost("/workflows/{fromVersionId:int}/duplicate", async (
            int fromVersionId, DuplicateWorkflowRequest body,
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client
                .DuplicateWorkflowAsync(fromVersionId, body.NewName, ct)
                .ConfigureAwait(false)));

        builder.MapPost("/workflows/{fromVersionId:int}/draft", async (
            int fromVersionId, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client
                .CreateDraftVersionAsync(fromVersionId, ct)
                .ConfigureAwait(false)));

        // Returned so the host can gate this subgroup on its own terms — editing a
        // definition is a different privilege from completing a task. See
        // MapTaskRouterEndpoints' configureBuilderGroup parameter.
        return builder;
    }
}

/// <summary>Creating a task type takes two strings, which is a body rather than a route.</summary>
public sealed record CreateTaskTypeRequest(string Key, string DisplayName);

public sealed record CreateOutcomeTypeRequest(string Key, string DisplayName);

/// <summary>The new workflow's name, which is free text and so travels in a body rather
/// than the route.</summary>
public sealed record DuplicateWorkflowRequest(string NewName);
