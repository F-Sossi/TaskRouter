using TaskRouter.Core.Model;

namespace TaskRouter.Core.Abstractions;

/// <summary>
/// The engine operations that can be authorized — every method on <c>IWorkflowEngine</c>
/// that changes state.
///
/// An enum rather than a method per operation on <see cref="IWorkflowAuthorizationPolicy"/>:
/// adding a gated operation is then a new member rather than a breaking change to every
/// host's implementation. Same reasoning as <c>WorkflowEventKind</c> on the trigger seam.
///
/// <para><b>Append only.</b> A host may persist or log these names, and reordering would
/// silently repoint stored values.</para>
/// </summary>
public enum WorkflowOperation
{
    /// <summary>Covers both <c>StartRunAsync</c> and <c>StartRunOnVersionAsync</c> —
    /// the same act on a different version.</summary>
    StartRun,
    CompleteTask,
    CompleteWithSelectiveRejection,
    CancelTask,
    ReassignTask,
    ForkTask,
    AddBranchToFork,
    AddAdHocTask,
    StartSubWorkflow,
    CancelSubWorkflow,
    UpdateTaskNotes,

    /// <summary>
    /// Abandoning a whole run — every open task at once.
    ///
    /// <para>Its own operation rather than a repeated <see cref="CancelTask"/>, because it is
    /// a different act: cancelling a task is doing something to one piece of work, while this
    /// ends everyone's work on the subject. A host will want a stricter rule for it, and
    /// could not express one if the two shared an operation.</para>
    ///
    /// <para>Appended, not inserted. These values are persisted by hosts.</para>
    /// </summary>
    CancelRun,

    /// <summary>
    /// Naming who will handle a step before it is reached.
    ///
    /// <para>Its own operation, deliberately. Committing somebody to work that does not exist
    /// yet is a different act from doing your own, and a host will want a different rule for
    /// it — a manager staffs, a reviewer reviews.</para>
    ///
    /// <para><b>Reassignment deliberately is not separate.</b>
    /// <see cref="ReassignTask"/> sits with completing and cancelling, because handing an
    /// existing task to somebody else is ordinary work on work that already exists. The line
    /// being drawn is between acting on real work and committing someone to work that is not
    /// there yet — not between doing a thing and delegating it.</para>
    ///
    /// <para>Appended, not inserted. These values are persisted by hosts.</para>
    /// </summary>
    PreAssignTask,
}

/// <summary>
/// What the policy gets to decide with.
///
/// <see cref="Task"/> is null exactly when the operation is
/// <see cref="WorkflowOperation.StartRun"/>, which has no task yet;
/// <see cref="Subject"/> and <see cref="WorkflowDefinitionId"/> are set exactly then and
/// null otherwise. One record rather than two context types, because a host switching on
/// the operation already knows which shape it is holding.
/// </summary>
public sealed record WorkflowAuthorizationContext(
    string ActorId,
    WorkflowTaskSnapshot? Task,
    WorkflowSubject? Subject,
    int? WorkflowDefinitionId);

/// <summary>
/// Allowed, or denied with a reason.
///
/// The reason reaches the caller in the exception message and therefore in the failed
/// <c>Result</c>. Telling somebody they may not act without saying why generates support
/// tickets; this is an internal line-of-business tool, and the org chart a reason might
/// disclose is on the wall.
/// </summary>
public sealed record WorkflowAuthorizationResult(bool IsAllowed, string? Reason)
{
    public static readonly WorkflowAuthorizationResult Allowed = new(true, null);

    public static WorkflowAuthorizationResult Denied(string reason) => new(false, reason);
}

/// <summary>
/// Whether an actor may perform an operation. Implemented by the host, consulted by the
/// engine before every mutating method does anything.
///
/// The rule is host knowledge — who may cancel a task depends on an org model the library
/// does not have — but asking has to be the engine's job, because the engine owns the only
/// chokepoint every caller passes through.
///
/// <para><b>This seam fails closed, unlike the two resolvers in <c>HostServices.cs</c>.</b>
/// <see cref="IWorkflowAssignmentResolver"/> and <see cref="IWorkflowDueDateResolver"/>
/// both document that an implementation which throws is logged and treated as null, never
/// fatal — correct for them, because the fallback is a lesser answer and work continues
/// visibly diminished. Here the fallback would be *no gate*: a policy throwing under load
/// would open every operation to everybody at the moment nobody is watching. So a policy
/// that throws <b>denies</b>, and the failure is logged.</para>
///
/// <para>Registering none is a legitimate configuration and gives the library's
/// pre-authorization behaviour: every operation permitted. It is never silent — see
/// <c>AllowAllAuthorizationPolicy</c>.</para>
/// </summary>
public interface IWorkflowAuthorizationPolicy
{
    Task<WorkflowAuthorizationResult> EvaluateAsync(
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a policy denies an operation, and surfaced to the caller as a failed
/// <c>Result</c> like every other engine refusal.
///
/// Derives from <see cref="InvalidOperationException"/> deliberately: the engine's idiom
/// is to throw one inside a <c>Try.RunAsync</c> body, so deriving leaves every existing
/// call site working untouched, while a host that wants to map a denial to HTTP 403
/// rather than 400 can catch this type specifically. The operation, actor and reason are
/// properties so a host need not parse the message.
/// </summary>
public sealed class WorkflowAuthorizationException(
    WorkflowOperation operation,
    string actorId,
    string reason)
    : InvalidOperationException(
        $"Actor '{actorId}' is not authorized to perform {operation}: {reason}")
{
    public WorkflowOperation Operation { get; } = operation;

    public string ActorId { get; } = actorId;

    public string Reason { get; } = reason;
}

/// <summary>
/// Thrown when an operation names an entity id that has no row, and surfaced to the
/// caller as a failed <c>Result</c> like every other engine refusal.
///
/// Derives from <see cref="InvalidOperationException"/> for the same reason
/// <see cref="WorkflowAuthorizationException"/> does: the engine's idiom is to throw one
/// inside a <c>Try.RunAsync</c> body, so deriving leaves every existing call site and
/// every existing test working untouched, while a host that wants to answer HTTP 404
/// rather than 400 can catch this type specifically.
///
/// <para><b>Absence only.</b> This means "there is no such row". An entity that exists
/// but is in a state the caller cannot use — an unpublished version, a completed task —
/// stays a plain <see cref="InvalidOperationException"/>. Drawing the line at row lookup
/// is what keeps the distinction mechanical rather than a judgement call at each site.
/// </para>
/// </summary>
/// <param name="entityKind">
/// What was looked for, e.g. "Task", capitalised for the message. This is display prose
/// composed freehand per call site, not a stable discriminator — the same row can be
/// described differently depending on the role it was being loaded for (a task
/// definition is "Task definition" in one place and "Convergence task definition" in
/// another). A host must not branch on it.
/// </param>
/// <param name="id">The id, stringified — a fork group is a Guid, everything else an int.</param>
public sealed class WorkflowNotFoundException(string entityKind, string id)
    : InvalidOperationException($"{entityKind} {id} not found.")
{
    /// <summary>
    /// Display prose, not a stable discriminator: the same row can be described
    /// differently depending on the role it was being loaded for, so a host must not
    /// branch on this value.
    /// </summary>
    public string EntityKind { get; } = entityKind;

    public string Id { get; } = id;
}
