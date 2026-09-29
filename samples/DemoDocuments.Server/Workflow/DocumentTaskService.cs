using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;
using TaskRouter.EntityFrameworkCore;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// The adapter layer: the original system's service shape on top of <see cref="IWorkflowEngine"/>.
///
/// This is the file to read when implementing the swap in the original system. Every method is a
/// thin translation, and the translation is always one of three kinds:
///
///   1. Subject mapping   — (documentId, documentType) becomes a WorkflowSubject.
///   2. Vocabulary        — the original system "section code" becomes the engine's opaque BranchKey.
///   3. Straight delegate — the engine already does it.
///
/// Nothing here contains workflow logic. If a method in the original system's real service does
/// contain logic, that logic either belongs in a host trigger or is a gap in the engine.
/// </summary>
public class DocumentTaskService(DemoDbContext db, IWorkflowEngine engine) : IDocumentTaskService
{
    /// <summary>Kind 1: the only place the host describes what a workflow is *about*.</summary>
    private static WorkflowSubject Subject(int documentId, DemoDocumentTypeRef type) =>
        new(type.ToString(), documentId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public async Task<Result<DocumentWorkflowStatus>> CreateInitialTasksAsync(
        int documentId, DemoDocumentTypeRef documentType, string actorId, CancellationToken ct = default)
    {
        // the original system looked up "the" active workflow for a document type. Here the host
        // picks a workflow definition explicitly, which is what allows more than one
        // workflow per document type later.
        //
        // Matched against the published version rather than the definition: subject type
        // is versioned, so an unpublished draft experimenting with it cannot change which
        // documents the live workflow answers to.
        var workflowId = await db.WorkflowDefinitionVersions
            .Where(v => v.SubjectType == documentType.ToString()
                     && v.IsPublished && v.IsLatest && !v.IsArchived)
            .OrderBy(v => v.WorkflowDefinitionId)
            .Select(v => (int?)v.WorkflowDefinitionId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (workflowId is null)
        {
            return Result<DocumentWorkflowStatus>.Fail(
                new InvalidOperationException($"No workflow defined for document type {documentType}."));
        }

        var started = await engine
            .StartRunAsync(Subject(documentId, documentType), workflowId.Value, actorId, ct: ct)
            .ConfigureAwait(false);

        return started.IsError
            ? Result<DocumentWorkflowStatus>.Fail(started.UnwrapError())
            : await GetWorkflowStatusAsync(documentId, documentType, ct).ConfigureAwait(false);
    }

    /// <summary>Kind 3: straight delegate.</summary>
    public Task<Result<Unit>> CompleteTaskAsync(
        int taskId, string outcomeKey, string actorId, string? notes = null,
        CancellationToken ct = default) =>
        engine.CompleteTaskAsync(taskId, outcomeKey, actorId, notes, ct);

    public async Task<Result<DocumentWorkflowStatus>> GetWorkflowStatusAsync(
        int documentId, DemoDocumentTypeRef documentType, CancellationToken ct = default)
    {
        var runs = await engine
            .GetRunsForSubjectAsync(Subject(documentId, documentType), ct)
            .ConfigureAwait(false);

        return runs.Map(list =>
        {
            var all = list.SelectMany(r => r.Tasks).ToList();

            // Fork origins are superseded, cancelled tasks are not actionable — both
            // are excluded so the counts mean what a user expects.
            var actionable = all
                .Where(t => !t.IsForkOrigin && t.Status != WorkflowTaskStatus.Cancelled)
                .ToList();

            var completed = actionable.Count(t => t.Status == WorkflowTaskStatus.Completed);
            var open = actionable.Count(t => t.Status is WorkflowTaskStatus.NotStarted
                                                     or WorkflowTaskStatus.InProgress);

            var overall = actionable.Count == 0 ? "Not Started"
                : completed == actionable.Count ? "Complete"
                : completed == 0 ? "Not Started"
                : "In Progress";

            return new DocumentWorkflowStatus(
                documentId, documentType.ToString(), list,
                actionable.Count, completed, open, overall);
        });
    }

    public async Task<Result<IReadOnlyList<WorkflowTaskSnapshot>>> GetTasksForDocumentAsync(
        int documentId, DemoDocumentTypeRef documentType, CancellationToken ct = default)
    {
        var runs = await engine
            .GetRunsForSubjectAsync(Subject(documentId, documentType), ct)
            .ConfigureAwait(false);

        return runs.Map(list =>
            (IReadOnlyList<WorkflowTaskSnapshot>)list.SelectMany(r => r.Tasks).ToList());
    }

    public Task<Result<IReadOnlyList<WorkflowTaskSnapshot>>> GetChildTasksAsync(
        int parentTaskId, CancellationToken ct = default) =>
        engine.GetChildTasksAsync(parentTaskId, ct);

    public Task<Result<IReadOnlyList<TaskLogEntry>>> GetLogsForTaskAsync(
        int taskId, CancellationToken ct = default) =>
        engine.GetTaskLogsAsync(taskId, ct);

    public Task<Result<IReadOnlyList<TaskOutcomeOption>>> GetValidOutcomesForTaskAsync(
        int taskId, CancellationToken ct = default) =>
        engine.GetValidOutcomesAsync(taskId, ct);

    /// <summary>Kind 2: the original system's section id becomes the engine's opaque branch key.</summary>
    public Task<Result<Unit>> ReassignTaskAsync(
        int taskId, string? actorId, string? sectionCode, string modifierId,
        string? note = null, CancellationToken ct = default) =>
        engine.ReassignTaskAsync(
            taskId, new WorkflowAssignment(actorId, sectionCode), modifierId, note, ct);

    public Task<Result<Unit>> CancelTaskAsync(
        int taskId, string actorId, string? note = null, CancellationToken ct = default) =>
        engine.CancelTaskAsync(taskId, actorId, note, ct);

    public Task<Result<Unit>> UpdateTaskNotesAsync(
        int taskId, string? notes, string actorId, CancellationToken ct = default) =>
        engine.UpdateTaskNotesAsync(taskId, notes, actorId, ct);

    public Task<Result<WorkflowTaskSnapshot>> AddAdHocTaskAsync(
        int parentTaskId, int taskDefinitionId, string actorId,
        string? notes = null, CancellationToken ct = default) =>
        engine.AddAdHocTaskAsync(parentTaskId, taskDefinitionId, actorId, notes: notes, ct: ct);

    /// <summary>
    /// Host-side query, not an engine concern: which ad-hoc definitions may be attached
    /// here. the original system additionally filtered by tech-code assignability and the caller's
    /// permissions — that filtering belongs in this layer too.
    /// </summary>
    public async Task<Result<IReadOnlyList<AdHocTaskOption>>> GetAdHocTaskTypesAsync(
        int taskId, CancellationToken ct = default) =>
        await Try.RunAsync(async () =>
        {
            var versionId = await db.WorkflowTasks
                .Where(t => t.Id == taskId)
                .Select(t => t.TaskDefinition!.WorkflowDefinitionVersionId)
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);

            if (versionId == 0)
            {
                throw new InvalidOperationException($"Task {taskId} not found.");
            }

            var options = await db.WorkflowTaskDefinitions
                .Include(d => d.TaskType)
                .AsNoTracking()
                .Where(d => d.WorkflowDefinitionVersionId == versionId && d.IsAdHoc && !d.IsArchived)
                .OrderBy(d => d.Id)
                .Select(d => new AdHocTaskOption(
                    d.Id,
                    d.DisplayName ?? d.TaskType!.DisplayName,
                    d.IsBlocking))
                .ToListAsync(ct).ConfigureAwait(false);

            return (IReadOnlyList<AdHocTaskOption>)options;
        }).ConfigureAwait(false);

    public Task<Result<ForkResult>> ForkTaskAsync(
        int taskId, IReadOnlyList<string> sectionCodes, int convergenceTaskDefinitionId,
        string actorId, string? notes = null, CancellationToken ct = default) =>
        engine.ForkTaskAsync(taskId, sectionCodes, convergenceTaskDefinitionId, actorId, notes, ct);

    public Task<Result<Unit>> CompleteWithSelectiveRejectionAsync(
        int convergenceTaskId, string outcomeKey, IReadOnlyList<string> rejectedSectionCodes,
        string actorId, string? notes = null, CancellationToken ct = default) =>
        engine.CompleteWithSelectiveRejectionAsync(
            convergenceTaskId, outcomeKey, rejectedSectionCodes, actorId, notes, ct);

    public Task<Result<AddBranchResult>> AddBranchToForkAsync(
        Guid forkGroupId, IReadOnlyList<string> sectionCodes, string actorId,
        string? notes = null, CancellationToken ct = default) =>
        engine.AddBranchToForkAsync(forkGroupId, sectionCodes, actorId, notes, ct);

    public Task<Result<ForkContext>> GetForkContextAsync(int taskId, CancellationToken ct = default) =>
        engine.GetForkContextAsync(taskId, ct);

    public Task<Result<ForkManifestView>> GetForkManifestAsync(
        int manifestId, CancellationToken ct = default) =>
        engine.GetForkManifestAsync(manifestId, ct);

    public async Task<Result<IReadOnlyList<ForkManifestView>>> GetForkManifestsForDocumentAsync(
        int documentId, DemoDocumentTypeRef documentType, CancellationToken ct = default)
    {
        var runs = await engine
            .GetRunsForSubjectAsync(Subject(documentId, documentType), ct)
            .ConfigureAwait(false);

        if (runs.IsError)
        {
            return Result<IReadOnlyList<ForkManifestView>>.Fail(runs.UnwrapError());
        }

        var all = new List<ForkManifestView>();

        foreach (var run in runs.Unwrap())
        {
            var manifests = await engine.GetForkManifestsForRunAsync(run.Id, ct).ConfigureAwait(false);
            if (manifests.IsError)
            {
                return manifests;
            }

            all.AddRange(manifests.Unwrap());
        }

        return Result<IReadOnlyList<ForkManifestView>>.Ok(all);
    }
}
