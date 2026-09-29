# Escalation actions

**Date:** 2026-08-22
**Status:** Implemented 2026-08-22 — see `docs/superpowers/plans/2026-08-22-escalation-actions.md`

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.

## What this is for

A deadline can pass and nothing happens. `docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md`
built the half that fires *before* a deadline — `TaskDueSoon`, swept by
`ReminderProcessor`, delivered through the ordinary trigger runtime — and named
escalation as the plausible next step it was deliberately leaving out. This is that step.

The gap is narrower than "escalation actions" suggests, because most of the machinery is
already here:

| Wanted | Already exists |
|---|---|
| Notify a supervisor | `NotifyTrigger` → `IWorkflowNotificationSink` |
| Reassign the task | `IWorkflowActions.AssignTaskAsync` |
| Cancel it | `IWorkflowActions.CancelTaskAsync` |
| Call something external | `WebhookTrigger` |
| **Know that a deadline passed** | **nothing** |

Only the last row is missing. So the engine's contribution here is one event, raised
once, reliably — and escalation is whatever triggers a host authors on it.

## The decision this rests on

**Escalation is not an engine concept.** The engine says a task is late; it has no
opinion about what should happen next, no notion of a supervisor, and no escalation
ladder. Three arguments for keeping it that way:

- **It is the same shape as reminders.** `TaskDueSoon` did not ship with a "nudge
  policy" — it ships an event, and the host decides what a nudge is. Escalation that
  worked differently would be a second vocabulary for the same idea.
- **Who is "above" somebody is host knowledge.** The demo can answer it
  (`Person.SectionId` → `Section.SectionLeadActorId` → `Division.DivisionHeadActorId`), and the original system
  can answer it, but only because they own an org model. A seam in the engine would be
  a seam every host must implement to use a feature most of them want one line of.
- **A ladder is expressible without one.** Two escalations at different offsets are two
  triggers with different conditions, authored in the builder, using the trigger
  runtime's existing ordering and condition evaluation.

## Deliberately not in scope

Each of these is a plausible next step, and none of them is this spec:

- **An escalation ladder.** No authored sequence of "after 1 day do X, after 3 do Y".
  `TaskOverdue` fires once, and a host wanting stages authors staged triggers.
- **Repeat firing.** No "still overdue, day 3". One event per task, ever.
- **A supervisor seam.** No `IWorkflowEscalationResolver`. Recipients are configured on
  the trigger, resolved by the host.
- **A grace period.** No `OverdueGraceMinutes`. Overdue means the deadline passed.
- **Timers as a workflow primitive.** Still out, for the reasons the deadlines spec gave.

## The event

`TaskOverdue` is **appended** to `WorkflowEventKind`, after `TaskDueSoon`:

```csharp
/// <summary>
/// A task's deadline has passed and it is still open. The second of the two events
/// raised because <b>nobody acted</b> — swept for, like TaskDueSoon, rather than
/// dispatched from a command.
///
/// Appended, like TaskDueSoon before it and for the same reason: these values are
/// persisted as ints on TriggerDefinition and TriggerExecution, so inserting one
/// mid-list renumbers every member after it and silently rewrites stored rows.
/// </summary>
TaskOverdue
```

The append rule is not stylistic. It is the third time this file has had to say so.

## Which tasks the sweep considers

**Every open task with a deadline. There is no opt-in.**

Reminders are opt-in because `ReminderLeadTimeMinutes` does double duty — it is both the
switch and the definition of the window. Overdue needs no window: it is the deadline
passing. A `RaisesOverdue` flag would therefore be a switch and nothing else, and a task
that is late is late whether or not somebody remembered to tick it. Overdue is a fact
about the task, not a feature you enable.

Dispatching to nobody is cheap. `TriggerDispatcher.DispatchAsync` queries for matching
trigger definitions and returns before building any context when there are none, so a
task nobody escalates costs one indexed query, once, ever.

## The asymmetry with the reminder sweep

This is the one part of the design that contradicts existing code, so it needs to argue
for itself.

`ReminderProcessor` refuses to narrow its candidate query on `DueDate`, and says why at
`ReminderProcessor.cs:57`: narrowing to tasks already inside their window means a
deadline moved *earlier* never enters the set and never nudges at all. The overdue sweep
does exactly what that comment warns against:

```
reminders   Status IN (NotStarted, InProgress)
            AND NOT IsArchived AND NOT Run.IsTest
            AND ReminderSentAt IS NULL
            AND ReminderLeadTimeMinutes IS NOT NULL
            -- no DueDate predicate, deliberately

overdue     Status IN (NotStarted, InProgress)
            AND NOT IsArchived AND NOT Run.IsTest
            AND OverdueFiredAt IS NULL
            AND DueDate IS NOT NULL AND DueDate <= now
            -- narrows on the cached column, by necessity
```

**Why the reminder sweep can afford not to narrow:** its set is bounded by
`ReminderLeadTimeMinutes IS NOT NULL` — only tasks configured to nudge — and it shrinks
as each one fires.

**Why the overdue sweep cannot:** with no opt-in there is no bounding predicate left.
`open AND OverdueFiredAt IS NULL` is every in-flight task with a deadline, and that set
does not shrink, because a task that is not overdue yet stays a candidate indefinitely.
Combined with `OrderBy(t => t.Id).Take(batchSize)` the sweep would re-examine the same
low-id tasks every pass and never reach the tail. Narrowing on `DueDate` is what makes
the set shrink, and shrinking is what makes the batching correct.

**What it costs.** The cached `DueDate` column is written when the task is created and
repaired by the sweep; for a task with no reminder configured, repair only happens once
it looks overdue. So:

| Cached date | Real date | Result |
|---|---|---|
| Dec 1 | Dec 1 | Fires on Dec 1. Correct. |
| Dec 1 | Dec 20 (extended) | Enters the set on Dec 1, re-resolves, repairs the column to Dec 20, does not fire. Correct. |
| Dec 1 | Nov 1 (pulled earlier) | Does not enter the set until Dec 1, then fires. **Late by the amount the deadline moved.** |
| null | Nov 1 | Never enters the set at all. **Never escalates** — not merely late — *provided the task also has no `ReminderLeadTimeMinutes`*. With one, the reminder sweep (which has no `DueDate` predicate) picks it up, repairs the column, and it re-enters the overdue set. |

The third row is the accepted cost. It is a late escalation, never a wrong one, and it
resolves itself the moment anything repairs the column. Closing it entirely would mean
re-resolving the due date for every open task on every pass — a host-data query per task
per sweep, to fix a case that only arises when a deadline is pulled forward on a task
nobody is being reminded about. The fourth is the sharper one, and the honest word for it
is *never*: a task whose deadline was unknown at creation and which nobody is being
reminded about has no path back into either candidate set. Closing it needs a repair pass
over null-dated open tasks, which this spec deliberately leaves out.

## Re-resolution

Every candidate has its due date re-resolved through `IWorkflowDueDateResolver` before
anything fires, and the cached column is repaired when it disagrees — the same step
reminders already perform, for the same reason. A date stamped at task creation is stale
from the moment the organisation moves it, and escalating against a stale date is worse
than not escalating: it tells a supervisor about a deadline that no longer exists.

A resolver that throws is logged and the task is skipped for that pass, matching the
existing failure policy.

## Firing once

A new nullable UTC column on `WorkflowTask`, mirroring `ReminderSentAt` exactly:

```csharp
/// <summary>
/// When TaskOverdue was dispatched for this task, in <b>UTC</b>, or null if it has
/// not been. Single-use, like ReminderSentAt: the claim that makes the sweep
/// idempotent, and the flag the inbox reads to say an escalation has gone out.
/// </summary>
public DateTime? OverdueFiredAt { get; set; }
```

Claimed with a conditional update inside `WorkflowTransaction.ExecuteAsync`, before the
dispatch rather than after, so a crash between the two produces a *missed* escalation
rather than a duplicate one. That is the right way round for the same reason it was for
reminders: a supervisor told twice about the same task stops reading the messages.

`WorkflowTransaction.ExecuteAsync` and never `BeginTransaction` — a provider configured
with `EnableRetryOnFailure` refuses a user-initiated transaction taken outside its
execution strategy, and this codebase has got that wrong twice.

An index mirroring the reminder one:

```csharp
e.HasIndex(x => new { x.Status, x.OverdueFiredAt })
 .HasFilter("[OverdueFiredAt] IS NULL");
```

## Where it runs

**Inside the existing processor and the existing hosted service, not new ones.** The two
sweeps share the subject lookup, the re-resolve-and-repair step, and the
claim-then-dispatch transaction; the only things that differ are the candidate query and
which column is claimed. Two processors would mean two timers, two registrations, two
sets of nearly identical tests, and two places to fix the next thing that is wrong with
sweeping.

`ClaimAndDispatchAsync` takes a private `Sweep` enum and branches on it, for the claim's
predicate and column and for the dispatched event kind. Not a passed-in expression: the
predicate as well as the assignment has to be translated by EF, and a runtime-selected
`Expression<Func<WorkflowTask, DateTime?>>` gives you the assignment only. One pass runs
both queries.

The class stops being called `ReminderProcessor`, because it no longer only does
reminders. The rename reaches the public surface:

| Now | After |
|---|---|
| `IWorkflowReminderProcessor` | `IWorkflowDeadlineProcessor` |
| `ReminderProcessor` | `DeadlineProcessor` |
| `WorkflowReminderHostedService` | `WorkflowDeadlineHostedService` |
| `WorkflowReminderOptions` | `WorkflowDeadlineOptions` |
| `AddReminderProcessing()` | `AddDeadlineProcessing()` |

This is a breaking change to a package with no consumers yet. It is free today and
expensive after the first release, which is the argument for doing it in this change
rather than leaving a misleading name in place.

## Demo host

The built-in `Notify` trigger cannot express this. `NotifyTrigger.ExecuteAsync` reads its
`recipient` with `context.Config.GetString("recipient")` and never renders it through
`TriggerTemplate`, and the token set has nothing that resolves an org chart anyway. A
seeded configuration is static; which Section Lead sits above a task depends on the section the
task landed in.

So the demo authors its own — `DemoEscalationTrigger`, keyed `demo.escalate`, registered
with `.AddTrigger<DemoEscalationTrigger>()`. It reads the task's `AssignedBranchKey` (the
demo's section `Code`), looks the section up, and notifies:

- the section's `SectionLeadActorId`, normally;
- the branch's `DivisionHeadActorId` when the late assignee **is** that Section Lead, because
  escalating to the person who is already late is not an escalation;
- nobody, when the late assignee is already the branch head. This is a design decision,
  not an omission: there is no rung above the top of this chart, and the alternative —
  falling through to the Section Lead — would send the message *down* it. Silence beats notifying
  somebody with no authority over the person who is late.

This is a better demonstration than a seeded `Notify` would have been. The spec's claim
is that escalation belongs to the host because only the host knows who is above whom;
a host trigger walking a host org model is that claim executed rather than asserted.

`DemoWorkflowSeeder` attaches it to the mainline `provide-input` task on `TaskOverdue`,
with `TriggerDispatchMode.AfterCommit` — the same reasoning as every other notification in
the seeder: a message cannot be un-sent if the transaction rolls back.

**`provide-input` and not `pm-review`, because the direction of an escalation depends on
which role holds the task.** `DemoAssignmentResolver` (`HostAdapters.cs:57-65`) resolves
`DemoRoles.SectionLead` to the section's `SectionLeadActorId` and `DemoRoles.DivisionHead` to the
branch's `DivisionHeadActorId`. `provide-input` is a Section Lead task, so its assignee **is** the
section Section Lead and the trigger takes its second branch — up to the branch head, which is an
escalation. `pm-review` is a DivisionHead task, so its assignee is already the branch
head — which is where the third case above applies: the top-of-ladder guard recognises the
assignee as the branch head, returns null, and the trigger sends nothing at all. So wiring
the demo there would have shipped a worked example of escalation that never fires, and a
demo that silently sends nothing demonstrates nothing.

That leaves the first branch unexercised by the seeded workflow, which is the right
trade: the seeded demo shows the case that makes sense, and
`DemoEscalationTests.It_escalates_to_the_sections_section_lead` covers the other by setting the
assignee directly. A demo is for showing the feature working, not for reaching every
branch of it.

## UI

The overdue *styling* already exists: `WorkflowInbox.razor:103` and
`WorkflowRunner.razor:185` render a past-due date in `Color.Error`, and the inbox prints
`"3d overdue"` through its `DueIn` helper. What no surface shows is whether anything was
*done* about it.

`OverdueFiredAt` is carried onto `WorkflowTaskSnapshot` and along the inbox's three-record
path — `InboxRow` (file-scoped, `WorkflowEngine.Inbox.cs`), then `InboxTaskSnapshot`, then
`InboxItem`. All four already carry `DueDate`, so this follows an established path through
the engine projection, the demo client, and the components — and the two surfaces
distinguish the states:

| State | Inbox | Runner task row |
|---|---|---|
| Due later | `due in 3d` | date chip, default colour |
| Late, nothing sent | `3d overdue`, error colour | date chip, error colour |
| Late, escalation sent | `3d overdue · escalated` | date chip plus an `escalated` chip |

`WorkflowTaskSnapshot` gains it as a defaulted trailing parameter, exactly as `DueDate`
was added, so the positional constructions elsewhere in the engine keep compiling.

## Testing

Integration tests against SQL Server, in the style of the existing suite:

- Fires `TaskOverdue` once for an open task whose deadline has passed.
- Does not fire before the deadline.
- Fires for a task with a due date and **no** reminder configuration — the no-opt-in rule.
- Does not fire for Completed, Cancelled, Forked, archived, or test-run tasks. The
  positive status list, checked explicitly, because `Forked` is the one a hand-written
  version forgets.
- A second sweep does not fire again.
- A deadline extended past now repairs `DueDate` and does not fire.
- A resolver that throws skips the task without failing the pass.
- The claim is honoured under a concurrent sweep — only one dispatch.
- Reminders and escalations both fire in a single pass for the same task where both apply.
- `MigrationTests` guards the new column.

The whole suite runs, because `DeadlineProcessor` is a shared path and the rename touches
registration.

## What this leaves for later

The ladder, repeat firing, a supervisor seam, and a grace period — all listed above as
out of scope, and none of them blocked by anything here. `TaskOverdue` is the hook each
would hang off, exactly as `TaskDueSoon` was the hook this one hung off.
