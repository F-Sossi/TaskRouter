using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>How a forked step is represented.</summary>
public enum ForkView
{
    /// <summary>
    /// The open branch tasks, one per branch. What an inbox wants: each person's own work.
    /// </summary>
    Branches = 0,

    /// <summary>
    /// The fork origin instead of its branches, collapsing a fanned-out step back to the one
    /// step it is. What a list view wants: "this document is on Provide Input", once, however
    /// many branches are working it.
    ///
    /// <para>The origin is <see cref="WorkflowTaskStatus.Forked"/> and not actionable, which is
    /// the point — it names the position, it is not something to click.</para>
    /// </summary>
    Origin = 1,
}

/// <summary>
/// A step a subject is currently on.
///
/// <para><see cref="OpenBranchCount"/> is how many branches of a forked step are still open,
/// and is 0 for anything that is not a fork origin — including every task under
/// <see cref="ForkView.Branches"/>, where the branches come back individually and counting
/// them is the caller's own <c>Count</c>.</para>
///
/// <para>It exists because <see cref="ForkView.Origin"/> deliberately withholds the branches,
/// so without it a caller has no way to know a step fanned out at all, let alone how widely.
/// The origin's own <see cref="WorkflowTaskSnapshot.AssignedToActorId"/> is returned unchanged
/// — whoever held the step before it forked — so a host can render a name, a count, or both.
/// </para>
/// </summary>
public sealed record CurrentTask(
    WorkflowTaskSnapshot Task,
    int OpenBranchCount);

/// <summary>
/// One workflow running on a subject, and where it has got to. Grouped rather than flattened
/// because a subject may carry several at once and a host showing them separately needs them
/// separable — and named, which a bare <see cref="WorkflowTaskSnapshot"/> is not.
/// </summary>
public sealed record SubjectWorkflowState(
    int WorkflowRunId,
    string WorkflowName,
    int Version,
    WorkflowRunStatus Status,
    bool IsTest,
    IReadOnlyList<CurrentTask> CurrentTasks);

/// <summary>
/// The bulk position read.
///
/// Every other read on this engine is single-entity, so a host building a list of documents
/// made one call per row — and in practice stopped using the read API and queried the tables
/// directly, which works but couples it to the schema. This answers the question that screen
/// actually has.
/// </summary>
public sealed partial class WorkflowEngine
{
    /// <summary>
    /// SQL Server caps parameters near 2,100. A caller may hand over a whole page of ids, so
    /// each subject type's ids are chunked well under that rather than trusted to be small.
    /// </summary>
    private const int SubjectIdChunkSize = 500;

    public Task<Result<IReadOnlyDictionary<WorkflowSubject, IReadOnlyList<SubjectWorkflowState>>>>
        GetCurrentStateForSubjectsAsync(
            IReadOnlyList<WorkflowSubject> subjects,
            ForkView forkView = ForkView.Branches,
            CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(subjects);

            var wanted = subjects
                .Where(s => !string.IsNullOrWhiteSpace(s.SubjectType)
                         && !string.IsNullOrWhiteSpace(s.SubjectId))
                .Distinct()
                .ToList();

            if (wanted.Count == 0)
            {
                return Empty();
            }

            var rows = new List<PositionRow>();

            // One query per distinct subject type, not per subject. Grouping keeps the
            // predicate to SubjectType = @t AND SubjectId IN (...), which uses the
            // (SubjectType, SubjectId) index on the owned subject; a flat OR over pairs
            // would not.
            foreach (var byType in wanted.GroupBy(s => s.SubjectType, StringComparer.Ordinal))
            {
                var subjectType = byType.Key;
                var ids = byType.Select(s => s.SubjectId).Distinct(StringComparer.Ordinal).ToList();

                foreach (var chunk in Chunk(ids, SubjectIdChunkSize))
                {
                    rows.AddRange(await PositionsAsync(subjectType, chunk, ct).ConfigureAwait(false));
                }
            }

            return Assemble(rows, forkView);
        });

    /// <summary>
    /// Open tasks, plus fork origins — the origins are needed even under
    /// <see cref="ForkView.Branches"/>, because that is how a group's open branches are
    /// counted, and they are dropped afterwards. A positive status list rather than a
    /// negative one, so a status added to the enum later is excluded until someone decides
    /// otherwise.
    /// </summary>
    private async Task<List<PositionRow>> PositionsAsync(
        string subjectType, List<string> subjectIds, CancellationToken ct) =>
        await db.WorkflowTasks
            .AsNoTracking()
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .Where(t => (t.Status == WorkflowTaskStatus.NotStarted
                         || t.Status == WorkflowTaskStatus.InProgress
                         || t.Status == WorkflowTaskStatus.Forked)
                        && !t.IsArchived
                        && !t.Run!.IsArchived
                        && t.Run.Subject.SubjectType == subjectType
                        && subjectIds.Contains(t.Run.Subject.SubjectId))
            // Created is not a total order: every branch of a fork is stamped with the same
            // DateTime.UtcNow, so Id is the tiebreak. Same reasoning as the inbox.
            .OrderBy(t => t.Created).ThenBy(t => t.Id)
            .Select(t => new PositionRow(
                new WorkflowSubject(t.Run!.Subject.SubjectType, t.Run.Subject.SubjectId),
                t.WorkflowRunId,
                t.Run.DefinitionVersion!.WorkflowDefinition!.Name,
                t.Run.DefinitionVersion.Version,
                t.Run.Status,
                t.Run.IsTest,
                t))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Groups by subject and run, and resolves the fork view. Everything here is in memory
    /// over rows already fetched — resolving a fork must not become a query per row, which
    /// would reintroduce the N+1 this method exists to remove.
    /// </summary>
    private static IReadOnlyDictionary<WorkflowSubject, IReadOnlyList<SubjectWorkflowState>> Assemble(
        List<PositionRow> rows, ForkView forkView)
    {
        var result = new Dictionary<WorkflowSubject, IReadOnlyList<SubjectWorkflowState>>();

        foreach (var bySubject in rows.GroupBy(r => r.Subject))
        {
            var workflows = new List<SubjectWorkflowState>();

            foreach (var byRun in bySubject.GroupBy(r => r.WorkflowRunId))
            {
                var current = Resolve([.. byRun], forkView);

                // A run whose only rows were fork origins with nothing still open has no
                // position: the fork is finished and the run is waiting on whatever it
                // converged into, which is a separate row if it is open.
                if (current.Count == 0)
                {
                    continue;
                }

                var first = byRun.First();

                workflows.Add(new SubjectWorkflowState(
                    first.WorkflowRunId,
                    first.WorkflowName,
                    first.Version,
                    first.RunStatus,
                    first.IsTest,
                    current));
            }

            if (workflows.Count > 0)
            {
                result[bySubject.Key] = workflows;
            }
        }

        return result;
    }

    /// <summary>
    /// One run's rows, reduced to the tasks that describe where it is.
    ///
    /// <para>A fork origin is current only while its group still has open branches — a fork
    /// whose branches have all finished is history, not position. Origin and branches share
    /// <c>ForkGroupId</c>, so one pass answers both which origins survive and how many
    /// branches each still has open; the count costs nothing extra.</para>
    /// </summary>
    private static IReadOnlyList<CurrentTask> Resolve(List<PositionRow> runRows, ForkView forkView)
    {
        var open = runRows
            .Where(r => r.Task.Status != WorkflowTaskStatus.Forked)
            .ToList();

        var openBranchCounts = open
            .Where(r => r.Task.ForkGroupId is not null)
            .GroupBy(r => r.Task.ForkGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        if (forkView is ForkView.Branches)
        {
            return [.. open.Select(r => new CurrentTask(r.Task.ToSnapshot(), 0))];
        }

        var live = runRows
            .Where(r => r.Task.IsForkOrigin
                     && r.Task.ForkGroupId is not null
                     && openBranchCounts.ContainsKey(r.Task.ForkGroupId.Value))
            .Select(r => new CurrentTask(
                r.Task.ToSnapshot(), openBranchCounts[r.Task.ForkGroupId!.Value]));

        // Open tasks that are not part of a still-running fork. A branch whose origin is
        // being reported in its place is dropped; anything else stands on its own.
        var standalone = open
            .Where(r => r.Task.ForkGroupId is null
                     || !openBranchCounts.ContainsKey(r.Task.ForkGroupId.Value))
            .Select(r => new CurrentTask(r.Task.ToSnapshot(), 0));

        return [.. live.Concat(standalone).OrderBy(c => c.Task.Id)];
    }

    private static IReadOnlyDictionary<WorkflowSubject, IReadOnlyList<SubjectWorkflowState>> Empty() =>
        new Dictionary<WorkflowSubject, IReadOnlyList<SubjectWorkflowState>>();

    private static IEnumerable<List<string>> Chunk(List<string> values, int size)
    {
        for (var i = 0; i < values.Count; i += size)
        {
            yield return values.GetRange(i, Math.Min(size, values.Count - i));
        }
    }

    /// <summary>One task, carrying the run facts its subject and workflow need.</summary>
    private sealed record PositionRow(
        WorkflowSubject Subject,
        int WorkflowRunId,
        string WorkflowName,
        int Version,
        WorkflowRunStatus RunStatus,
        bool IsTest,
        WorkflowTask Task);
}
