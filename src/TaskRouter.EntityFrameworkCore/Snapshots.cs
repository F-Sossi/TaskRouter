using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>Maps entities to the immutable snapshots handed out to triggers and conditions.</summary>
internal static class SnapshotMapper
{
    public static WorkflowTaskSnapshot ToSnapshot(this WorkflowTask t) => new(
        Id: t.Id,
        WorkflowRunId: t.WorkflowRunId,
        TaskDefinitionId: t.TaskDefinitionId,
        TaskTypeKey: t.TaskDefinition?.TaskType?.Key ?? string.Empty,
        // Same fallback rule as the inbox's Label in WorkflowEngine.Inbox.cs — if this
        // rule changes, check there too, and vice versa.
        DisplayName: t.TaskDefinition?.DisplayName
                     ?? t.TaskDefinition?.TaskType?.DisplayName
                     ?? string.Empty,
        Status: t.Status,
        OutcomeKey: t.OutcomeKey,
        AssignedToActorId: t.AssignedToActorId,
        AssignedBranchKey: t.AssignedBranchKey,
        ParentTaskId: t.ParentTaskId,
        SubWorkflowInstanceId: t.SubWorkflowInstanceId,
        IsForkOrigin: t.IsForkOrigin,
        ForkGroupId: t.ForkGroupId,
        ForkManifestId: t.ForkManifestId,
        Notes: t.Notes,
        CompletedDate: t.CompletedDate,
        DueDate: t.DueDate,
        OverdueFiredAt: t.OverdueFiredAt);

    public static WorkflowRunSnapshot ToSnapshot(this WorkflowRun run, IEnumerable<WorkflowTask> tasks) => new(
        Id: run.Id,
        Subject: run.Subject,
        WorkflowDefinitionVersionId: run.WorkflowDefinitionVersionId,
        Version: run.DefinitionVersion?.Version ?? 0,
        Status: run.Status,
        Tasks: tasks.Select(ToSnapshot).ToList(),
        IsTest: run.IsTest);
}
