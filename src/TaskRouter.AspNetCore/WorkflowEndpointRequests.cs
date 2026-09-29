using TaskRouter.Core.Abstractions;

namespace TaskRouter.AspNetCore;

// None of these carries an ActorId. That is the visible difference from the demo's
// contracts and the reason a caller cannot choose who they are: the property does not
// exist to bind, so a handler that wanted to trust one would have to add it first.

public sealed record StartRunRequest(string SubjectType, string SubjectId, int WorkflowDefinitionId);

public sealed record StartRunOnVersionRequest(
    string SubjectType, string SubjectId, int WorkflowDefinitionVersionId, bool IsTest = false);

public sealed record CompleteTaskRequest(string OutcomeKey, string? Notes = null);

public sealed record CancelTaskRequest(string? Note = null);

/// <param name="ActorId">
/// The <i>target</i> assignee — who the task is being handed to. Not the caller, who
/// comes from the principal. The demo's equivalent record has the same trap and names
/// the caller <c>ModifierId</c> to survive it.
/// </param>
public sealed record ReassignTaskRequest(string? ActorId, string? BranchKey, string? Note = null)
{
    internal WorkflowAssignment ToAssignment() => new(ActorId, BranchKey);
}

public sealed record UpdateNotesRequest(string? Notes);

public sealed record ForkTaskRequest(
    IReadOnlyList<string> BranchKeys, int ConvergenceTaskDefinitionId, string? Notes = null);

public sealed record CompleteSelectiveRequest(
    string OutcomeKey, IReadOnlyList<string> RejectedBranchKeys, string? Notes = null);

public sealed record AddAdHocTaskRequest(
    int TaskDefinitionId, string? AssignedActorId = null, string? AssignedBranchKey = null,
    string? Notes = null)
{
    internal WorkflowAssignment? ToAssignment() =>
        AssignedActorId is null && AssignedBranchKey is null
            ? null
            : new WorkflowAssignment(AssignedActorId, AssignedBranchKey);
}

public sealed record StartSubWorkflowRequest(
    int SubWorkflowDefinitionId, string? AssignedActorId = null, string? AssignedBranchKey = null,
    string? Notes = null)
{
    internal WorkflowAssignment? ToAssignment() =>
        AssignedActorId is null && AssignedBranchKey is null
            ? null
            : new WorkflowAssignment(AssignedActorId, AssignedBranchKey);
}

public sealed record CancelSubWorkflowRequest(string? Reason = null);

public sealed record AddBranchRequest(IReadOnlyList<string> BranchKeys, string? Notes = null);

/// <summary>
/// Who will handle a step that has not been reached. The actor doing the naming comes from
/// the principal, never the body — a caller says who gets the work, not who decided it.
/// </summary>
public sealed record PreAssignRequest(string ActorId, string? BranchKey = null);
