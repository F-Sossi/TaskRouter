using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.Core.Runner;

/// <summary>
/// What the runner UI needs from the host.
///
/// The sibling of <c>IWorkflowBuilderClient</c>, and separate for the same reason: a
/// Blazor Server host implements it against the engine in-process, a WebAssembly host
/// implements it over HTTP, and the components care about neither.
///
/// The point of this being one component rather than two is that the builder's "try it"
/// pane and whatever a host puts on a document page are the same surface. If they were
/// separate the preview would drift from what users actually see, which would make
/// testing a workflow in the builder worth less than nothing.
/// </summary>
public interface IWorkflowRunnerClient
{
    /// <summary>Runs against a subject, newest first.</summary>
    Task<IReadOnlyList<RunView>> GetRunsAsync(
        string subjectType, string subjectId, CancellationToken ct = default);

    /// <summary>
    /// One run, with per-task detail for <paramref name="actorId"/> specifically:
    /// <see cref="RunTaskView.CanComplete"/> on each returned task is derived from the
    /// host's <c>IWorkflowAuthorizationPolicy</c> evaluated for *this* actor, so the same
    /// run rendered for two different actors can offer the Complete button on different
    /// tasks.
    /// </summary>
    Task<RunDetail?> GetRunAsync(int runId, string actorId, CancellationToken ct = default);

    /// <summary>Outcomes this task's definition allows. Never a fixed list — an outcome
    /// is a data row on the task definition.</summary>
    Task<IReadOnlyList<OutcomeOption>> GetOutcomesAsync(int taskId, CancellationToken ct = default);

    Task<IReadOnlyList<LogEntryView>> GetLogAsync(int taskId, CancellationToken ct = default);

    /// <summary>
    /// Fork state around a task: the branch counts, and each branch with whatever it
    /// decided. Null when the task has nothing to do with a fork.
    /// </summary>
    Task<ForkInfo?> GetForkInfoAsync(int taskId, CancellationToken ct = default);

    /// <summary>
    /// Org units a task can be forked across. Branch keys are opaque to the engine, so
    /// only the host can say what the choices are — sections here, whatever the host calls
    /// them there.
    /// </summary>
    Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(int taskId, CancellationToken ct = default);

    /// <summary>
    /// Where a fork of this task could converge. Restricted to the run's own pinned
    /// version, because the engine refuses a convergence task from any other.
    /// </summary>
    Task<IReadOnlyList<ConvergenceOption>> GetConvergenceOptionsAsync(
        int taskId, CancellationToken ct = default);

    /// <summary>
    /// Sub-workflows attachable to a task, and whether each can be started right now.
    /// </summary>
    Task<IReadOnlyList<SubWorkflowOptionView>> GetSubWorkflowOptionsAsync(
        int taskId, CancellationToken ct = default);

    /// <summary>
    /// Starts a sub-workflow, delegated to an org unit and optionally to a person in it.
    ///
    /// <para><b>The org unit is the part that matters.</b> Every role in the sub-workflow —
    /// its reviews, its approvals — resolves against the unit the chain belongs to, so
    /// delegating to a person alone left the whole sub-workflow resolving against whatever
    /// unit that person happened to sit in. Naming the unit is how work is handed to a
    /// section; naming a person as well is how it is handed to somebody in particular.</para>
    ///
    /// <para>Both null means inherit the parent task's assignment, which is what starting a
    /// sub-workflow meant before either could be chosen. <paramref name="assignToActorId"/>
    /// with no unit falls back to that person's own, which is what the host's directory
    /// says.</para>
    /// </summary>
    Task<RunnerResult> StartSubWorkflowAsync(
        int parentTaskId, int subWorkflowDefinitionId, string actorId,
        string? assignToActorId, string? assignToBranchKey, string? notes,
        CancellationToken ct = default);

    /// <summary>Sub-workflow instances in a run, so the runner can show and cancel them.</summary>
    Task<IReadOnlyList<SubWorkflowInstanceView>> GetSubWorkflowInstancesAsync(
        int runId, CancellationToken ct = default);

    /// <summary>Abandons a running instance, leaving its parent task alone.</summary>
    Task<RunnerResult> CancelSubWorkflowAsync(
        int instanceId, string actorId, string? reason, CancellationToken ct = default);

    /// <summary>Task definitions that may be added ad-hoc under a task.</summary>
    Task<IReadOnlyList<AdHocOption>> GetAdHocOptionsAsync(int taskId, CancellationToken ct = default);

    /// <summary>Actors the host can assign to, for the reassign picker.</summary>
    Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every org unit the host has, for the reassign picker.
    ///
    /// <para>Deliberately not <see cref="GetBranchOptionsAsync"/>, which answers a different
    /// question: that one is scoped to a task and removes the units a fork has already
    /// branched on, because the engine rejects a duplicate branch. Reassignment is not
    /// forking — moving a task to a unit is legal whether or not some other branch is also
    /// there — so filtering by fork state here would hide valid choices.</para>
    ///
    /// <para>The pair mirrors <see cref="GetActorsAsync"/>: the host's list, unfiltered, for
    /// a picker. Without it the runner asked a person to type a branch key, which is an
    /// opaque host id — a row id, in the first host that used this — so the dialog was
    /// asking for "3" from somebody who thinks in the unit's name.</para>
    /// </summary>
    Task<IReadOnlyList<BranchOption>> GetOrgUnitsAsync(CancellationToken ct = default);

    Task<RunnerResult> CompleteAsync(
        int taskId, string outcomeKey, string actorId, string? notes, CancellationToken ct = default);

    Task<RunnerResult> CancelAsync(int taskId, string actorId, string? note, CancellationToken ct = default);

    Task<RunnerResult> ReassignAsync(
        int taskId, string? toActorId, string? toBranchKey, string actorId, string? note,
        CancellationToken ct = default);

    Task<RunnerResult> UpdateNotesAsync(
        int taskId, string? notes, string actorId, CancellationToken ct = default);

    Task<RunnerResult> AddAdHocAsync(
        int parentTaskId, int taskDefinitionId, string actorId, string? notes,
        CancellationToken ct = default);

    /// <summary>Splits a task into one parallel branch per key, converging at the given
    /// task definition.</summary>
    Task<RunnerResult> ForkAsync(
        int taskId, IReadOnlyList<string> branchKeys, int convergenceTaskDefinitionId,
        string actorId, string? notes, CancellationToken ct = default);

    /// <summary>Adds branches to a fork that has not converged yet.</summary>
    Task<RunnerResult> AddBranchesAsync(
        Guid forkGroupId, IReadOnlyList<string> branchKeys, string actorId, string? notes,
        CancellationToken ct = default);

    /// <summary>
    /// Completes a convergence task, sending the named branches back for rework and
    /// accepting the rest.
    ///
    /// Separate from <see cref="CompleteAsync"/> because the engine refuses a rejection
    /// outcome through the ordinary path: it has no way to know which branches were the
    /// problem, and re-forking all of them would discard work that was already accepted.
    /// </summary>
    Task<RunnerResult> CompleteWithSelectiveRejectionAsync(
        int convergenceTaskId, string outcomeKey, IReadOnlyList<string> rejectedBranchKeys,
        string actorId, string? notes, CancellationToken ct = default);

    /// <summary>
    /// Starts a run for trying a workflow out. Marked as a test so hosts can keep these
    /// out of inboxes and reports, and allowed on an unpublished draft — testing before
    /// publishing is the whole point.
    /// </summary>
    Task<TestRunResult> StartTestRunAsync(
        int workflowDefinitionVersionId, string actorId, CancellationToken ct = default);

    /// <summary>
    /// Abandons a run: every open task cancelled, the run marked Cancelled.
    ///
    /// <para>Distinct from <see cref="DeleteTestRunAsync"/>, which destroys a test run
    /// outright. This keeps everything — a cancelled run is part of the record of what
    /// happened to its subject.</para>
    /// </summary>
    Task<RunnerResult> CancelRunAsync(
        int runId, string actorId, string? reason, CancellationToken ct = default);

    /// <summary>
    /// Every step of the run's workflow, with who holds it and who is promised it.
    ///
    /// <para>The planning view: a manager looks at the whole chain, not just the task in
    /// front of them, and says who will pick up steps nobody has reached yet.</para>
    /// </summary>
    Task<IReadOnlyList<PlannedStepView>> GetPlannedStepsAsync(
        int runId, CancellationToken ct = default);

    /// <summary>Names who will handle a step of this run. Replaces any previous answer.</summary>
    Task<RunnerResult> PreAssignAsync(
        int runId, int taskDefinitionId, string assignToActorId, string? branchKey,
        string actorId, CancellationToken ct = default);

    /// <summary>Drops a standing pre-assignment; the step falls back to its role.</summary>
    Task<RunnerResult> RemovePreAssignmentAsync(
        int runId, int taskDefinitionId, string actorId, CancellationToken ct = default);

    /// <summary>Discards a test run and everything under it. Refuses a real run.</summary>
    Task<RunnerResult> DeleteTestRunAsync(int runId, string actorId, CancellationToken ct = default);
}

// ─────────────────────────────── Views ───────────────────────────────

/// <param name="PercentComplete">
/// How far the run has got, <b>0 to 100</b> — a percentage, not a fraction.
///
/// <para>Said out loud because the difference is invisible at a glance and wrong by two
/// orders of magnitude: rendering this with a format that multiplies by 100 put "8,000%" on
/// a run that was eighty per cent done.</para>
/// </param>
public sealed record RunView(
    int Id,
    string SubjectType,
    string SubjectId,
    string WorkflowName,
    int Version,
    WorkflowRunStatus Status,
    bool IsTest,
    DateTime Created,
    int OpenTaskCount,
    double PercentComplete);

public sealed record RunDetail(
    RunView Run,
    IReadOnlyList<RunTaskView> Tasks);

/// <summary>
/// A task as the runner shows it: what it is, who has it, what can be done to it.
///
/// <see cref="CanComplete"/> is decided by the host rather than the component, because
/// whether someone may act on a task is an authorization question and the rule belongs to
/// the host's org model. The engine does not decide it, but it does <b>enforce</b> it: a
/// host registering an <c>IWorkflowAuthorizationPolicy</c> has the same rule applied to
/// every mutating engine method, so a host that derives this from that policy cannot show
/// a button the engine will then refuse.
/// </summary>
/// <param name="DueDate">When this task is due, in <b>UTC</b>, or null for no deadline.</param>
/// <param name="RunningSubWorkflowCount">Sub-workflow instances running against this task.</param>
/// <param name="BlockingSubWorkflowCount">
/// Of those, the ones that stop it completing. A separate count because the runner
/// must disable Complete for exactly these, and <see cref="CanComplete"/> cannot
/// carry it: <see cref="CanComplete"/> is host policy about *who may act*, while
/// blocking is an engine rule about whether the action is legal at all. Folding one
/// into the other would oblige every host to reimplement the rule, and a host that
/// forgot would get the engine's exception back.
/// </param>
/// <param name="AssignedBranchLabel">
/// What <c>AssignedBranchKey</c> is called, from the host's directory, or null if it could not
/// be resolved. Branch keys are opaque and often ids, so rendering one raw puts a bare number
/// in front of somebody. Appended, like everything else added to this record.
/// </param>
/// <param name="OverdueFiredAt">
/// When an escalation was raised for this task, in <b>UTC</b>, or null if none was.
///
/// Appended rather than filed next to <see cref="DueDate"/>: this record is the seam
/// a second host implements for itself, so a field inserted mid-list breaks anyone
/// constructing it positionally — silently, if the inserted type happens to match its
/// new neighbour. New fields go on the end.
/// </param>
/// <param name="SubWorkflowInstanceId">
/// The delegated chain this task belongs to, or null for a task of the workflow itself.
///
/// <para>A sub-workflow's tasks live in the <b>same run</b> as the task that spawned them,
/// so without this a renderer cannot tell them apart and lists them intermixed with the
/// main chain — which is what the runner did, leaving delegated work looking like steps of
/// the workflow that delegated it.</para>
/// </param>
public sealed record RunTaskView(
    int Id,
    int TaskDefinitionId,
    string TaskTypeKey,
    string Label,
    WorkflowTaskStatus Status,
    string? OutcomeKey,
    string? AssignedToActorId,
    string? AssignedToDisplayName,
    string? AssignedBranchKey,
    DateTime? DueDate,

    string? Notes,
    int? ParentTaskId,
    bool IsAdHoc,
    bool IsBlocking,
    bool IsForkOrigin,
    bool IsForkable,
    int RunningSubWorkflowCount,
    int BlockingSubWorkflowCount,
    bool HasSubWorkflowOptions,
    bool IsConvergencePoint,
    Guid? ForkGroupId,
    DateTime Created,
    DateTime? CompletedDate,
    bool CanComplete,
    DateTime? OverdueFiredAt,
    string? AssignedBranchLabel = null,
    int? SubWorkflowInstanceId = null);

/// <summary>
/// An outcome a task may be completed with. <see cref="IsRework"/> marks one that routes
/// backwards — on a convergence task those must go through selective rejection, so the
/// UI has to be able to tell them apart before offering a button that would be refused.
/// </summary>
public sealed record OutcomeOption(string OutcomeKey, string DisplayName, int Order, bool IsRework = false);

public sealed record ConvergenceOption(int TaskDefinitionId, string Label, bool IsDeclaredConvergencePoint);

public sealed record ForkInfo(
    bool IsPartOfFork,
    Guid? ForkGroupId,
    bool IsConvergenceTask,
    int? ForkManifestId,
    int CompletedBranchCount,
    int CancelledBranchCount,
    int PendingBranchCount,
    IReadOnlyList<ForkBranchView> Branches);

/// <summary>
/// One branch of a fork. While the fork is running this reflects the live task; once it
/// has converged the outcome and notes come from the manifest, which is the record of
/// what each branch decided and the thing selective rejection is chosen from.
/// </summary>
public sealed record ForkBranchView(
    string BranchKey,
    int? TaskId,
    WorkflowTaskStatus? Status,
    string? OutcomeKey,
    string? AssignedToActorId,
    string? AssignedToDisplayName,
    string? Notes);

// ActorOption and BranchOption used to be declared here. They moved to
// TaskRouter.Core.Abstractions, because IWorkflowActorResolver returns them and a host seam
// must not depend on a namespace named for a UI surface -- a directory of people is not a
// runner concept.

public sealed record AdHocOption(int TaskDefinitionId, string Label);

public sealed record SubWorkflowOptionView(
    int SubWorkflowDefinitionId, string Name, bool IsBlocking, bool CanStart);

/// <summary>A delegated chain hanging off a task, and whether it is still holding the
/// task up.</summary>
public sealed record SubWorkflowInstanceView(
    int Id, int ParentTaskId, string Name, bool IsBlocking, bool IsRunning);

/// <summary>
/// One step of a workflow as a planning screen sees it: what it is, who has it, who is
/// promised it.
/// </summary>
/// <param name="Status">
/// The task's status, or null when the run has not reached this step — which is exactly when
/// it can be pre-assigned.
/// </param>
/// <param name="CanPreAssign">
/// Whether this step accepts a pre-assignment. False for a forkable step, which gives each
/// unit its own copy, and for one that has already started, which is a reassignment. The
/// engine refuses both; this stops the screen offering them.
/// </param>
/// <param name="AssignedDate">
/// When the task was created, in <b>UTC</b>, or null if the run has not reached this step.
///
/// <para>Creation rather than assignment, and the difference matters if a step is later
/// handed to somebody else: this is when the step landed, not when its current holder got
/// it. The task's log is where the reassignments are.</para>
/// </param>
/// <param name="CompletedDate">When it finished, in <b>UTC</b>, or null while it is open.</param>
public sealed record PlannedStepView(
    int TaskDefinitionId,
    string Label,
    WorkflowTaskStatus? Status,
    string? AssignedToActorId,
    string? AssignedToDisplayName,
    string? PreAssignedActorId,
    string? PreAssignedDisplayName,
    string? PreAssignedBranchKey,
    bool CanPreAssign,
    DateTime? AssignedDate = null,
    DateTime? CompletedDate = null);

public sealed record LogEntryView(
    int Id, string Action, string? Detail, string ActorId, string? ActorDisplayName, DateTime Created);

/// <summary>
/// Success or a message. The engine returns a Result carrying an exception; the runner
/// only needs to know whether to refresh or to show something went wrong, and the
/// component should never be handed an exception to render.
/// </summary>
public sealed record RunnerResult(bool Success, string? Error)
{
    public static readonly RunnerResult Ok = new(true, null);

    public static RunnerResult Failed(string error) => new(false, error);
}

public sealed record TestRunResult(bool Success, int RunId, string? Error)
{
    public static TestRunResult Started(int runId) => new(true, runId, null);

    public static TestRunResult Failed(string error) => new(false, 0, error);
}
