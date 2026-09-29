namespace TaskRouter.Core.Model;

/// <summary>
/// Lifecycle state of a single task. Deliberately closed: the engine owns these,
/// and transitions are validated by the engine rather than accepted from callers.
/// (the original engine's review finding C3: UpdateTaskAsync accepted a client-supplied Status,
/// letting a caller complete a task while bypassing routing and triggers.)
/// </summary>
public enum WorkflowTaskStatus
{
    NotStarted = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3,
    /// <summary>Superseded by a fork; terminal and not actionable.</summary>
    Forked = 4
}

public enum WorkflowRunStatus
{
    Running = 0,
    Completed = 1,
    Cancelled = 2
}

public enum SubWorkflowStatus
{
    Running = 0,
    Completed = 1,
    Cancelled = 2
}

/// <summary>
/// Engine lifecycle events that triggers may subscribe to. Closed by design — the
/// engine owns its own lifecycle. Hosts raise their own domain events via
/// <see cref="Custom"/> plus a CustomEventName.
/// </summary>
public enum WorkflowEventKind
{
    RunStarted,
    RunCompleted,
    TaskCreated,
    TaskAssigned,
    TaskCompleted,
    TaskCancelled,
    TaskForked,
    BranchAdded,
    BranchCompleted,
    ForkConverged,
    SelectiveRejection,
    SubWorkflowStarted,
    SubWorkflowCompleted,
    Custom,

    /// <summary>
    /// A sub-workflow instance was abandoned rather than finished — either on its own or
    /// because its parent task was cancelled. Distinct from
    /// <see cref="SubWorkflowCompleted"/> on purpose: a trigger reacting to delegated work
    /// ending usually cares which of the two happened, and reusing Completed would tell it
    /// the work was done.
    ///
    /// Appended rather than filed beside its siblings: these values are persisted as ints
    /// on TriggerDefinition and TriggerExecution, so inserting one mid-list renumbers every
    /// member after it and silently rewrites the meaning of stored rows.
    /// </summary>
    SubWorkflowCancelled,

    /// <summary>
    /// A task is inside its reminder lead time and has not been completed. The only event
    /// in this enum that is raised because <b>nobody acted</b> — every other one comes from
    /// an engine operation somebody invoked, so this is the one dispatched by the sweeper
    /// rather than by <c>FireAsync</c> inside a command.
    ///
    /// Appended, like SubWorkflowCancelled above it and for the same reason: these values
    /// are persisted as ints on TriggerDefinition and TriggerExecution, so inserting one
    /// mid-list renumbers every member after it and silently rewrites the meaning of
    /// stored rows.
    /// </summary>
    TaskDueSoon,

    /// <summary>
    /// A task's deadline has passed and it is still open. The second of the two events
    /// raised because <b>nobody acted</b>, and like <see cref="TaskDueSoon"/> it is swept
    /// for rather than dispatched from inside a command.
    ///
    /// Unlike TaskDueSoon it needs no configuration to fire. A reminder has a lead time
    /// that is both its switch and its window; overdue has no window — it is the deadline
    /// passing — so a switch would be a switch and nothing else, and a task that is late
    /// is late whether or not somebody ticked a box. What happens next is entirely the
    /// host's: the engine has no notion of a supervisor and no escalation ladder.
    ///
    /// Appended, like TaskDueSoon before it and for the same reason: these values are
    /// persisted as ints on TriggerDefinition and TriggerExecution, so inserting one
    /// mid-list renumbers every member after it and silently rewrites the meaning of
    /// stored rows.
    /// </summary>
    TaskOverdue,

    /// <summary>
    /// A run was abandoned rather than finished.
    ///
    /// <para>Distinct from <see cref="RunCompleted"/> on purpose: a trigger that reacts to a
    /// workflow ending almost always cares which of the two happened, and reusing
    /// <c>RunCompleted</c> would tell it the work was done.</para>
    ///
    /// <para>Appended for the reason given above.</para>
    /// </summary>
    RunCancelled
}

/// <summary>
/// When a trigger runs relative to the engine transaction.
/// InTransaction is the only mode the original engine had, which meant a
/// notification trigger could fire and then survive a rolled-back transaction.
/// </summary>
public enum TriggerDispatchMode
{
    InTransaction = 0,
    AfterCommit = 1,
    Background = 2
}

/// <summary>Whether a failing trigger aborts the operation that raised it.</summary>
public enum TriggerFailurePolicy
{
    LogAndContinue = 0,
    FailOperation = 1
}

public enum TriggerExecutionStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>Parameter kinds a trigger can declare, so the builder UI can render a form.</summary>
public enum TriggerParameterKind
{
    Text,
    MultilineText,
    Integer,
    Decimal,
    Boolean,
    Choice,
    TaskDefinitionRef,
    RoleRef,
    VariableRef,
    Url,
    Template
}

public enum VariableType
{
    String = 0,
    Number = 1,
    Boolean = 2,
    Date = 3,
    Json = 4
}
