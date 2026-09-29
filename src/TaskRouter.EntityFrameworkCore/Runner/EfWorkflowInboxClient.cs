using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Inbox;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Runner;

/// <summary>
/// The in-process half of the inbox.
///
/// <para>Two things it does that the engine will not, both for the same reason — the
/// engine's subject is deliberately opaque:</para>
///
/// <list type="bullet">
///   <item><b>Which org units the actor is in</b>, from
///   <see cref="IWorkflowActorResolver"/>, so the caller never has to know what an org unit
///   is.</item>
///   <item><b>What a subject is called and where it lives</b>, from
///   <see cref="IWorkflowSubjectResolver"/> — one batched call, not a join, and it needs no
///   shared DbContext. A host whose documents live in another database, or behind an HTTP
///   call, implements that seam the same way.</item>
/// </list>
///
/// <para><b>This class does not degrade the way the runner does, and that is the point of
/// it being separate.</b> An empty inbox renders as "Nothing is waiting on you", so
/// swallowing a failed engine read would tell the user the exact opposite of the truth. The
/// engine call is unwrapped and allowed to throw. Subject resolution around it still fails
/// soft, because an unlabelled row is visibly degraded rather than false.</para>
/// </summary>
public sealed class EfWorkflowInboxClient(
    IWorkflowEngine engine,
    IWorkflowActorResolver actors,
    IWorkflowSubjectResolver subjects,
    ILogger<EfWorkflowInboxClient> logger) : IWorkflowInboxClient
{
    /// <summary>
    /// The org units an inbox may be pointed at, flagged with the actor's own.
    /// </summary>
    public async Task<IReadOnlyList<InboxOrgUnit>> GetOrgUnitsAsync(
        string actorId, CancellationToken ct = default)
    {
        var mine = await actors.GetOrgUnitsForAsync(actorId, ct).ConfigureAwait(false);
        var all = await actors.GetBranchOptionsAsync(ct).ConfigureAwait(false);

        return
        [
            .. all.Select(option => new InboxOrgUnit(
                option.Key,
                option.DisplayName,
                mine.Contains(option.Key, StringComparer.Ordinal))),
        ];
    }

    public async Task<IReadOnlyList<InboxItem>> GetInboxAsync(
        string actorId, IReadOnlyList<string>? orgUnits = null, CancellationToken ct = default)
    {
        // Nothing asked for means the actor's own -- every unit they cover, not just one.
        // The first real host has a many-to-many person/section table and treats one person
        // as covering several sections, so asking for a single assignment here hid half of
        // their inbox with nothing to indicate it had.
        //
        // Units that were asked for are checked against the directory first. Honouring an
        // arbitrary key would let a caller enumerate unclaimed work by guessing, which is
        // why the endpoints refused to take keys at all until somebody needed to cover for
        // another section. The directory is the host's own list of what may be looked at.
        var units = orgUnits is null
            ? await actors.GetOrgUnitsForAsync(actorId, ct).ConfigureAwait(false)
            : await KnownUnitsAsync(orgUnits, ct).ConfigureAwait(false);

        // Unwrap, deliberately, where DemoRunnerClient's eleven calls all degrade instead.
        // A failure here must not be swallowed: an empty inbox renders as "Nothing is
        // waiting on you", so returning [] on a broken query tells the user the opposite
        // of the truth. Throwing surfaces it in the component's error alert.
        var rows = (await engine.GetOpenTasksForActorAsync(actorId, units, ct).ConfigureAwait(false))
            .Unwrap();

        if (rows.Count == 0)
        {
            return [];
        }

        // One batched call for every subject mentioned, rather than one per row. The host
        // decides how to answer -- a query, a cache, an HTTP call -- and the engine only
        // requires that it answer for many at once.
        var distinct = rows
            .Select(r => r.Subject)
            .Distinct()
            .ToList();

        // What the org units are called. One call for the whole inbox, and only when a row
        // actually carries a key -- a personal inbox with nothing unclaimed in it should not
        // pay for a directory lookup.
        var branchLabels = new Dictionary<string, string>(StringComparer.Ordinal);

        if (rows.Any(r => r.AssignedBranchKey is not null))
        {
            try
            {
                foreach (var option in await actors.GetBranchOptionsAsync(ct).ConfigureAwait(false))
                {
                    branchLabels[option.Key] = option.DisplayName;
                }
            }
            catch (Exception ex)
            {
                // Fails soft, like the subject resolver. An unlabelled section shows its raw
                // key, which is worse than a name and far better than no inbox.
                logger.LogError(ex, "The directory failed; inbox sections will show raw keys.");
            }
        }

        IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor> described;

        try
        {
            described = await subjects.ResolveAsync(distinct, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fails soft, unlike the engine read above. Every row falls back to its raw
            // key and renders unclickable, which is visibly degraded rather than false --
            // and an inbox that shows the work with poor labels beats one that shows
            // nothing because a document lookup failed.
            logger.LogError(
                ex, "The subject resolver failed; inbox rows will show raw subject keys.");

            described = new Dictionary<WorkflowSubject, SubjectDescriptor>();
        }

        return
        [
            .. rows.Select(r =>
            {
                described.TryGetValue(r.Subject, out var found);

                return new InboxItem(
                    TaskId: r.TaskId,
                    TaskLabel: r.Label,
                    WorkflowName: r.WorkflowName,
                    // A subject the host could not describe keeps its row, labelled with
                    // the raw key and nothing to click. An orphaned task is a defect worth
                    // seeing, not worth hiding.
                    SubjectLabel: found?.Label ?? r.Subject.ToString(),
                    SubjectSubtitle: found?.Subtitle,
                    SubjectUrl: found?.Url ?? string.Empty,
                    BranchKey: r.AssignedBranchKey,
                    IsUnclaimed: r.IsUnclaimed,
                    IsBlocked: r.IsBlocked,
                    Created: r.Created,
                    DueDate: r.DueDate,
                    OverdueFiredAt: r.OverdueFiredAt,
                    BranchLabel: r.AssignedBranchKey is { } key
                                 && branchLabels.TryGetValue(key, out var label)
                        ? label
                        : null);
            })
        ];
    }

    /// <summary>
    /// The requested units the directory actually lists. An unknown key is dropped rather
    /// than refused: a stale bookmark or a renamed section should narrow an inbox, not
    /// break it.
    /// </summary>
    private async Task<IReadOnlyList<string>> KnownUnitsAsync(
        IReadOnlyList<string> requested, CancellationToken ct)
    {
        if (requested.Count == 0)
        {
            return [];
        }

        var known = (await actors.GetBranchOptionsAsync(ct).ConfigureAwait(false))
            .Select(option => option.Key)
            .ToHashSet(StringComparer.Ordinal);

        return [.. requested.Where(known.Contains)];
    }
}
