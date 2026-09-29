using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using TaskRouter.Core.Runner;

namespace TaskRouter.AspNetCore;

/// <summary>
/// The runner's operations over HTTP, for a host that cannot run
/// <see cref="IWorkflowRunnerClient"/> in process — a WebAssembly host, which has no
/// DbContext because it runs in a browser.
///
/// <para><b>These are not the engine endpoints again.</b> <see cref="TaskEndpoints"/> and
/// <see cref="RunEndpoints"/> expose <c>IWorkflowEngine</c>; these expose the runner
/// <i>client</i>, which is the engine plus the view mapping the components need — labels
/// resolved, <c>CanComplete</c> evaluated per actor, fork branches gathered. A WebAssembly
/// host cannot do that mapping itself, which is the whole reason this exists.</para>
///
/// <para><b><c>[FromServices]</c> is load-bearing, not decoration.</b> Without it, minimal
/// APIs infer an unregistered complex type as a <i>body</i> parameter — and a GET may not
/// have one, so a host that mapped these endpoints without calling <c>AddWorkflowRunner()</c>
/// would fail to start at all, with an error naming neither the runner nor the route. There
/// is a test for exactly that.</para>
///
/// <para><b>No authorization check lives here, deliberately.</b> Every mutating route maps
/// onto an engine operation the host's <c>IWorkflowAuthorizationPolicy</c> already gates, and
/// a check in this layer would be a second gate that can disagree with the first. The
/// engine's is the one that matters, because it also guards the in-process path that has no
/// endpoint at all. Like the rest of the package this does not call
/// <c>RequireAuthorization</c> either — the host applies its own policy to the group.</para>
///
/// <para>The actor comes from the authenticated principal on every route that needs one,
/// never from the body — including <c>GET /runs/{runId}</c>, because
/// <see cref="RunTaskView.CanComplete"/> is evaluated for a specific person and a caller must
/// not be able to ask "what could somebody else do".</para>
/// </summary>
internal static class RunnerEndpoints
{
    internal static void MapRunnerEndpoints(this RouteGroupBuilder group)
    {
        var runner = group.MapGroup("/runner");

        // ───────────────────────────── Reads ─────────────────────────────

        runner.MapGet("/runs", async (
            string subjectType,
            string subjectId,
            [FromServices] IWorkflowRunnerClient client,
            CancellationToken ct) =>
            Results.Ok(await client.GetRunsAsync(subjectType, subjectId, ct).ConfigureAwait(false)));

        runner.MapGet("/runs/{runId:int}", async (
            int runId,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            // A read that resolves an actor, like the inbox. CanComplete is per-person, so
            // there is no actor-free answer to give.
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            var detail = await client.GetRunAsync(runId, actorId, ct).ConfigureAwait(false);

            // 404 rather than 200 with a null body, so a client can tell "no such run" from
            // "a run with nothing in it".
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        runner.MapGet("/tasks/{taskId:int}/outcomes", async (
            int taskId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetOutcomesAsync(taskId, ct).ConfigureAwait(false)));

        runner.MapGet("/tasks/{taskId:int}/log", async (
            int taskId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetLogAsync(taskId, ct).ConfigureAwait(false)));

        runner.MapGet("/tasks/{taskId:int}/fork", async (
            int taskId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
        {
            var info = await client.GetForkInfoAsync(taskId, ct).ConfigureAwait(false);

            // Null means "this task has nothing to do with a fork", which is an ordinary
            // answer rather than a missing resource -- most tasks are not forked.
            return info is null ? Results.NotFound() : Results.Ok(info);
        });

        runner.MapGet("/tasks/{taskId:int}/branch-options", async (
            int taskId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetBranchOptionsAsync(taskId, ct).ConfigureAwait(false)));

        runner.MapGet("/tasks/{taskId:int}/convergence-options", async (
            int taskId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetConvergenceOptionsAsync(taskId, ct).ConfigureAwait(false)));

        runner.MapGet("/tasks/{taskId:int}/sub-workflow-options", async (
            int taskId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetSubWorkflowOptionsAsync(taskId, ct).ConfigureAwait(false)));

        runner.MapGet("/tasks/{taskId:int}/ad-hoc-options", async (
            int taskId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetAdHocOptionsAsync(taskId, ct).ConfigureAwait(false)));

        runner.MapGet("/runs/{runId:int}/sub-workflow-instances", async (
            int runId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetSubWorkflowInstancesAsync(runId, ct).ConfigureAwait(false)));

        runner.MapGet("/actors", async (
            [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetActorsAsync(ct).ConfigureAwait(false)));

        runner.MapGet("/runs/{runId:int}/planned-steps", async (
            int runId, [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetPlannedStepsAsync(runId, ct).ConfigureAwait(false)));

        runner.MapGet("/org-units", async (
            [FromServices] IWorkflowRunnerClient client, CancellationToken ct) =>
            Results.Ok(await client.GetOrgUnitsAsync(ct).ConfigureAwait(false)));

        // ───────────────────────────── Writes ─────────────────────────────
        //
        // Every one resolves the actor from the principal and returns the client's
        // RunnerResult as 200. A refusal is a result, not a fault: the component renders the
        // message, and a 500 would leave a user with a failed action and nothing to read.
        // The engine's authorization gate is what refuses; nothing here second-guesses it.

        runner.MapPost("/tasks/{taskId:int}/complete", async (
            int taskId,
            CompleteTaskRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .CompleteAsync(taskId, body.OutcomeKey, actorId, body.Notes, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/tasks/{taskId:int}/complete-selective", async (
            int taskId,
            CompleteSelectiveRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .CompleteWithSelectiveRejectionAsync(
                    taskId, body.OutcomeKey, body.RejectedBranchKeys, actorId, body.Notes, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/tasks/{taskId:int}/cancel", async (
            int taskId,
            CancelTaskRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .CancelAsync(taskId, actorId, body.Note, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/tasks/{taskId:int}/reassign", async (
            int taskId,
            ReassignTaskRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            // body.ActorId is who the task goes *to*; actorId is who is doing it. Two
            // different people, and conflating them is how a reassignment gets attributed
            // to its recipient.
            return Results.Ok(await client
                .ReassignAsync(taskId, body.ActorId, body.BranchKey, actorId, body.Note, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/tasks/{taskId:int}/notes", async (
            int taskId,
            UpdateNotesRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .UpdateNotesAsync(taskId, body.Notes, actorId, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/tasks/{taskId:int}/ad-hoc", async (
            int taskId,
            AddAdHocTaskRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .AddAdHocAsync(taskId, body.TaskDefinitionId, actorId, body.Notes, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/tasks/{taskId:int}/fork", async (
            int taskId,
            ForkTaskRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .ForkAsync(
                    taskId, body.BranchKeys, body.ConvergenceTaskDefinitionId,
                    actorId, body.Notes, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/forks/{forkGroupId:guid}/branches", async (
            Guid forkGroupId,
            AddBranchRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .AddBranchesAsync(forkGroupId, body.BranchKeys, actorId, body.Notes, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/tasks/{taskId:int}/sub-workflows", async (
            int taskId,
            StartSubWorkflowRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            // The org unit crosses as well as the person, since 2026-09-16.
            //
            // It used to be the actor id alone, and the client resolved that person's unit
            // through the directory, on the reasoning that a caller naming a unit could put
            // work somewhere the person is not. That is the normal case rather than the
            // abuse: delegating a chain to a section, with or without naming somebody in it,
            // is what a sub-workflow is for -- and every role inside it resolves against the
            // unit, so withholding it left whole chains resolving against the wrong section.
            return Results.Ok(await client
                .StartSubWorkflowAsync(
                    taskId, body.SubWorkflowDefinitionId, actorId,
                    body.AssignedActorId, body.AssignedBranchKey, body.Notes, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/runs/{runId:int}/pre-assignments/{taskDefinitionId:int}", async (
            int runId,
            int taskDefinitionId,
            PreAssignRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .PreAssignAsync(runId, taskDefinitionId, body.ActorId, body.BranchKey, actorId, ct)
                .ConfigureAwait(false));
        });

        runner.MapDelete("/runs/{runId:int}/pre-assignments/{taskDefinitionId:int}", async (
            int runId,
            int taskDefinitionId,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .RemovePreAssignmentAsync(runId, taskDefinitionId, actorId, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/runs/{runId:int}/cancel", async (
            int runId,
            CancelTaskRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            // Reuses CancelTaskRequest: the body is a reason either way, and a second record
            // with one nullable string on it would be two shapes for one thing.
            return Results.Ok(await client
                .CancelRunAsync(runId, actorId, body.Note, ct)
                .ConfigureAwait(false));
        });

        runner.MapPost("/sub-workflow-instances/{instanceId:int}/cancel", async (
            int instanceId,
            CancelSubWorkflowRequest body,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .CancelSubWorkflowAsync(instanceId, actorId, body.Reason, ct)
                .ConfigureAwait(false));
        });

        // ─────────────────────── Test runs ───────────────────────

        runner.MapPost("/test-runs/{versionId:int}", async (
            int versionId,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            return Results.Ok(await client
                .StartTestRunAsync(versionId, actorId, ct)
                .ConfigureAwait(false));
        });

        runner.MapDelete("/test-runs/{runId:int}", async (
            int runId,
            HttpContext http,
            [FromServices] IWorkflowRunnerClient client,
            [FromServices] IWorkflowEndpointActorAccessor accessor,
            CancellationToken ct) =>
        {
            if (WorkflowEndpointActor.Resolve(http, accessor, out var actorId) is { } failure)
            {
                return failure;
            }

            // The client refuses a real run. This is a hard delete, so that guard is the
            // whole safety of the operation and must not be duplicated or relaxed here.
            return Results.Ok(await client
                .DeleteTestRunAsync(runId, actorId, ct)
                .ConfigureAwait(false));
        });
    }
}
