using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.AspNetCore;

/// <summary>
/// Mutations against a single task.
///
/// Every handler follows the same three lines: resolve the actor through
/// <see cref="WorkflowEndpointActor"/>, call the engine, map the Result. The repetition
/// is deliberate — a helper that hid the actor resolution would hide the one step that
/// must never be skipped.
/// </summary>
internal static class TaskEndpoints
{
    internal static void MapTaskEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/tasks/{taskId:int}/complete", async (
            int taskId,
            CompleteTaskRequest body,
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

            return mapper.MapUnit(await engine.CompleteTaskAsync(
                taskId, body.OutcomeKey, actorId, body.Notes, ct));
        });

        group.MapPost("/tasks/{taskId:int}/complete-selective", async (
            int taskId,
            CompleteSelectiveRequest body,
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

            return mapper.MapUnit(await engine.CompleteWithSelectiveRejectionAsync(
                taskId, body.OutcomeKey, body.RejectedBranchKeys, actorId, body.Notes, ct));
        });

        group.MapPost("/tasks/{taskId:int}/cancel", async (
            int taskId,
            CancelTaskRequest body,
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

            return mapper.MapUnit(await engine.CancelTaskAsync(taskId, actorId, body.Note, ct));
        });

        group.MapPost("/tasks/{taskId:int}/reassign", async (
            int taskId,
            ReassignTaskRequest body,
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

            // body.ActorId is the target; actorId is the caller. Not the same person.
            return mapper.MapUnit(await engine.ReassignTaskAsync(
                taskId, body.ToAssignment(), actorId, body.Note, ct));
        });

        group.MapPut("/tasks/{taskId:int}/notes", async (
            int taskId,
            UpdateNotesRequest body,
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

            return mapper.MapUnit(await engine.UpdateTaskNotesAsync(taskId, body.Notes, actorId, ct));
        });

        group.MapPost("/tasks/{taskId:int}/fork", async (
            int taskId,
            ForkTaskRequest body,
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

            return mapper.Map(await engine.ForkTaskAsync(
                taskId, body.BranchKeys, body.ConvergenceTaskDefinitionId, actorId, body.Notes, ct));
        });

        group.MapPost("/tasks/{taskId:int}/adhoc", async (
            int taskId,
            AddAdHocTaskRequest body,
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

            return mapper.Map(await engine.AddAdHocTaskAsync(
                taskId, body.TaskDefinitionId, actorId, body.ToAssignment(), body.Notes, ct));
        });

        group.MapPost("/tasks/{taskId:int}/sub-workflows", async (
            int taskId,
            StartSubWorkflowRequest body,
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

            return mapper.Map(await engine.StartSubWorkflowAsync(
                taskId, body.SubWorkflowDefinitionId, actorId, body.ToAssignment(), body.Notes, ct));
        });
    }
}
