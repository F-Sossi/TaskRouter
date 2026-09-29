using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Model;
using TaskRouter.Core.Results;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// The shape pulled server-side in the first query. It exists only because
/// <see cref="InboxTaskSnapshot.IsBlocked"/> depends on a second query keyed on the ids
/// this one returns — everything else here maps straight across.
/// </summary>
file sealed record InboxRow(
    int TaskId,
    int WorkflowRunId,
    WorkflowSubject Subject,
    string TaskTypeKey,
    string Label,
    string WorkflowName,
    WorkflowTaskStatus Status,
    string? AssignedToActorId,
    string? AssignedBranchKey,
    int? SubWorkflowInstanceId,
    DateTime Created,
    DateTime? DueDate,
    DateTime? OverdueFiredAt);

/// <summary>
/// One row of somebody's inbox: a task that is open, theirs, and describes the subject
/// it belongs to so a host can turn it into a link.
/// </summary>
/// <param name="DueDate">
/// When this is due, in <b>UTC</b>, or null for no deadline. Straight from the
/// engine's cached column — the sweeper repairs it, so it is as fresh as the last
/// sweep. Compare against <see cref="DateTime.UtcNow"/>.
/// </param>
/// <param name="OverdueFiredAt">
/// When <see cref="WorkflowEventKind.TaskOverdue"/> fired for this task, in
/// <b>UTC</b>, or null if it has not. A past <see cref="DueDate"/> says the task is
/// late; this says something was raised about it, which is a different fact and the
/// one a triage list needs to avoid chasing the same task twice.
/// </param>
public sealed record InboxTaskSnapshot(
    int TaskId,
    int WorkflowRunId,
    WorkflowSubject Subject,
    string TaskTypeKey,
    string Label,
    // Deliberately the mainline workflow's name even for a task that belongs to a
    // sub-workflow instance: a sub-workflow's tasks are ordinary tasks in the parent's
    // run (see WorkflowEngine.SubWorkflows.cs), so there is no other run to name it
    // after.
    string WorkflowName,
    WorkflowTaskStatus Status,
    string? AssignedToActorId,
    string? AssignedBranchKey,
    bool IsUnclaimed,
    // A flag, not a filter: a blocked task is still genuinely the actor's and still
    // genuinely waiting on them to notice it, so it stays in the inbox rather than
    // being hidden by it.
    bool IsBlocked,
    int? SubWorkflowInstanceId,
    DateTime Created,
    DateTime? DueDate,
    DateTime? OverdueFiredAt);

/// <summary>
/// The inbox query.
///
/// It is here rather than in each host because the predicate has four ways to be subtly
/// wrong, and the hand-written version this replaces had two of them. A host that writes
/// its own will forget <see cref="WorkflowTaskStatus.Forked"/> — a superseded ghost that
/// can never be completed — and <see cref="WorkflowRun.IsTest"/>, which puts an admin's
/// experiments into real people's inboxes.
/// </summary>
public sealed partial class WorkflowEngine
{
    public Task<Result<IReadOnlyList<InboxTaskSnapshot>>> GetOpenTasksForActorAsync(
        string actorId,
        IReadOnlyList<string> branchKeys,
        CancellationToken ct = default) =>
        Try.RunAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
            ArgumentNullException.ThrowIfNull(branchKeys);

            // Blanks dropped so a stray "" or whitespace entry from a sloppy caller can't
            // accidentally match a task whose branch key is itself blank, and Distinct so
            // a caller passing overlapping units doesn't change the result, only the size
            // of the generated IN (...) list. An empty list short-circuits the unclaimed
            // clause out of the generated SQL entirely.
            var units = branchKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();

            // Projected server-side rather than pulled in with Include/ThenInclude: the
            // three include chains this replaced dragged every column of six tables
            // across the wire to populate a 13-field record, including WorkflowTasks.Notes
            // (nvarchar(max) — see WorkflowModelBuilder.cs, it has no HasMaxLength) for a
            // field the inbox never reads. GetValidOutcomesAsync and GetTaskLogsAsync
            // (WorkflowEngine.Reads.cs) are the siblings this follows for projecting with
            // .Select instead of Include.
            var rows = await db.WorkflowTasks
                .AsNoTracking()
                // A positive status list, not a negative one. The negative form silently
                // admits any status added to the enum later, and Forked is exactly the
                // value somebody writing this by hand leaves out.
                .Where(t => (t.Status == WorkflowTaskStatus.NotStarted
                             || t.Status == WorkflowTaskStatus.InProgress)
                            && !t.IsArchived
                            && !t.Run!.IsTest
                            && (t.AssignedToActorId == actorId
                                || (t.AssignedToActorId == null
                                    && t.AssignedBranchKey != null
                                    && units.Contains(t.AssignedBranchKey))))
                // Run status is deliberately not filtered. Task status is the authority
                // on whether work exists; a Completed run holding an open task is a
                // defect, and hiding it would conceal the defect in the one place
                // somebody would notice it.
                //
                // ThenBy(Id) because Created is not a total order: every branch of a fork
                // is stamped with the same DateTime.UtcNow (WorkflowEngine.Fork.cs takes it
                // once per fork, not once per branch), so SQL Server is free to return
                // same-Created rows in whatever order its plan produces. Without the
                // tiebreak an inbox sorted by age reshuffles between refreshes, and any
                // future paging over this order would silently drop or duplicate rows.
                .OrderBy(t => t.Created).ThenBy(t => t.Id)
                .Select(t => new InboxRow(
                    TaskId: t.Id,
                    WorkflowRunId: t.WorkflowRunId,
                    // Null-forgiving rather than defensive: every navigation here has a
                    // non-nullable FK, so EF emits INNER JOINs and a missing row cannot
                    // occur under the FK constraints. The `!` only quiets the compiler —
                    // it changes no SQL. (Label's COALESCE below is a real business rule,
                    // not null-handling.)
                    Subject: new WorkflowSubject(t.Run!.Subject.SubjectType, t.Run.Subject.SubjectId),
                    TaskTypeKey: t.TaskDefinition!.TaskType!.Key,
                    // Same fallback rule as SnapshotMapper.ToSnapshot's DisplayName in
                    // Snapshots.cs — if that rule changes, check here too, and vice versa.
                    Label: t.TaskDefinition.DisplayName ?? t.TaskDefinition.TaskType.DisplayName,
                    WorkflowName: t.Run.DefinitionVersion!.WorkflowDefinition!.Name,
                    Status: t.Status,
                    AssignedToActorId: t.AssignedToActorId,
                    AssignedBranchKey: t.AssignedBranchKey,
                    SubWorkflowInstanceId: t.SubWorkflowInstanceId,
                    Created: t.Created,
                    DueDate: t.DueDate,
                    OverdueFiredAt: t.OverdueFiredAt))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (rows.Count == 0)
            {
                return (IReadOnlyList<InboxTaskSnapshot>)[];
            }

            // A second query rather than a correlated subquery per row: the set of
            // running, blocking sub-workflow instances is small, and this keeps the main
            // projection free of database work per row. Safe at scale for the same reason
            // the actor/branch-key IN (...) above is: EF Core buckets the parameter list
            // and falls back to a single OPENJSON parameter for large ones.
            var candidateIds = rows.Select(r => r.TaskId).ToList();

            // BlockingInstanceNamesAsync (WorkflowEngine.SubWorkflows.cs) is the sibling
            // definition of "still blocking": the two must agree on all four conjuncts or
            // the inbox can flag a task CompleteTaskAsync would happily let through.
            var blockedTaskIds = (await db.WorkflowSubWorkflowInstances
                .AsNoTracking()
                .Where(i => i.Status == SubWorkflowStatus.Running
                            && i.IsBlocking
                            && !i.IsArchived
                            && candidateIds.Contains(i.ParentTaskId))
                .Select(i => i.ParentTaskId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false))
                // HashSet, not List: every row is tested against this with Contains, and
                // there is no need to preserve order.
                .ToHashSet();

            return (IReadOnlyList<InboxTaskSnapshot>)[.. rows.Select(r => new InboxTaskSnapshot(
                TaskId: r.TaskId,
                WorkflowRunId: r.WorkflowRunId,
                Subject: r.Subject,
                TaskTypeKey: r.TaskTypeKey,
                Label: r.Label,
                WorkflowName: r.WorkflowName,
                Status: r.Status,
                AssignedToActorId: r.AssignedToActorId,
                AssignedBranchKey: r.AssignedBranchKey,
                IsUnclaimed: r.AssignedToActorId == null,
                IsBlocked: blockedTaskIds.Contains(r.TaskId),
                SubWorkflowInstanceId: r.SubWorkflowInstanceId,
                Created: r.Created,
                DueDate: r.DueDate,
                OverdueFiredAt: r.OverdueFiredAt))];
        });
}
