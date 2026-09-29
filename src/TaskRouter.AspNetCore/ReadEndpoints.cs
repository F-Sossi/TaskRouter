using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.AspNetCore;

/// <summary>
/// The read side.
///
/// No actor resolution: reads are ungated in the engine, deliberately, and this layer
/// does not invent a second policy the in-process path would not share. See the
/// authorization spec for why, and the design spec for where the pressure will show up
/// if a host ever needs run-level visibility rules.
///
/// Responses are the engine's snapshot types serialized directly. They already are the
/// read model — a parallel set of DTOs would be two things to keep in step for no gain.
///
/// A collection read whose parent id does not exist answers 200 with an empty array
/// rather than 404, because that is what the engine returns and the two paths must not
/// disagree. Ratified 2026-08-25.
/// </summary>
internal static class ReadEndpoints
{
    internal static void MapReadEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/runs/{runId:int}", async (
            int runId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetRunAsync(runId, ct)));

        // WorkflowSubject is a class, and minimal APIs will not bind a complex type from
        // a query string -- so the two halves arrive separately and the subject is built
        // here. Deliberate, not an oversight.
        group.MapGet("/runs", async (
            string subjectType,
            string subjectId,
            IWorkflowEngine engine,
            WorkflowResultMapper mapper,
            CancellationToken ct) =>
            mapper.Map(await engine.GetRunsForSubjectAsync(
                new WorkflowSubject(subjectType, subjectId), ct)));

        group.MapGet("/runs/{runId:int}/sub-workflow-instances", async (
            int runId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetSubWorkflowInstancesAsync(runId, ct)));

        group.MapGet("/runs/{runId:int}/fork-manifests", async (
            int runId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetForkManifestsForRunAsync(runId, ct)));

        group.MapGet("/tasks/{taskId:int}/outcomes", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetValidOutcomesAsync(taskId, ct)));

        group.MapGet("/tasks/{taskId:int}/logs", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetTaskLogsAsync(taskId, ct)));

        group.MapGet("/tasks/{taskId:int}/children", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetChildTasksAsync(taskId, ct)));

        group.MapGet("/tasks/{taskId:int}/fork-context", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetForkContextAsync(taskId, ct)));

        group.MapGet("/tasks/{taskId:int}/sub-workflow-options", async (
            int taskId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetSubWorkflowOptionsAsync(taskId, ct)));

        group.MapGet("/fork-manifests/{manifestId:int}", async (
            int manifestId, IWorkflowEngine engine, WorkflowResultMapper mapper, CancellationToken ct) =>
            mapper.Map(await engine.GetForkManifestAsync(manifestId, ct)));
    }
}
