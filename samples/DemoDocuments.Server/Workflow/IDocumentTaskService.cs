using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Results;
using TaskRouter.EntityFrameworkCore;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Deliberately mirrors the shape of the original system's existing <c>IDocumentTaskService</c>.
///
/// This is the integration guide. the original system's controllers and Blazor components already
/// call an interface of roughly this shape, so keeping it means the swap is confined
/// to the implementation — <see cref="DocumentTaskService"/> — rather than rippling
/// into every caller.
///
/// The one intentional difference: there is no <c>UpdateTaskAsync(DocumentTaskDto)</c>
/// that can carry a Status. the original system's version accepted a client-supplied status and
/// completed tasks with it, bypassing routing, triggers and blocking checks (review
/// finding C3). Notes editing and completion are separate operations here.
/// </summary>
public interface IDocumentTaskService
{
    Task<Result<DocumentWorkflowStatus>> CreateInitialTasksAsync(
        int documentId, DemoDocumentTypeRef documentType, string actorId, CancellationToken ct = default);

    Task<Result<Unit>> CompleteTaskAsync(
        int taskId, string outcomeKey, string actorId, string? notes = null, CancellationToken ct = default);

    Task<Result<DocumentWorkflowStatus>> GetWorkflowStatusAsync(
        int documentId, DemoDocumentTypeRef documentType, CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkflowTaskSnapshot>>> GetTasksForDocumentAsync(
        int documentId, DemoDocumentTypeRef documentType, CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkflowTaskSnapshot>>> GetChildTasksAsync(
        int parentTaskId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<TaskLogEntry>>> GetLogsForTaskAsync(
        int taskId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<TaskOutcomeOption>>> GetValidOutcomesForTaskAsync(
        int taskId, CancellationToken ct = default);

    Task<Result<Unit>> ReassignTaskAsync(
        int taskId, string? actorId, string? sectionCode, string modifierId,
        string? note = null, CancellationToken ct = default);

    Task<Result<Unit>> CancelTaskAsync(
        int taskId, string actorId, string? note = null, CancellationToken ct = default);

    Task<Result<Unit>> UpdateTaskNotesAsync(
        int taskId, string? notes, string actorId, CancellationToken ct = default);

    Task<Result<WorkflowTaskSnapshot>> AddAdHocTaskAsync(
        int parentTaskId, int taskDefinitionId, string actorId,
        string? notes = null, CancellationToken ct = default);

    Task<Result<IReadOnlyList<AdHocTaskOption>>> GetAdHocTaskTypesAsync(
        int taskId, CancellationToken ct = default);

    Task<Result<ForkResult>> ForkTaskAsync(
        int taskId, IReadOnlyList<string> sectionCodes, int convergenceTaskDefinitionId,
        string actorId, string? notes = null, CancellationToken ct = default);

    Task<Result<Unit>> CompleteWithSelectiveRejectionAsync(
        int convergenceTaskId, string outcomeKey, IReadOnlyList<string> rejectedSectionCodes,
        string actorId, string? notes = null, CancellationToken ct = default);

    Task<Result<AddBranchResult>> AddBranchToForkAsync(
        Guid forkGroupId, IReadOnlyList<string> sectionCodes, string actorId,
        string? notes = null, CancellationToken ct = default);

    Task<Result<ForkContext>> GetForkContextAsync(int taskId, CancellationToken ct = default);

    Task<Result<ForkManifestView>> GetForkManifestAsync(int manifestId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<ForkManifestView>>> GetForkManifestsForDocumentAsync(
        int documentId, DemoDocumentTypeRef documentType, CancellationToken ct = default);
}

/// <summary>Alias so the interface reads like the original system's without importing the enum here.</summary>
public readonly record struct DemoDocumentTypeRef(Domain.DemoDocumentType Value)
{
    public static implicit operator DemoDocumentTypeRef(Domain.DemoDocumentType v) => new(v);
    public override string ToString() => Value.ToString();
}

/// <summary>Document-level view assembled from one or more workflow runs.</summary>
public sealed record DocumentWorkflowStatus(
    int DocumentId,
    string DocumentType,
    IReadOnlyList<WorkflowRunSnapshot> Runs,
    int TotalTasks,
    int CompletedTasks,
    int OpenTasks,
    string OverallStatus);

public sealed record AdHocTaskOption(int TaskDefinitionId, string DisplayName, bool IsBlocking);
