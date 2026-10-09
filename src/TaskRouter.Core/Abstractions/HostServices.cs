using TaskRouter.Core.Model;

namespace TaskRouter.Core.Abstractions;

/// <summary>
/// The host's directory of people and org units.
///
/// <para>The engine stores only opaque ids and never models a user entity — the system this
/// was extracted from carried a User navigation property on its base class and a fixed-length
/// id format, neither of which a general library can impose. So every question about who
/// somebody is, or what org units exist, comes back here.</para>
///
/// <para><b>Every method fails soft.</b> An implementation that throws is logged and treated
/// as no answer: an empty picker, a null name, an unassigned assignment. A directory that is
/// down should degrade a drop-down, not take down a run view. This matches
/// <see cref="IWorkflowAssignmentResolver"/> and <see cref="IWorkflowDueDateResolver"/>;
/// authorization is the one seam that fails closed instead.</para>
/// </summary>
public interface IWorkflowActorResolver
{
    /// <summary>What an actor id is called, or null if the host cannot say.</summary>
    Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct = default);

    /// <summary>
    /// Actors that may be assigned to, for the reassign and delegate pickers.
    ///
    /// <para>Every actor the host is willing to show, unfiltered by the current task — the
    /// engine applies no rule of its own to this list, so a host that wants to restrict who
    /// appears does it here.</para>
    /// </summary>
    Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every org unit a fork could branch across.
    ///
    /// <para><b>Return them all.</b> The engine removes the ones a given fork has already
    /// branched on, because that is a question about fork state rather than about the host's
    /// org chart. A host that filters here as well is reimplementing a rule the engine
    /// enforces anyway — and one it would get an exception for getting wrong, at fork
    /// time.</para>
    /// </summary>
    Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every org unit this actor can see work in.
    ///
    /// <para>Separate from <see cref="GetAssignmentForAsync"/>, and the difference is not
    /// cosmetic. Delegating a task needs exactly <i>one</i> unit — work goes to one place.
    /// An inbox needs <i>all</i> of them, because "unclaimed work waiting for my section to
    /// pick up" is a different question for somebody who covers three sections than for
    /// somebody who covers one.</para>
    ///
    /// <para>Empty is a real answer, not a broken one: it means nothing unclaimed is theirs.
    /// Work assigned to them by name arrives regardless.</para>
    /// </summary>
    Task<IReadOnlyList<string>> GetOrgUnitsForAsync(
        string actorId, CancellationToken ct = default);

    /// <summary>
    /// An actor's org unit, as a complete assignment.
    ///
    /// <para>Actor and branch key travel together everywhere else — the engine inherits them
    /// as a pair — and an actor set without their org unit is what makes a downstream role
    /// key resolve against the wrong one. Returning
    /// <see cref="WorkflowAssignment.Unassigned"/> for an unknown actor is a real answer and
    /// not an error: the assignment resolver already treats a missing branch key as "keep the
    /// current assignment".</para>
    /// </summary>
    Task<WorkflowAssignment> GetAssignmentForAsync(
        string? actorId, CancellationToken ct = default);
}

/// <summary>
/// What the host's subjects are called and where they live.
///
/// <para>The engine stores an opaque <c>(type, id)</c> pair and can say nothing more about
/// it. It knows a run is about <c>ChangeRequest:42</c>; only the host knows that is
/// "CR-2026-0042, Pump room rewire" and that it lives at <c>documents/42</c>.</para>
///
/// <para><b>Batched deliberately.</b> A per-subject signature would make N+1 the default for
/// every host that implements it — an inbox with forty rows would issue forty queries. One
/// call, one query.</para>
///
/// <para>Fails soft, like the directory: a resolver that throws leaves rows labelled with
/// their raw keys rather than emptying the list.</para>
/// </summary>
public interface IWorkflowSubjectResolver
{
    /// <summary>
    /// Describes each subject. A subject absent from the returned dictionary, or present
    /// with null fields, is one the host could not resolve — the caller falls back to the raw
    /// key and renders it unclickable. An orphaned task is a defect worth seeing, not worth
    /// hiding.
    /// </summary>
    Task<IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor>> ResolveAsync(
        IReadOnlyList<WorkflowSubject> subjects, CancellationToken ct = default);
}

/// <summary>
/// Decides who a task goes to. Replaces a hardcoded switch over org-role enum values
/// in the original system; the role key here is opaque and the host interprets it.
/// </summary>
public interface IWorkflowAssignmentResolver
{
    Task<WorkflowAssignment> ResolveAsync(
        string? roleKey,
        WorkflowAssignment current,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default);
}

/// <summary>
/// When a task is due. The engine has no opinion — a deadline belongs to whatever the
/// run is about, and only the host knows that a run on ChangeRequest:42 inherits that ChangeRequest's date.
///
/// Returning null means "no deadline", which is the honest answer for most tasks and the
/// default behaviour of the whole feature.
///
/// Failure policy matches <see cref="IWorkflowAssignmentResolver"/>: a resolver that
/// throws is logged and treated as null, never fatal. The original engine's review finding M8 was that a
/// silent fallback made a misrouted assignment undiagnosable; the same reasoning applies
/// to a date.
/// </summary>
public interface IWorkflowDueDateResolver
{
    Task<DateTime?> ResolveAsync(
        WorkflowSubject subject,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default);
}

/// <summary>
/// Evaluates a named routing condition. The original engine hardcoded five of these as substring
/// matches against free-text notes.
/// </summary>
public interface IRouteConditionEvaluator
{
    string ConditionKey { get; }
    bool Evaluate(WorkflowTaskSnapshot task, IReadOnlyDictionary<string, string?> variables);
}

/// <summary>Where computed progress goes. The engine calculates it; the host stores it.</summary>
public interface IWorkflowProgressSink
{
    /// <param name="subject">What the run is about.</param>
    /// <param name="branchKey">The branch this number is for, or null for the whole run.</param>
    /// <param name="percentComplete">The engine's figure. Store it; do not recompute it.</param>
    /// <param name="actorId">
    /// Whose action produced the number — the person who completed or cancelled the task, or
    /// <see cref="WorkflowActors.System"/> when the deadline sweep did. A host writing to
    /// audited storage needs this, and it is not necessarily one of that host's own users:
    /// expect the system id and map it rather than writing it through to a user column.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task ReportAsync(
        WorkflowSubject subject,
        string? branchKey,
        double percentComplete,
        string actorId,
        CancellationToken ct = default);
}

/// <summary>Outbound notifications. Implemented by the host; invoked after commit.</summary>
public interface IWorkflowNotificationSink
{
    Task SendAsync(WorkflowNotification notification, CancellationToken ct = default);
}

/// <param name="Subject">What the run is about.</param>
/// <param name="TaskId">The task the notification concerns.</param>
/// <param name="RecipientActorId">Who it is for, or null if the trigger named nobody.</param>
/// <param name="Subject_">The notification's own subject line.</param>
/// <param name="Body">Its body.</param>
/// <param name="ActorId">
/// Whose action produced it — the person who acted, or <see cref="WorkflowActors.System"/>
/// for the deadline sweep. Distinct from <paramref name="RecipientActorId"/>: one is who
/// caused it, the other is who receives it. A host recording a sender needs the first, and
/// must expect an id that is not one of its users.
/// </param>
public sealed record WorkflowNotification(
    WorkflowSubject Subject,
    int TaskId,
    string? RecipientActorId,
    string Subject_,
    string Body,
    string ActorId);

/// <summary>
/// Background dispatch. The library defines the contract and ships no job framework;
/// a host might implement this with Hangfire, the sample uses an in-memory runner.
/// </summary>
public interface IWorkflowJobQueue
{
    Task EnqueueAsync(WorkflowJob job, CancellationToken ct = default);
}

public sealed record WorkflowJob(
    string TriggerKey,
    int TriggerDefinitionId,
    int TaskId,
    WorkflowEventKind Event,
    string IdempotencyKey);
