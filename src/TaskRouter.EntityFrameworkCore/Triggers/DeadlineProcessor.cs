using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Triggers;

public interface IWorkflowDeadlineProcessor
{
    /// <summary>
    /// One pass over both halves of a deadline: tasks inside their reminder lead time get
    /// <see cref="WorkflowEventKind.TaskDueSoon"/>, tasks past their deadline get
    /// <see cref="WorkflowEventKind.TaskOverdue"/>. Returns how many events fired, which
    /// may be two for a single task — a late task is also inside its lead window, and
    /// neither half suppresses the other.
    /// </summary>
    Task<int> ProcessDueAsync(int batchSize = 100, CancellationToken ct = default);
}

/// <summary>
/// The only two things in this engine that happen because <b>nobody acted</b>.
///
/// Every other event is dispatched from an engine operation somebody invoked. A reminder
/// has no such operation, so it is swept for — and the design is deliberately
/// <b>state-based rather than schedule-based</b>: each pass asks a fresh question against
/// live data instead of acting on a decision frozen earlier. A task completed since the
/// last pass, an archived one, a cancelled run, a lead time edited in the builder, or a
/// trigger added <i>after</i> the task was created all resolve correctly with no
/// invalidation step, because nothing was ever pre-computed.
///
/// <para>Split from its hosted service exactly as <see cref="IWorkflowOutboxProcessor"/>
/// is from <see cref="WorkflowOutboxHostedService"/>: everything worth testing is here,
/// and none of it needs a host or a timer to reach.</para>
///
/// <para>Reminders and escalations are one class rather than two because they differ in
/// only two things — the candidate query and the column claimed — and share everything
/// else: the subject lookup, the live re-resolution, the repair of the cached date, and
/// the claim-then-dispatch transaction. Two processors would mean two timers, two
/// registrations, two near-identical test files, and two places to fix the next thing
/// found wrong with sweeping. The <c>Sweep</c> enum is sized for one-shot claims on a
/// nullable timestamp column and nothing wider — a repeating escalation needs a count
/// rather than a stamp, at which point <see cref="ClaimAndDispatchAsync"/>'s "claim by
/// setting a null column non-null" contract stops applying and the seam moves rather
/// than widens.</para>
/// </summary>
public sealed class DeadlineProcessor(
    IWorkflowDbContext db,
    IWorkflowTriggerDispatcher dispatcher,
    IWorkflowDueDateResolver dueDates,
    ILogger<DeadlineProcessor> logger) : IWorkflowDeadlineProcessor
{
    /// <summary>Which half of a deadline a sweep is doing.</summary>
    private enum Sweep
    {
        Reminder,
        Overdue
    }

    /// <summary>
    /// AsNoTracking throughout: every write this pass makes is an ExecuteUpdateAsync, so
    /// nothing here needs the change tracker — and keeping these entities untracked means
    /// the conditional claim cannot be second-guessed by a stale tracked copy.
    /// </summary>
    public async Task<int> ProcessDueAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var reminders = await ReminderCandidatesAsync(batchSize, ct).ConfigureAwait(false);
        var overdue = await OverdueCandidatesAsync(batchSize, now, ct).ConfigureAwait(false);

        if (reminders.Count == 0 && overdue.Count == 0)
        {
            return 0;
        }

        // Subjects for every run involved in either sweep, in one query rather than one
        // per task.
        var runIds = reminders.Concat(overdue)
            .Select(t => t.WorkflowRunId).Distinct().ToList();

        var subjects = (await db.WorkflowRuns
            .AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Subject.SubjectType, r.Subject.SubjectId })
            .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(r => r.Id, r => new WorkflowSubject(r.SubjectType, r.SubjectId));

        var fired = await SweepAsync(reminders, subjects, now, Sweep.Reminder, ct)
            .ConfigureAwait(false);

        fired += await SweepAsync(overdue, subjects, now, Sweep.Overdue, ct)
            .ConfigureAwait(false);

        return fired;
    }

    /// <summary>
    /// Open, not yet reminded, configured to nudge.
    ///
    /// Note what is NOT here: DueDate. Narrowing to tasks already inside their window
    /// would mean a deadline moved *earlier* never enters the set and never nudges at
    /// all. This set is already bounded by the lead-time predicate — only tasks
    /// configured to nudge — and it shrinks as each one fires, which is what makes the
    /// batching correct. If that ever costs, narrow on a generous outer bound (DueDate ==
    /// null || DueDate &lt;= now + maxLeadTime + slack), never on the exact window.
    /// </summary>
    private async Task<List<WorkflowTask>> ReminderCandidatesAsync(
        int batchSize, CancellationToken ct) =>
        await db.WorkflowTasks
            .AsNoTracking()
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            // A positive status list, not a negative one — the same discipline as the
            // inbox query, for the same reason. The negative form silently admits any
            // status added to the enum later, and Forked is the one a hand-written
            // version forgets: a superseded ghost that can never be completed.
            .Where(t => (t.Status == WorkflowTaskStatus.NotStarted
                         || t.Status == WorkflowTaskStatus.InProgress)
                        && !t.IsArchived
                        && !t.Run!.IsTest
                        && t.ReminderSentAt == null
                        && t.TaskDefinition!.ReminderLeadTimeMinutes != null)
            .OrderBy(t => t.Id)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Open, not yet escalated, past its cached deadline.
    ///
    /// <b>This narrows on DueDate, which is exactly what the reminder query above refuses
    /// to do.</b> It has to. Overdue has no opt-in — a task that is late is late whether
    /// or not anybody configured anything — so there is no lead-time predicate left to
    /// bound the set with. Without the date filter the candidates are every open task
    /// with a deadline, a set that does not shrink, because a task that is not overdue yet
    /// stays a candidate indefinitely; combined with OrderBy(Id).Take(batchSize) the sweep
    /// would re-examine the same low-id tasks every pass and never reach the tail.
    ///
    /// <para>The cost is paid by the case the reminder comment warns about. A deadline
    /// moved *earlier* on a task with no reminder configured does not enter the set until
    /// the stale cached date passes, so its escalation is late by however far the deadline
    /// moved. Late, never wrong: the live re-resolution below still refuses to fire
    /// against a date that no longer exists. Closing it entirely would mean re-resolving
    /// every open task on every pass — a host-data query per task per sweep — to fix a
    /// case that only arises for a task nobody is being reminded about.</para>
    ///
    /// <para><b>And one case that is never, not late.</b> <c>DueDate != null</c> excludes a
    /// task whose resolver returned null when it was created and whose definition sets no
    /// reminder lead time: the only two writers of the column are task creation and the
    /// repair below, the repair only runs for tasks already in a candidate set, and this task
    /// is in neither. Nothing will re-resolve it, so it never escalates however late it
    /// becomes. Closing that needs a repair pass over null-dated open tasks — deliberately
    /// out of scope here, and pinned by
    /// <c>EscalationTests.A_task_with_no_cached_due_date_never_escalates</c> so it cannot
    /// change unnoticed.</para>
    /// </summary>
    private async Task<List<WorkflowTask>> OverdueCandidatesAsync(
        int batchSize, DateTime now, CancellationToken ct) =>
        await db.WorkflowTasks
            .AsNoTracking()
            .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
            .Where(t => (t.Status == WorkflowTaskStatus.NotStarted
                         || t.Status == WorkflowTaskStatus.InProgress)
                        && !t.IsArchived
                        && !t.Run!.IsTest
                        && t.OverdueFiredAt == null
                        && t.DueDate != null
                        && t.DueDate <= now)
            .OrderBy(t => t.Id)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Re-resolve, repair, test the window, claim, dispatch — the part both halves share.
    /// </summary>
    private async Task<int> SweepAsync(
        List<WorkflowTask> candidates,
        Dictionary<int, WorkflowSubject> subjects,
        DateTime now,
        Sweep kind,
        CancellationToken ct)
    {
        var fired = 0;

        foreach (var task in candidates)
        {
            if (!subjects.TryGetValue(task.WorkflowRunId, out var subject))
            {
                continue;
            }

            // Re-resolved every pass, not read from the cached column. Deadlines move: a
            // ChangeRequest gets extended, and a date stamped at task creation is stale from that
            // moment. For a reminder this keeps the nudge honest; for an escalation it is
            // what stops a supervisor being told about a deadline that no longer exists.
            // Repairing DueDate is a side effect that keeps the inbox honest too.
            DateTime? due;

            try
            {
                due = await dueDates.ResolveAsync(subject, task.ToSnapshot(), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Due-date resolver failed for task {TaskId} during the {Sweep} sweep; " +
                    "skipping it this pass.", task.Id, kind);
                continue;
            }

            if (due != task.DueDate)
            {
                await db.WorkflowTasks
                    .Where(t => t.Id == task.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.DueDate, due), ct)
                    .ConfigureAwait(false);
            }

            if (due is null)
            {
                continue;
            }

            if (!IsDue(task, due.Value, now, kind))
            {
                continue;
            }

            // Contained at the same granularity as the resolver above, and for a sharper
            // reason. A trigger with FailurePolicy.FailOperation rethrows out of the
            // dispatcher, and "the operation" it means is the one somebody invoked —
            // except nobody invoked this. Uncaught, one misconfigured trigger would take
            // down every later candidate in both halves, back the sweep off for the error
            // interval, and do it again next pass: a single bad trigger stalling every
            // reminder and every escalation in the system. The claim rolls back with the
            // transaction, so the task is simply retried next pass.
            try
            {
                if (await ClaimAndDispatchAsync(task, now, kind, ct).ConfigureAwait(false))
                {
                    fired++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Dispatch failed for task {TaskId} during the {Sweep} sweep; " +
                    "skipping it this pass.", task.Id, kind);
            }
        }

        return fired;
    }

    /// <summary>
    /// Whether the freshly resolved date puts this task inside the half being swept.
    /// </summary>
    private static bool IsDue(WorkflowTask task, DateTime due, DateTime now, Sweep kind) =>
        kind switch
        {
            // Null-forgiving is safe: the candidate query's predicate is what guarantees
            // a lead time exists on this half, and nothing between there and here can
            // clear it.
            Sweep.Reminder =>
                due.AddMinutes(-task.TaskDefinition!.ReminderLeadTimeMinutes!.Value) <= now,

            // Re-checked against the live date, not the cached one the query matched on —
            // which is what makes an extended deadline a repair rather than an escalation.
            Sweep.Overdue => due <= now,

            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    /// <summary>
    /// Claim first, dispatch second, both inside one transaction.
    ///
    /// The claim is an update conditioned on the sweep's column being null; if it affects
    /// zero rows another instance won and this one skips. Doing it before the dispatch
    /// rather than after makes the failure mode a <i>missed</i> event rather than a
    /// duplicated one — which is the right way round for both halves, because a person
    /// nudged twice stops trusting the nudges and a supervisor told twice stops reading
    /// the escalations. The outbox message's IdempotencyKey is a second line of defence
    /// behind this, not the primary one.
    ///
    /// <para>A branch rather than a passed-in column expression: <c>SetProperty</c> is what
    /// resists a non-inline lambda. The predicate would compose fine — <c>.Where(...)</c>
    /// takes a stored <c>Expression&lt;Func&lt;WorkflowTask, bool&gt;&gt;</c> without
    /// complaint — so it is the assignment, not the filter, that makes the generic version
    /// more trouble than two arms.</para>
    /// </summary>
    private async Task<bool> ClaimAndDispatchAsync(
        WorkflowTask task, DateTime now, Sweep kind, CancellationToken ct) =>
        // WorkflowTransaction.ExecuteAsync, never BeginTransaction: a provider configured
        // with EnableRetryOnFailure refuses a user-initiated transaction taken outside its
        // execution strategy. This codebase has got that wrong twice already.
        await WorkflowTransaction.ExecuteAsync(db, async token =>
        {
            var claimed = kind switch
            {
                Sweep.Reminder => await db.WorkflowTasks
                    .Where(t => t.Id == task.Id && t.ReminderSentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReminderSentAt, now), token)
                    .ConfigureAwait(false),
                Sweep.Overdue => await db.WorkflowTasks
                    .Where(t => t.Id == task.Id && t.OverdueFiredAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.OverdueFiredAt, now), token)
                    .ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };

            if (claimed == 0)
            {
                logger.LogDebug(
                    "Task {TaskId} was claimed by another {Sweep} sweep; skipping.",
                    task.Id, kind);
                return false;
            }

            var eventKind = kind switch
            {
                Sweep.Reminder => WorkflowEventKind.TaskDueSoon,
                Sweep.Overdue => WorkflowEventKind.TaskOverdue,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };

            await dispatcher.DispatchAsync(
                task, eventKind, WorkflowActors.System, ct: token)
                .ConfigureAwait(false);

            return true;
        }, ct).ConfigureAwait(false);
}
