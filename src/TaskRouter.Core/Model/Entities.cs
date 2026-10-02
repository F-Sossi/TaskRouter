namespace TaskRouter.Core.Model;

/// <summary>
/// Base for every persisted workflow entity.
/// Deliberately minimal: no User navigation property, no fixed-length actor id, no
/// GraphQL or grid attributes. The original engine's base entity transitively imported
/// all of those, which is what made the engine unpackageable.
/// </summary>
public abstract class WorkflowEntity
{
    public int Id { get; set; }
    public string CreatorId { get; set; } = string.Empty;
    public string ModifierId { get; set; } = string.Empty;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    public bool IsArchived { get; set; }
}

/// <summary>
/// What a workflow run is about. Opaque to the engine — a document, a work order,
/// an onboarding case, a piece of equipment. The host maps its own keys onto this.
/// </summary>
public sealed class WorkflowSubject : IEquatable<WorkflowSubject>
{
    public WorkflowSubject() { }

    public WorkflowSubject(string subjectType, string subjectId)
    {
        SubjectType = subjectType;
        SubjectId = subjectId;
    }

    /// <summary>Host discriminator, e.g. "ChangeRequest", "WorkOrder".</summary>
    public string SubjectType { get; set; } = string.Empty;

    /// <summary>Host key, stringified. Ints, GUIDs and composite codes all fit.</summary>
    public string SubjectId { get; set; } = string.Empty;

    public bool Equals(WorkflowSubject? other) =>
        other is not null && SubjectType == other.SubjectType && SubjectId == other.SubjectId;

    public override bool Equals(object? obj) => Equals(obj as WorkflowSubject);
    public override int GetHashCode() => HashCode.Combine(SubjectType, SubjectId);
    public override string ToString() => $"{SubjectType}:{SubjectId}";
}

// ─────────────────────────────── Definition side ───────────────────────────────

/// <summary>
/// A task type is a data row, not an enum value. The original engine used a 55-value C# enum,
/// which a general library cannot ship and which forced one node per type per workflow.
/// </summary>
public class TaskTypeDefinition : WorkflowEntity
{
    public required string Key { get; set; }          // stable, e.g. "section-review"
    public required string DisplayName { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Who will handle a step of a run, named before the step is reached.
///
/// <para>A standing instruction about one run, not a token that gets consumed: a step
/// re-created by rework goes to the same person again. Removing the row lets the step fall
/// back to its assignment role.</para>
///
/// <para><b>Per run, not per definition.</b> The next document does not inherit it — this is
/// a manager staffing one piece of work, not editing the workflow. And it cannot live in a
/// host: the assignment resolver is not called for a step with no role key, and when it is
/// called it is told about the task that just <i>completed</i> rather than the one being
/// created, so it cannot tell which step it is assigning.</para>
/// </summary>
public class PreAssignment : WorkflowEntity
{
    public required int WorkflowRunId { get; set; }
    public WorkflowRun? Run { get; set; }

    /// <summary>The step. Must belong to the run's own version.</summary>
    public required int TaskDefinitionId { get; set; }
    public WorkflowTaskDefinition? TaskDefinition { get; set; }

    public required string ActorId { get; set; }

    /// <summary>
    /// Optional. Null keeps whatever org unit the run is in, which is the normal case —
    /// naming a person does not usually mean moving the work to another unit.
    /// </summary>
    public string? BranchKey { get; set; }
}

/// <summary>
/// An outcome an author can give a task: the organisation's list of ways work can end.
///
/// <para><b>Not to be confused with <see cref="TaskOutcomeDefinition"/></b>, whose name is
/// unhelpfully similar. That one is an outcome <i>on a particular task of a particular
/// version</i> — the buttons a person sees on that step. This is the catalogue those are
/// chosen from.</para>
///
/// <para>Before this existed, outcomes were only the per-task rows: an author retyped
/// <c>approved</c> on every task that needed it, and a typo produced a step whose outcome
/// nothing routes on. One database had both <c>approved</c> and <c>Approved</c> in it.</para>
///
/// <para><b>Deliberately not a foreign key from <see cref="TaskOutcomeDefinition"/>.</b> A
/// published version is immutable and self-contained — that is what lets a run pin to it —
/// so choosing an outcome copies the key into the version rather than pointing at a row that
/// somebody may later rename. The catalogue decides what authors can pick, not what running
/// workflows mean.</para>
/// </summary>
public class OutcomeTypeDefinition : WorkflowEntity
{
    /// <summary>
    /// Stable, e.g. <c>approved</c>. Lower-cased when created, because this key used to be
    /// compared in SQL where case was the database collation's business and in C# where it
    /// was not — the same key could match in one place and not the other.
    /// </summary>
    public required string Key { get; set; }

    public required string DisplayName { get; set; }
    public string? Description { get; set; }
}

/// <summary>The logical workflow. Concrete behaviour lives in its versions.</summary>
public class WorkflowDefinition : WorkflowEntity
{
    /// <summary>
    /// The workflow's identity, shared by every version. Renaming a workflow renames
    /// it everywhere, which is the intent: the name identifies the logical workflow,
    /// not one revision of it.
    /// </summary>
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Marks a definition meant to be attached to a task rather than started against a
    /// subject.
    ///
    /// A sub-workflow is an ordinary workflow with this flag set, not a separate kind of
    /// thing. The original design proposed a distinct SubWorkflowDefinition, but that was
    /// written before versions existed here — a parallel definition side would duplicate
    /// tasks, outcomes, routes, triggers, the validator and the builder, and would leave
    /// sub-workflows unversioned while mainline workflows were pinned. Reusing the
    /// definition means a sub-workflow is built in the same editor and its instances pin
    /// to a published version the same way runs do.
    /// </summary>
    public bool IsSubWorkflow { get; set; }

    public ICollection<WorkflowDefinitionVersion> Versions { get; set; } = [];
}

/// <summary>
/// Which sub-workflows may hang off which mainline task.
///
/// A join row rather than a copy of the sub-graph: attaching one sub-workflow to five
/// mainline tasks is five rows, which is the whole reason sub-workflows are worth having
/// over ad-hoc chains.
/// </summary>
public class SubWorkflowAttachment : WorkflowEntity
{
    /// <summary>The <see cref="WorkflowDefinition"/> to instantiate. Must have
    /// <see cref="WorkflowDefinition.IsSubWorkflow"/> set.</summary>
    public required int SubWorkflowDefinitionId { get; set; }
    public WorkflowDefinition? SubWorkflowDefinition { get; set; }

    /// <summary>
    /// The version this attachment belongs to. The scope: an attachment is authored
    /// against one version and does not leak into another, exactly as routes and
    /// triggers do not.
    /// </summary>
    public required int WorkflowDefinitionVersionId { get; set; }
    public WorkflowDefinitionVersion? DefinitionVersion { get; set; }

    /// <summary>
    /// The mainline task definition it may hang off, or null for any task in the
    /// version. Null is the common case — "available at any point" — and means a task
    /// added later inherits it rather than silently lacking it.
    /// </summary>
    public int? TaskDefinitionId { get; set; }
    public WorkflowTaskDefinition? TaskDefinition { get; set; }

    /// <summary>Spawn as soon as the parent task is created, rather than on request.</summary>
    public bool IsAutomatic { get; set; }

    /// <summary>Whether the parent task must wait for the instance to finish.</summary>
    public bool IsBlocking { get; set; } = true;

    /// <summary>Whether several instances may run against one parent task at once.</summary>
    public bool AllowMultiple { get; set; }
}

/// <summary>
/// One run of a sub-workflow, hanging off a parent task.
///
/// Its tasks are ordinary <see cref="WorkflowTask"/> rows in the parent's run, carrying
/// <see cref="WorkflowTask.SubWorkflowInstanceId"/>. There is no second engine and no
/// second run: the same routing, fork and convergence code drives them, which is what
/// keeps this a model change rather than a parallel implementation.
/// </summary>
public class SubWorkflowInstance : WorkflowEntity
{
    /// <summary>Pinned at spawn, exactly as a run pins to a version. Editing the
    /// sub-workflow cannot reroute instances already running.</summary>
    public required int SubWorkflowDefinitionVersionId { get; set; }
    public WorkflowDefinitionVersion? DefinitionVersion { get; set; }

    /// <summary>The task it hangs off.</summary>
    public required int ParentTaskId { get; set; }
    public WorkflowTask? ParentTask { get; set; }

    /// <summary>Denormalised from the parent so instances can be found per run without
    /// walking back through tasks.</summary>
    public required int WorkflowRunId { get; set; }

    public SubWorkflowStatus Status { get; set; } = SubWorkflowStatus.Running;

    /// <summary>Copied from the attachment at spawn, so changing the attachment later
    /// cannot unblock or block work already in flight.</summary>
    public bool IsBlocking { get; set; }

    public DateTime? CompletedDate { get; set; }
}

/// <summary>
/// An immutable published snapshot of a workflow. Runs pin to a version, so editing
/// a workflow can never change the routing of work already in flight.
/// The original engine had no versioning at all: CreateOrUpdateWorkflowAsync merged task definitions
/// in place on rows that in-flight tasks referenced.
/// </summary>
public class WorkflowDefinitionVersion : WorkflowEntity
{
    public required int WorkflowDefinitionId { get; set; }
    public WorkflowDefinition? WorkflowDefinition { get; set; }

    public required int Version { get; set; }

    /// <summary>
    /// What this version applies to; null means any subject. Versioned rather than held
    /// on the definition because it is behavioural, not descriptive — hosts select a
    /// workflow to start by matching it against a subject type. Editing it on a draft
    /// must not change what the published version answers to, and a new version is
    /// entitled to apply to something the old one did not.
    /// </summary>
    public string? SubjectType { get; set; }

    /// <summary>
    /// When a step names nobody — no pre-assignment for the run, no assignment role of its
    /// own — should it go to whoever held the previous step, or to nobody?
    ///
    /// <para><b>Default false: to nobody.</b> The task arrives unassigned and sits in its
    /// section's unclaimed work until somebody takes it. True restores the older behaviour,
    /// where an unroled step silently follows whoever finished the one before it — right
    /// for a workflow a single person shepherds end to end, wrong for most others.</para>
    ///
    /// <para>Only the person is dropped. The org unit is carried forward either way,
    /// because the inbox shows unclaimed work exclusively to members of the task's own unit
    /// — a task with neither would be visible to nobody, which is work lost rather than
    /// work waiting.</para>
    ///
    /// <para>Versioned rather than held on the definition for the same reason as
    /// <see cref="SubjectType"/>: it is behavioural, and editing it on a draft must not
    /// change how runs already pinned to the published version behave.</para>
    /// </summary>
    public bool CarryAssignmentForward { get; set; }

    public bool IsPublished { get; set; }
    public bool IsLatest { get; set; }
    public DateTime? PublishedAt { get; set; }

    /// <summary>Explicit entry point. The original engine inferred it as "no inbound routes", which
    /// diverged from its own validator and breaks outright for sub-workflows.</summary>
    public int? EntryTaskDefinitionId { get; set; }

    public ICollection<WorkflowTaskDefinition> Tasks { get; set; } = [];

    /// <summary>Sub-workflows attachable within this version. On the version rather
    /// than the task because an attachment may apply to any task in it.</summary>
    public ICollection<SubWorkflowAttachment> SubWorkflowAttachments { get; set; } = [];
}

public class WorkflowTaskDefinition : WorkflowEntity
{
    public required int WorkflowDefinitionVersionId { get; set; }
    public WorkflowDefinitionVersion? WorkflowDefinitionVersion { get; set; }

    public required int TaskTypeDefinitionId { get; set; }
    public TaskTypeDefinition? TaskType { get; set; }

    /// <summary>Per-definition label, so two nodes of the same task type are
    /// distinguishable in the builder. Falls back to the task type's display name.</summary>
    public string? DisplayName { get; set; }

    public bool IsRequired { get; set; } = true;
    public bool IsAdHoc { get; set; }
    public bool IsBlocking { get; set; }
    public bool IsForkable { get; set; }
    public bool IsConvergencePoint { get; set; }
    public bool IsTerminal { get; set; }

    /// <summary>Opaque role key resolved by the host's IWorkflowAssignmentResolver.
    /// The original system hardcoded an org-role enum here, which is why this is a
    /// string the host defines rather than a fixed set the library ships.</summary>
    public string? AssignmentRoleKey { get; set; }

    /// <summary>
    /// How far ahead of <see cref="WorkflowTask.DueDate"/> to nudge the assignee, in
    /// minutes. Null means this task type never nudges, which is the default.
    ///
    /// Minutes rather than a <see cref="TimeSpan"/> deliberately: EF Core maps TimeSpan
    /// to SQL Server <c>time(7)</c>, which represents a time of day and caps at 24 hours,
    /// so every lead time of a day or more would silently truncate or throw. The builder
    /// collects days and stores minutes.
    /// </summary>
    public int? ReminderLeadTimeMinutes { get; set; }

    public ICollection<TaskOutcomeDefinition> ValidOutcomes { get; set; } = [];
    public ICollection<TaskRoute> OutgoingRoutes { get; set; } = [];
    public ICollection<TriggerDefinition> Triggers { get; set; } = [];
}

/// <summary>
/// One outcome of one task, in one version — a button on that step.
///
/// <para>Distinct from <see cref="OutcomeTypeDefinition"/>, which is the catalogue these are
/// chosen from. The key is copied rather than referenced, so renaming a catalogue entry
/// cannot change what a published workflow means.</para>
/// </summary>
public class TaskOutcomeDefinition : WorkflowEntity
{
    public required int TaskDefinitionId { get; set; }
    public WorkflowTaskDefinition? TaskDefinition { get; set; }

    public required string OutcomeKey { get; set; }   // "approved", "rejected"
    public required string DisplayName { get; set; }
    public int Order { get; set; }
}

/// <summary>
/// A routing edge. Targets a task *definition*, not a task type — the single change
/// that lets one workflow contain the same task type more than once, which is what
/// reusable sub-workflows require.
/// </summary>
public class TaskRoute : WorkflowEntity
{
    public required int TaskDefinitionId { get; set; }
    public WorkflowTaskDefinition? TaskDefinition { get; set; }

    /// <summary>
    /// The declared outcome this route fires on — a row of the same task, by id.
    ///
    /// <para>This was the outcome <i>key</i> until 2026-09-22, and matching it was a string
    /// comparison. Three things were wrong with that. A route could name an outcome the task
    /// did not declare, and only the validator caught it. Renaming an outcome silently
    /// stopped every route on it from firing. And the comparison happened in SQL, so whether
    /// <c>Approved</c> matched <c>approved</c> depended on the database's collation — while
    /// the same comparison in a host's route condition, in C#, was ordinal. The same key
    /// could match in one place and not another.</para>
    ///
    /// <para>Safe to key on an id because a published version is an immutable deep copy:
    /// drafting a new version re-materialises every task, outcome and route as new rows, so
    /// these ids never change under a run that is pinned to the version.</para>
    ///
    /// <para><b>Ids do not travel between versions</b>, which is why the outcome <i>key</i>
    /// survives on <see cref="WorkflowTask.OutcomeKey"/> and in trigger conditions: the key is
    /// the only identity an outcome has across versions, and that is what a report or a
    /// condition needs.</para>
    /// </summary>
    public required int TaskOutcomeDefinitionId { get; set; }
    public TaskOutcomeDefinition? Outcome { get; set; }

    public required int NextTaskDefinitionId { get; set; }
    public WorkflowTaskDefinition? NextTaskDefinition { get; set; }

    /// <summary>Optional condition key resolved against registered evaluators.</summary>
    public string? ConditionKey { get; set; }

    public bool IsDefault { get; set; }
    public int Order { get; set; }

    /// <summary>
    /// Marks a route that sends work backwards for rework. Making this explicit avoids
    /// hardcoding an outcome name: the original engine special-cased the literal "Rejected" outcome
    /// on convergence tasks, which only worked because that name was fixed by an enum.
    /// </summary>
    public bool IsReworkRoute { get; set; }
}

public class TriggerDefinition : WorkflowEntity
{
    public required int TaskDefinitionId { get; set; }
    public WorkflowTaskDefinition? TaskDefinition { get; set; }

    /// <summary>Registry key, e.g. "workflow.webhook". A string, not an enum, so
    /// consumers can register triggers the library has never heard of.</summary>
    public required string TriggerKey { get; set; }

    public required WorkflowEventKind Event { get; set; }
    public string? CustomEventName { get; set; }

    /// <summary>JSON, validated against the trigger's declared descriptor at save time.</summary>
    public string? Configuration { get; set; }

    public string? Condition { get; set; }
    public int Order { get; set; }
    public bool IsActive { get; set; } = true;
    public TriggerDispatchMode DispatchMode { get; set; } = TriggerDispatchMode.InTransaction;
    public TriggerFailurePolicy FailurePolicy { get; set; } = TriggerFailurePolicy.LogAndContinue;
}

// ──────────────────────────────── Runtime side ────────────────────────────────

public class WorkflowRun : WorkflowEntity
{
    public required WorkflowSubject Subject { get; set; }

    /// <summary>Pinned at start; never repointed.</summary>
    public required int WorkflowDefinitionVersionId { get; set; }
    public WorkflowDefinitionVersion? DefinitionVersion { get; set; }

    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Running;
    public DateTime? CompletedDate { get; set; }

    /// <summary>
    /// A run started to try a workflow out rather than to do real work — the builder's
    /// test pane sets it. A flag rather than a naming convention because hosts must be
    /// able to exclude these from inboxes, counts and reports with one predicate, and a
    /// reserved subject type is something every consumer has to remember.
    /// </summary>
    public bool IsTest { get; set; }

    public ICollection<WorkflowTask> Tasks { get; set; } = [];
}

public class WorkflowTask : WorkflowEntity
{
    public required int WorkflowRunId { get; set; }
    public WorkflowRun? Run { get; set; }

    public required int TaskDefinitionId { get; set; }
    public WorkflowTaskDefinition? TaskDefinition { get; set; }

    public WorkflowTaskStatus Status { get; set; } = WorkflowTaskStatus.NotStarted;
    public string? OutcomeKey { get; set; }
    public DateTime? CompletedDate { get; set; }
    public string? Notes { get; set; }

    public string? AssignedToActorId { get; set; }
    /// <summary>Opaque org-unit key. The original engine's AssignedSectionId, generalised.</summary>
    public string? AssignedBranchKey { get; set; }

    /// <summary>
    /// When this task is due, in <b>UTC</b>, as the host's IWorkflowDueDateResolver
    /// answered it. Cached on the row rather than resolved on read so the sweeper and the
    /// inbox can filter and sort on it in SQL. The sweeper re-resolves and repairs it, so
    /// a deadline that moves after the task was created does not go stale here.
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// When the due-soon reminder fired, in <b>UTC</b>. Doubles as the fire-once stamp
    /// and the row-claim target: the sweeper's conditional update sets it only where it
    /// is still null, so two app instances sweeping at once produce one dispatch.
    /// </summary>
    public DateTime? ReminderSentAt { get; set; }

    /// <summary>
    /// When <see cref="WorkflowEventKind.TaskOverdue"/> fired for this task, in
    /// <b>UTC</b>. The same fire-once stamp and row-claim target as
    /// <see cref="ReminderSentAt"/>, for the other half of the deadline.
    ///
    /// Also read by the inbox and the runner, which is the one way it differs: a
    /// non-null value here is how a surface says "late, and somebody has been told"
    /// rather than merely "late".
    /// </summary>
    public DateTime? OverdueFiredAt { get; set; }

    public int? ParentTaskId { get; set; }
    public WorkflowTask? ParentTask { get; set; }

    public int? SubWorkflowInstanceId { get; set; }

    // Fork context
    public Guid? ForkGroupId { get; set; }
    public bool IsForkOrigin { get; set; }
    public int? ForkManifestId { get; set; }
    public ForkManifest? ForkManifest { get; set; }
    public int? ConvergenceTaskDefinitionId { get; set; }
}

public class ForkManifest : WorkflowEntity
{
    public required Guid ForkGroupId { get; set; }
    public required int WorkflowRunId { get; set; }
    public int? SubWorkflowInstanceId { get; set; }

    public required int OriginTaskId { get; set; }
    public required int ConvergenceTaskDefinitionId { get; set; }

    public ICollection<ForkManifestEntry> Entries { get; set; } = [];
}

public class ForkManifestEntry : WorkflowEntity
{
    public required int ForkManifestId { get; set; }
    public ForkManifest? ForkManifest { get; set; }

    public required string BranchKey { get; set; }
    public string? AssignedToActorId { get; set; }
    public string? BranchOutcomeKey { get; set; }
    public string? BranchNotes { get; set; }
}

/// <summary>
/// Arbitrary per-run or per-task state. Gives "track anything" somewhere to put the
/// anything, and lets route conditions evaluate real values instead of substring-matching
/// free-text notes the way the original engine's TaskRoutingService did.
/// </summary>
public class WorkflowVariable : WorkflowEntity
{
    public required int WorkflowRunId { get; set; }
    public int? TaskId { get; set; }
    public required string Name { get; set; }
    public string? Value { get; set; }
    public VariableType Type { get; set; } = VariableType.String;
}

public class WorkflowTaskLog : WorkflowEntity
{
    public required int TaskId { get; set; }
    public WorkflowTask? Task { get; set; }

    public required string Action { get; set; }
    public string? Note { get; set; }
    public required string PerformedBy { get; set; }
    public DateTime PerformedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A trigger queued to run after the transaction commits.
///
/// Written *inside* the transaction so it either exists or the whole operation rolled
/// back. Without this, an after-commit side effect — sending a notification, calling a
/// webhook — is lost whenever the process dies between commit and dispatch, and worse,
/// running such triggers inline (the original engine's only mode) means the effect survives a
/// rollback and cannot be undone.
///
/// Delivery is at-least-once; <see cref="TriggerExecution.IdempotencyKey"/> is how
/// consumers deduplicate. Exactly-once is not attempted.
/// </summary>
public class WorkflowOutboxMessage : WorkflowEntity
{
    public required int TriggerDefinitionId { get; set; }
    public required int TaskId { get; set; }
    public required WorkflowEventKind Event { get; set; }
    public string? CustomEventName { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string ActorId { get; set; }

    public TriggerDispatchMode DispatchMode { get; set; } = TriggerDispatchMode.AfterCommit;
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>Set while a worker holds this row, so two workers cannot both run it.</summary>
    public Guid? LeaseId { get; set; }
    public DateTime? LeasedUntil { get; set; }
}

public enum OutboxStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Abandoned = 3
}

/// <summary>
/// One row per trigger attempt. Replaces writing prose into the task log, which was
/// unqueryable and — per the original engine's review finding H3 — sometimes recorded failures as successes.
/// </summary>
public class TriggerExecution : WorkflowEntity
{
    public required int TriggerDefinitionId { get; set; }
    public required int TaskId { get; set; }
    public required WorkflowEventKind Event { get; set; }
    public required string IdempotencyKey { get; set; }

    public TriggerExecutionStatus Status { get; set; } = TriggerExecutionStatus.Pending;
    public string? Error { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
