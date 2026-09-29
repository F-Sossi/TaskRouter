# Escalation Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raise `WorkflowEventKind.TaskOverdue` once per task when its deadline passes, so a host can author escalation as an ordinary trigger.

**Architecture:** The existing reminder sweep grows a second candidate query and a second claim column, and is renamed to reflect that it now handles both halves of a deadline. Escalation itself stays out of the engine: the demo host authors a trigger that walks its own org chart. Two UI surfaces gain a marker distinguishing "late" from "late, and someone was told".

**Tech Stack:** .NET 10, EF Core against SQL Server, MSTest integration tests, MudBlazor components, Blazor Server demo host.

**Spec:** `docs/superpowers/specs/2026-08-22-escalation-actions-design.md`

---

## Before you start

Everything here runs from the repository root.

**Environment.** `dotnet` is not on the default PATH, and the tests need the SA password:

```bash
export DOTNET_ROOT=~/.dotnet
export PATH=$DOTNET_ROOT:$PATH
source "$WORKFLOW_DEV_ENV"    # provides SA_PASSWORD
```

Every test in this repo is an integration test against the SQL Server in the the SQL Server dev container
container. There are no unit tests and no in-memory provider. `docker ps` should show
the SQL Server dev container up on `127.0.0.1:1433` before you start.

**Division.** The work belongs on `escalation-actions`, which already exists and already
carries the spec and this plan. `main` is untouched at `e58217c`.

```bash
git checkout escalation-actions
```

**Baseline.** Confirm green before changing anything:

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and
`Passed! - Failed: 0, Passed: 214, Skipped: 0, Total: 214`.

If the baseline is not green, stop and report it rather than building on top of it.

**Read first.** `src/Workflow.Persistence.EF/Triggers/ReminderProcessor.cs` in full. Every
decision in this plan is either a copy of something that file already does or a
documented departure from it, and the file's comments carry reasoning this plan does not
repeat.

## Three things the spec says that the code does not support

Task 1 amends the spec for these. They are listed here so you know why that task exists.

1. **The demo wiring cannot use the built-in `Notify` trigger.** The spec says to seed a
   `Notify` trigger "with the reviewing section's `SectionLeadActorId` as the recipient".
   `NotifyTrigger.ExecuteAsync` reads `recipient` with `context.Config.GetString("recipient")`
   and does **not** pass it through `TriggerTemplate.Render` — and even if it did, the
   token set (`{task.branch}`, `{task.assignee}`, …) has nothing that resolves an org
   chart. Seeded configuration is static, but which Section Lead is above a task depends on the
   section the task landed in. The demo therefore needs a host-authored trigger, which is
   also the more faithful demonstration of the spec's central claim.
2. **The inbox path has three records, not one.** The spec names `InboxItem`. The value
   has to cross `InboxRow` (file-scoped, `WorkflowEngine.Inbox.cs:13`) and
   `InboxTaskSnapshot` (`WorkflowEngine.Inbox.cs:31`) before it reaches `InboxItem`
   (`IWorkflowInboxClient.cs:48`).
3. **`ClaimAndDispatchAsync` cannot be "generic over the claimed column" the easy way.**
   `ExecuteUpdateAsync` takes an expression tree EF must translate; you cannot pass a
   `Expression<Func<WorkflowTask, DateTime?>>` selected at runtime into
   `SetProperty` and have EF translate the *predicate* too. The working shape is a branch
   on a private enum, which is what Task 5 uses.

## File structure

| File | Change | Responsible for |
|---|---|---|
| `src/Workflow.Core/Model/Enums.cs` | Modify | Appending `TaskOverdue` |
| `src/Workflow.Core/Model/Entities.cs` | Modify | `WorkflowTask.OverdueFiredAt` |
| `src/Workflow.Persistence.EF/WorkflowModelBuilder.cs` | Modify | The filtered index |
| `samples/DemoDocuments.Server/Data/Migrations/` | Create | The generated migration |
| `src/Workflow.Persistence.EF/Triggers/ReminderProcessor.cs` | Rename → `DeadlineProcessor.cs` | Both sweeps |
| `src/Workflow.Persistence.EF/Triggers/ReminderHostedService.cs` | Rename → `DeadlineHostedService.cs` | The timer |
| `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs` | Modify | Registration + `AddDeadlineProcessing` |
| `src/Workflow.Core/Abstractions/Snapshots.cs` | Modify | `WorkflowTaskSnapshot.OverdueFiredAt` |
| `src/Workflow.Persistence.EF/Snapshots.cs` | Modify | Mapping it |
| `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs` | Modify | Projecting it |
| `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs` | Modify | `InboxItem.OverdueFiredAt` |
| `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs` | Modify | `RunTaskView.OverdueFiredAt` |
| `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs` | Modify | Carrying it |
| `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs` | Modify | Carrying it |
| `src/Workflow.MudBlazor/WorkflowInbox.razor` | Modify | The `· escalated` suffix |
| `src/Workflow.MudBlazor/WorkflowRunner.razor` | Modify | The escalated chip |
| `samples/DemoDocuments.Server/Workflow/DemoEscalationTrigger.cs` | Create | Walking the demo org chart |
| `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs` | Modify | Seeding the escalation |
| `samples/DemoDocuments.Server/Program.cs` | Modify | Registering the trigger |
| `tests/Workflow.Tests/EscalationTests.cs` | Create | The sweep's behaviour |
| `tests/Workflow.Tests/TestHost.cs` | Modify | Exposing the renamed processor |
| `tests/Workflow.Tests/ReminderTests.cs` | Modify | Following the rename |
| `STATE.md` | Modify | Recording the feature |

---

## Task 1: Amend the spec where the code disagrees with it

The spec is the record of intent, and three of its statements do not survive contact with
the code. Fix the spec first so the rest of the plan is not implementing something the
spec contradicts. (`deadlines-and-reminders` did the same in commit `c06a1c5`.)

**Files:**
- Modify: `docs/superpowers/specs/2026-08-22-escalation-actions-design.md`

- [ ] **Step 1: Replace the "Demo host" section**

Find this section:

```markdown
## Demo host

`DemoWorkflowSeeder` seeds a `Notify` trigger on `TaskOverdue` for the Section Lead review task,
with the reviewing section's `SectionLeadActorId` as the recipient — one worked escalation,
using the org chain the demo already models, proving the feature end to end without
inventing anything for it.
```

Replace it with:

```markdown
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
  escalating to the person who is already late is not an escalation.

This is a better demonstration than a seeded `Notify` would have been. The spec's claim
is that escalation belongs to the host because only the host knows who is above whom;
a host trigger walking a host org model is that claim executed rather than asserted.

`DemoWorkflowSeeder` attaches it to the mainline `pm-review` task on `TaskOverdue`, with
`TriggerDispatchMode.AfterCommit` — the same reasoning as every other notification in the
seeder: a message cannot be un-sent if the transaction rolls back.
```

- [ ] **Step 2: Correct the inbox record list in the UI section**

Find this sentence in the `## UI` section:

```markdown
`OverdueFiredAt` is carried onto `InboxItem` and `WorkflowTaskSnapshot` — both already
carry `DueDate`, so this follows an established path through the engine projection, the
demo client, and the components — and the two surfaces distinguish the states:
```

Replace it with:

```markdown
`OverdueFiredAt` is carried onto `WorkflowTaskSnapshot` and along the inbox's three-record
path — `InboxRow` (file-scoped, `WorkflowEngine.Inbox.cs`), then `InboxTaskSnapshot`, then
`InboxItem`. All four already carry `DueDate`, so this follows an established path through
the engine projection, the demo client, and the components — and the two surfaces
distinguish the states:
```

- [ ] **Step 3: Correct the claim-generic claim**

Find this sentence in the `## Where it runs` section:

```markdown
`ClaimAndDispatchAsync` becomes generic over the claimed column and the dispatched event
kind. One pass runs both queries.
```

Replace it with:

```markdown
`ClaimAndDispatchAsync` takes a private `Sweep` enum and branches on it, for the claim's
predicate and column and for the dispatched event kind. Not a passed-in expression: the
predicate as well as the assignment has to be translated by EF, and a runtime-selected
`Expression<Func<WorkflowTask, DateTime?>>` gives you the assignment only. One pass runs
both queries.
```

- [ ] **Step 4: Mark the spec in progress**

Change the header line:

```markdown
**Status:** Designed — not yet implemented

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.
```

to:

```markdown
**Status:** In progress — see `docs/superpowers/plans/2026-08-22-escalation-actions.md`
```

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-08-22-escalation-actions-design.md
git commit -m "$(cat <<'EOF'
Amend the escalation spec where the code disagreed with it

Three statements did not survive contact with the code:

- The demo cannot use the built-in Notify trigger. Its recipient is read
  straight from config, never rendered through TriggerTemplate, and no
  token resolves an org chart. The demo authors its own trigger instead,
  which demonstrates the spec's own claim rather than asserting it.
- The inbox path is three records, not one: InboxRow, InboxTaskSnapshot,
  InboxItem.
- ClaimAndDispatchAsync branches on an enum rather than taking a column
  expression. EF has to translate the claim's predicate as well as its
  assignment, and a runtime-selected expression only gives the latter.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: The overdue column and its index

Schema first, because every test after this needs the column to exist.

**Files:**
- Modify: `src/Workflow.Core/Model/Entities.cs`
- Modify: `src/Workflow.Persistence.EF/WorkflowModelBuilder.cs`
- Create: `samples/DemoDocuments.Server/Data/Migrations/<timestamp>_Overdue.cs`

- [ ] **Step 1: Add the column**

In `src/Workflow.Core/Model/Entities.cs`, find `ReminderSentAt` on `WorkflowTask`:

```csharp
    /// <summary>
    /// When the due-soon reminder fired, in <b>UTC</b>. Doubles as the fire-once stamp
    /// and the row-claim target: the sweeper's conditional update sets it only where it
    /// is still null, so two app instances sweeping at once produce one dispatch.
    /// </summary>
    public DateTime? ReminderSentAt { get; set; }
```

Add directly beneath it:

```csharp
    /// <summary>
    /// When <see cref="WorkflowEventKind.TaskOverdue"/> fired for this task, in
    /// <b>UTC</b>. The same fire-once stamp and row-claim target as
    /// <see cref="ReminderSentAt"/>, for the other half of the deadline.
    ///
    /// Also read by the inbox and the runner, which is the one way it differs: a
    /// non-null value here is how a surface says "late, and somebody has been told"
    /// rather than merely "late".
    /// </summary>
    public DateTime? OverdueFiredAt { get; set; }
```

- [ ] **Step 2: Index for the overdue sweep**

In `src/Workflow.Persistence.EF/WorkflowModelBuilder.cs`, find the reminder index:

```csharp
            // The sweeper's candidate set: open, not yet reminded, configured to nudge.
            // Filtered on ReminderSentAt because the interesting rows are the ones that
            // have not fired, and every task that ever fires leaves the set permanently.
            // Without the filter this index grows with the table forever while the query
            // only ever wants its shrinking head.
            e.HasIndex(x => new { x.Status, x.ReminderSentAt })
             .HasFilter("[ReminderSentAt] IS NULL");
```

Add directly beneath it:

```csharp
            // The overdue sweep's candidate set. DueDate is in the key, unlike the
            // reminder index above, because that query does filter on it — see the
            // asymmetry argued out in DeadlineProcessor.OverdueCandidatesAsync. Same
            // filtered shape for the same reason: a task that fires leaves the set for
            // good, so the index only ever needs its shrinking head.
            e.HasIndex(x => new { x.Status, x.OverdueFiredAt, x.DueDate })
             .HasFilter("[OverdueFiredAt] IS NULL");
```

- [ ] **Step 3: Generate the migration**

The engine has no database of its own — its tables live on the host's context, so the
demo host owns the migrations.

```bash
dotnet ef migrations add Overdue \
  --project samples/DemoDocuments.Server --context DemoDbContext \
  --output-dir Data/Migrations
```

Expected: `Done. To undo this action, use 'ef migrations remove'` and two new files in
`samples/DemoDocuments.Server/Data/Migrations/`.

If `dotnet ef` is not found: `dotnet tool install --global dotnet-ef` then re-run.

- [ ] **Step 4: Read the generated migration before trusting it**

```bash
cat samples/DemoDocuments.Server/Data/Migrations/*_Overdue.cs
```

`Up()` must contain exactly two operations and nothing else:

```csharp
migrationBuilder.AddColumn<DateTime>(
    name: "OverdueFiredAt",
    table: "WorkflowTasks",
    type: "datetime2",
    nullable: true);

migrationBuilder.CreateIndex(
    name: "IX_WorkflowTasks_Status_OverdueFiredAt_DueDate",
    table: "WorkflowTasks",
    columns: new[] { "Status", "OverdueFiredAt", "DueDate" },
    filter: "[OverdueFiredAt] IS NULL");
```

If it contains anything else — a dropped index, a renamed column, an altered type — the
model has drifted from the previous migration and that drift is a separate bug. Stop and
report it rather than committing a migration that does more than this task asks for.

- [ ] **Step 5: Run the migration guards**

```bash
dotnet test --filter "FullyQualifiedName~MigrationTests"
```

Expected: `Passed! - Failed: 0, Passed: 2`.

`The_model_and_the_migrations_have_not_drifted` is the one that matters — it compares the
model against the migration snapshot without connecting, so it fails if you changed an
entity and skipped the migration.

- [ ] **Step 6: Commit**

```bash
git add src/Workflow.Core/Model/Entities.cs \
        src/Workflow.Persistence.EF/WorkflowModelBuilder.cs \
        samples/DemoDocuments.Server/Data/Migrations/
git commit -m "$(cat <<'EOF'
Add the overdue column

WorkflowTask.OverdueFiredAt, mirroring ReminderSentAt: the fire-once stamp
and the row-claim target for the other half of a deadline. It differs in
one way -- the inbox and the runner read it, so a surface can say "late,
and somebody has been told" rather than only "late".

The index carries DueDate in its key, unlike the reminder index, because
the overdue query does filter on it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: The TaskOverdue event

**Files:**
- Modify: `src/Workflow.Core/Model/Enums.cs`

- [ ] **Step 1: Append the event kind**

In `src/Workflow.Core/Model/Enums.cs`, find the end of `WorkflowEventKind` — `TaskDueSoon`
is currently the last member, with its own doc comment. After the closing of that member
(and before the enum's closing brace), add:

```csharp
,

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
    TaskOverdue
```

Take care with the comma: `TaskDueSoon` currently ends the enum with no trailing comma,
so you are adding `,` after it and then the new member.

- [ ] **Step 2: Verify it compiles**

```bash
dotnet build
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

The builder's trigger-event picker reads `Enum.GetValues<WorkflowEventKind>()`, so the new
event appears in `TriggerEditDialog` with no further work.

- [ ] **Step 3: Commit**

```bash
git add src/Workflow.Core/Model/Enums.cs
git commit -m "$(cat <<'EOF'
Add the TaskOverdue event

Appended, not inserted: these values persist as ints on TriggerDefinition
and TriggerExecution, so a mid-list insertion rewrites the meaning of
stored rows. Third time this file has had to say so.

Needs no configuration to fire, unlike TaskDueSoon. A reminder's lead time
is both its switch and its window; overdue has no window, so a switch would
be a switch and nothing else.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Rename the reminder sweep to the deadline sweep

A pure refactor, done **before** the behaviour change so the new sweep lands in its final
home and its tests are written against the final name. No test changes beyond following
the rename; the whole suite is the check.

This reaches the public surface. That is free today — the package has no consumers — and
expensive after a first release, which is the argument for doing it now.

**Files:**
- Rename: `src/Workflow.Persistence.EF/Triggers/ReminderProcessor.cs` → `DeadlineProcessor.cs`
- Rename: `src/Workflow.Persistence.EF/Triggers/ReminderHostedService.cs` → `DeadlineHostedService.cs`
- Modify: `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`
- Modify: `tests/Workflow.Tests/TestHost.cs`
- Modify: `tests/Workflow.Tests/ReminderTests.cs`
- Modify: `samples/DemoDocuments.Server/Program.cs`

- [ ] **Step 1: See the full extent of the rename first**

```bash
grep -rn 'IWorkflowReminderProcessor\|ReminderProcessor\|WorkflowReminderOptions\|WorkflowReminderHostedService\|AddReminderProcessing' \
  --include='*.cs' --include='*.razor' --include='*.md' src samples tests STATE.md
```

Read the whole list before editing. Every hit is renamed except ones inside prose in
`docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md` and
`docs/superpowers/plans/2026-08-21-deadlines-and-reminders.md` — **those two files are the
historical record of a finished branch and must not be edited.**

- [ ] **Step 2: Move the files**

```bash
git mv src/Workflow.Persistence.EF/Triggers/ReminderProcessor.cs \
       src/Workflow.Persistence.EF/Triggers/DeadlineProcessor.cs
git mv src/Workflow.Persistence.EF/Triggers/ReminderHostedService.cs \
       src/Workflow.Persistence.EF/Triggers/DeadlineHostedService.cs
```

- [ ] **Step 3: Rename the identifiers**

```bash
grep -rl 'IWorkflowReminderProcessor\|ReminderProcessor\|WorkflowReminderOptions\|WorkflowReminderHostedService\|AddReminderProcessing' \
  --include='*.cs' src samples tests \
| xargs sed -i \
  -e 's/IWorkflowReminderProcessor/IWorkflowDeadlineProcessor/g' \
  -e 's/\bReminderProcessor\b/DeadlineProcessor/g' \
  -e 's/WorkflowReminderOptions/WorkflowDeadlineOptions/g' \
  -e 's/WorkflowReminderHostedService/WorkflowDeadlineHostedService/g' \
  -e 's/AddReminderProcessing/AddDeadlineProcessing/g'
```

Order matters: `IWorkflowReminderProcessor` is substituted before the bare
`ReminderProcessor` word-boundary rule, so the interface does not become
`IWorkflowDeadlineProcessor` by two overlapping edits.

- [ ] **Step 4: Rename the TestHost property**

`sed` cannot do this one safely — `Reminders` is too common a word. Edit
`tests/Workflow.Tests/TestHost.cs` by hand. Find:

```csharp
    /// <summary>
    /// The reminder sweep. Null on a host built without triggers — the sweeper's whole
    /// job is to dispatch, so there is nothing to test without the trigger subsystem.
    /// </summary>
    public IWorkflowDeadlineProcessor? Reminders { get; private init; }
```

Replace with:

```csharp
    /// <summary>
    /// The deadline sweep — reminders and escalations both. Null on a host built without
    /// triggers: the sweeper's whole job is to dispatch, so there is nothing to test
    /// without the trigger subsystem.
    /// </summary>
    public IWorkflowDeadlineProcessor? Deadlines { get; private init; }
```

And further down, in the object initialiser of the returned `TestHost`, find:

```csharp
            Reminders = provider.GetRequiredService<IWorkflowDeadlineProcessor>()
```

Replace with:

```csharp
            Deadlines = provider.GetRequiredService<IWorkflowDeadlineProcessor>()
```

Then update the call sites in `tests/Workflow.Tests/ReminderTests.cs`:

```bash
sed -i 's/_host\.Reminders!/_host.Deadlines!/g' tests/Workflow.Tests/ReminderTests.cs
```

- [ ] **Step 5: Update the prose in the renamed files and the registration**

The identifiers are renamed but three doc comments now read oddly. Fix them by hand.

In `src/Workflow.Persistence.EF/Triggers/DeadlineHostedService.cs`, find:

```csharp
/// <summary>Options for the reminder sweep.</summary>
public sealed class WorkflowDeadlineOptions
```

Replace with:

```csharp
/// <summary>Options for the deadline sweep — reminders and escalations share it.</summary>
public sealed class WorkflowDeadlineOptions
```

In the same file, find the two log messages and widen them:

```csharp
        logger.LogInformation(
            "Workflow reminder sweep started; polling every {Interval}.", options.PollInterval);
```

becomes

```csharp
        logger.LogInformation(
            "Workflow deadline sweep started; polling every {Interval}.", options.PollInterval);
```

and

```csharp
                logger.LogError(ex, "Reminder sweep failed; retrying in {Backoff}.", options.ErrorBackoff);
```

becomes

```csharp
                logger.LogError(ex, "Deadline sweep failed; retrying in {Backoff}.", options.ErrorBackoff);
```

and

```csharp
        logger.LogInformation("Workflow reminder sweep stopped.");
```

becomes

```csharp
        logger.LogInformation("Workflow deadline sweep stopped.");
```

In `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`, find the summary above
`AddDeadlineProcessing` and replace the whole comment block plus signature line:

```csharp
    /// <summary>
    /// Runs the reminder sweep: a hosted service that asks
    /// <see cref="Triggers.IWorkflowDeadlineProcessor"/> for due reminders on a timer.
    ///
    /// **Without this nothing ever nudges anybody.** The columns are still written and the
    /// inbox still shows deadlines — visibility needs no background machinery — but no
    /// <see cref="WorkflowEventKind.TaskDueSoon"/> is ever raised.
```

with:

```csharp
    /// <summary>
    /// Runs the deadline sweep: a hosted service that asks
    /// <see cref="Triggers.IWorkflowDeadlineProcessor"/> on a timer for tasks that are
    /// due soon and tasks that are already late.
    ///
    /// **Without this nothing ever nudges anybody and nothing ever escalates.** The
    /// columns are still written and the inbox still shows deadlines — visibility needs no
    /// background machinery — but neither <see cref="WorkflowEventKind.TaskDueSoon"/> nor
    /// <see cref="WorkflowEventKind.TaskOverdue"/> is ever raised.
```

- [ ] **Step 6: Update the demo host's comment**

In `samples/DemoDocuments.Server/Program.cs`, find:

```csharp
    // And without this nothing ever nudges anybody. A short poll for the same reason --
    // the default is 5 minutes, which is far too slow to watch working.
    .AddDeadlineProcessing(o => o.PollInterval = TimeSpan.FromSeconds(15));
```

Replace with:

```csharp
    // And without this nothing ever nudges anybody and nothing ever escalates. A short
    // poll for the same reason -- the default is 5 minutes, far too slow to watch working.
    .AddDeadlineProcessing(o => o.PollInterval = TimeSpan.FromSeconds(15));
```

- [ ] **Step 7: Build and run the whole suite**

A rename either compiles everywhere or does not compile at all, and the suite is the only
thing that proves nothing else moved.

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and
`Passed! - Failed: 0, Passed: 214, Skipped: 0, Total: 214`.

Still 214 — this task adds no tests.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
Rename the reminder sweep to the deadline sweep

Pure refactor, before the behaviour change, so the overdue sweep lands in
its final home and its tests are written against the final name.

IWorkflowReminderProcessor  -> IWorkflowDeadlineProcessor
ReminderProcessor           -> DeadlineProcessor
WorkflowReminderOptions     -> WorkflowDeadlineOptions
WorkflowReminderHostedService -> WorkflowDeadlineHostedService
AddReminderProcessing()     -> AddDeadlineProcessing()
TestHost.Reminders          -> TestHost.Deadlines

This is a breaking change to a package with no consumers. Free now,
expensive after a release, and the old name stops being true in the very
next commit.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: The overdue sweep

The heart of the feature. TDD: the tests come first and must fail for the right reason.

**Files:**
- Create: `tests/Workflow.Tests/EscalationTests.cs`
- Modify: `src/Workflow.Persistence.EF/Triggers/DeadlineProcessor.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Workflow.Tests/EscalationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;
using Workflow.Persistence.EF.Triggers;

namespace Workflow.Tests;

/// <summary>
/// The overdue sweep.
///
/// Its sibling is <see cref="ReminderTests"/>, and the two differ in exactly one way
/// worth remembering: a reminder needs a lead time configured on the task definition, and
/// an escalation needs nothing at all. Overdue is a fact about a task, not a feature
/// somebody enables, so <see cref="It_fires_for_a_task_with_no_reminder_configuration"/>
/// is the test that pins the whole design decision down.
/// </summary>
[TestClass]
public class EscalationTests
{
    /// <summary>
    /// Answers a date the test controls, can be moved between sweeps, and can be made to
    /// throw — the sweep must treat a failing resolver as a skipped task, never a failed
    /// pass.
    /// </summary>
    private sealed class MutableDueDateResolver : IWorkflowDueDateResolver
    {
        public DateTime? Due { get; set; }
        public bool ShouldThrow { get; set; }

        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) =>
            ShouldThrow
                ? Task.FromException<DateTime?>(new InvalidOperationException("resolver down"))
                : Task.FromResult(Due);
    }

    private MutableDueDateResolver _dueDates = null!;
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _dueDates = new MutableDueDateResolver { Due = DateTime.UtcNow.AddDays(-1) };

        _host = await TestHost.CreateAsync(withTriggers: true, dueDates: _dueDates);

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Starts a run and attaches a notify trigger for TaskOverdue to the entry task's
    /// definition, so a dispatch is observable.
    ///
    /// Note what it does <b>not</b> do: it sets no ReminderLeadTimeMinutes. The seeded
    /// entry task ("Enter Record") has none, which is exactly the condition the overdue
    /// sweep must fire under and the reminder sweep must not.
    /// </summary>
    private async Task<WorkflowTask> OverdueTaskAsync(bool isTest = false)
    {
        var run = isTest
            ? (await _host.Engine.StartRunOnVersionAsync(
                new WorkflowSubject("ChangeRequest", "1"),
                await LatestVersionIdAsync(),
                "user-originator",
                isTest: true)).Unwrap()
            : (await _host.Engine.StartRunAsync(
                new WorkflowSubject("ChangeRequest", "1"), _definitionId, "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = task.TaskDefinitionId,
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = WorkflowEventKind.TaskOverdue,
            // InTransaction rather than the notify trigger's AfterCommit default, so the
            // notification lands in DemoNotificationSink by the time ProcessDueAsync
            // returns. Same reasoning as ReminderTests.
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Configuration = """{"subject":"Overdue","body":"This is late."}""",
            Order = 1,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();

        return task;
    }

    private async Task<int> LatestVersionIdAsync() =>
        await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _definitionId)
            .OrderByDescending(v => v.Version)
            .Select(v => v.Id)
            .FirstAsync();

    private async Task<DateTime?> OverdueFiredAtAsync(int taskId) =>
        await _host.Db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.OverdueFiredAt)
            .SingleAsync();

    private async Task<DateTime?> DueDateAsync(int taskId) =>
        await _host.Db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.DueDate)
            .SingleAsync();

    [TestMethod]
    public async Task It_fires_when_the_deadline_has_passed()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        var fired = await _host.Deadlines!.ProcessDueAsync();

        Assert.AreEqual(1, fired);
        Assert.IsNotNull(await OverdueFiredAtAsync(task.Id));
        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_fires_for_a_task_with_no_reminder_configuration()
    {
        // The seeded "Enter Record" definition has no ReminderLeadTimeMinutes, so the
        // reminder sweep will never look at it. This is the no-opt-in rule: overdue is a
        // fact about the task, not a feature somebody enabled.
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        var definition = await _host.Db.WorkflowTaskDefinitions
            .AsNoTracking().SingleAsync(d => d.Id == task.TaskDefinitionId);

        Assert.IsNull(definition.ReminderLeadTimeMinutes, "the fixture's premise");

        Assert.AreEqual(1, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNotNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_does_not_fire_before_the_deadline()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(30);
        var task = await OverdueTaskAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_does_not_fire_for_a_task_with_no_due_date()
    {
        _dueDates.Due = null;
        var task = await OverdueTaskAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_fires_once()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        Assert.AreEqual(1, await _host.Deadlines!.ProcessDueAsync());
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());

        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_completed_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        await _host.Deadlines!.ProcessDueAsync();

        // Asserted on this task rather than the pass total: completing it creates the
        // follow-on Provide Input task, which is itself overdue against this resolver and
        // legitimately fires. Same trap ReminderTests documents.
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
        Assert.AreEqual(0, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_cancelled_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        (await _host.Engine.CancelTaskAsync(task.Id, "user-originator", "not needed")).Unwrap();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_forked_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        // Set directly rather than by forking: this is a test of the candidate query's
        // status list, and Forked is the member a hand-written predicate forgets — a
        // superseded ghost that can never be completed and must never be escalated.
        var tracked = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        tracked.Status = WorkflowTaskStatus.Forked;
        await _host.Db.SaveChangesAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_an_archived_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        var tracked = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        tracked.IsArchived = true;
        await _host.Db.SaveChangesAsync();

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_task_in_a_test_run()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync(isTest: true);

        // The reason this predicate is in the library and not in each host: an admin
        // trying a workflow out in the builder must not escalate to a real supervisor.
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task A_deadline_moved_later_repairs_the_column_and_does_not_fire()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        // Extended before the sweep ever ran. Firing here would tell a supervisor about a
        // deadline that no longer exists, which is worse than not firing at all.
        var extended = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        _dueDates.Due = extended;

        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));

        // Re-resolving repairs the cached column as a side effect, which is what keeps
        // the inbox's Due column honest without a second sweep.
        Assert.AreEqual(extended, await DueDateAsync(task.Id));
    }

    [TestMethod]
    public async Task A_resolver_that_throws_skips_the_task_without_failing_the_pass()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        _dueDates.ShouldThrow = true;

        // Not an exception: the failure policy for a host resolver is logged-and-skipped,
        // matching IWorkflowDueDateResolver's documented contract.
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNull(await OverdueFiredAtAsync(task.Id));

        // And it recovers on the next pass rather than being poisoned.
        _dueDates.ShouldThrow = false;

        Assert.AreEqual(1, await _host.Deadlines!.ProcessDueAsync());
        Assert.IsNotNull(await OverdueFiredAtAsync(task.Id));
    }

    [TestMethod]
    public async Task Two_concurrent_sweeps_produce_one_dispatch()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        // Two scopes, two processors, two DbContexts — the shape a web farm has. One
        // shared context would prove nothing: it serialises the two claims for free.
        using var a = _host.Provider!.CreateScope();
        using var b = _host.Provider!.CreateScope();

        var results = await Task.WhenAll(
            a.ServiceProvider.GetRequiredService<IWorkflowDeadlineProcessor>().ProcessDueAsync(),
            b.ServiceProvider.GetRequiredService<IWorkflowDeadlineProcessor>().ProcessDueAsync());

        Assert.AreEqual(1, results.Sum(), "the conditional claim lets exactly one through");
        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task The_dispatch_is_attributed_to_the_system_actor()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(-1);
        var task = await OverdueTaskAsync();

        await _host.Deadlines!.ProcessDueAsync();

        var execution = await _host.Db.WorkflowTriggerExecutions
            .AsNoTracking()
            .Where(e => e.TaskId == task.Id && e.Event == WorkflowEventKind.TaskOverdue)
            .SingleAsync();

        // TriggerExecution has no ActorId column: WorkflowEntity's CreatorId is where the
        // dispatching actor lands (TriggerDispatcher.cs:172). Nobody acted, so it must not
        // be the assignee.
        Assert.AreEqual(WorkflowActors.System, execution.CreatorId);
    }

    [TestMethod]
    public async Task A_reminder_and_an_escalation_both_fire_in_one_pass()
    {
        // One task, both halves due: a lead time wide enough that it is inside the
        // reminder window, and a deadline already in the past.
        _dueDates.Due = DateTime.UtcNow.AddHours(-1);

        var task = await OverdueTaskAsync();

        var definition = await _host.Db.WorkflowTaskDefinitions
            .SingleAsync(d => d.Id == task.TaskDefinitionId);

        definition.ReminderLeadTimeMinutes = 2880;

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = task.TaskDefinitionId,
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = WorkflowEventKind.TaskDueSoon,
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Configuration = """{"subject":"Due soon","body":"This is due soon."}""",
            Order = 1,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();

        // Both sweeps run in the same pass and both claim their own column. A task that
        // is already late is also, trivially, inside its lead window — the two are not
        // exclusive and neither should suppress the other.
        Assert.AreEqual(2, await _host.Deadlines!.ProcessDueAsync());

        Assert.IsNotNull(await OverdueFiredAtAsync(task.Id));

        var reminded = await _host.Db.WorkflowTasks
            .AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.ReminderSentAt).SingleAsync();

        Assert.IsNotNull(reminded);
        Assert.AreEqual(2, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build 2>&1 | grep -E 'error|Build (succeeded|FAILED)'
```

Expected: **compile errors**, specifically `CS1061` on `_host.Deadlines` /
`OverdueFiredAt` being fine (those exist after Tasks 2 and 4) but the tests failing at
runtime instead. Concretely, after `dotnet build` succeeds, run:

```bash
dotnet test --filter "FullyQualifiedName~EscalationTests"
```

Expected: `Failed: 6, Passed: 8` or thereabouts — the "skips" tests pass vacuously
because nothing fires at all yet, while every test asserting a fire fails with
`Assert.AreEqual failed. Expected:<1>. Actual:<0>`.

That is the right failure: the sweep does not look for overdue tasks yet.

- [ ] **Step 3: Restructure ProcessDueAsync around two sweeps**

Open `src/Workflow.Persistence.EF/Triggers/DeadlineProcessor.cs`.

Replace the interface's doc comment and signature:

```csharp
public interface IWorkflowDeadlineProcessor
{
    /// <summary>
    /// Sweeps for tasks inside their reminder lead time and dispatches
    /// <see cref="WorkflowEventKind.TaskDueSoon"/> for each. Returns how many fired.
    /// </summary>
    Task<int> ProcessDueAsync(int batchSize = 100, CancellationToken ct = default);
}
```

with:

```csharp
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
```

Replace the class's doc comment:

```csharp
/// <summary>
/// The one thing in this engine that happens because <b>nobody acted</b>.
```

with:

```csharp
/// <summary>
/// The only two things in this engine that happen because <b>nobody acted</b>.
```

and, in the same comment block, replace:

```csharp
/// <para>Split from its hosted service exactly as <see cref="IWorkflowOutboxProcessor"/>
/// is from <see cref="WorkflowOutboxHostedService"/>: everything worth testing is here,
/// and none of it needs a host or a timer to reach.</para>
/// </summary>
```

with:

```csharp
/// <para>Split from its hosted service exactly as <see cref="IWorkflowOutboxProcessor"/>
/// is from <see cref="WorkflowOutboxHostedService"/>: everything worth testing is here,
/// and none of it needs a host or a timer to reach.</para>
///
/// <para>Reminders and escalations are one class rather than two because they differ in
/// only two things — the candidate query and the column claimed — and share everything
/// else: the subject lookup, the live re-resolution, the repair of the cached date, and
/// the claim-then-dispatch transaction. Two processors would mean two timers, two
/// registrations, two near-identical test files, and two places to fix the next thing
/// found wrong with sweeping.</para>
/// </summary>
```

Now replace the entire body of `ProcessDueAsync` — from `public async Task<int> ProcessDueAsync(`
down to the closing brace of that method — with:

```csharp
    /// <summary>Which half of a deadline a sweep is doing.</summary>
    private enum Sweep
    {
        Reminder,
        Overdue
    }

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

            if (await ClaimAndDispatchAsync(task, now, kind, ct).ConfigureAwait(false))
            {
                fired++;
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

            _ => false
        };
```

- [ ] **Step 4: Make the claim branch on the sweep**

Still in `DeadlineProcessor.cs`, replace the whole `ClaimAndDispatchAsync` method —
comment included — with:

```csharp
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
    /// <para>A branch rather than a passed-in column expression: EF has to translate the
    /// claim's <i>predicate</i> as well as its assignment, and a runtime-selected
    /// <c>Expression&lt;Func&lt;WorkflowTask, DateTime?&gt;&gt;</c> only gives the
    /// latter.</para>
    /// </summary>
    private async Task<bool> ClaimAndDispatchAsync(
        WorkflowTask task, DateTime now, Sweep kind, CancellationToken ct) =>
        // WorkflowTransaction.ExecuteAsync, never BeginTransaction: a provider configured
        // with EnableRetryOnFailure refuses a user-initiated transaction taken outside its
        // execution strategy. This codebase has got that wrong twice already.
        await WorkflowTransaction.ExecuteAsync(db, async token =>
        {
            var claimed = kind == Sweep.Reminder
                ? await db.WorkflowTasks
                    .Where(t => t.Id == task.Id && t.ReminderSentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReminderSentAt, now), token)
                    .ConfigureAwait(false)
                : await db.WorkflowTasks
                    .Where(t => t.Id == task.Id && t.OverdueFiredAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.OverdueFiredAt, now), token)
                    .ConfigureAwait(false);

            if (claimed == 0)
            {
                logger.LogDebug(
                    "Task {TaskId} was claimed by another {Sweep} sweep; skipping.",
                    task.Id, kind);
                return false;
            }

            var eventKind = kind == Sweep.Reminder
                ? WorkflowEventKind.TaskDueSoon
                : WorkflowEventKind.TaskOverdue;

            await dispatcher.DispatchAsync(
                task, eventKind, WorkflowActors.System, ct: token)
                .ConfigureAwait(false);

            return true;
        }, ct).ConfigureAwait(false);
```

- [ ] **Step 5: Run the escalation tests**

```bash
dotnet test --filter "FullyQualifiedName~EscalationTests"
```

Expected: `Passed! - Failed: 0, Passed: 14, Skipped: 0, Total: 14`.

- [ ] **Step 6: Run the reminder tests, which share the code you just rewrote**

```bash
dotnet test --filter "FullyQualifiedName~ReminderTests"
```

Expected: `Passed! - Failed: 0, Passed: 13`.

If `A_deadline_moved_earlier_brings_the_nudge_forward` fails, you narrowed the *reminder*
query on `DueDate`. That is the one thing this task must not do.

- [ ] **Step 7: Run the whole suite — this changed a shared code path**

```bash
dotnet test
```

Expected: `Passed! - Failed: 0, Passed: 228, Skipped: 0, Total: 228` (214 + 14).

- [ ] **Step 8: Commit**

```bash
git add src/Workflow.Persistence.EF/Triggers/DeadlineProcessor.cs \
        tests/Workflow.Tests/EscalationTests.cs
git commit -m "$(cat <<'EOF'
Add the overdue sweep

One pass, two candidate queries, two claim columns. A task that is both
inside its lead window and past its deadline fires both events; neither
half suppresses the other.

The overdue query narrows on the cached DueDate, which is exactly what the
reminder query refuses to do. With no opt-in there is no lead-time
predicate left to bound the set, and an unshrinking set makes
OrderBy(Id).Take(batchSize) starve its own tail. The cost lands on the case
the reminder comment warns about -- a deadline pulled forward on a task
nobody reminds escalates late -- and the live re-resolution keeps it late
rather than wrong.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Carry OverdueFiredAt into the inbox

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs`
- Modify: `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs`
- Modify: `src/Workflow.MudBlazor/WorkflowInbox.razor`
- Modify: `tests/Workflow.Tests/InboxClientTests.cs`

- [ ] **Step 1: Write the failing test**

Append this test to the existing class in `tests/Workflow.Tests/InboxClientTests.cs`
(inside the closing brace, matching the file's existing style):

```csharp
    [TestMethod]
    public async Task An_escalated_task_carries_its_escalation_stamp()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var stamped = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        entry.OverdueFiredAt = stamped;
        await _host.Db.SaveChangesAsync();

        var items = await _client.GetInboxAsync("user-originator");

        // The inbox distinguishes "late" from "late, and somebody has been told". Only
        // the second of those carries a stamp, and it has to survive the whole path —
        // InboxRow, InboxTaskSnapshot, InboxItem — to get here.
        Assert.AreEqual(stamped, items.Single().OverdueFiredAt);
    }
```

`CreateDocumentAsync` and `StartRunOnAsync` are the file's existing helpers, and
`GetInboxAsync` takes an actor id and nothing else — both verified against the file, not
assumed.

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet build 2>&1 | grep -E 'error CS'
```

Expected: `error CS1061: 'InboxItem' does not contain a definition for 'OverdueFiredAt'`.

That is the right failure — the value has no path to the client yet.

- [ ] **Step 3: Add it to the engine's projection**

In `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs`, add a trailing member to the
file-scoped `InboxRow` record. Find:

```csharp
    DateTime Created,
    DateTime? DueDate);
```

Replace with:

```csharp
    DateTime Created,
    DateTime? DueDate,
    DateTime? OverdueFiredAt);
```

Then add it to the public `InboxTaskSnapshot`. Find:

```csharp
    /// <summary>
    /// When this is due, in <b>UTC</b>, or null for no deadline. Straight from the
    /// engine's cached column — the sweeper repairs it, so it is as fresh as the last
    /// sweep. Compare against <see cref="DateTime.UtcNow"/>.
    /// </summary>
    DateTime? DueDate);
```

Replace with:

```csharp
    /// <summary>
    /// When this is due, in <b>UTC</b>, or null for no deadline. Straight from the
    /// engine's cached column — the sweeper repairs it, so it is as fresh as the last
    /// sweep. Compare against <see cref="DateTime.UtcNow"/>.
    /// </summary>
    DateTime? DueDate,

    /// <summary>
    /// When <see cref="WorkflowEventKind.TaskOverdue"/> fired for this task, in
    /// <b>UTC</b>, or null if it has not. A past <see cref="DueDate"/> says the task is
    /// late; this says something was raised about it, which is a different fact and the
    /// one a triage list needs to avoid chasing the same task twice.
    /// </summary>
    DateTime? OverdueFiredAt);
```

Now the two projections. In the first, find:

```csharp
                    Created: t.Created,
                    DueDate: t.DueDate))
```

Replace with:

```csharp
                    Created: t.Created,
                    DueDate: t.DueDate,
                    OverdueFiredAt: t.OverdueFiredAt))
```

In the second, find:

```csharp
                Created: r.Created,
                DueDate: r.DueDate))];
```

Replace with:

```csharp
                Created: r.Created,
                DueDate: r.DueDate,
                OverdueFiredAt: r.OverdueFiredAt))];
```

- [ ] **Step 4: Add it to the client seam**

In `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs`, find the end of the `InboxItem`
record:

```csharp
    DateTime Created,
    DateTime? DueDate);
```

Replace with:

```csharp
    DateTime Created,
    DateTime? DueDate,

    /// <summary>
    /// When an escalation was raised for this task, in <b>UTC</b>, or null if none was.
    /// Lets the row say "late, and somebody has been told" rather than only "late".
    /// </summary>
    DateTime? OverdueFiredAt);
```

In `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs`, find:

```csharp
                    Created: r.Created,
                    DueDate: r.DueDate);
```

Replace with:

```csharp
                    Created: r.Created,
                    DueDate: r.DueDate,
                    OverdueFiredAt: r.OverdueFiredAt);
```

- [ ] **Step 5: Render it**

In `src/Workflow.MudBlazor/WorkflowInbox.razor`, find the Due cell:

```razor
                <MudTd DataLabel="Due">
                    @if (context.DueDate is { } due)
                    {
                        <MudText Typo="Typo.body2"
                                 Color="@(due < DateTime.UtcNow ? Color.Error : Color.Default)">
                            @DueIn(due)
                        </MudText>
                    }
                    else
                    {
                        <MudText Typo="Typo.body2" Class="mud-text-secondary">—</MudText>
                    }
                </MudTd>
```

Replace with:

```razor
                <MudTd DataLabel="Due">
                    @if (context.DueDate is { } due)
                    {
                        <MudText Typo="Typo.body2"
                                 Color="@(due < DateTime.UtcNow ? Color.Error : Color.Default)">
                            @DueIn(due)@(context.OverdueFiredAt is null ? "" : " · escalated")
                        </MudText>
                    }
                    else
                    {
                        <MudText Typo="Typo.body2" Class="mud-text-secondary">—</MudText>
                    }
                </MudTd>
```

A suffix on the existing text rather than a second chip: this is a dense triage table,
and the distinction being drawn is a footnote on lateness, not a fact of its own.

- [ ] **Step 6: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~InboxClientTests|FullyQualifiedName~InboxTests"
```

Expected: all pass, with one more than before in `InboxClientTests`.

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs \
        src/Workflow.Core/Inbox/IWorkflowInboxClient.cs \
        samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs \
        src/Workflow.MudBlazor/WorkflowInbox.razor \
        tests/Workflow.Tests/InboxClientTests.cs
git commit -m "$(cat <<'EOF'
Show the escalation stamp in the inbox

OverdueFiredAt across the inbox's three records -- InboxRow,
InboxTaskSnapshot, InboxItem -- and rendered as a suffix on the existing
Due text rather than a chip of its own. A dense triage table; the
distinction is a footnote on lateness, not a fact beside it.

"3d overdue" and "3d overdue - escalated" are different situations, and
the second is the one you do not need to chase.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Carry OverdueFiredAt into the runner

**Files:**
- Modify: `src/Workflow.Core/Abstractions/Snapshots.cs`
- Modify: `src/Workflow.Persistence.EF/Snapshots.cs`
- Modify: `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs`
- Modify: `src/Workflow.MudBlazor/WorkflowRunner.razor`

- [ ] **Step 1: Carry it on the task snapshot**

In `src/Workflow.Core/Abstractions/Snapshots.cs`, find the end of `WorkflowTaskSnapshot`:

```csharp
    DateTime? DueDate = null);
```

Replace with:

```csharp
    DateTime? DueDate = null,

    /// <summary>
    /// When <see cref="WorkflowEventKind.TaskOverdue"/> fired for this task, in
    /// <b>UTC</b>, or null if it has not. Defaulted for the same reason
    /// <see cref="DueDate"/> is: the positional constructions elsewhere in the engine —
    /// the assignment resolver's Placeholder among them — must keep compiling.
    /// </summary>
    DateTime? OverdueFiredAt = null);
```

`WorkflowEventKind` is in `Workflow.Core.Model`; check the file's `using` block has it and
add `using Workflow.Core.Model;` if not.

In `src/Workflow.Persistence.EF/Snapshots.cs`, find:

```csharp
        CompletedDate: t.CompletedDate,
        DueDate: t.DueDate);
```

Replace with:

```csharp
        CompletedDate: t.CompletedDate,
        DueDate: t.DueDate,
        OverdueFiredAt: t.OverdueFiredAt);
```

- [ ] **Step 2: Carry it to the runner's view**

In `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs`, find in `RunTaskView`:

```csharp
    /// <summary>When this task is due, in <b>UTC</b>, or null for no deadline.</summary>
    DateTime? DueDate,
```

Replace with:

```csharp
    /// <summary>When this task is due, in <b>UTC</b>, or null for no deadline.</summary>
    DateTime? DueDate,

    /// <summary>
    /// When an escalation was raised for this task, in <b>UTC</b>, or null if none was.
    /// </summary>
    DateTime? OverdueFiredAt,
```

In `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs`, find:

```csharp
                    AssignedBranchKey: t.AssignedBranchKey,
                    DueDate: t.DueDate,
```

Replace with:

```csharp
                    AssignedBranchKey: t.AssignedBranchKey,
                    DueDate: t.DueDate,
                    OverdueFiredAt: t.OverdueFiredAt,
```

- [ ] **Step 3: Render it**

In `src/Workflow.MudBlazor/WorkflowRunner.razor`, find the due-date chip:

```razor
                        @if (task.DueDate is { } due)
                        {
                            <MudChip T="string" Size="Size.Small"
                                     Variant="Variant.Outlined"
                                     Color="@(due < DateTime.UtcNow ? Color.Error : Color.Default)"
                                     Icon="@Icons.Material.Filled.Schedule">
                                @due.ToString("d MMM yyyy")
                            </MudChip>
                        }
```

Add directly beneath it:

```razor
                        @* A chip of its own, unlike the inbox's suffix. One task is being
                           worked here rather than triaged among many, so "somebody has
                           already been told about this" is a fact worth its own weight
                           instead of a footnote on the date. *@
                        @if (task.OverdueFiredAt is not null)
                        {
                            <MudChip T="string" Size="Size.Small"
                                     Variant="Variant.Outlined"
                                     Color="Color.Warning"
                                     Icon="@Icons.Material.Filled.NotificationImportant">
                                escalated
                            </MudChip>
                        }
```

- [ ] **Step 4: Build and run the whole suite**

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Failed: 0`, with the same total
as after Task 6.

`WorkflowTaskSnapshot` is handed to every trigger and every route condition, so the whole
suite is the check that a defaulted trailing parameter broke none of them.

- [ ] **Step 5: Commit**

```bash
git add src/Workflow.Core/Abstractions/Snapshots.cs \
        src/Workflow.Persistence.EF/Snapshots.cs \
        src/Workflow.Core/Runner/IWorkflowRunnerClient.cs \
        samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs \
        src/Workflow.MudBlazor/WorkflowRunner.razor
git commit -m "$(cat <<'EOF'
Show the escalation stamp on a runner task row

A chip of its own rather than the inbox's text suffix. One task is being
worked here instead of triaged among many, so "somebody has already been
told" earns its own weight rather than a footnote on the date.

WorkflowTaskSnapshot takes it as a defaulted trailing parameter, exactly
as DueDate did, so the positional constructions elsewhere keep compiling.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: The demo escalation trigger

The spec's claim is that escalation belongs to the host because only the host knows who
is above whom. This is that claim executed.

**Files:**
- Create: `samples/DemoDocuments.Server/Workflow/DemoEscalationTrigger.cs`
- Modify: `samples/DemoDocuments.Server/Program.cs`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs`
- Create: `tests/Workflow.Tests/DemoEscalationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Workflow.Tests/DemoEscalationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;

namespace Workflow.Tests;

/// <summary>
/// The demo host's escalation, which is the worked example the engine deliberately does
/// not provide. The engine raises TaskOverdue and stops; who sits above a late person is
/// host knowledge, and this is a host walking its own org chart to find out.
/// </summary>
[TestClass]
public class DemoEscalationTests
{
    private sealed class FixedDueDateResolver : IWorkflowDueDateResolver
    {
        public DateTime? Due { get; set; } = DateTime.UtcNow.AddDays(-1);

        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) => Task.FromResult(Due);
    }

    private TestHost _host = null!;

    [TestInitialize]
    public async Task Setup() =>
        _host = await TestHost.CreateAsync(
            withTriggers: true,
            dueDates: new FixedDueDateResolver(),
            configure: services => services.AddScoped<IWorkflowTrigger, DemoEscalationTrigger>());

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Runs one task through the trigger directly, with the given branch key and
    /// assignee, and returns who the notification went to.
    /// </summary>
    private async Task<string?> EscalateAsync(string? branchKey, string? assignee)
    {
        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        task.AssignedBranchKey = branchKey;
        task.AssignedToActorId = assignee;
        task.DueDate = DateTime.UtcNow.AddDays(-1);
        await _host.Db.SaveChangesAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = task.TaskDefinitionId,
            TriggerKey = DemoEscalationTrigger.TriggerKey,
            Event = WorkflowEventKind.TaskOverdue,
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Configuration = """{"subject":"Overdue","body":"Task {task.id} is late."}""",
            Order = 1,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();

        await _host.Deadlines!.ProcessDueAsync();

        return _host.Notifications.Delivered
            .Where(n => n.TaskId == task.Id)
            .Select(n => n.RecipientActorId)
            .SingleOrDefault();
    }

    [TestMethod]
    public async Task It_escalates_to_the_sections_section_lead()
    {
        // C100's Section Lead, per DemoWorkflowSeeder's org seed.
        Assert.AreEqual("user-section-lead-c100", await EscalateAsync("C100", "user-engineer"));
    }

    [TestMethod]
    public async Task It_escalates_past_a_section_lead_who_is_the_late_one()
    {
        // Escalating to the person who is already late is not an escalation.
        Assert.AreEqual("user-branch-head", await EscalateAsync("C100", "user-section-lead-c100"));
    }

    [TestMethod]
    public async Task It_sends_nothing_when_the_section_is_unknown()
    {
        // A task with no branch key has no org position, so there is nobody above it.
        // Silence beats guessing at a recipient.
        Assert.IsNull(await EscalateAsync(null, "user-engineer"));
    }
}
```

`TestHost.CreateAsync(bool withTriggers, Action<IServiceCollection>? configure,
IWorkflowDueDateResolver? dueDates)` — verified against `TestHost.cs:68`. The `configure`
hook runs last, so a test can register its own trigger exactly as this one does.

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet build 2>&1 | grep -E 'error CS'
```

Expected: `error CS0246: The type or namespace name 'DemoEscalationTrigger' could not be
found`.

- [ ] **Step 3: Write the trigger**

Create `samples/DemoDocuments.Server/Workflow/DemoEscalationTrigger.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Data;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Escalation, which the engine deliberately does not provide.
///
/// The engine raises <see cref="WorkflowEventKind.TaskOverdue"/> and stops there. It has
/// no notion of a supervisor, because who sits above a late person is a fact about an
/// organisation and every host models that differently — a seam in the library would be
/// a seam every host had to implement to use a feature most of them want one line of.
///
/// So this is a host trigger walking a host org chart:
/// <c>AssignedBranchKey</c> is the demo's section <c>Code</c>, the section knows its
/// Section Lead, and the branch above it knows its head.
///
/// <para>It cannot be the built-in <c>workflow.notify</c> trigger. That one reads its
/// recipient straight from stored configuration and never renders it through
/// TriggerTemplate, and no token resolves an org chart anyway — but a seeded
/// configuration is static while the Section Lead above a task depends on which section the task
/// landed in.</para>
/// </summary>
public sealed class DemoEscalationTrigger(DemoDbContext db) : IWorkflowTrigger
{
    public const string TriggerKey = "demo.escalate";

    public string Key => TriggerKey;

    public TriggerDescriptor Describe() => new(
        Key,
        "Escalate to Supervisor",
        "Notifies the person above the task's assignee: the section's Section Lead, or the "
        + "branch head when the Section Lead is the one who is late.",
        // Only TaskOverdue. Every other built-in trigger declares the whole enum, but
        // this one means nothing on TaskCreated, and the builder's event picker filters
        // by exactly this list — so narrowing it is what stops somebody authoring an
        // escalation that can never make sense.
        SupportedEvents: [WorkflowEventKind.TaskOverdue],
        Parameters:
        [
            new("subject", "Subject", TriggerParameterKind.Text, Required: true),
            new("body", "Body", TriggerParameterKind.Template, Required: true)
        ],
        // After commit, never inline: a message to somebody's supervisor cannot be
        // un-sent if the transaction rolls back.
        DefaultDispatch: TriggerDispatchMode.AfterCommit);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sink = context.Services?.GetService<IWorkflowNotificationSink>();
        if (sink is null)
        {
            return;
        }

        var recipient = await SupervisorOfAsync(
            context.Task.AssignedBranchKey, context.Task.AssignedToActorId, ct)
            .ConfigureAwait(false);

        // Nobody above them, or no org position at all. Silence beats guessing: a
        // notification sent to the wrong person is worse than one not sent.
        if (recipient is null)
        {
            return;
        }

        await sink.SendAsync(new WorkflowNotification(
            context.Subject,
            context.Task.Id,
            recipient,
            context.Config.GetString("subject") ?? "Task overdue",
            Render(context.Config.GetString("body"), context)), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One step up the org chart. Normally the section's Section Lead — but when the late person
    /// <i>is</i> that Section Lead, one step up is the branch head, because escalating to the
    /// person who is already late is not an escalation.
    /// </summary>
    private async Task<string?> SupervisorOfAsync(
        string? branchKey, string? assignee, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(branchKey))
        {
            return null;
        }

        var section = await db.Sections
            .AsNoTracking()
            .Include(s => s.Branch)
            .SingleOrDefaultAsync(s => s.Code == branchKey, ct)
            .ConfigureAwait(false);

        if (section is null)
        {
            return null;
        }

        return string.Equals(section.SectionLeadActorId, assignee, StringComparison.Ordinal)
            ? section.Branch?.DivisionHeadActorId
            : section.SectionLeadActorId;
    }

    /// <summary>
    /// The two tokens worth having here. TriggerTemplate in the library is internal, and
    /// duplicating its whole token set for a demo trigger would be worse than supporting
    /// the two a supervisor actually needs: which task, and who was holding it.
    /// </summary>
    private static string Render(string? template, WorkflowTriggerContext context) =>
        (template ?? string.Empty)
            .Replace("{task.id}",
                context.Task.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase)
            .Replace("{task.assignee}",
                context.Task.AssignedToActorId ?? "nobody",
                StringComparison.OrdinalIgnoreCase);
}
```

`DemoDbContext` exposes `Sections`, `Branches` and `People` (`DemoDbContext.cs:26-28`) —
verified, not assumed. `Section.Branch` is a real navigation, so the `Include` resolves.

- [ ] **Step 4: Register it in the demo host**

In `samples/DemoDocuments.Server/Program.cs`, find:

```csharp
    .AddRouteCondition<RequiresReviewCondition>()
```

Add directly beneath it:

```csharp
    // Escalation is host code by design -- the engine raises TaskOverdue and has no
    // notion of who is above whom. This walks the demo's own org chart.
    .AddTrigger<DemoEscalationTrigger>()
```

- [ ] **Step 5: Seed a worked escalation**

In `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs`, find the last seeded
trigger:

```csharp
        // Conditional: only notify on rejection.
        Trigger(pmReview, BuiltInTriggerKeys.Notify, WorkflowEventKind.TaskCompleted,
            """{"subject":"Work rejected","body":"Task {task.id} was rejected."}""",
            TriggerDispatchMode.AfterCommit,
            condition: "task.outcome == 'rejected'",
            order: 2);
```

Add directly beneath it:

```csharp
        // Escalate when a review goes past its deadline. On provideInput rather than
        // pmReview because provideInput is the task that carries a reminder lead time and
        // is assigned into a section -- so the demo shows both halves of a deadline on one
        // task, and the trigger has a section to walk up from.
        Trigger(provideInput, DemoEscalationTrigger.TriggerKey, WorkflowEventKind.TaskOverdue,
            """{"subject":"Overdue review","body":"Task {task.id} is overdue; {task.assignee} was holding it."}""",
            TriggerDispatchMode.AfterCommit,
            order: 3);
```

- [ ] **Step 6: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~DemoEscalationTests"
```

Expected: `Passed! - Failed: 0, Passed: 3`.

- [ ] **Step 7: Run the whole suite — the seeder feeds every test's fixture**

```bash
dotnet test
```

Expected: `Failed: 0`. Every test class seeds via `DemoWorkflowSeeder`, so a new seeded
trigger is a change to every fixture in the suite. If `TriggerRuntimeTests` or
`BuilderClientTests` counts a number of triggers, that count moved by one and the
assertion needs updating to match — check the failure before changing anything, because a
count that was asserting something else is a different problem.

- [ ] **Step 8: Commit**

```bash
git add samples/DemoDocuments.Server/Workflow/DemoEscalationTrigger.cs \
        samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs \
        samples/DemoDocuments.Server/Program.cs \
        tests/Workflow.Tests/DemoEscalationTests.cs
git commit -m "$(cat <<'EOF'
Escalate to a supervisor in the demo host

The engine raises TaskOverdue and stops; who sits above a late person is
host knowledge. This is a host trigger walking a host org chart --
AssignedBranchKey is the section Code, the section knows its Section Lead, the
branch above knows its head -- which demonstrates the spec's claim rather
than asserting it.

It escalates past a Section Lead who is themselves the late one, because
escalating to the person who is already late is not an escalation. It
sends nothing when the task has no org position: a notification to the
wrong person is worse than one not sent.

Declares only TaskOverdue in SupportedEvents, unlike the built-ins that
declare the whole enum. The builder's event picker filters on that list,
so narrowing it is what stops somebody authoring an escalation on
TaskCreated.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: Record it and finish

**Files:**
- Modify: `STATE.md`
- Modify: `docs/superpowers/specs/2026-08-22-escalation-actions-design.md`

- [ ] **Step 1: Update the Status block in STATE.md**

Find the test count near the top:

```markdown
**Builds clean, 214/214 integration tests pass** against SQL Server.
```

Replace `214/214` with the actual final total from your last full run. Do not guess it —
paste what `dotnet test` printed.

- [ ] **Step 2: Move escalation out of "Not done"**

In `STATE.md`, find this bullet under `### Not done`:

```markdown
- **Escalation actions.** Nothing reassigns, notifies a supervisor, or cancels when a
  deadline actually passes. `TaskDueSoon` is the pattern a `TaskOverdue` event and its
  actions would follow, and the sweeper is where they go.
```

Delete it, and add to the `### Done` list (matching that list's existing style):

```markdown
- **Escalation.** `TaskOverdue` fires once per task when its deadline passes, swept by
  `DeadlineProcessor` alongside reminders. No opt-in: overdue is a fact about a task, not
  a feature you enable. Escalation itself is host code — the demo's
  `DemoEscalationTrigger` walks section → Section Lead → branch head — because the engine has no
  notion of who is above whom.
```

- [ ] **Step 3: Add the asymmetry to "Things to be careful about"**

In `STATE.md`, under `## Things to be careful about`, add:

```markdown
- **The two deadline sweeps narrow differently, on purpose.** The reminder query does not
  filter on `DueDate` — a deadline moved earlier would never enter a set narrowed to
  "already inside its window". The overdue query does filter on it, because with no opt-in
  there is no other bounding predicate and an unshrinking candidate set makes
  `OrderBy(Id).Take(batchSize)` starve its own tail. The cost: a deadline pulled *forward*
  on a task with no reminder configured escalates late, once the stale cached date passes.
  Late, never wrong — the live re-resolution refuses to fire against a date that no longer
  exists. Both queries are commented; read them before changing either.
```

- [ ] **Step 4: Mark the spec implemented**

In `docs/superpowers/specs/2026-08-22-escalation-actions-design.md`, change:

```markdown
**Status:** In progress — see `docs/superpowers/plans/2026-08-22-escalation-actions.md`
```

to:

```markdown
**Status:** Implemented 2026-08-22 — see `docs/superpowers/plans/2026-08-22-escalation-actions.md`
```

- [ ] **Step 5: Final verification**

```bash
dotnet build && dotnet test
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` and `Failed: 0`.

Both numbers go in the commit message, and neither is written before it is seen.

- [ ] **Step 6: Commit**

```bash
git add STATE.md docs/superpowers/specs/2026-08-22-escalation-actions-design.md
git commit -m "$(cat <<'EOF'
Record escalation in STATE.md

Moves escalation from Not done to Done, and adds the deadline sweeps'
deliberate asymmetry to the careful-about list: the reminder query refuses
to narrow on DueDate and the overdue query must. Anyone touching either
one will otherwise "fix" the inconsistency.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 7: Hand the branch back**

Do not merge. Report the final test count and stop — how the branch is integrated is
`superpowers:finishing-a-development-branch`'s question and the user's decision.

---

## Self-review notes

Checked against the spec, section by section:

| Spec section | Task |
|---|---|
| The event | 3 |
| Which tasks the sweep considers (no opt-in) | 5, tested by `It_fires_for_a_task_with_no_reminder_configuration` |
| The asymmetry with the reminder sweep | 5, documented in `OverdueCandidatesAsync` and STATE.md |
| Re-resolution | 5, tested by `A_deadline_moved_later_repairs_the_column_and_does_not_fire` |
| Firing once (column, claim, index) | 2 and 5 |
| Where it runs (one processor, the rename) | 4 and 5 |
| Demo host | 8 |
| UI | 6 and 7 |
| Testing | 5, 6, 7, 8 |

Every test named in the spec's Testing section has a task. The spec's "Forked" case is
covered by setting the status directly rather than by forking, which is the honest shape
for a test of a query predicate.

Two numbers in this plan are predictions rather than facts and must be replaced by what
the terminal actually prints: the 228 total in Task 5 Step 7, and the "Failed: 6, Passed:
8" split in Task 5 Step 2.
