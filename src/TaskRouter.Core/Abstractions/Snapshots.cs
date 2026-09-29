using TaskRouter.Core.Model;

namespace TaskRouter.Core.Abstractions;

/// <summary>
/// Immutable view of a task handed to triggers and condition evaluators.
/// Never the EF entity: the original engine passed the tracked DocumentTask straight into triggers,
/// so any consumer could mutate the change tracker arbitrarily.
/// </summary>
/// <param name="DueDate">
/// When this is due, in <b>UTC</b>, or null for no deadline. Defaulted so the
/// sixteen-argument positional constructions elsewhere in the engine keep compiling —
/// including the assignment resolver's Placeholder, where the task does not exist yet
/// and there is nothing to be due.
/// </param>
/// <param name="OverdueFiredAt">
/// When <see cref="WorkflowEventKind.TaskOverdue"/> fired for this task, in
/// <b>UTC</b>, or null if it has not. Defaulted for the same reason
/// <see cref="DueDate"/> is: the positional constructions elsewhere in the engine —
/// the assignment resolver's Placeholder among them — must keep compiling.
/// </param>
public sealed record WorkflowTaskSnapshot(
    int Id,
    int WorkflowRunId,
    int TaskDefinitionId,
    string TaskTypeKey,
    string DisplayName,
    WorkflowTaskStatus Status,
    string? OutcomeKey,
    string? AssignedToActorId,
    string? AssignedBranchKey,
    int? ParentTaskId,
    int? SubWorkflowInstanceId,
    bool IsForkOrigin,
    Guid? ForkGroupId,
    int? ForkManifestId,
    string? Notes,
    DateTime? CompletedDate,
    DateTime? DueDate = null,
    DateTime? OverdueFiredAt = null);

public sealed record WorkflowRunSnapshot(
    int Id,
    WorkflowSubject Subject,
    int WorkflowDefinitionVersionId,
    int Version,
    WorkflowRunStatus Status,
    IReadOnlyList<WorkflowTaskSnapshot> Tasks,
    bool IsTest = false);

/// <summary>Fork context, with opaque branch keys rather than the original engine's section ids.</summary>
public sealed record BranchContext(
    Guid? ForkGroupId,
    int? ForkManifestId,
    string? BranchKey,
    IReadOnlyList<string> RejectedBranchKeys);

/// <summary>Result of resolving who a task should go to.</summary>
public sealed record WorkflowAssignment(string? ActorId, string? BranchKey)
{
    public static readonly WorkflowAssignment Unassigned = new(null, null);
}
