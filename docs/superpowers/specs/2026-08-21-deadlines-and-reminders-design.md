# Deadlines and reminders

**Date:** 2026-08-21
**Status:** Implemented 2026-08-21 — see `docs/superpowers/plans/2026-08-21-deadlines-and-reminders.md`

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.

## What this is for

A task can be late, and nobody finds out. The engine has no notion of when work is due,
so the inbox cannot show it, no report can rank by it, and nothing nudges the person
holding it. `WORKFLOW_BUILD_VS_BUY.md` names SLA escalation as "a near-universal
follow-up request for approval workflows, and you currently have no mechanism for it at
all" — it is the last designed-for-but-unbuilt feature, and the argument that most
often sends teams to Elsa instead.

This spec covers two halves of that:

| Half | What it is | Needs a clock? |
|---|---|---|
| **Visibility** | A task knows when it is due; the inbox and runner show due-soon and overdue | No |
| **Reminders** | A nudge fires before the deadline, so somebody is told without opening a page | Yes |

## Deliberately not in scope

Named here because each is a plausible next step and none of them is this spec:

- **Escalation actions.** Nothing reassigns, notifies a supervisor, or cancels when a
  deadline passes. Overdue is handled by a person looking at their inbox. The event this
  spec adds is the hook a later escalation feature would hang off.
- **Timers as a workflow primitive.** No "wait 10 days, then advance on your own". That
  is durable suspend/resume and a different feature.
- **Re-dating from the UI.** No `SetTaskDueDateAsync`. Deadlines come from the host.
- **Working-day arithmetic.** Lead times are calendar days. A Monday deadline with a
  two-day lead nudges on Saturday and is seen Monday morning — early, never late. A
  holiday calendar is a host concern and would need a seam of its own; not worth one yet.
- **A series of reminders.** One nudge per task, not "5 days, 2 days, 1 day".

## The shape of the problem

Every event this engine raises today comes from somebody doing something.
`WorkflowEventKind` is dispatched from an engine operation, through
`TriggerDispatcher`, into an outbox that `WorkflowOutboxHostedService` drains. Nothing
in the system can currently happen *because nobody acted*, which is precisely what a
reminder is.

That gap is the whole design problem. Everything else here is bookkeeping.

## Where the due date comes from

**The host supplies it; the engine never computes it.** In the original system a deadline is a
property of the document — a ChangeRequest has a `DueDate` and an `InternalDueDate` — and the work
inherits it. A duration on the task definition ("this step allows 5 days") would let the
engine answer on its own, but it would be inventing a deadline the organisation already
has an answer for.

The engine therefore stores a date it cannot derive. A new host service fills it in —
one of the `HostServices.cs` family alongside `IWorkflowAssignmentResolver`, not another
UI client seam like `IWorkflowInboxClient`:

```csharp
/// <summary>
/// When a task is due. The engine has no opinion — a deadline belongs to whatever the
/// run is about, and only the host knows that a run on ChangeRequest:42 inherits that ChangeRequest's date.
/// </summary>
public interface IWorkflowDueDateResolver
{
    Task<DateTime?> ResolveAsync(
        WorkflowSubject subject,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default);
}
```

Returning `null` means "no deadline", which is the honest answer for most tasks and the
default behaviour of the whole feature.

This mirrors `IWorkflowAssignmentResolver` deliberately, including its failure policy: a
resolver that throws is **logged and treated as null**, never fatal. the original engine's review finding
M8 was that a silent fallback made a misrouted assignment undiagnosable; the same
reasoning applies to a date.

`NullDueDateResolver` is registered with `TryAddScoped`, exactly as
`NullAssignmentResolver` is (`ServiceCollectionExtensions.cs:124`), so a host that does
not opt in gets null dates and no behaviour change. Opting in is
`AddDueDateResolver<T>()` on `WorkflowEngineBuilder`.

### One funnel, so no creation path can forget

`NewTask` (`WorkflowEngine.cs:631`) is the single place a `WorkflowTask` is constructed.
All **eight** creation paths go through it:

| Path | Call site |
|---|---|
| Run start (entry task) | `WorkflowEngine.cs:163` |
| Advance on completion | `WorkflowEngine.cs:268` |
| Convergence task | `WorkflowEngine.cs:351` |
| Fork branch | `WorkflowEngine.Fork.cs:97` |
| Rework task after selective rejection | `WorkflowEngine.Fork.cs:257` |
| Branch added to a live fork | `WorkflowEngine.Reads.cs:282` |
| Sub-workflow entry | `WorkflowEngine.SubWorkflows.cs:215` |
| Ad-hoc task | `WorkflowEngine.Queries.cs:125` |

Five of those eight are fork-related or sub-workflow-related — which is the argument for
the funnel in one sentence. Anyone adding a deadline at "the place tasks are made" by
hand would find three of them and miss the rest.

The due date is resolved **inside that funnel**, not at each call site. `NewTask` becomes
an instance method and async.

It does **not** take the run's `WorkflowSubject`, which an earlier draft of this spec
called for. `LoadTaskAsync` does not `Include(t => t.Run)`, and of the eight creation
sites only `StartOnAsync` has a subject in hand — so a subject parameter would mean
adding a run load at seven call sites, which is the exact per-site burden the funnel
exists to remove. `NewTaskAsync` resolves the subject from `runId` instead, through a
memoised `Dictionary<int, WorkflowSubject>` on the scoped engine, seeded by
`StartOnAsync` with the subject it already holds. Caching is safe because a run's subject
is written once at creation and nothing on `IWorkflowEngine` can change it.

This is the opposite choice from `ResolveAssignmentAsync`, which is called from nine
places. Assignment has per-site nuance — a fork branch resolves differently from a
sub-workflow entry — and a due date does not. Putting it in the funnel means a creation
path added in future inherits deadlines for free rather than silently omitting them.

## Data model

Three columns. No new tables.

| Column | Type | Why |
|---|---|---|
| `WorkflowTask.DueDate` | `DateTime?` (UTC) | The resolved deadline, cached so the sweeper and the inbox can query it in SQL |
| `WorkflowTask.ReminderSentAt` | `DateTime?` (UTC) | The fire-once stamp, and the row-claim target |
| `WorkflowTaskDefinition.ReminderLeadTimeMinutes` | `int?` | How far ahead to nudge. Null means this task type never nudges |

**`ReminderLeadTimeMinutes` is an `int`, not a `TimeSpan`.** EF Core maps `TimeSpan` to
SQL Server `time(7)`, which represents a time of day and **caps at 24 hours** — every
lead time of a day or more would silently truncate or throw. The builder collects days
and stores minutes. This is the same class of trap as `DocumentBase.DocumentType` being
`Ignore`d: it compiles, and it is wrong at runtime.

Both `DateTime` columns are **UTC**, matching `WorkflowTask.Created`. Anything rendering
them compares against `DateTime.UtcNow`; `DateTime.Now` compiles and is wrong by the
host's offset.

## Visibility

No background machinery on the read path.

`DueDate` is added to `InboxTaskSnapshot` (projected server-side in
`WorkflowEngine.Inbox.cs`, one more column in the existing `.Select`) and carried across
into `InboxItem` by `DemoInboxClient`. `WorkflowInbox` renders it in a Due column —
"due in 2d", "3d overdue" — using the same UTC arithmetic as its existing `Waited()`
helper, and colours an overdue row.

Deliberately unchanged: **row order stays oldest-created-first**. Sorting overdue work to
the top sounds right and is not — it makes the list reorder as deadlines pass, and the
engine's stable `(Created, TaskId)` ordering exists precisely to stop rows moving between
refreshes. The Due column is sortable for anyone who wants that view.

`WorkflowRunner` shows the due date on a task row for the same reason it shows the
assignee: it is a property of the task, and the runner is where the task is worked.

## Reminders

### What fires

A new `WorkflowEventKind.TaskDueSoon`, dispatched through the existing
`TriggerDispatcher`. Hosts already write triggers for notification, so a reminder reuses
the whole runtime — conditions, dispatch modes, failure policy, and `TriggerExecution`
audit — rather than growing a second parallel notification path beside it.

### Delivery needs no new code

This is the payoff for reusing the trigger runtime rather than adding an
`IWorkflowReminderSink`. `NotifyTrigger` (`BuiltInTriggers.cs:111`, key
`workflow.notify`) declares:

```csharp
SupportedEvents: Enum.GetValues<WorkflowEventKind>()
```

— every event kind, present and future. The moment `TaskDueSoon` exists, the built-in
notification trigger supports it, selectable in the builder like any other. Its defaults
happen to be exactly right for a reminder: dispatch is `AfterCommit` ("an email cannot be
un-sent if the transaction rolls back"), and the recipient defaults to the task's current
assignee — which for a nudge is the person being nudged.

So the delivery path is `TaskDueSoon` → `workflow.notify` → `IWorkflowNotificationSink`,
and every link but the first already exists. The demo's `DemoNotificationSink` logs, and
a real host sends mail.

**`TaskDueSoon` must be appended to the end of the enum.** `WorkflowEventKind` values are
persisted as ints on `TriggerDefinition` and `TriggerExecution`; inserting a member
mid-list renumbers every value after it and silently rewrites the meaning of stored rows.
`SubWorkflowCancelled` carries a comment saying exactly this — follow it.

### What raises it

Split in two, the way the outbox is: a scoped `IWorkflowReminderProcessor` with
`ProcessDueAsync(batchSize, ct)` returning a count, and a thin
`WorkflowReminderHostedService` that resolves one per pass and does nothing else. Opt-in
via `AddReminderProcessing()`, mirroring `AddOutboxProcessing()`. Options:
`PollInterval` (default 5 minutes), `BatchSize`, and `ErrorBackoff`, following
`WorkflowOutboxOptions`.

The split is what makes this spec's own sweeper test list reachable. "Fires once", "two
concurrent sweeps produce one dispatch" and "skips test runs" are all statements about
the processor, and testing them through a `BackgroundService` would mean standing up a
host and racing a timer to observe them.

The design is **state-based, not schedule-based**. Each pass asks a fresh question
against live data rather than acting on a decision frozen earlier. A task completed since
the last pass, an archived one, a cancelled run, a lead time edited in the builder, or a
trigger added *after* the task was created all resolve correctly with no invalidation
step, because nothing was ever pre-computed.

### The candidate query

Open tasks that could still nudge:

```csharp
.Where(t => (t.Status == WorkflowTaskStatus.NotStarted
             || t.Status == WorkflowTaskStatus.InProgress)
            && !t.IsArchived
            && !t.Run!.IsTest
            && t.ReminderSentAt == null
            && t.TaskDefinition!.ReminderLeadTimeMinutes != null)
```

The same predicate discipline as the inbox query, for the same reasons: a **positive**
status list, because the negative form silently admits any status added to the enum
later and `Forked` is the one a hand-written version forgets; and `!IsTest`, so an
admin trying a workflow out in the builder does not page real people.

Note what is **not** in the query: `DueDate`. That is deliberate — see below.

### Re-resolving, and why the window is not in the query

For each candidate the sweeper calls `IWorkflowDueDateResolver` again, writes the answer
back to `DueDate` if it changed, and only then tests whether `DueDate - leadTime <= now`.

Deadlines move. A ChangeRequest gets extended, and a date stamped at task creation is stale from
that moment. Re-resolving is what keeps the reminder honest, and it repairs the cached
`DueDate` the inbox reads as a side effect.

The candidate set therefore cannot be narrowed to tasks already inside their window: a
deadline moved **earlier** would never enter that set and would never nudge at all. The
set is bounded instead by "open, not yet reminded, and configured to nudge" — which for
any realistic host is a small number of rows, checked every five minutes.

If that cost ever matters, the fix is to narrow on a generous outer bound
(`DueDate == null || DueDate <= now + maxLeadTime + slack`) rather than on the exact
window. Not worth doing now.

### Firing once, with more than one app instance

Two web servers both running the sweeper would both dispatch. The outbox already solved
this with leases; the sweeper solves it with a conditional claim.

Per task, inside `WorkflowTransaction.ExecuteAsync`:

1. Claim the row — an update setting `ReminderSentAt = now` **conditioned on
   `ReminderSentAt IS NULL`**. If it affects zero rows, another instance won; skip.
2. Only then dispatch `TaskDueSoon`.
3. Commit.

The claim happens before the dispatch, not after, so the failure mode is a missed
reminder rather than a duplicated one. That is the right way round: a person who is
nudged twice for the same task stops trusting the nudges.

`IdempotencyKey` on the outbox message is a second line of defence behind this, not the
primary one.

**Never call `BeginTransaction` directly** — `WorkflowTransaction.ExecuteAsync`, as
everywhere else in this codebase.

### Who the actor is

`DispatchAsync` requires an `actorId`, and no human is present. A `WorkflowActors.System`
constant (`"workflow:system"`) is introduced for it, and lands in the `TriggerExecution`
audit trail. Passing the task's assignee would be a lie — that person did not do this —
and passing an empty string would fail the dispatcher's guards.

## Builder UI

`TaskEditDialog` gains a numeric "Remind this many days before due" field, empty by
default, bound to a `double?` day-facing property over the stored `int?` minutes. It must
be `double?`: an `int?` getter does integer division on the stored minutes, so a 12-hour
lead time renders as `0` — showing the user a value that is not what is stored, and
rewriting it to zero on the next save. It must round-trip through `WorkflowEditModel` — `CopyFrom` and the clone path —
or it will appear to save and silently vanish, which is the failure STATE.md records for
attachments.

The demo seeds a lead time on at least one task type so `/inbox` has something to show.

## Demo host

`DemoDueDateResolver` implements the seam against the document the subject names:

```
InternalDueDate ?? DueDate
```

Internal first, because an internal deadline is the one the organisation actually works
to. It resolves the subject to a document the same way `DemoInboxClient` does — and
inherits the same documented limitation, that it keys on the id's shape without checking
`SubjectType`.

## Testing

Integration tests against SQL Server, as everything else here.

**The seam and the funnel**
- A due date is stamped on a task created by each of the eight paths
- `NullDueDateResolver` yields a null date and no error
- A resolver that throws is logged, the task is still created, `DueDate` is null
- The date stored is the date the resolver returned, in UTC

**The sweeper**
- Fires inside the lead window; does not fire outside it
- Fires **once** — a second pass over the same task dispatches nothing
- Two concurrent sweeps produce exactly one dispatch (the conditional claim)
- Skips completed, cancelled, forked, and archived tasks
- Skips tasks in a test run
- Skips tasks whose definition has no lead time
- Skips tasks with a null due date
- A deadline moved **later** defers the nudge; moved **earlier** brings it forward
- The re-resolved date is written back to `DueDate`

**Visibility**
- `DueDate` survives the projection into `InboxTaskSnapshot` and on into `InboxItem`

**Schema**
- The existing migration drift guard covers the three new columns

No bUnit. Component tests remain deferred while the UI changes shape, consistent with
the builder, runner, and inbox.

## What this leaves for later

- **Escalation.** `TaskDueSoon` is the pattern; a `TaskOverdue` event and actions that
  reassign or notify upward are the obvious follow-up, and the sweeper is where they go.
- **A reminder series.** One lead time became a column; a series becomes a child table
  and per-reminder delivery tracking.
- **Working days.** A calendar seam, if a Saturday nudge ever actually annoys somebody.
- **Re-dating from the UI.** A person changing one task's deadline without touching the
  document.
