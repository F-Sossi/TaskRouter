using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.AspNetCore;

/// <summary>
/// Starting runs, and the operations that address something other than a single task —
/// a fork group, a sub-workflow instance.
/// </summary>
internal static class RunEndpoints
{
    internal static void MapRunEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/runs", async (
            StartRunRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.StartRunAsync(
                new WorkflowSubject(body.SubjectType, body.SubjectId),
                body.WorkflowDefinitionId,
                actorId,
                ct: ct));
        });

        group.MapPost("/runs/version", async (
            StartRunOnVersionRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.StartRunOnVersionAsync(
                new WorkflowSubject(body.SubjectType, body.SubjectId),
                body.WorkflowDefinitionVersionId,
                actorId,
                body.IsTest,
                ct: ct));
        });

        group.MapPost("/forks/{forkGroupId:guid}/branches", async (
            Guid forkGroupId,
            AddBranchRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.Map(await engine.AddBranchToForkAsync(
                forkGroupId, body.BranchKeys, actorId, body.Notes, ct));
        });

        group.MapPost("/sub-workflow-instances/{instanceId:int}/cancel", async (
            int instanceId,
            CancelSubWorkflowRequest body,
            HttpContext http,
            IWorkflowEngine engine,
            IWorkflowEndpointActorAccessor accessor,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return mapper.MapUnit(await engine.CancelSubWorkflowAsync(
                instanceId, actorId, body.Reason, ct));
        });
    }
}
