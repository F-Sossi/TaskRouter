using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Asks the host's policy whether an actor may do something, and throws if not. Shared by
/// the engine and the field-update trigger so that both bypass the system actor, fail
/// closed on a throwing policy, and treat a null answer as a denial in exactly the same way.
/// </summary>
internal static class WorkflowAuthorization
{
    public static async Task EnsureAllowedAsync(
        IWorkflowAuthorizationPolicy policy,
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        ILogger logger,
        CancellationToken ct)
    {
        // The sweeper and the trigger dispatcher act under this id. The bypass lives here
        // rather than in every host's policy because a host that forgot it would break its
        // own deadline sweep, and the symptom would be tasks quietly not escalating.
        //
        // Hosts must never let this id arrive from an untrusted caller: it is checked
        // before the policy is consulted, so a request that supplies it is ungated.
        if (context.ActorId == WorkflowActors.System)
        {
            return;
        }

        WorkflowAuthorizationResult result;

        try
        {
            result = await policy
                .EvaluateAsync(operation, context, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is not OperationCanceledException and not WorkflowAuthorizationException)
        {
            // Fail closed. See IWorkflowAuthorizationPolicy: this is the opposite of the
            // rule the resolver seams document, and deliberately so.
            //
            // Two exclusions, neither of which weakens that. Cancellation is not a policy
            // failure, and reporting it as a denial would tell a host mapping this to 403
            // that an aborted request was forbidden. A policy that throws
            // WorkflowAuthorizationException has already said precisely why, and wrapping
            // it would replace that reason with a vaguer one. Both still abort the
            // transaction, so nothing proceeds ungated either way.
            //
            // The cause lives in this log line and not on the exception: the Result
            // carries a stable message for the host to show a user, and the stack trace
            // is a server-side concern.
            logger.LogError(
                ex,
                "Authorization policy threw evaluating {Operation} for actor {ActorId}; denying.",
                operation,
                context.ActorId);

            throw new WorkflowAuthorizationException(
                operation, context.ActorId, "the authorization policy failed");
        }

        if (result is null || !result.IsAllowed)
        {
            throw new WorkflowAuthorizationException(
                operation, context.ActorId, result?.Reason ?? "not permitted");
        }
    }
}
