using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

public interface IWorkflowEngine
{
    /// <summary>Starts a run of the latest published version for a subject.</summary>
    /// <param name="subject">What the run is about.</param>
    /// <param name="workflowDefinitionId">The workflow to start. Its latest published version is used.</param>
    /// <param name="actorId">Who is starting it.</param>
    /// <param name="initialAssignment">
    /// The org unit the run belongs to, and optionally the person its first task goes to.
    ///
    /// <para>Supply this when the work belongs to an org unit from the outset — a document
    /// owned by a section, a case belonging to a team. The entry task carries it, every
    /// routed task inherits it as its starting assignment, and role keys then have something
    /// to resolve against: <see cref="IWorkflowAssignmentResolver"/> is handed this as
    /// <c>current</c>, and a resolver that maps "this role, in that org unit" to a person
    /// needs the org unit to be there.</para>
    ///
    /// <para><b>Omitting it means the run has no org unit at all</b>, and a workflow that
    /// never forks never acquires one — so a role key resolves against nothing and the
    /// resolver correctly keeps the assignment as it found it. That is the right default for
    /// a host with no org model, and a silent surprise for one that has.</para>
    ///
    /// <para>Same shape as the delegation parameter on
    /// <see cref="StartSubWorkflowAsync"/>, which does this for a chain handed to a different
    /// org unit, and as the per-branch keys <see cref="ForkTaskAsync"/> takes.</para>
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<WorkflowRunSnapshot>> StartRunAsync(
        WorkflowSubject subject,
        int workflowDefinitionId,
        string actorId,
        WorkflowAssignment? initialAssignment = null,
        CancellationToken ct = default);

    /// <summary>
    /// Starts a run on a named version. Exists so a draft can be tried out before it is
    /// published; an unpublished version is refused unless the run is marked as a test.
    /// </summary>
    /// <param name="initialAssignment">
    /// As on <see cref="StartRunAsync"/>: the org unit the run belongs to, and optionally the
    /// person its first task goes to.
    /// </param>
    Task<Result<WorkflowRunSnapshot>> StartRunOnVersionAsync(
        WorkflowSubject subject,
        int workflowDefinitionVersionId,
        string actorId,
        bool isTest = false,
        WorkflowAssignment? initialAssignment = null,
        CancellationToken ct = default);

    /// <summary>
    /// Completes a task with an outcome and routes to follow-on tasks.
    /// Status is never accepted from the caller — only an outcome — which closes the
    /// hole in the original engine where a client-supplied Status bypassed routing and triggers.
    /// </summary>
    Task<Result<Unit>> CompleteTaskAsync(
        int taskId,
        string outcomeKey,
        string actorId,
        string? notes = null,
        CancellationToken ct = default);

    Task<Result<Unit>> CancelTaskAsync(
        int taskId,
        string actorId,
        string? note = null,
        CancellationToken ct = default);

    Task<Result<Unit>> ReassignTaskAsync(
        int taskId,
        WorkflowAssignment assignment,
        string actorId,
        string? note = null,
        CancellationToken ct = default);

    /// <summary>
    /// Abandons a run: every open task cancelled, the run marked
    /// <see cref="WorkflowRunStatus.Cancelled"/>.
    ///
    /// <para>For a workflow that should not have been started, or a process that changed
    /// under work already in flight. <b>Not the same as finishing</b> — cancelling every task
    /// by hand leaves the run <see cref="WorkflowRunStatus.Completed"/>, which no report can
    /// tell from work that was actually done.</para>
    ///
    /// <para>Completed tasks keep their outcomes. Cancelling ends what is outstanding; it
    /// does not rewrite what was decided.</para>
    ///
    /// <para>Nothing stops a replacement starting on the same subject — concurrent runs are
    /// allowed — so the point of cancelling first is that the abandoned one stops appearing
    /// in inboxes and is visibly abandoned.</para>
    ///
    /// <para>Gated as <see cref="WorkflowOperation.CancelRun"/>, deliberately its own
    /// operation: ending everyone's work on a subject is a different act from cancelling one
    /// task, and a host will want a different rule for it.</para>
    /// </summary>
    /// <param name="reason">
    /// Why. Recorded against every task it cancels, because "why did this stop?" is the first
    /// question anyone asks of a cancelled run.
    /// </param>
    Task<Result<Unit>> CancelRunAsync(
        int runId,
        string actorId,
        string? reason = null,
        CancellationToken ct = default);

    Task<Result<WorkflowRunSnapshot>> GetRunAsync(int runId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkflowRunSnapshot>>> GetRunsForSubjectAsync(
        WorkflowSubject subject,
        CancellationToken ct = default);

    /// <summary>
    /// Open tasks for one actor: those assigned to them, plus unclaimed work in the org
    /// units they belong to.
    ///
    /// The host passes its own <paramref name="branchKeys"/> because org membership is
    /// host knowledge — the engine has no user model and no directory, and a membership
    /// seam would be a third way to ask a question the host can already answer.
    ///
    /// Actor and branch-key matching is done as a SQL equality comparison, so it follows
    /// the database column collation rather than C# string comparison rules — on the
    /// default collation that makes both matches case-insensitive.
    /// </summary>
    Task<Result<IReadOnlyList<InboxTaskSnapshot>>> GetOpenTasksForActorAsync(
        string actorId,
        IReadOnlyList<string> branchKeys,
        CancellationToken ct = default);

    /// <summary>Forks a task into parallel branches, one per branch key.</summary>
    Task<Result<ForkResult>> ForkTaskAsync(
        int taskId,
        IReadOnlyList<string> branchKeys,
        int convergenceTaskDefinitionId,
        string actorId,
        string? notes = null,
        CancellationToken ct = default);

    /// <summary>Completes a convergence task, sending selected branches back for rework.</summary>
    Task<Result<Unit>> CompleteWithSelectiveRejectionAsync(
        int convergenceTaskId,
        string outcomeKey,
        IReadOnlyList<string> rejectedBranchKeys,
        string actorId,
        string? notes = null,
        CancellationToken ct = default);

    /// <summary>Adds an ad-hoc task under a parent, optionally blocking it.</summary>
    Task<Result<WorkflowTaskSnapshot>> AddAdHocTaskAsync(
        int parentTaskId,
        int taskDefinitionId,
        string actorId,
        WorkflowAssignment? assignment = null,
        string? notes = null,
        CancellationToken ct = default);

    /// <summary>
    /// Starts a sub-workflow against a task, on the sub-workflow's latest published
    /// version. The sub-workflow must be attached to the task's definition.
    ///
    /// <paramref name="assignment"/> is who the chain is delegated to. Supplying it is
    /// what makes a downstream role key resolve against *their* org unit rather than
    /// the parent's; omitting it inherits the parent, as before. The actor and the
    /// branch key must describe the same person — the host is what knows the mapping.
    /// </summary>
    Task<Result<SubWorkflowInstanceSnapshot>> StartSubWorkflowAsync(
        int parentTaskId,
        int subWorkflowDefinitionId,
        string actorId,
        WorkflowAssignment? assignment = null,
        string? notes = null,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels a running sub-workflow instance and everything still open in it, leaving
    /// the parent task alone. A blocking parent becomes completable again.
    /// </summary>
    Task<Result<Unit>> CancelSubWorkflowAsync(
        int instanceId,
        string actorId,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>Sub-workflows attachable to a task, and whether each can be started now.</summary>
    Task<Result<IReadOnlyList<SubWorkflowOption>>> GetSubWorkflowOptionsAsync(
        int taskId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<SubWorkflowInstanceSnapshot>>> GetSubWorkflowInstancesAsync(
        int runId, CancellationToken ct = default);

    /// <summary>
    /// Where each of many subjects has got to, for a list view: one row per document, showing
    /// what step it is on and who has it.
    ///
    /// <para>Every other read here is single-entity, so a host with a list of documents made
    /// one call per row. This is the same question asked of a set.</para>
    ///
    /// <para>Grouped by run, because a subject may carry several workflows at once and a host
    /// showing them separately needs them separable — and named, which a bare
    /// <see cref="WorkflowTaskSnapshot"/> is not.</para>
    ///
    /// <para>Current means <see cref="WorkflowTaskStatus.NotStarted"/> or
    /// <see cref="WorkflowTaskStatus.InProgress"/>, plus — under <see cref="ForkView.Origin"/>
    /// — the <see cref="WorkflowTaskStatus.Forked"/> origin of any fork group that still has
    /// open branches. A fork whose branches have all finished is history, not position, and is
    /// excluded. Archived runs and archived tasks are excluded; test runs are not, matching
    /// <see cref="GetRunsForSubjectAsync"/>, and <see cref="SubjectWorkflowState.IsTest"/> is
    /// there so a caller can drop them.</para>
    ///
    /// <para>A subject with nothing running is absent from the result rather than present with
    /// an empty list, so a caller can tell "nothing open" from "never asked".</para>
    /// </summary>
    Task<Result<IReadOnlyDictionary<WorkflowSubject, IReadOnlyList<SubjectWorkflowState>>>>
        GetCurrentStateForSubjectsAsync(
            IReadOnlyList<WorkflowSubject> subjects,
            ForkView forkView = ForkView.Branches,
            CancellationToken ct = default);

    /// <summary>Adds branches to an active fork that has not yet converged.</summary>
    Task<Result<AddBranchResult>> AddBranchToForkAsync(
        Guid forkGroupId,
        IReadOnlyList<string> branchKeys,
        string actorId,
        string? notes = null,
        CancellationToken ct = default);

    // ── Read side: what a UI needs ──

    Task<Result<IReadOnlyList<TaskOutcomeOption>>> GetValidOutcomesAsync(
        int taskId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<TaskLogEntry>>> GetTaskLogsAsync(
        int taskId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<WorkflowTaskSnapshot>>> GetChildTasksAsync(
        int parentTaskId, CancellationToken ct = default);

    Task<Result<ForkContext>> GetForkContextAsync(int taskId, CancellationToken ct = default);

    Task<Result<ForkManifestView>> GetForkManifestAsync(
        int manifestId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<ForkManifestView>>> GetForkManifestsForRunAsync(
        int runId, CancellationToken ct = default);

    /// <summary>Edits task notes without completing it. Accepts no status or outcome.</summary>
    Task<Result<Unit>> UpdateTaskNotesAsync(
        int taskId, string? notes, string actorId, CancellationToken ct = default);

    /// <inheritdoc cref="WorkflowEngine.PreAssignAsync"/>
    Task<Result<Unit>> PreAssignAsync(
        int workflowRunId,
        int taskDefinitionId,
        WorkflowAssignment assignment,
        string actorId,
        CancellationToken ct = default);

    /// <inheritdoc cref="WorkflowEngine.RemovePreAssignmentAsync"/>
    Task<Result<Unit>> RemovePreAssignmentAsync(
        int workflowRunId,
        int taskDefinitionId,
        string actorId,
        CancellationToken ct = default);

    /// <inheritdoc cref="WorkflowEngine.GetPreAssignmentsForRunAsync"/>
    Task<Result<IReadOnlyList<PreAssignmentSnapshot>>> GetPreAssignmentsForRunAsync(
        int workflowRunId, CancellationToken ct = default);

    /// <inheritdoc cref="WorkflowEngine.GetUpcomingTasksForActorAsync"/>
    Task<Result<IReadOnlyList<UpcomingTaskSnapshot>>> GetUpcomingTasksForActorAsync(
        string actorId, CancellationToken ct = default);
}

public sealed record ForkResult(
    Guid ForkGroupId,
    int ForkManifestId,
    IReadOnlyList<WorkflowTaskSnapshot> Branches,
    int OriginTaskId);
