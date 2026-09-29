namespace TaskRouter.Core.Inbox;

/// <summary>
/// What the inbox UI needs from the host.
///
/// The third of the host seams, alongside <c>IWorkflowBuilderClient</c> and
/// <c>IWorkflowRunnerClient</c>, and separate for the same reason: a Blazor Server host
/// implements it against the engine in-process, a WebAssembly host over HTTP, and the
/// component cares about neither.
///
/// This seam exists specifically because the engine cannot answer the user's actual
/// question. The engine knows a run is about <c>ChangeRequest:42</c>; only the host knows that is
/// "CR-2026-0042, Pump room rewire" and that it lives at <c>/documents/42</c>.
/// </summary>
public interface IWorkflowInboxClient
{
    /// <summary>
    /// Everything open and waiting on this actor, oldest first.
    /// </summary>
    /// <param name="orgUnits">
    /// Which org units' unclaimed work to include, or null for the ones the actor belongs to
    /// — which is what the host's directory answers, and what this did before it could be
    /// asked anything else.
    ///
    /// <para>Passing units is how somebody looks at another section's queue: work handed to
    /// a section by a sub-workflow waits there unclaimed, and a person covering for another
    /// section could not see it. Tasks assigned to the actor by name are always included
    /// whatever is passed — it is still their inbox.</para>
    ///
    /// <para><b>A unit not in the host's directory is ignored rather than honoured</b>, so a
    /// caller cannot enumerate work by guessing keys. A host needing per-person scoping
    /// beyond that implements this seam itself.</para>
    /// </param>
    Task<IReadOnlyList<InboxItem>> GetInboxAsync(
        string actorId, IReadOnlyList<string>? orgUnits = null, CancellationToken ct = default);

    /// <summary>
    /// The org units the inbox can be pointed at, and which of them are the actor's own.
    ///
    /// <para>One call rather than two because a picker needs both at once: the list to offer
    /// and the subset to start on. The component never learns what an org unit <i>is</i> —
    /// only that these have names and some belong to the person looking.</para>
    /// </summary>
    Task<IReadOnlyList<InboxOrgUnit>> GetOrgUnitsAsync(
        string actorId, CancellationToken ct = default);
}

/// <summary>
/// An org unit an inbox may be scoped to.
/// </summary>
/// <param name="IsMine">
/// Whether the actor belongs to it. Decides what the picker starts on, so that an untouched
/// inbox shows what it always showed.
/// </param>
public sealed record InboxOrgUnit(string Key, string DisplayName, bool IsMine);

/// <summary>
/// One row of the inbox, already resolved to something a person can read and click.
/// </summary>
/// <param name="SubjectLabel">
/// What the subject is called — a document number, typically. Falls back to the raw
/// subject key (<c>"ChangeRequest:9999"</c>) when the host cannot resolve it, so an orphaned task
/// stays visible rather than being silently dropped. Never empty.
/// </param>
/// <param name="SubjectUrl">
/// Where the work happens. A plain string the host builds — there is no routing
/// abstraction, because the component must not know what a document is. Empty when the
/// host cannot resolve the subject, which leaves the row visible but not clickable.
/// </param>
/// <param name="Created">
/// When the task was created, in <b>UTC</b> — it comes straight from the engine's
/// column. Compare against <see cref="DateTime.UtcNow"/>; a renderer that reaches for
/// <c>DateTime.Now</c> compiles and is silently wrong by the host's offset.
/// </param>
/// <param name="DueDate">
/// When the task is due, in <b>UTC</b>, or null for no deadline — straight from the
/// engine's column, like <paramref name="Created"/>. Compare against
/// <see cref="DateTime.UtcNow"/>.
/// </param>
/// <param name="OverdueFiredAt">
/// When an escalation was raised for this task, in <b>UTC</b>, or null if none was.
/// Lets the row say "late, and somebody has been told" rather than only "late".
/// </param>
/// <param name="BranchLabel">
/// What <paramref name="BranchKey"/> is called, from the host's directory, or null if it
/// could not be resolved.
///
/// <para>A branch key is opaque and frequently an id — the first host's are section row ids —
/// so rendering it raw put a bare "3" in front of people. Appended rather than filed beside
/// <paramref name="BranchKey"/>: this record is a seam a host may construct positionally.</para>
/// </param>
public sealed record InboxItem(
    int TaskId,
    string TaskLabel,
    string WorkflowName,
    string SubjectLabel,
    string? SubjectSubtitle,
    string SubjectUrl,
    string? BranchKey,
    bool IsUnclaimed,
    bool IsBlocked,
    DateTime Created,
    DateTime? DueDate,
    DateTime? OverdueFiredAt,
    string? BranchLabel = null);
