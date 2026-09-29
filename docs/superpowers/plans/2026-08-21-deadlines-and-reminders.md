# Deadlines and Reminders Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give a task a deadline the host supplies, show it wherever the task is seen, and nudge the person holding it before the deadline passes.

**Architecture:** Three separable pieces. A host seam (`IWorkflowDueDateResolver`) answers "when is this due?", called from the single `NewTask` funnel so no creation path can forget it. Two cached columns (`WorkflowTask.DueDate`, `WorkflowTask.ReminderSentAt`) plus one definition column (`ReminderLeadTimeMinutes`) make the question answerable in SQL. A sweeper — a scoped `IWorkflowReminderProcessor` driven by a thin `BackgroundService`, mirroring the outbox exactly — re-resolves each open candidate, claims the row conditionally, and dispatches a new `WorkflowEventKind.TaskDueSoon` through the existing trigger runtime, where `workflow.notify` already delivers it.

**Tech Stack:** .NET 10, EF Core against SQL Server, MSTest integration tests, Blazor Server + MudBlazor 9.7.0.

**Spec:** `docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md`

---

## Three departures from the spec, and why

The spec was approved before the code was read closely. Three of its statements do not
survive contact with the codebase. **Task 1 amends the spec** so the two documents agree;
the reasoning is recorded here because the plan is what gets executed.

### 1. The funnel resolves the subject itself; it is not passed one

The spec says `NewTask` "takes the run's `WorkflowSubject`". It cannot, cheaply.
`LoadTaskAsync` (`src/Workflow.Persistence.EF/WorkflowEngine.cs:448`) does **not**
`Include(t => t.Run)`, and of the eight creation sites only `StartOnAsync` has a subject
in hand. Threading a subject parameter through means adding a run load at seven call
sites — the exact per-site burden the funnel exists to remove, reintroduced as the price
of using it.

Instead `NewTaskAsync` resolves the subject from `runId` through a memoised
`Dictionary<int, WorkflowSubject>` on the engine, seeded by `StartOnAsync` with the
subject it already holds. The engine is scoped, so the dictionary lives for one unit of
work. Caching is safe because a run's subject is written once at creation and never
mutated — nothing in `IWorkflowEngine` exposes a way to change it.

### 2. The sweeper is split the way the outbox is split

The spec names only `WorkflowReminderHostedService`. That would put the query, the
re-resolution, the claim and the dispatch inside a `BackgroundService`, and every test in
the spec's own sweeper list ("fires once", "two concurrent sweeps produce one dispatch",
"skips test runs") would need a host stood up to reach them.

Mirror `IWorkflowOutboxProcessor` / `WorkflowOutboxHostedService` instead: a scoped
`IWorkflowReminderProcessor` with `ProcessDueAsync(batchSize, ct)` returning a count, and
a thin `BackgroundService` that resolves one per pass and does nothing else. `TestHost`
already exposes `Outbox` for exactly this reason; it gains `Reminders` the same way.

### 3. The builder collects `double?` days, not `int?` days

The stored column is `int?` minutes — that part of the spec is right, and `TimeSpan`
really would truncate at `time(7)`. But the edit model's day-facing property must be
`double?`. An `int?` getter over stored minutes does integer division: a 12-hour lead
time set programmatically or by an earlier version of the UI renders as `0`, showing the
user a number that is not what is stored and silently rewriting it to zero on the next
save. `double?` round-trips 0.5 days as 720 minutes.

---

## Before you start

Every command below assumes these two exports. `dotnet` is **not** on the default PATH
in this environment — it is a `dotnet-install.sh` install at `~/.dotnet` — and the tests
need `SA_PASSWORD`:

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
source "$WORKFLOW_DEV_ENV"
cd <repo root>
```

The tests need the the SQL Server dev container Docker container running (`$WORKFLOW_DEV_DB_START`).
Each test class creates and drops its own database.

Baseline before you touch anything: `dotnet build` clean, `dotnet test` green at
**175 tests**. Run it and confirm that number before Task 2 — everything after this
measures itself against it.

### Standing hazards that this plan will actually hit

1. **Never call `BeginTransaction`.** Use `WorkflowTransaction.ExecuteAsync`
   (`src/Workflow.Persistence.EF/WorkflowTransaction.cs`). This has been got wrong twice
   already, in `DemoBuilderClient` and in `OutboxProcessor`, and both shipped past a green
   suite. Task 6 writes the third component in this codebase that opens a transaction —
   it is the one place in this plan where the mistake is available to make.
2. **The namespace trap.** Any `using` of `MudBlazor` inside `Workflow.MudBlazor`, or of
   `Workflow.*` inside `DemoDocuments.Server`, must be written `@using global::…`. A bare
   using binds to the enclosing namespace and fails as CS0246 on hand-written `@inject`
   / `@code` while markup still compiles. Tasks 8, 9 and 10 touch Razor files.
3. **The host owns the schema.** The engine has no database of its own, so the three new
   columns are a change to the demo's migrations. Task 3 generates that migration;
   `MigrationTests.The_model_and_the_migrations_have_not_drifted` fails if you forget.
4. **UTC everywhere.** `WorkflowTask.Created` is UTC and both new `DateTime` columns
   follow it. Anything comparing or rendering them uses `DateTime.UtcNow`;
   `DateTime.Now` compiles and is wrong by the host's offset.
5. **A DbContext permits one operation at a time**, and Blazor components re-render
   concurrently. Nothing in this plan adds a new component, so the existing semaphores in
   `WorkflowRunner` and `WorkflowInbox` continue to cover it.

### Facts you will need, already verified

- **The engine constructor** (`WorkflowEngine.cs:10`) is
  `(IWorkflowDbContext db, IWorkflowAssignmentResolver assignmentResolver,
  IEnumerable<IRouteConditionEvaluator> conditionEvaluators, ILogger<WorkflowEngine> logger,
  Triggers.IWorkflowTriggerDispatcher? dispatcher = null,
  IWorkflowPostCommitActions? postCommit = null)`. The new resolver is appended **after**
  `postCommit` as another optional parameter, because `TestHost.CreateAsync(withTriggers:
  false)` constructs the engine positionally with four arguments and must keep compiling.
- **The eight `NewTask` call sites**, all of which Task 4 converts:

  | Path | Call site |
  |---|---|
  | Run start (entry task) | `WorkflowEngine.cs:163` |
  | Advance on completion | `WorkflowEngine.cs:268` |
  | Convergence task | `WorkflowEngine.cs:351` |
  | Fork branch | `WorkflowEngine.Fork.cs:97` |
  | Rework after selective rejection | `WorkflowEngine.Fork.cs:257` |
  | Branch added to a live fork | `WorkflowEngine.Reads.cs:282` |
  | Sub-workflow entry | `WorkflowEngine.SubWorkflows.cs:215` |
  | Ad-hoc task | `WorkflowEngine.Queries.cs:125` |

- **`WorkflowRun.Subject` is an owned type.** Project its two scalars and rebuild it —
  `new WorkflowSubject(r.Subject.SubjectType, r.Subject.SubjectId)` — the way
  `WorkflowEngine.Inbox.cs` does. Do not `.Select(r => r.Subject)`.
- **`IWorkflowDbContext.WorkflowTasks` is a `DbSet<WorkflowTask>`**, so
  `ExecuteUpdateAsync` is available on it. That is what makes the conditional claim in
  Task 6 a single atomic statement.
- **`NotifyTrigger` already declares `SupportedEvents: Enum.GetValues<WorkflowEventKind>()`**
  (`Triggers/BuiltInTriggers.cs:120`) and defaults to `TriggerDispatchMode.AfterCommit`
  with `recipient` falling back to `context.Task.AssignedToActorId`. Adding the enum
  member is the entire delivery path; no trigger code changes.
- **Seeded workflow**: definition "ChangeRequest Document Review", subject type `ChangeRequest`, tasks
  `enter-record` ("Enter Record", role `originator`), `provide-input` ("Provide Input",
  role `section-lead`, forkable), `pm-review` ("PM Review", role `branch-manager`, convergence),
  `close-document` ("Close Document", terminal), plus three ad-hoc definitions.
- **`StartRunAsync(subject, definitionId, "user-originator")`** creates the
  `enter-record` entry task assigned to `user-originator` with a null branch key.
- **The seeder creates no documents.** Tests create their own, as
  `DocumentTaskServiceTests.cs:28` does. A due-date test that wants a real date must
  insert a `ChangeRequest` whose `Id` matches the subject id it starts the run on.
- **`DocumentBase` already has `DueDate` and `InternalDueDate`**
  (`samples/DemoDocuments.Server/Domain/DemoDomain.cs:56-57`). No domain change is needed
  for the demo resolver.

## File structure

| File | Responsibility |
|---|---|
| Modify: `docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md` | Record the three departures |
| Modify: `src/Workflow.Core/Abstractions/HostServices.cs` | Declare `IWorkflowDueDateResolver` |
| Modify: `src/Workflow.Core/Model/Entities.cs` | `DueDate`, `ReminderSentAt`, `ReminderLeadTimeMinutes` |
| Modify: `src/Workflow.Core/Model/Enums.cs` | Append `WorkflowEventKind.TaskDueSoon` |
| Create: `src/Workflow.Core/Model/WorkflowActors.cs` | The `System` actor constant |
| Modify: `src/Workflow.Persistence.EF/WorkflowModelBuilder.cs` | Column config and the sweeper's index |
| Modify: `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs` | `NullDueDateResolver`, `AddDueDateResolver<T>()`, `AddReminderProcessing()` |
| Modify: `src/Workflow.Persistence.EF/WorkflowEngine.cs` | `NewTaskAsync`, subject memoisation, due-date resolution |
| Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Fork.cs` | Two call sites |
| Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Reads.cs` | One call site |
| Modify: `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs` | One call site |
| Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Queries.cs` | One call site |
| Create: `src/Workflow.Persistence.EF/Triggers/ReminderProcessor.cs` | `IWorkflowReminderProcessor` and the sweep |
| Create: `src/Workflow.Persistence.EF/Triggers/ReminderHostedService.cs` | `WorkflowReminderOptions` and the timer |
| Modify: `src/Workflow.Persistence.EF/Snapshots.cs` | Carry `DueDate` into `WorkflowTaskSnapshot` |
| Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs` | `DueDate` in `InboxRow` and `InboxTaskSnapshot` |
| Modify: `src/Workflow.Core/Abstractions/Snapshots.cs` | `WorkflowTaskSnapshot.DueDate` |
| Modify: `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs` | `InboxItem.DueDate` |
| Modify: `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs` | `RunTaskView.DueDate` |
| Modify: `src/Workflow.Core/Builder/EditModels.cs` | `ReminderLeadTimeMinutes` + `ReminderLeadTimeDays` |
| Modify: `src/Workflow.MudBlazor/WorkflowInbox.razor` | The Due column |
| Modify: `src/Workflow.MudBlazor/WorkflowRunner.razor` | Due on a task row |
| Modify: `src/Workflow.MudBlazor/TaskEditDialog.razor` | The lead-time field |
| Create: `samples/DemoDocuments.Server/Workflow/DemoDueDateResolver.cs` | The worked example |
| Modify: `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs` | Carry `DueDate` across |
| Modify: `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs` | Carry `DueDate` across |
| Modify: `samples/DemoDocuments.Server/Workflow/DemoBuilderClient.cs` | Round-trip the lead time |
| Modify: `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs` | Seed a lead time |
| Modify: `samples/DemoDocuments.Server/Program.cs` | Register the resolver and the sweeper |
| Create: `samples/DemoDocuments.Server/Data/Migrations/*_Deadlines.cs` | Generated, not hand-written |
| Modify: `tests/Workflow.Tests/TestHost.cs` | Expose `Reminders`; allow a fake resolver |
| Create: `tests/Workflow.Tests/DueDateTests.cs` | The seam and the funnel |
| Create: `tests/Workflow.Tests/ReminderTests.cs` | The sweeper |
| Modify: `tests/Workflow.Tests/InboxTests.cs` | `DueDate` survives the projection |
| Modify: `tests/Workflow.Tests/InboxClientTests.cs` | `DueDate` reaches `InboxItem` |
| Modify: `tests/Workflow.Tests/EditModelCloneTests.cs` | The lead time round-trips |
| Modify: `tests/Workflow.Tests/SchemaGuardTests.cs` | Pin `TaskDueSoon`'s ordinal |
| Modify: `STATE.md` | Record what shipped and the new hazards |

Task 1 amends the spec. Tasks 2–4 build the seam, the columns and the funnel. Task 5 adds
the event and the system actor. Tasks 6–7 build the sweeper and its host. Tasks 8–9 make
it visible. Task 10 wires the builder. Task 11 wires the demo. Task 12 verifies and
records.

---

### Task 1: Amend the spec

The spec is the document somebody reads in six months. It currently says three things
this plan does not do. Fix it first, so nothing downstream has to remember the
difference.

**Files:**
- Modify: `docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md`

- [ ] **Step 1: Replace the funnel paragraph**

Find this paragraph under **One funnel, so no creation path can forget**:

```markdown
The due date is resolved **inside that funnel**, not at each call site. `NewTask` becomes
an instance method and async, and takes the run's `WorkflowSubject` so the resolver has
something to answer about.
```

Replace it with:

```markdown
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
```

- [ ] **Step 2: Replace the sweeper paragraph**

Find this paragraph under **What raises it**:

```markdown
`WorkflowReminderHostedService`, opt-in via `AddReminderProcessing()`, mirroring
`AddOutboxProcessing()`. Options: `PollInterval` (default 5 minutes), `BatchSize`, and
`ErrorBackoff`, following `WorkflowOutboxOptions`.
```

Replace it with:

```markdown
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
```

- [ ] **Step 3: Correct the builder field's type**

Find this sentence under **Builder UI**:

```markdown
`TaskEditDialog` gains a numeric "Remind this many days before due" field, empty by
default.
```

Replace it with:

```markdown
`TaskEditDialog` gains a numeric "Remind this many days before due" field, empty by
default, bound to a `double?` day-facing property over the stored `int?` minutes. It must
be `double?`: an `int?` getter does integer division on the stored minutes, so a 12-hour
lead time renders as `0` — showing the user a value that is not what is stored, and
rewriting it to zero on the next save.
```

- [ ] **Step 4: Mark it in progress**

Change the status line at the top of the spec:

```markdown
**Status:** Approved, in progress — see `docs/superpowers/plans/2026-08-21-deadlines-and-reminders.md`

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.
```

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md
git commit -m "Amend the deadlines spec where the code disagreed with it"
```

---

### Task 2: The due-date seam

The host answers "when is this due?", and a host that does not opt in gets nulls and no
behaviour change. Nothing calls this yet — Task 4 wires it into the funnel.

**Files:**
- Modify: `src/Workflow.Core/Abstractions/HostServices.cs`
- Modify: `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`
- Test: `tests/Workflow.Tests/DueDateTests.cs` (created here)

- [ ] **Step 1: Write the failing test**

Create `tests/Workflow.Tests/DueDateTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;
using Workflow.Persistence.EF;

namespace Workflow.Tests;

/// <summary>
/// The due-date seam and the funnel it is called from.
///
/// The funnel is the whole point: five of the eight paths that create a task are
/// fork- or sub-workflow-related, so anyone stamping a deadline at "the place tasks are
/// made" by hand finds three of them. Each test here is one of the eight.
/// </summary>
[TestClass]
public class DueDateTests
{
    /// <summary>A resolver that answers the same date for everything, so a test can
    /// assert on the value rather than on a computation.</summary>
    private sealed class FixedDueDateResolver(DateTime? due) : IWorkflowDueDateResolver
    {
        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) => Task.FromResult(due);
    }

    [TestMethod]
    public void The_default_registration_is_the_null_resolver()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflowEngine(includeBuiltInTriggers: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkflowDueDateResolver>();

        Assert.AreEqual("NullDueDateResolver", resolver.GetType().Name);
    }

    [TestMethod]
    public async Task The_null_resolver_answers_null()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflowEngine(includeBuiltInTriggers: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkflowDueDateResolver>();

        var answer = await resolver.ResolveAsync(
            new WorkflowSubject("ChangeRequest", "1"),
            new WorkflowTaskSnapshot(0, 0, 0, "t", "T", WorkflowTaskStatus.NotStarted,
                null, null, null, null, null, false, null, null, null, null));

        Assert.IsNull(answer);
    }

    [TestMethod]
    public void AddDueDateResolver_replaces_the_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkflowEngine(includeBuiltInTriggers: false)
                .AddDueDateResolver<AlwaysTomorrow>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<IWorkflowDueDateResolver>();

        Assert.IsInstanceOfType<AlwaysTomorrow>(resolver);
    }

    private sealed class AlwaysTomorrow : IWorkflowDueDateResolver
    {
        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) =>
            Task.FromResult<DateTime?>(DateTime.UtcNow.AddDays(1));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~DueDateTests"
```

Expected: **build failure**, CS0246 — `IWorkflowDueDateResolver` could not be found.

- [ ] **Step 3: Declare the seam**

Append to `src/Workflow.Core/Abstractions/HostServices.cs`, after
`IWorkflowAssignmentResolver`:

```csharp
/// <summary>
/// When a task is due. The engine has no opinion — a deadline belongs to whatever the
/// run is about, and only the host knows that a run on ChangeRequest:42 inherits that ChangeRequest's date.
///
/// Returning null means "no deadline", which is the honest answer for most tasks and the
/// default behaviour of the whole feature.
///
/// Failure policy matches <see cref="IWorkflowAssignmentResolver"/>: a resolver that
/// throws is logged and treated as null, never fatal. the original engine's review finding M8 was that a
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
```

- [ ] **Step 4: Register the default and the opt-in**

In `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`, add to
`WorkflowEngineBuilder`, immediately after `AddAssignmentResolver<T>()`:

```csharp
    /// <summary>
    /// Supplies deadlines. Without this the engine stamps null due dates and the whole
    /// deadline feature is inert — which is the correct default for a host that has no
    /// deadlines to give.
    /// </summary>
    public WorkflowEngineBuilder AddDueDateResolver<T>() where T : class, IWorkflowDueDateResolver
    {
        Services.AddScoped<IWorkflowDueDateResolver, T>();
        return this;
    }
```

In `AddWorkflowEngine`, beside the existing null assignment resolver:

```csharp
        // A host that supplies none of these still gets a working engine.
        services.TryAddScoped<IWorkflowAssignmentResolver, NullAssignmentResolver>();
        services.TryAddScoped<IWorkflowDueDateResolver, NullDueDateResolver>();
```

And at the bottom of the file, beside `NullAssignmentResolver`:

```csharp
/// <summary>No deadline for anything. Used when the host supplies no resolver.</summary>
internal sealed class NullDueDateResolver : IWorkflowDueDateResolver
{
    public Task<DateTime?> ResolveAsync(
        WorkflowSubject subject,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default) => Task.FromResult<DateTime?>(null);
}
```

> `TryAddScoped`, not `AddScoped`, and `AddDueDateResolver<T>()` uses plain `AddScoped` —
> a later registration wins, so a host's resolver overrides the null one whichever order
> the two calls appear in.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~DueDateTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Workflow.Core/Abstractions/HostServices.cs \
        src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs \
        tests/Workflow.Tests/DueDateTests.cs
git commit -m "Add the due-date host seam"
```

---

### Task 3: The three columns

Nothing reads or writes these yet. They land in one migration so the schema change is a
single reviewable step.

**Files:**
- Modify: `src/Workflow.Core/Model/Entities.cs`
- Modify: `src/Workflow.Persistence.EF/WorkflowModelBuilder.cs`
- Create: `samples/DemoDocuments.Server/Data/Migrations/*_Deadlines.cs` (generated)

- [ ] **Step 1: Add the two task columns**

In `src/Workflow.Core/Model/Entities.cs`, in `class WorkflowTask`, after
`AssignedBranchKey`:

```csharp
    /// <summary>
    /// When this task is due, in <b>UTC</b>, as the host's IWorkflowDueDateResolver
    /// answered it. Cached on the row rather than resolved on read so the sweeper and the
    /// inbox can filter and sort on it in SQL. The sweeper re-resolves and repairs it, so
    /// a deadline that moves after the task was created does not go stale here.
    /// </summary>
    public DateTime? DueDate { get; set; }

    /// <summary>
    /// When the due-soon reminder fired, in <b>UTC</b>. Doubles as the fire-once stamp
    /// and the row-claim target: the sweeper's conditional update sets it only where it
    /// is still null, so two app instances sweeping at once produce one dispatch.
    /// </summary>
    public DateTime? ReminderSentAt { get; set; }
```

In `class WorkflowTaskDefinition`, after `AssignmentRoleKey`:

```csharp
    /// <summary>
    /// How far ahead of <see cref="WorkflowTask.DueDate"/> to nudge the assignee, in
    /// minutes. Null means this task type never nudges, which is the default.
    ///
    /// Minutes rather than a <see cref="TimeSpan"/> deliberately: EF Core maps TimeSpan
    /// to SQL Server <c>time(7)</c>, which represents a time of day and caps at 24 hours,
    /// so every lead time of a day or more would silently truncate or throw. The builder
    /// collects days and stores minutes.
    /// </summary>
    public int? ReminderLeadTimeMinutes { get; set; }
```

- [ ] **Step 2: Index for the sweeper**

In `src/Workflow.Persistence.EF/WorkflowModelBuilder.cs`, inside
`b.Entity<WorkflowTask>(e => { … })`, after the existing
`e.HasIndex(x => new { x.AssignedToActorId, x.Status });`:

```csharp
            // The sweeper's candidate set: open, not yet reminded, configured to nudge.
            // Filtered on ReminderSentAt because the interesting rows are the ones that
            // have not fired, and every task that ever fires leaves the set permanently.
            // Without the filter this index grows with the table forever while the query
            // only ever wants its shrinking head.
            e.HasIndex(x => new { x.Status, x.ReminderSentAt })
             .HasFilter("[ReminderSentAt] IS NULL");
```

> `HasFilter` is SQL Server-specific, like the convergence index four lines below it.
> STATE.md already records that as a provider hazard; Task 12 adds this index to the same
> note.

No explicit column configuration is needed — `DateTime?` and `int?` map to
`datetime2(7)` and `int` by convention, which is what `WorkflowTask.Created` and
`CompletedDate` already do.

- [ ] **Step 3: Generate the migration**

```bash
dotnet ef migrations add Deadlines \
  --project samples/DemoDocuments.Server --context DemoDbContext --output-dir Data/Migrations
```

Expected: a new `Data/Migrations/<timestamp>_Deadlines.cs` plus its `.Designer.cs`, and
an updated `DemoDbContextModelSnapshot.cs`.

- [ ] **Step 4: Read the generated migration before trusting it**

```bash
sed -n '1,80p' samples/DemoDocuments.Server/Data/Migrations/*_Deadlines.cs
```

Expected `Up()`: three `AddColumn` calls — `DueDate` and `ReminderSentAt` of type
`datetime2` nullable on `WorkflowTasks`, `ReminderLeadTimeMinutes` of type `int` nullable
on `WorkflowTaskDefinitions` — and one `CreateIndex` on
`WorkflowTasks (Status, ReminderSentAt)` with
`filter: "[ReminderSentAt] IS NULL"`.

**If it contains anything else — a dropped column, a renamed table, a recreated index —
stop.** That means the model had already drifted before you started, and the drift is now
mixed into your migration.

- [ ] **Step 5: Run the migration guards**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~MigrationTests"
```

Expected: PASS, 2 tests. `The_model_and_the_migrations_have_not_drifted` is the one that
proves Step 3 actually happened; `A_database_built_from_the_migrations_runs_a_workflow`
proves the script produces a schema the engine works against.

- [ ] **Step 6: Commit**

```bash
git add src/Workflow.Core/Model/Entities.cs \
        src/Workflow.Persistence.EF/WorkflowModelBuilder.cs \
        samples/DemoDocuments.Server/Data/Migrations
git commit -m "Add the deadline columns"
```

---

### Task 4: The funnel

`NewTask` becomes `NewTaskAsync`, resolves the subject itself, and asks the host for a
deadline. This is the task that makes all eight creation paths inherit deadlines, and the
one a future ninth path inherits for free.

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.cs`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Fork.cs:97,257`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Reads.cs:282`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs:215`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Queries.cs:125`
- Modify: `tests/Workflow.Tests/TestHost.cs`
- Test: `tests/Workflow.Tests/DueDateTests.cs`

- [ ] **Step 1: Let TestHost inject a resolver**

The eight-path tests need a resolver that answers a known date. `TestHost.CreateAsync`'s
`configure` hook already allows this for the trigger path; the plain path constructs the
engine by hand and needs one more parameter.

In `tests/Workflow.Tests/TestHost.cs`, change the `CreateAsync` signature:

```csharp
    public static async Task<TestHost> CreateAsync(
        bool withTriggers = false,
        Action<IServiceCollection>? configure = null,
        IWorkflowDueDateResolver? dueDates = null)
```

and the plain-path engine construction:

```csharp
            var plainEngine = new WorkflowEngine(
                plainDb,
                new DemoAssignmentResolver(plainDb, NullLogger<DemoAssignmentResolver>.Instance),
                [new RequiresReviewCondition()],
                NullLogger<WorkflowEngine>.Instance,
                dueDateResolver: dueDates);
```

> Named argument, not positional: `dispatcher` and `postCommit` sit between `logger` and
> the new parameter, and both must stay defaulted here.

For the trigger path, add just above `configure?.Invoke(services);`:

```csharp
        // Before configure?.Invoke, so a test that wants something else still wins.
        // The service type is written out rather than inferred: `dueDates` is declared
        // nullable, and letting inference pick the type off it is how you end up
        // registering the concrete class instead of the interface.
        if (dueDates is not null)
        {
            services.AddScoped<IWorkflowDueDateResolver>(_ => dueDates);
        }
```

- [ ] **Step 2: Write the failing tests**

Add to `tests/Workflow.Tests/DueDateTests.cs`, inside the class:

```csharp
    private static readonly DateTime Due = new(2026, 12, 25, 17, 0, 0, DateTimeKind.Utc);

    /// <summary>A resolver that records what it was asked, and throws on demand.</summary>
    private sealed class RecordingResolver(DateTime? due, bool throws = false) : IWorkflowDueDateResolver
    {
        public List<WorkflowSubject> Asked { get; } = [];

        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default)
        {
            Asked.Add(subject);

            if (throws)
            {
                throw new InvalidOperationException("resolver is broken");
            }

            return Task.FromResult(due);
        }
    }

    private static async Task<int> MainlineDefinitionIdAsync(TestHost host) =>
        await host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

    [TestMethod]
    public async Task Run_start_stamps_the_entry_task()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var task = await host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        Assert.AreEqual(Due, task.DueDate);
    }

    [TestMethod]
    public async Task The_resolver_is_asked_about_the_runs_subject()
    {
        var resolver = new RecordingResolver(Due);
        await using var host = await TestHost.CreateAsync(dueDates: resolver);
        var definitionId = await MainlineDefinitionIdAsync(host);

        await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "42"), definitionId, "user-originator");

        var asked = resolver.Asked.Single();
        Assert.AreEqual("ChangeRequest", asked.SubjectType);
        Assert.AreEqual("42", asked.SubjectId);
    }

    [TestMethod]
    public async Task Advancing_on_completion_stamps_the_next_task()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();
        var entryId = run.Tasks.Single().Id;

        (await host.Engine.CompleteTaskAsync(entryId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var next = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Id != entryId)
            .SingleAsync();

        Assert.AreEqual(Due, next.DueDate);
    }

    [TestMethod]
    public async Task An_ad_hoc_task_is_stamped()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();
        var entryId = run.Tasks.Single().Id;

        var adHocDef = await host.Db.WorkflowTaskDefinitions
            .Where(d => d.IsAdHoc).Select(d => d.Id).FirstAsync();

        // Returns a WorkflowTaskSnapshot, not an id — see IWorkflowEngine.cs:94.
        var created = (await host.Engine.AddAdHocTaskAsync(
            entryId, adHocDef, "user-originator")).Unwrap();

        var adHoc = await host.Db.WorkflowTasks.SingleAsync(t => t.Id == created.Id);

        Assert.AreEqual(Due, adHoc.DueDate);
    }

    [TestMethod]
    public async Task A_resolver_that_throws_leaves_a_null_date_and_creates_the_task()
    {
        var resolver = new RecordingResolver(Due, throws: true);
        await using var host = await TestHost.CreateAsync(dueDates: resolver);
        var definitionId = await MainlineDefinitionIdAsync(host);

        // Not Result.Failure: a broken deadline resolver is logged and treated as null,
        // never fatal — the same policy IWorkflowAssignmentResolver has, for the same
        // reason (the original engine's review finding M8).
        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var task = await host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        Assert.IsNull(task.DueDate);
    }

    [TestMethod]
    public async Task No_resolver_means_no_date_and_no_error()
    {
        await using var host = await TestHost.CreateAsync();
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        var task = await host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        Assert.IsNull(task.DueDate);
    }

    [TestMethod]
    public async Task The_subject_is_resolved_once_per_run_however_many_tasks_are_created()
    {
        var resolver = new RecordingResolver(Due);
        await using var host = await TestHost.CreateAsync(dueDates: resolver);
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

        (await host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        // Two tasks created, so the resolver was asked twice — but both answers describe
        // the same subject, which is the memoisation working. (The engine is scoped and
        // this test shares one, so the second call hits the cache.)
        Assert.AreEqual(2, resolver.Asked.Count);
        Assert.AreEqual(1, resolver.Asked.Select(s => s.SubjectId).Distinct().Count());
    }
```

Fork, convergence, rework, added-branch and sub-workflow entry are covered in Step 6 —
they need a forked run to exist first, and are grouped so the setup is written once.

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~DueDateTests"
```

Expected: the four "is stamped" tests FAIL with `Assert.AreEqual failed. Expected:
<12/25/2026 5:00:00 PM>. Actual:<(null)>`. `No_resolver_means_no_date_and_no_error` and
the Task 2 tests still pass.

- [ ] **Step 4: Make NewTask async and resolve the date**

In `src/Workflow.Persistence.EF/WorkflowEngine.cs`, add the new optional constructor
parameter — **after** `postCommit`, so `TestHost`'s four-argument positional call keeps
compiling:

```csharp
public sealed partial class WorkflowEngine(
    IWorkflowDbContext db,
    IWorkflowAssignmentResolver assignmentResolver,
    IEnumerable<IRouteConditionEvaluator> conditionEvaluators,
    ILogger<WorkflowEngine> logger,
    Triggers.IWorkflowTriggerDispatcher? dispatcher = null,
    IWorkflowPostCommitActions? postCommit = null,
    IWorkflowDueDateResolver? dueDateResolver = null) : IWorkflowEngine
{
```

Replace the whole `private static WorkflowTask NewTask(…)` method (currently at
`WorkflowEngine.cs:631`) with:

```csharp
    /// <summary>
    /// A run's subject, memoised. The engine is scoped, so this lives for one unit of
    /// work — and a run's subject is written once at creation and never mutated, so
    /// there is nothing for a stale entry to be stale about.
    ///
    /// It exists because the due-date funnel needs a subject and seven of the eight
    /// creation paths do not have the run loaded: LoadTaskAsync does not Include it.
    /// Threading a subject parameter through those seven call sites would reintroduce
    /// exactly the per-site burden the funnel exists to remove.
    /// </summary>
    private readonly Dictionary<int, WorkflowSubject> _subjectsByRun = [];

    private async Task<WorkflowSubject?> SubjectForRunAsync(int runId, CancellationToken ct)
    {
        if (_subjectsByRun.TryGetValue(runId, out var cached))
        {
            return cached;
        }

        // Projected as two scalars and rebuilt, not selected as the owned type itself —
        // the same shape WorkflowEngine.Inbox.cs uses.
        var row = await db.WorkflowRuns
            .AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Subject.SubjectType, r.Subject.SubjectId })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var subject = new WorkflowSubject(row.SubjectType, row.SubjectId);
        _subjectsByRun[runId] = subject;
        return subject;
    }

    /// <summary>
    /// The single place a <see cref="WorkflowTask"/> is constructed. All eight creation
    /// paths go through here, and five of them are fork- or sub-workflow-related — which
    /// is the argument for the funnel in one sentence. Anyone adding a deadline at "the
    /// place tasks are made" by hand would find three of them.
    /// </summary>
    private async Task<WorkflowTask> NewTaskAsync(
        int runId,
        WorkflowTaskDefinition definition,
        WorkflowAssignment assignment,
        string actorId,
        DateTime now,
        CancellationToken ct)
    {
        var task = new WorkflowTask
        {
            WorkflowRunId = runId,
            TaskDefinitionId = definition.Id,
            TaskDefinition = definition,
            Status = WorkflowTaskStatus.NotStarted,
            AssignedToActorId = assignment.ActorId,
            AssignedBranchKey = assignment.BranchKey,
            CreatorId = actorId,
            ModifierId = actorId,
            Created = now,
            Modified = now
        };

        task.DueDate = await ResolveDueDateAsync(runId, task, ct).ConfigureAwait(false);

        return task;
    }

    private async Task<DateTime?> ResolveDueDateAsync(
        int runId, WorkflowTask task, CancellationToken ct)
    {
        if (dueDateResolver is null)
        {
            return null;
        }

        var subject = await SubjectForRunAsync(runId, ct).ConfigureAwait(false);

        if (subject is null)
        {
            // Only reachable if the run row is not there yet. Every caller saves the run
            // before creating its first task, so this is a defect rather than a state.
            logger.LogWarning(
                "No run {RunId} when resolving a due date; leaving it null.", runId);
            return null;
        }

        try
        {
            // The snapshot's Id is 0 and its TaskTypeKey may be empty: the task is not
            // saved yet, and TaskDefinition.TaskType is not loaded on every path. A
            // resolver that needs either must key on the subject instead — which is what
            // a deadline belongs to anyway.
            return await dueDateResolver
                .ResolveAsync(subject, task.ToSnapshot(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Logged rather than silently swallowed, and never fatal: the same policy
            // ResolveAssignmentAsync has, for the same reason (the original engine's finding M8).
            logger.LogError(ex,
                "Due-date resolver failed for definition {DefinitionId} on run {RunId}; " +
                "leaving the due date null.",
                task.TaskDefinitionId, runId);
            return null;
        }
    }
```

- [ ] **Step 5: Convert all eight call sites**

Each is a one-line change. The cancellation token's name differs by site — use the one
already in scope, shown in the right-hand column.

| File and line | Was | Becomes |
|---|---|---|
| `WorkflowEngine.cs:163` | `NewTask(run.Id, entryDef, assignment, actorId, now)` | `await NewTaskAsync(run.Id, entryDef, assignment, actorId, now, token).ConfigureAwait(false)` |
| `WorkflowEngine.cs:268` | `NewTask(task.WorkflowRunId, nextDef, assignment, actorId, now)` | `await NewTaskAsync(task.WorkflowRunId, nextDef, assignment, actorId, now, ct).ConfigureAwait(false)` |
| `WorkflowEngine.cs:351` | `NewTask(task.WorkflowRunId, convergenceDef, assignment, actorId, now)` | `await NewTaskAsync(task.WorkflowRunId, convergenceDef, assignment, actorId, now, ct).ConfigureAwait(false)` |
| `WorkflowEngine.Fork.cs:97` | `NewTask(origin.WorkflowRunId, originDef, assignment, actorId, now)` | `await NewTaskAsync(origin.WorkflowRunId, originDef, assignment, actorId, now, token).ConfigureAwait(false)` |
| `WorkflowEngine.Fork.cs:257` | `NewTask(task.WorkflowRunId, reworkDef, assignment, actorId, now)` | `await NewTaskAsync(task.WorkflowRunId, reworkDef, assignment, actorId, now, token).ConfigureAwait(false)` |
| `WorkflowEngine.Reads.cs:282` | `NewTask(template.WorkflowRunId, templateDef, assignment, actorId, now)` | `await NewTaskAsync(template.WorkflowRunId, templateDef, assignment, actorId, now, token).ConfigureAwait(false)` |
| `WorkflowEngine.SubWorkflows.cs:215` | `NewTask(parent.WorkflowRunId, entryDef, assignment, actorId, now)` | `await NewTaskAsync(parent.WorkflowRunId, entryDef, assignment, actorId, now, ct).ConfigureAwait(false)` |
| `WorkflowEngine.Queries.cs:125` | `NewTask(parent.WorkflowRunId, definition, resolved, actorId, now)` | `await NewTaskAsync(parent.WorkflowRunId, definition, resolved, actorId, now, token).ConfigureAwait(false)` |

Every one of these sits immediately after an `await ResolveAssignmentAsync(…)` in the
same block, so all eight are already in an async context and no signature above them
changes.

Confirm nothing was missed:

```bash
grep -rn "NewTask(" src/
```

Expected: **no output**.

- [ ] **Step 6: Write the remaining five path tests**

Add to `tests/Workflow.Tests/DueDateTests.cs`:

```csharp
    /// <summary>
    /// The five fork- and sub-workflow-related paths, which share a forked run.
    /// Modelled on ForkConvergenceTests' setup: fork "Provide Input" across two
    /// sections, then drive it to convergence.
    /// </summary>
    private async Task<(TestHost Host, int RunId, int OriginTaskId, Guid ForkGroupId)>
        ForkedRunAsync(IWorkflowDueDateResolver resolver)
    {
        var host = await TestHost.CreateAsync(dueDates: resolver);
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();
        var entryId = run.Tasks.Single().Id;

        (await host.Engine.CompleteTaskAsync(entryId, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var provideInput = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Id != entryId)
            .SingleAsync();

        var convergenceDefId = await host.Db.WorkflowTaskDefinitions
            .Where(d => d.IsConvergencePoint).Select(d => d.Id).SingleAsync();

        (await host.Engine.ForkTaskAsync(
            provideInput.Id, ["C100", "C200"], convergenceDefId, "user-originator", null)).Unwrap();

        var forkGroupId = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.ForkGroupId != null)
            .Select(t => t.ForkGroupId!.Value)
            .FirstAsync();

        return (host, run.Id, provideInput.Id, forkGroupId);
    }

    [TestMethod]
    public async Task Fork_branches_are_stamped()
    {
        var (host, runId, originId, _) = await ForkedRunAsync(new FixedDueDateResolver(Due));

        await using (host)
        {
            var branches = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId && t.ForkGroupId != null)
                .ToListAsync();

            Assert.AreEqual(2, branches.Count);
            Assert.IsTrue(branches.All(b => b.DueDate == Due),
                "every branch of a fork goes through the funnel");
            Assert.AreNotEqual(0, originId);
        }
    }

    [TestMethod]
    public async Task A_branch_added_to_a_live_fork_is_stamped()
    {
        var (host, runId, _, forkGroupId) = await ForkedRunAsync(new FixedDueDateResolver(Due));

        await using (host)
        {
            (await host.Engine.AddBranchToForkAsync(
                forkGroupId, ["C300"], "user-originator", null)).Unwrap();

            var added = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId && t.AssignedBranchKey == "C300")
                .SingleAsync();

            Assert.AreEqual(Due, added.DueDate);
        }
    }

    [TestMethod]
    public async Task The_convergence_task_is_stamped()
    {
        var (host, runId, _, _) = await ForkedRunAsync(new FixedDueDateResolver(Due));

        await using (host)
        {
            var branches = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId && t.ForkGroupId != null)
                .ToListAsync();

            foreach (var branch in branches)
            {
                (await host.Engine.CompleteTaskAsync(
                    branch.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-section-lead-c200")).Unwrap();
            }

            var convergence = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId && t.ForkManifestId != null)
                .SingleAsync();

            Assert.AreEqual(Due, convergence.DueDate);
        }
    }

    [TestMethod]
    public async Task Rework_tasks_from_selective_rejection_are_stamped()
    {
        var (host, runId, _, _) = await ForkedRunAsync(new FixedDueDateResolver(Due));

        await using (host)
        {
            var branches = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId && t.ForkGroupId != null)
                .ToListAsync();

            foreach (var branch in branches)
            {
                (await host.Engine.CompleteTaskAsync(
                    branch.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-section-lead-c200")).Unwrap();
            }

            var convergence = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId && t.ForkManifestId != null)
                .SingleAsync();

            var before = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId).Select(t => t.Id).ToListAsync();

            (await host.Engine.CompleteWithSelectiveRejectionAsync(
                convergence.Id,
                DemoWorkflowSeeder.Outcomes.Rejected,
                ["C100"],
                "user-branch-head")).Unwrap();

            var rework = await host.Db.WorkflowTasks
                .Where(t => t.WorkflowRunId == runId && !before.Contains(t.Id))
                .ToListAsync();

            Assert.IsTrue(rework.Count > 0, "selective rejection re-forks the rejected branches");
            Assert.IsTrue(rework.All(t => t.DueDate == Due));
        }
    }

    [TestMethod]
    public async Task A_sub_workflow_entry_task_is_stamped()
    {
        await using var host = await TestHost.CreateAsync(dueDates: new FixedDueDateResolver(Due));
        var definitionId = await MainlineDefinitionIdAsync(host);

        var run = (await host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();
        var entryId = run.Tasks.Single().Id;

        var attachment = await host.Db.WorkflowSubWorkflowAttachments
            .Include(a => a.SubWorkflowDefinition)
            .FirstAsync();

        var before = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id).Select(t => t.Id).ToListAsync();

        // The second argument is the sub-workflow *definition* id, not the attachment's
        // own id (IWorkflowEngine.cs:111). Passing attachment.Id compiles and is wrong.
        (await host.Engine.StartSubWorkflowAsync(
            entryId, attachment.SubWorkflowDefinitionId, "user-originator")).Unwrap();

        var spawned = await host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && !before.Contains(t.Id))
            .ToListAsync();

        Assert.IsTrue(spawned.Count > 0, "the sub-workflow's entry task is in the parent's run");
        Assert.IsTrue(spawned.All(t => t.DueDate == Due));
    }
```

> **The exact signatures of `ForkTaskAsync`, `AddBranchToForkAsync`,
> `CompleteWithSelectiveRejectionAsync` and `StartSubWorkflowAsync` are in
> `src/Workflow.Persistence.EF/IWorkflowEngine.cs`, and `ForkConvergenceTests.cs` and
> `SubWorkflowTests.cs` already call every one of them.** Copy the call shape from
> those files rather than the sketch above if they disagree — the point of these five
> tests is the `DueDate` assertion, not the setup.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~DueDateTests"
```

Expected: PASS, 15 tests.

- [ ] **Step 8: Run the whole suite — this task changed a shared code path**

```bash
dotnet test
```

Expected: PASS, **175 + 15 = 190 tests**. Every existing test creates tasks through the
funnel, so a regression here shows up as a broad failure rather than a local one.

- [ ] **Step 9: Commit**

```bash
git add src/Workflow.Persistence.EF tests/Workflow.Tests/DueDateTests.cs tests/Workflow.Tests/TestHost.cs
git commit -m "Resolve a due date in the task-creation funnel"
```

---

### Task 5: The event and the system actor

Two small additions with one large consequence: the moment `TaskDueSoon` exists, the
built-in `workflow.notify` trigger supports it, because it declares
`SupportedEvents: Enum.GetValues<WorkflowEventKind>()`. The delivery path is finished by
adding an enum member.

**Files:**
- Modify: `src/Workflow.Core/Model/Enums.cs`
- Create: `src/Workflow.Core/Model/WorkflowActors.cs`
- Test: `tests/Workflow.Tests/SchemaGuardTests.cs`

- [ ] **Step 1: Write the failing test**

In `tests/Workflow.Tests/SchemaGuardTests.cs`, add one line to
`Event_kind_ordinals_are_stable`, after the `SubWorkflowCancelled` assertion:

```csharp
        Assert.AreEqual(15, (int)WorkflowEventKind.TaskDueSoon);
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~SchemaGuardTests"
```

Expected: **build failure**, CS0117 — `WorkflowEventKind` does not contain a definition
for `TaskDueSoon`.

- [ ] **Step 3: Append the event kind**

In `src/Workflow.Core/Model/Enums.cs`, add to the **end** of `WorkflowEventKind`, after
`SubWorkflowCancelled`:

```csharp
    ,

    /// <summary>
    /// A task is inside its reminder lead time and has not been completed. The only event
    /// in this enum that is raised because *nobody acted* — every other one comes from an
    /// engine operation somebody invoked, so this is the one dispatched by the sweeper
    /// rather than by <c>FireAsync</c> inside a command.
    ///
    /// Appended, like SubWorkflowCancelled above it and for the same reason: these values
    /// are persisted as ints on TriggerDefinition and TriggerExecution, so inserting one
    /// mid-list renumbers every member after it and silently rewrites the meaning of
    /// stored rows.
    /// </summary>
    TaskDueSoon
```

> Written as a separate `,` line above the comment so the diff shows the comma being
> added to `SubWorkflowCancelled` rather than the member being moved. Merge it into the
> previous line if the file's style prefers that; what matters is the position.

- [ ] **Step 4: Add the system actor**

Create `src/Workflow.Core/Model/WorkflowActors.cs`:

```csharp
namespace Workflow.Core.Model;

/// <summary>
/// Actor ids the engine itself uses. Opaque strings, like every other actor id here —
/// the engine never models a user.
/// </summary>
public static class WorkflowActors
{
    /// <summary>
    /// Who acted when nobody did. The reminder sweeper dispatches under this, and it
    /// lands in the TriggerExecution audit trail.
    ///
    /// Not the task's assignee, which would be a lie — that person did not do this, and
    /// the audit trail is the one place that distinction is recoverable. Not an empty
    /// string either: TriggerDispatcher's guards reject one.
    ///
    /// Prefixed so a host reading its own audit log can tell it apart from an id of its
    /// own at a glance.
    /// </summary>
    public const string System = "workflow:system";
}
```

- [ ] **Step 5: Run it to verify it passes**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~SchemaGuardTests"
```

Expected: PASS. The ordinal assertion is what proves the member went on the end.

- [ ] **Step 6: Commit**

```bash
git add src/Workflow.Core/Model tests/Workflow.Tests/SchemaGuardTests.cs
git commit -m "Add the TaskDueSoon event and the system actor"
```

---

### Task 6: The sweeper

The one piece of this feature that has no analogue in the engine today: something that
happens because nobody acted. State-based, not schedule-based — each pass asks a fresh
question against live data, so a completed task, a cancelled run, an edited lead time or
a trigger added after the task was created all resolve correctly with no invalidation
step.

**Files:**
- Create: `src/Workflow.Persistence.EF/Triggers/ReminderProcessor.cs`
- Modify: `tests/Workflow.Tests/TestHost.cs`
- Test: `tests/Workflow.Tests/ReminderTests.cs`

- [ ] **Step 1: Expose the processor on TestHost**

In `tests/Workflow.Tests/TestHost.cs`, add a property beside `Outbox`:

```csharp
    /// <summary>
    /// The reminder sweep. Null on a host built without triggers — the sweeper's whole
    /// job is to dispatch, so there is nothing to test without the trigger subsystem.
    /// </summary>
    public IWorkflowReminderProcessor? Reminders { get; private init; }
```

and set it on the `withTriggers: true` return:

```csharp
        return new TestHost(
            db,
            provider.GetRequiredService<IWorkflowEngine>(),
            provider.GetRequiredService<IWorkflowOutboxProcessor>(),
            provider)
        {
            Notifications = notifications,
            Reminders = provider.GetRequiredService<IWorkflowReminderProcessor>()
        };
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Workflow.Tests/ReminderTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;
using Workflow.Persistence.EF;
using Workflow.Persistence.EF.Triggers;

namespace Workflow.Tests;

/// <summary>
/// The reminder sweep.
///
/// The claim happens before the dispatch, not after, so the failure mode is a missed
/// reminder rather than a duplicated one. That is the right way round: a person nudged
/// twice for the same task stops trusting the nudges.
/// </summary>
[TestClass]
public class ReminderTests
{
    /// <summary>Answers a date the test controls, and can be moved between sweeps.</summary>
    private sealed class MutableDueDateResolver : IWorkflowDueDateResolver
    {
        public DateTime? Due { get; set; }

        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) => Task.FromResult(Due);
    }

    private MutableDueDateResolver _dueDates = null!;
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _dueDates = new MutableDueDateResolver { Due = DateTime.UtcNow.AddHours(1) };

        _host = await TestHost.CreateAsync(withTriggers: true, dueDates: _dueDates);

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Starts a run, gives the entry task's definition a lead time, and attaches a
    /// notify trigger for TaskDueSoon so a dispatch is observable.
    /// </summary>
    private async Task<WorkflowTask> ArmedTaskAsync(
        int leadMinutes = 120, bool isTest = false, string subjectId = "1")
    {
        var run = isTest
            ? (await _host.Engine.StartRunOnVersionAsync(
                new WorkflowSubject("ChangeRequest", subjectId),
                await LatestVersionIdAsync(),
                "user-originator",
                isTest: true)).Unwrap()
            : (await _host.Engine.StartRunAsync(
                new WorkflowSubject("ChangeRequest", subjectId), _definitionId, "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        var definition = await _host.Db.WorkflowTaskDefinitions
            .SingleAsync(d => d.Id == task.TaskDefinitionId);

        definition.ReminderLeadTimeMinutes = leadMinutes;

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = definition.Id,
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = WorkflowEventKind.TaskDueSoon,
            // InTransaction rather than the notify trigger's AfterCommit default, so the
            // notification lands in DemoNotificationSink by the time ProcessDueAsync
            // returns. Asserting on after-commit delivery would mean draining the outbox
            // too, which is OutboxIsolationTests' job, not this file's.
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

        return task;
    }

    private async Task<int> LatestVersionIdAsync() =>
        await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _definitionId)
            .OrderByDescending(v => v.Version)
            .Select(v => v.Id)
            .FirstAsync();

    private async Task<DateTime?> ReminderSentAtAsync(int taskId) =>
        await _host.Db.WorkflowTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.ReminderSentAt)
            .SingleAsync();

    [TestMethod]
    public async Task It_fires_inside_the_lead_window()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        var fired = await _host.Reminders!.ProcessDueAsync();

        Assert.AreEqual(1, fired);
        Assert.IsNotNull(await ReminderSentAtAsync(task.Id));
        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_does_not_fire_outside_the_lead_window()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        var fired = await _host.Reminders!.ProcessDueAsync();

        Assert.AreEqual(0, fired);
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_fires_once()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        Assert.AreEqual(1, await _host.Reminders!.ProcessDueAsync());
        Assert.AreEqual(0, await _host.Reminders!.ProcessDueAsync());

        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task Two_concurrent_sweeps_produce_one_dispatch()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        // Two scopes, two processors, two DbContexts — the shape a web farm has. One
        // shared context would prove nothing: it serialises the two claims for free.
        using var a = _host.Provider!.CreateScope();
        using var b = _host.Provider!.CreateScope();

        var results = await Task.WhenAll(
            a.ServiceProvider.GetRequiredService<IWorkflowReminderProcessor>().ProcessDueAsync(),
            b.ServiceProvider.GetRequiredService<IWorkflowReminderProcessor>().ProcessDueAsync());

        Assert.AreEqual(1, results.Sum(), "the conditional claim lets exactly one through");
        Assert.AreEqual(1, _host.Notifications.Delivered.Count(n => n.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_completed_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        (await _host.Engine.CompleteTaskAsync(
            task.Id, DemoWorkflowSeeder.Outcomes.Approved, "user-originator")).Unwrap();

        var fired = await _host.Reminders!.ProcessDueAsync();

        // The follow-on task the completion created has no lead time on its own
        // definition, so it is not a candidate either.
        Assert.AreEqual(0, fired);
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_cancelled_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        (await _host.Engine.CancelTaskAsync(task.Id, "user-originator", "not needed")).Unwrap();

        Assert.AreEqual(0, await _host.Reminders!.ProcessDueAsync());
    }

    [TestMethod]
    public async Task It_skips_an_archived_task()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        var tracked = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);
        tracked.IsArchived = true;
        await _host.Db.SaveChangesAsync();

        Assert.AreEqual(0, await _host.Reminders!.ProcessDueAsync());
    }

    [TestMethod]
    public async Task It_skips_a_task_in_a_test_run()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync(isTest: true);

        // The reason this predicate is in the library and not in each host: an admin
        // trying a workflow out in the builder must not page real people.
        Assert.AreEqual(0, await _host.Reminders!.ProcessDueAsync());
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task It_skips_a_definition_with_no_lead_time()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), _definitionId, "user-originator")).Unwrap();

        Assert.AreEqual(0, await _host.Reminders!.ProcessDueAsync());
        Assert.IsNull(await ReminderSentAtAsync(run.Tasks.Single().Id));
    }

    [TestMethod]
    public async Task It_skips_a_task_with_no_due_date()
    {
        _dueDates.Due = null;
        var task = await ArmedTaskAsync();

        Assert.AreEqual(0, await _host.Reminders!.ProcessDueAsync());
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task A_deadline_moved_later_defers_the_nudge()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        // Extended before the sweep ever ran. A date stamped at creation would fire here.
        _dueDates.Due = DateTime.UtcNow.AddDays(10);

        Assert.AreEqual(0, await _host.Reminders!.ProcessDueAsync());
        Assert.IsNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task A_deadline_moved_earlier_brings_the_nudge_forward()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        Assert.AreEqual(0, await _host.Reminders!.ProcessDueAsync());

        // This is why the candidate query does not filter on DueDate: a task pulled
        // forward would never enter a set narrowed to "already inside its window".
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);

        Assert.AreEqual(1, await _host.Reminders!.ProcessDueAsync());
        Assert.IsNotNull(await ReminderSentAtAsync(task.Id));
    }

    [TestMethod]
    public async Task The_re_resolved_date_is_written_back()
    {
        _dueDates.Due = DateTime.UtcNow.AddDays(30);
        var task = await ArmedTaskAsync(leadMinutes: 120);

        var moved = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        _dueDates.Due = moved;

        await _host.Reminders!.ProcessDueAsync();

        var stored = await _host.Db.WorkflowTasks
            .AsNoTracking().Where(t => t.Id == task.Id).Select(t => t.DueDate).SingleAsync();

        // Repairing the cached date is a side effect of re-resolving, and it is what
        // keeps the inbox's Due column honest without a second sweep.
        Assert.AreEqual(moved, stored);
    }

    [TestMethod]
    public async Task The_dispatch_is_attributed_to_the_system_actor()
    {
        _dueDates.Due = DateTime.UtcNow.AddMinutes(30);
        var task = await ArmedTaskAsync();

        await _host.Reminders!.ProcessDueAsync();

        var execution = await _host.Db.WorkflowTriggerExecutions
            .AsNoTracking()
            .Where(e => e.TaskId == task.Id && e.Event == WorkflowEventKind.TaskDueSoon)
            .SingleAsync();

        // TriggerExecution has no ActorId column: WorkflowEntity's CreatorId is where
        // the dispatching actor lands (TriggerDispatcher.cs:172).
        Assert.AreEqual(WorkflowActors.System, execution.CreatorId);
    }
}
```

> Two property names worth double-checking against
> `src/Workflow.Core/Model/Entities.cs`, because both are easy to guess wrong:
> `TriggerDefinition`'s JSON column is **`Configuration`**, not `ConfigJson`; and
> `TriggerExecution` has **no `ActorId`** — the dispatching actor lands in
> `WorkflowEntity.CreatorId` (`TriggerDispatcher.cs:172`). `TriggerRuntimeTests.cs`
> already builds `TriggerDefinition` rows and asserts on `TriggerExecution`; copy the
> shape from there if anything else disagrees.

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~ReminderTests"
```

Expected: **build failure**, CS0246 — `IWorkflowReminderProcessor` could not be found.

- [ ] **Step 4: Write the processor**

Create `src/Workflow.Persistence.EF/Triggers/ReminderProcessor.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;

namespace Workflow.Persistence.EF.Triggers;

public interface IWorkflowReminderProcessor
{
    /// <summary>
    /// Sweeps for tasks inside their reminder lead time and dispatches
    /// <see cref="WorkflowEventKind.TaskDueSoon"/> for each. Returns how many fired.
    /// </summary>
    Task<int> ProcessDueAsync(int batchSize = 100, CancellationToken ct = default);
}

/// <summary>
/// The one thing in this engine that happens because <b>nobody acted</b>.
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
/// </summary>
public sealed class ReminderProcessor(
    IWorkflowDbContext db,
    IWorkflowTriggerDispatcher dispatcher,
    IWorkflowDueDateResolver dueDates,
    ILogger<ReminderProcessor> logger) : IWorkflowReminderProcessor
{
    public async Task<int> ProcessDueAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // AsNoTracking throughout: every write below is an ExecuteUpdateAsync, so nothing
        // here needs the change tracker — and keeping these entities untracked means the
        // conditional claim cannot be second-guessed by a stale tracked copy.
        var candidates = await db.WorkflowTasks
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
            // Note what is NOT here: DueDate. Narrowing to tasks already inside their
            // window would mean a deadline moved *earlier* never enters the set and never
            // nudges at all. The set is bounded by "open, not yet reminded, configured to
            // nudge", which for any realistic host is a small number of rows. If that ever
            // costs, narrow on a generous outer bound (DueDate == null || DueDate <= now +
            // maxLeadTime + slack), never on the exact window.
            .OrderBy(t => t.Id)
            .Take(batchSize)
            .ToListAsync(ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return 0;
        }

        // Subjects for the runs involved, in one query rather than one per task.
        var runIds = candidates.Select(t => t.WorkflowRunId).Distinct().ToList();

        var subjects = (await db.WorkflowRuns
            .AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Subject.SubjectType, r.Subject.SubjectId })
            .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(r => r.Id, r => new WorkflowSubject(r.SubjectType, r.SubjectId));

        var fired = 0;

        foreach (var task in candidates)
        {
            if (!subjects.TryGetValue(task.WorkflowRunId, out var subject))
            {
                continue;
            }

            // Re-resolved every pass, not read from the cached column. Deadlines move: a
            // ChangeRequest gets extended, and a date stamped at task creation is stale from that
            // moment. This is what keeps the reminder honest, and repairing DueDate is a
            // side effect that keeps the inbox honest too.
            DateTime? due;

            try
            {
                due = await dueDates.ResolveAsync(subject, task.ToSnapshot(), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Due-date resolver failed for task {TaskId} during the reminder sweep; " +
                    "skipping it this pass.", task.Id);
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

            var lead = task.TaskDefinition!.ReminderLeadTimeMinutes!.Value;

            if (due.Value.AddMinutes(-lead) > now)
            {
                continue;
            }

            if (await ClaimAndDispatchAsync(task, now, ct).ConfigureAwait(false))
            {
                fired++;
            }
        }

        return fired;
    }

    /// <summary>
    /// Claim first, dispatch second, both inside one transaction.
    ///
    /// The claim is an update conditioned on <c>ReminderSentAt IS NULL</c>; if it affects
    /// zero rows another instance won and this one skips. Doing it before the dispatch
    /// rather than after makes the failure mode a <i>missed</i> reminder rather than a
    /// duplicated one — which is the right way round, because a person nudged twice for
    /// the same task stops trusting the nudges. The outbox message's IdempotencyKey is a
    /// second line of defence behind this, not the primary one.
    /// </summary>
    private async Task<bool> ClaimAndDispatchAsync(
        WorkflowTask task, DateTime now, CancellationToken ct) =>
        // WorkflowTransaction.ExecuteAsync, never BeginTransaction: a provider configured
        // with EnableRetryOnFailure refuses a user-initiated transaction taken outside its
        // execution strategy. This codebase has got that wrong twice already.
        await WorkflowTransaction.ExecuteAsync(db, async token =>
        {
            var claimed = await db.WorkflowTasks
                .Where(t => t.Id == task.Id && t.ReminderSentAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReminderSentAt, now), token)
                .ConfigureAwait(false);

            if (claimed == 0)
            {
                logger.LogDebug(
                    "Task {TaskId} was claimed by another sweep; skipping.", task.Id);
                return false;
            }

            await dispatcher.DispatchAsync(
                task, WorkflowEventKind.TaskDueSoon, WorkflowActors.System, ct: token)
                .ConfigureAwait(false);

            return true;
        }, ct).ConfigureAwait(false);
}
```

- [ ] **Step 5: Register it**

In `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`, inside
`AddWorkflowEngine`, beside the outbox processor:

```csharp
        services.AddScoped<IWorkflowOutboxProcessor, OutboxProcessor>();
        services.AddScoped<IWorkflowReminderProcessor, ReminderProcessor>();
```

> Registered unconditionally, like the outbox processor. Registering the *processor* is
> not the same as running it — that is `AddReminderProcessing()` in Task 7, and without
> that call nothing ever sweeps.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~ReminderTests"
```

Expected: PASS, 14 tests.

If `Two_concurrent_sweeps_produce_one_dispatch` fails with both sweeps claiming, check
that the two scopes really did get two `DemoDbContext` instances —
`services.AddDbContext<DemoDbContext>` is scoped, so they should, but a stray singleton
registration in a test's `configure` hook would defeat the whole test.

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.Persistence.EF tests/Workflow.Tests/ReminderTests.cs tests/Workflow.Tests/TestHost.cs
git commit -m "Add the reminder sweep"
```

---

### Task 7: The sweeper's host

A thin `BackgroundService` and an opt-in registration. Everything worth testing was in
Task 6; this is the timer that drives it.

**Files:**
- Create: `src/Workflow.Persistence.EF/Triggers/ReminderHostedService.cs`
- Modify: `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Write the hosted service**

Create `src/Workflow.Persistence.EF/Triggers/ReminderHostedService.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Workflow.Persistence.EF.Triggers;

/// <summary>Options for the reminder sweep.</summary>
public sealed class WorkflowReminderOptions
{
    /// <summary>
    /// How often to sweep. Five minutes rather than the outbox's ten seconds because
    /// there is nothing to signal here: a reminder becomes due by the clock advancing,
    /// and nothing in-process knows when that happened. Lead times are measured in days,
    /// so five minutes of latency is invisible.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Candidate tasks examined per pass.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// How long to wait after a failed pass before trying again. Longer than the poll
    /// interval, as the outbox's is: a pass fails when the database is unreachable, and
    /// hammering it helps nobody.
    /// </summary>
    public TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Drives <see cref="IWorkflowReminderProcessor"/> on a timer.
///
/// Deliberately thin, and deliberately unlike <see cref="WorkflowOutboxHostedService"/>
/// in one respect: there is no signal to wake it early. An outbox row appears because
/// something in this process just wrote it; a reminder becomes due because time passed,
/// which nothing can signal.
/// </summary>
public sealed class WorkflowReminderHostedService(
    IServiceScopeFactory scopeFactory,
    WorkflowReminderOptions options,
    ILogger<WorkflowReminderHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Workflow reminder sweep started; polling every {Interval}.", options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.PollInterval;

            try
            {
                // Keep going while a full batch keeps coming back, so a backlog is not
                // metered out one batch per poll. Same shape as the outbox drain.
                int fired;

                do
                {
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider
                        .GetRequiredService<IWorkflowReminderProcessor>();

                    fired = await processor
                        .ProcessDueAsync(options.BatchSize, stoppingToken)
                        .ConfigureAwait(false);
                }
                while (fired >= options.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed pass must not kill the service: nothing was claimed, so the
                // next pass sees the same candidates.
                logger.LogError(ex, "Reminder sweep failed; retrying in {Backoff}.", options.ErrorBackoff);
                delay = options.ErrorBackoff;
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Workflow reminder sweep stopped.");
    }
}
```

> **`fired >= options.BatchSize` is not quite the outbox's condition, and that is
> correct.** The outbox counts messages *attempted*, so a full batch means more may be
> waiting. `ProcessDueAsync` returns only the ones that *fired*, and a pass can examine a
> full batch of candidates while firing none — so this loop under-continues rather than
> spins. The next poll picks up the remainder five minutes later, which for a lead time
> measured in days is not a problem worth a second counter.

- [ ] **Step 2: Add the opt-in registration**

In `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`, add to
`WorkflowEngineBuilder`, immediately after `AddOutboxProcessing`:

```csharp
    /// <summary>
    /// Runs the reminder sweep: a hosted service that asks
    /// <see cref="IWorkflowReminderProcessor"/> for due reminders on a timer.
    ///
    /// **Without this nothing ever nudges anybody.** The columns are still written and the
    /// inbox still shows deadlines — visibility needs no background machinery — but no
    /// <see cref="Workflow.Core.Model.WorkflowEventKind.TaskDueSoon"/> is ever raised.
    ///
    /// Register it once per process. In a web farm every instance sweeps the same table,
    /// which is safe: each task is claimed with a conditional update before it is
    /// dispatched, so exactly one instance wins.
    ///
    /// Reminders are delivered through the ordinary trigger runtime, and the built-in
    /// <c>workflow.notify</c> trigger defaults to after-commit dispatch — so a host that
    /// wants a reminder actually delivered needs <see cref="AddOutboxProcessing"/> as
    /// well.
    /// </summary>
    public WorkflowEngineBuilder AddReminderProcessing(Action<WorkflowReminderOptions>? configure = null)
    {
        var options = new WorkflowReminderOptions();
        configure?.Invoke(options);

        Services.AddSingleton(options);
        Services.AddHostedService<WorkflowReminderHostedService>();

        return this;
    }
```

- [ ] **Step 3: Build and run the whole suite**

```bash
dotnet build && dotnet test
```

Expected: build clean; PASS at **190 + 14 = 204 tests**. Nothing tests the hosted service
directly — it holds a timer and a scope factory and no logic of its own, which is the
point of Task 6's split.

- [ ] **Step 4: Commit**

```bash
git add src/Workflow.Persistence.EF
git commit -m "Run the reminder sweep from a hosted service"
```

---

### Task 8: The due date in the inbox

No background machinery on the read path — one more column in a projection that already
exists, carried across two seams and rendered.

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs`
- Modify: `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs`
- Modify: `src/Workflow.MudBlazor/WorkflowInbox.razor`
- Test: `tests/Workflow.Tests/InboxTests.cs`, `tests/Workflow.Tests/InboxClientTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/Workflow.Tests/InboxTests.cs`:

```csharp
    [TestMethod]
    public async Task The_due_date_survives_the_projection()
    {
        // The projection is hand-written .Select, so a new column reaches InboxTaskSnapshot
        // only if somebody adds it in two places. This is the test that notices.
        var task = await StartAndGetEntryAsync("1");

        var due = new DateTime(2026, 11, 3, 9, 30, 0, DateTimeKind.Utc);
        task.DueDate = due;
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", [])).Unwrap();

        Assert.AreEqual(due, rows.Single(r => r.TaskId == task.Id).DueDate);
    }

    [TestMethod]
    public async Task A_task_with_no_due_date_projects_null()
    {
        var task = await StartAndGetEntryAsync("1");

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", [])).Unwrap();

        Assert.IsNull(rows.Single(r => r.TaskId == task.Id).DueDate);
    }
```

Add to `tests/Workflow.Tests/InboxClientTests.cs`, using that file's own
`CreateDocumentAsync` / `StartRunOnAsync` helpers:

```csharp
    [TestMethod]
    public async Task The_due_date_reaches_the_inbox_item()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var due = new DateTime(2026, 11, 3, 9, 30, 0, DateTimeKind.Utc);

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        task.DueDate = due;
        await _host.Db.SaveChangesAsync();

        var items = await _client.GetInboxAsync("user-originator");

        Assert.AreEqual(due, items.Single().DueDate);
    }

    [TestMethod]
    public async Task An_item_with_no_deadline_has_a_null_due_date()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0043", "Switchboard survey");
        await StartRunOnAsync(documentId);

        // TestHost registers no due-date resolver, so the funnel stamped null.
        var items = await _client.GetInboxAsync("user-originator");

        Assert.IsNull(items.Single().DueDate);
    }
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~Inbox"
```

Expected: **build failure**, CS1061 — `InboxTaskSnapshot` does not contain a definition
for `DueDate`.

- [ ] **Step 3: Add the column to the engine projection**

In `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs`, three edits.

`InboxRow` gains a field at the end:

```csharp
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
    DateTime? DueDate);
```

`InboxTaskSnapshot` gains the same, documented:

```csharp
    int? SubWorkflowInstanceId,
    DateTime Created,
    /// <summary>
    /// When this is due, in <b>UTC</b>, or null for no deadline. Straight from the
    /// engine's cached column — the sweeper repairs it, so it is as fresh as the last
    /// sweep. Compare against <see cref="DateTime.UtcNow"/>.
    /// </summary>
    DateTime? DueDate);
```

The `.Select` gains one line, after `Created: t.Created`:

```csharp
                    Created: t.Created,
                    DueDate: t.DueDate))
```

and the final `rows.Select(...)` mapping gains, after `Created: r.Created`:

```csharp
                Created: r.Created,
                DueDate: r.DueDate))];
```

- [ ] **Step 4: Add it to the seam and the demo client**

In `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs`, append to `InboxItem` and document
it in the same style as `Created`:

```csharp
/// <param name="DueDate">
/// When the task is due, in <b>UTC</b>, or null for no deadline — straight from the
/// engine's column, like <paramref name="Created"/>. Compare against
/// <see cref="DateTime.UtcNow"/>.
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
    DateTime? DueDate);
```

In `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs`, one line at the end of the
`new InboxItem(…)` construction:

```csharp
                    Created: r.Created,
                    DueDate: r.DueDate);
```

- [ ] **Step 5: Render the Due column**

In `src/Workflow.MudBlazor/WorkflowInbox.razor`, add a header after the "Waiting" one:

```razor
                @* Sortable, but not the default sort. Ordering overdue work to the top
                   sounds right and is not: the list would reorder as deadlines pass, and
                   the engine's stable (Created, TaskId) ordering exists precisely to stop
                   rows moving between refreshes. Anyone who wants that view clicks this.

                   Nulls sort as DateTime.MaxValue so "no deadline" lands at the far end
                   rather than at the top, where an ascending sort on a nullable would
                   otherwise put every undated row ahead of the most overdue one. *@
                <MudTh><MudTableSortLabel SortBy="@(new Func<InboxItem, object?>(i => (i.DueDate ?? DateTime.MaxValue, i.TaskId)))" T="InboxItem">Due</MudTableSortLabel></MudTh>
```

and a cell after the "Waiting" one:

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

and the helper, beside `Waited`:

```razor
    /// <summary>
    /// How long until this is due, or how long it has been overdue. DueDate is UTC, so
    /// the comparison must be against UtcNow — DateTime.Now compiles and is wrong by the
    /// host's offset. Same arithmetic as <see cref="Waited"/>, opposite sign.
    /// </summary>
    private static string DueIn(DateTime due)
    {
        var remaining = due - DateTime.UtcNow;

        if (remaining < TimeSpan.Zero)
        {
            var over = remaining.Negate();

            return over.TotalDays >= 1 ? $"{(int)over.TotalDays}d overdue"
                : over.TotalHours >= 1 ? $"{(int)over.TotalHours}h overdue"
                : "overdue";
        }

        return remaining.TotalDays >= 1 ? $"due in {(int)remaining.TotalDays}d"
            : remaining.TotalHours >= 1 ? $"due in {(int)remaining.TotalHours}h"
            : $"due in {Math.Max(1, (int)remaining.TotalMinutes)}m";
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~Inbox"
```

Expected: PASS, **4 new tests** — two in `InboxTests`, two in `InboxClientTests` — on
top of whatever those two classes already had. Running total: 208.

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs \
        src/Workflow.Core/Inbox/IWorkflowInboxClient.cs \
        src/Workflow.MudBlazor/WorkflowInbox.razor \
        samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs \
        tests/Workflow.Tests
git commit -m "Show the due date in the inbox"
```

---

### Task 9: The due date in the runner

The runner shows the due date for the same reason it shows the assignee: it is a property
of the task, and the runner is where the task is worked.

**Files:**
- Modify: `src/Workflow.Core/Abstractions/Snapshots.cs`
- Modify: `src/Workflow.Persistence.EF/Snapshots.cs`
- Modify: `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs`
- Modify: `src/Workflow.MudBlazor/WorkflowRunner.razor`

- [ ] **Step 1: Carry it on the task snapshot**

In `src/Workflow.Core/Abstractions/Snapshots.cs`, append to `WorkflowTaskSnapshot`:

```csharp
public sealed record WorkflowTaskSnapshot(
    int Id,
    int WorkflowRunId,
    int TaskDefinitionId,
    string TaskTypeKey,
    string DisplayName,
    WorkflowTaskStatus Status,
    string? OutcomeKey,
    string? AssignedToActorId,
    string? AssignedBranchKey,
    int? ParentTaskId,
    int? SubWorkflowInstanceId,
    bool IsForkOrigin,
    Guid? ForkGroupId,
    int? ForkManifestId,
    string? Notes,
    DateTime? CompletedDate,
    /// <summary>
    /// When this is due, in <b>UTC</b>, or null for no deadline. Defaulted so the
    /// sixteen-argument positional constructions elsewhere in the engine keep compiling —
    /// including the assignment resolver's Placeholder, where the task does not exist yet
    /// and there is nothing to be due.
    /// </summary>
    DateTime? DueDate = null);
```

> **The default is load-bearing.** `WorkflowEngine.Placeholder` (`WorkflowEngine.cs:626`)
> and the tests written in Task 2 construct this positionally with sixteen arguments.
> Without `= null` every one of them breaks.

In `src/Workflow.Persistence.EF/Snapshots.cs`, one line at the end of `ToSnapshot`:

```csharp
        CompletedDate: t.CompletedDate,
        DueDate: t.DueDate);
```

- [ ] **Step 2: Carry it to the runner's view**

In `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs`, add to `RunTaskView` — put it
immediately after `AssignedBranchKey`, so it reads beside the other per-task facts rather
than among the fork machinery:

```csharp
    string? AssignedBranchKey,

    /// <summary>When this task is due, in <b>UTC</b>, or null for no deadline.</summary>
    DateTime? DueDate,

    string? Notes,
```

> This is a positional record with a long parameter list, and `DemoRunnerClient` is the
> only place that constructs it — using named arguments throughout, so inserting a
> parameter mid-list is safe here in a way it would not be for a persisted enum.

In `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs`, add to the
`new RunTaskView(…)` construction, after `AssignedBranchKey: t.AssignedBranchKey,`:

```csharp
                    AssignedBranchKey: t.AssignedBranchKey,
                    DueDate: t.DueDate,
```

- [ ] **Step 3: Render it**

In `src/Workflow.MudBlazor/WorkflowRunner.razor`, in the task row's chip stack, add
immediately **before** the `<MudSpacer />`:

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

> A date, not a countdown. The inbox is a triage list where "3d overdue" is the useful
> form; the runner is where one task is worked, and the actual deadline is what somebody
> needs to read off the screen. Both compare against `DateTime.UtcNow` because both
> columns are UTC.

- [ ] **Step 4: Build and run the whole suite**

```bash
dotnet build && dotnet test
```

Expected: build clean, PASS at **208 tests** — this task adds none of its own.
`RunnerClientTests` exercises `DemoRunnerClient`'s projection, so a missed field shows up
there.

- [ ] **Step 5: Commit**

```bash
git add src/Workflow.Core src/Workflow.Persistence.EF src/Workflow.MudBlazor samples/DemoDocuments.Server
git commit -m "Show the due date on a runner task row"
```

---

### Task 10: The lead time in the builder

A workflow author decides which task types nudge and how far ahead. This is the task
where a field can appear to save and silently vanish — the failure STATE.md records for
sub-workflow attachments, which were neither loaded nor rewritten for a full day before
anybody noticed.

**Files:**
- Modify: `src/Workflow.Core/Builder/EditModels.cs`
- Modify: `src/Workflow.MudBlazor/TaskEditDialog.razor`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoBuilderClient.cs`
- Test: `tests/Workflow.Tests/EditModelCloneTests.cs`, `tests/Workflow.Tests/BuilderClientTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `tests/Workflow.Tests/EditModelCloneTests.cs`:

```csharp
    [TestMethod]
    public void Clone_carries_the_reminder_lead_time()
    {
        var original = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = 2880 };

        var clone = original.Clone();

        Assert.AreEqual(2880, clone.ReminderLeadTimeMinutes);
    }

    [TestMethod]
    public void CopyFrom_carries_the_reminder_lead_time()
    {
        var target = new TaskEditModel { TaskTypeKey = "review" };
        var source = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = 720 };

        target.CopyFrom(source);

        Assert.AreEqual(720, target.ReminderLeadTimeMinutes);
    }

    [TestMethod]
    public void CopyFrom_clears_a_lead_time_the_editor_removed()
    {
        // Cancel means cancel, and so does clearing a field. A CopyFrom that only assigns
        // non-null values would make the field impossible to unset.
        var target = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = 720 };
        var source = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = null };

        target.CopyFrom(source);

        Assert.IsNull(target.ReminderLeadTimeMinutes);
    }

    [TestMethod]
    public void The_day_facing_property_round_trips_a_half_day()
    {
        // The reason this is double? and not int?: an int? getter does integer division on
        // the stored minutes, so a 12-hour lead time reads back as 0 — a value that is not
        // what is stored, and that the next save would write back as zero.
        var model = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeDays = 0.5 };

        Assert.AreEqual(720, model.ReminderLeadTimeMinutes);
        Assert.AreEqual(0.5, model.ReminderLeadTimeDays);
    }

    [TestMethod]
    public void Clearing_the_day_facing_property_clears_the_minutes()
    {
        var model = new TaskEditModel { TaskTypeKey = "review", ReminderLeadTimeMinutes = 2880 };

        model.ReminderLeadTimeDays = null;

        Assert.IsNull(model.ReminderLeadTimeMinutes);
    }
```

Add to `tests/Workflow.Tests/BuilderClientTests.cs`, using that file's own
`_client` and `SeededVersionIdAsync()` — note the read method is
**`GetWorkflowAsync`**, not `LoadAsync`, and it returns a nullable:

```csharp
    [TestMethod]
    public async Task A_reminder_lead_time_survives_a_save_and_reload()
    {
        // Attachments were neither loaded nor rewritten for a full day before anybody
        // noticed, because the builder appeared to save them. This is that test, for the
        // field added this time.
        var model = (await _client.GetWorkflowAsync(await SeededVersionIdAsync()))!;

        var typeKey = model.Tasks[0].TaskTypeKey;
        model.Tasks[0].ReminderLeadTimeMinutes = 2880;

        // The seeded version is published, so SaveAsync forks a draft rather than editing
        // in place — reload the version it actually wrote to, as the file's other
        // round-trip tests do.
        var saved = await _client.SaveAsync(model);
        var reloaded = (await _client.GetWorkflowAsync(saved.VersionId))!;

        Assert.AreEqual(2880, reloaded.Tasks
            .First(t => t.TaskTypeKey == typeKey)
            .ReminderLeadTimeMinutes);
    }
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~EditModelCloneTests|FullyQualifiedName~BuilderClientTests"
```

Expected: **build failure**, CS0117 — `TaskEditModel` has no `ReminderLeadTimeMinutes`.

- [ ] **Step 3: Add both properties to the edit model**

In `src/Workflow.Core/Builder/EditModels.cs`, in `class TaskEditModel`, after
`AssignmentRoleKey`:

```csharp
    /// <summary>
    /// How far ahead of the due date to nudge, in minutes. Null means this task never
    /// nudges. Minutes because that is what the column stores — see
    /// <see cref="Workflow.Core.Model.WorkflowTaskDefinition.ReminderLeadTimeMinutes"/>
    /// for why it is not a TimeSpan.
    /// </summary>
    public int? ReminderLeadTimeMinutes { get; set; }

    /// <summary>
    /// The same value in days, which is what a person authoring a workflow thinks in and
    /// what the builder binds to.
    ///
    /// <b>double, not int.</b> An int getter over stored minutes does integer division, so
    /// a 12-hour lead time set programmatically or by an earlier build reads back as 0 —
    /// showing the author a number that is not what is stored, and writing that zero back
    /// on the next save. Nothing warns; the field simply lies.
    /// </summary>
    public double? ReminderLeadTimeDays
    {
        get => ReminderLeadTimeMinutes is { } minutes ? minutes / 1440d : null;
        set => ReminderLeadTimeMinutes = value is { } days
            ? (int)Math.Round(days * 1440d)
            : null;
    }
```

In `Clone()`, after `AssignmentRoleKey = AssignmentRoleKey,`:

```csharp
        ReminderLeadTimeMinutes = ReminderLeadTimeMinutes,
```

In `CopyFrom(…)`, after `AssignmentRoleKey = source.AssignmentRoleKey;`:

```csharp
        ReminderLeadTimeMinutes = source.ReminderLeadTimeMinutes;
```

> Both copy the **minutes**, never the days property. Copying days would round-trip the
> value through a division and a multiplication for no reason, and `Math.Round` is not
> guaranteed to land back on the same integer.

- [ ] **Step 4: Round-trip it through the builder client**

`DemoBuilderClient` reads and writes the graph in two separate places, and both need the
field. Missing either is the attachments bug again.

**Load** — in `ToEditModel`, in the `new TaskEditModel { … }` initialiser, after
`AssignmentRoleKey = task.AssignmentRoleKey,`:

```csharp
                AssignmentRoleKey = task.AssignmentRoleKey,
                ReminderLeadTimeMinutes = task.ReminderLeadTimeMinutes,
```

**Save** — in the `new WorkflowTaskDefinition { … }` at `DemoBuilderClient.cs:432`, after
`AssignmentRoleKey = NullIfBlank(task.AssignmentRoleKey),`:

```csharp
                AssignmentRoleKey = NullIfBlank(task.AssignmentRoleKey),
                ReminderLeadTimeMinutes = task.ReminderLeadTimeMinutes,
```

The second `new WorkflowTaskDefinition` in this file, at `DemoBuilderClient.cs:768`, is
inside `ToTransientVersion` — the projection the **validator** runs against. It needs
nothing: no validation rule mentions lead times, and adding the field there would suggest
one does.

- [ ] **Step 5: Add the dialog field**

In `src/Workflow.MudBlazor/TaskEditDialog.razor`, after the "Assign to role" `MudSelect`
and before the `<MudDivider />`:

```razor
            <MudNumericField T="double?" @bind-Value="_edit.ReminderLeadTimeDays"
                             Label="Remind this many days before due"
                             Variant="Variant.Outlined" Clearable="true"
                             Min="0" Step="1"
                             HelperText="Blank means this task never sends a reminder. Needs a due date from the host, and a Task Due Soon trigger to deliver it." />
```

> `T="double?"`, matching the property. A `MudNumericField` bound to `int?` here would
> reintroduce exactly the truncation the property exists to avoid, at the UI layer
> instead of the model layer.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/Workflow.Tests --filter "FullyQualifiedName~EditModelCloneTests|FullyQualifiedName~BuilderClientTests"
```

Expected: PASS, **6 new tests** — five in `EditModelCloneTests`, one in
`BuilderClientTests`. Running total: 214.

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.Core/Builder/EditModels.cs \
        src/Workflow.MudBlazor/TaskEditDialog.razor \
        samples/DemoDocuments.Server/Workflow/DemoBuilderClient.cs \
        tests/Workflow.Tests
git commit -m "Author reminder lead times in the builder"
```

---

### Task 11: The demo host

The worked example: a resolver against the host's own documents, a seeded lead time so
`/inbox` has something to show, and the two registrations.

**Files:**
- Create: `samples/DemoDocuments.Server/Workflow/DemoDueDateResolver.cs`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs`
- Modify: `samples/DemoDocuments.Server/Program.cs`

- [ ] **Step 1: Write the resolver**

Create `samples/DemoDocuments.Server/Workflow/DemoDueDateResolver.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Where a deadline comes from in this host: the document the run is about.
///
/// The fourth worked example of a host seam, and the smallest — which is the point. The
/// engine stores a date it cannot derive, because a deadline belongs to whatever the run
/// is about and only the host knows that a run on <c>ChangeRequest:42</c> inherits that ChangeRequest's date.
///
/// <c>InternalDueDate ?? DueDate</c>: internal first, because an internal deadline is the
/// one the organisation actually works to. The external date is what was promised to
/// somebody else, and is usually later.
/// </summary>
public sealed class DemoDueDateResolver(DemoDbContext db) : IWorkflowDueDateResolver
{
    public async Task<DateTime?> ResolveAsync(
        WorkflowSubject subject,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // Keyed on the id's shape, not on SubjectType — the same limitation
        // DemoInboxClient documents, and for the same reason. Correct while every subject
        // this host creates is a document; the first non-document subject type with an
        // integer key would inherit an unrelated document's deadline. Filter on
        // subject.SubjectType when a second type appears.
        if (!int.TryParse(subject.SubjectId, out var documentId))
        {
            return null;
        }

        var dates = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.InternalDueDate, d.DueDate })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return dates?.InternalDueDate ?? dates?.DueDate;
    }
}
```

> **Deliberately not per-task.** Every task in a run gets the document's date. A per-step
> allowance ("this review gets 5 days of it") would be the engine inventing a deadline the
> organisation already has an answer for — the spec's "Where the due date comes from" says
> so, and this resolver is where that decision would otherwise leak in.

- [ ] **Step 2: Seed a lead time**

In `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs`, extend the local `Task`
helper with one more optional parameter:

```csharp
        WorkflowTaskDefinition Task(
            string typeKey,
            string? displayName = null,
            string? role = null,
            bool adHoc = false,
            bool forkable = false,
            bool convergence = false,
            bool terminal = false,
            bool blocking = false,
            int? reminderLeadTimeMinutes = null)
```

and set it in the initialiser, after `IsBlocking = blocking,`:

```csharp
                IsBlocking = blocking,
                ReminderLeadTimeMinutes = reminderLeadTimeMinutes,
```

Then give "Provide Input" a two-day lead time:

```csharp
        // Two days, so /inbox has something to show and the sweeper has something to find.
        // Provide Input rather than the entry task on purpose: it is the forkable one, so
        // a forked run produces several reminded tasks and exercises the funnel's
        // fork path in the demo as well as in the tests.
        var provideInput = Task("provide-input", "Provide Input", DemoRoles.SectionLead,
            forkable: true, reminderLeadTimeMinutes: 2880);
```

> **Check whether `SeedAsync` short-circuits on an existing database** — the top of the
> method returns early if a definition is already there. A demo database created before
> this change keeps its old seed, so drop and recreate it (delete the `WorkflowDemo`
> database, or let `MigrateAsync` run against a fresh one) if the lead time does not
> appear.

- [ ] **Step 3: Wire it up**

In `samples/DemoDocuments.Server/Program.cs`, extend the engine registration block:

```csharp
builder.Services.AddWorkflowEngine()
    .AddAssignmentResolver<DemoAssignmentResolver>()
    .AddActorResolver<DemoActorResolver>()
    .AddProgressSink<DemoProgressSink>()
    .AddNotificationSink<DemoNotificationSink>()
    .AddRouteCondition<RequiresReviewCondition>()
    // Deadlines come from the document the run is about. Without this the engine stamps
    // null dates and the whole feature is inert.
    .AddDueDateResolver<DemoDueDateResolver>()
    // Without this the outbox fills up and nothing drains it: after-commit triggers
    // never run at all. A short poll here because it is a demo; the default is 10s.
    .AddOutboxProcessing(o => o.PollInterval = TimeSpan.FromSeconds(5))
    // And without this nothing ever nudges anybody. A short poll for the same reason —
    // the default is 5 minutes, which is far too slow to watch working.
    .AddReminderProcessing(o => o.PollInterval = TimeSpan.FromSeconds(15));
```

- [ ] **Step 4: Build and run the whole suite**

```bash
dotnet build && dotnet test
```

Expected: build clean, PASS at **214 tests** — this task adds none of its own.

`TestHost` builds its own container and does **not** call `AddDueDateResolver`, so the
demo's resolver does not change any existing test's behaviour — which is what keeps the
counts above stable through Tasks 5–11.

- [ ] **Step 5: See it work in the demo**

```bash
source "$WORKFLOW_DEV_ENV"
export ConnectionStrings__Demo="Server=localhost,1433;Database=WorkflowDemo;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;Encrypt=False"
cd samples/DemoDocuments.Server && dotnet run --urls http://localhost:5199
```

Then, in a browser:

1. `/documents` → open a document, and set its **Internal due date** to tomorrow (or
   create one via `POST /api/documents` with an `InternalDueDate`).
2. Start a run on it and complete **Enter Record**, so a **Provide Input** task exists.
3. `/inbox` as the Section Lead for that section → the row shows **due in 1d** in the Due column.
4. On `/workflows/{versionId}`, open **Provide Input**, add a trigger on the
   **Task Due Soon** event with the **Send Notification** implementation, and publish.
5. Within 15 seconds the console logs `Notification to user-section-lead-c200: …` from
   `DemoNotificationSink`.

**If step 5 produces nothing**, check in this order: the trigger is on the published
version the run is pinned to (a run does not pick up a trigger added to a newer version);
`AddOutboxProcessing` is registered (notify defaults to after-commit, so the outbox is
what actually delivers it); and the task's `ReminderSentAt` is still null in the database
(it fires once, so a second look at the same task shows nothing).

- [ ] **Step 6: Commit**

```bash
git add samples/DemoDocuments.Server
git commit -m "Wire deadlines and reminders into the demo host"
```

---

### Task 12: Verify and record

**Files:**
- Modify: `STATE.md`
- Modify: `docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md`

- [ ] **Step 1: Full verification, from a clean build**

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
source "$WORKFLOW_DEV_ENV"
cd <repo root>

dotnet clean && dotnet build && dotnet test
```

Expected: build clean with **no warnings introduced by this branch**, and **214 tests
passing**:

| Source | New tests | Running total |
|---|---|---|
| Baseline | — | 175 |
| Task 2 — the seam | 3 | 178 |
| Task 4 — the funnel | 12 | 190 |
| Task 5 — the event | 0 (one assertion added to an existing test) | 190 |
| Task 6 — the sweeper | 14 | 204 |
| Task 8 — the inbox | 4 | 208 |
| Task 10 — the builder | 6 | 214 |

**Do not claim this is done on a partial run.** If the count differs from 214, reconcile
it before writing anything into STATE.md — a test that silently failed to compile into
the suite is the usual reason, and it looks exactly like success.

- [ ] **Step 2: Confirm the funnel really has no bypass**

```bash
grep -rn "new WorkflowTask$\|new WorkflowTask {\|new WorkflowTask()" src/ samples/
```

Expected: exactly **one** hit, inside `NewTaskAsync` in
`src/Workflow.Persistence.EF/WorkflowEngine.cs`. Anything else is a creation path that
never gets a deadline, and the whole argument for the funnel is that such a path is
invisible until somebody asks this question.

- [ ] **Step 3: Mark the spec implemented**

In `docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md`:

```markdown
**Status:** Implemented 2026-08-21 — see `docs/superpowers/plans/2026-08-21-deadlines-and-reminders.md`
```

- [ ] **Step 4: Update STATE.md**

Four edits.

**a. The status line**, near the top:

```markdown
**Builds clean, 214/214 integration tests pass** against SQL Server. Every project,
`Workflow.MudBlazor` included, is in the solution.
```

**b. Add to the "Done" list**, after the task inbox entry:

```markdown
- **Deadlines and reminders.** `IWorkflowDueDateResolver` is the fourth host seam — the
  host answers when a task is due and the engine never computes it. The answer is resolved
  inside the single `NewTaskAsync` funnel, so all eight creation paths inherit deadlines
  and a ninth added later inherits one for free. `WorkflowTask.DueDate` and
  `ReminderSentAt` cache the answer and the fire-once stamp;
  `WorkflowTaskDefinition.ReminderLeadTimeMinutes` says how far ahead to nudge. The inbox
  and the runner show the date with no background machinery at all. A scoped
  `IWorkflowReminderProcessor`, driven by `WorkflowReminderHostedService` and opted into
  with `AddReminderProcessing()`, re-resolves each open candidate every pass, repairs the
  cached date, claims the row conditionally and dispatches `WorkflowEventKind.TaskDueSoon`
  — which the built-in `workflow.notify` trigger already supported, because it declares
  every event kind.
```

**c. Remove "Timers / SLA escalation" from "Not done"** and replace it with what is
genuinely still missing:

```markdown
- **Escalation actions.** Nothing reassigns, notifies a supervisor, or cancels when a
  deadline actually passes. `TaskDueSoon` is the pattern a `TaskOverdue` event and its
  actions would follow, and the sweeper is where they go.
- **Timers as a workflow primitive.** No "wait 10 days, then advance on your own". That
  is durable suspend/resume and a different feature from a deadline.
```

**d. Add to "Things to be careful about"**:

```markdown
- **The reminder claim happens before the dispatch, not after.** If dispatch fails the
  reminder is lost rather than repeated, and that is the deliberate choice: a person nudged
  twice for the same task stops trusting the nudges. The outbox message's `IdempotencyKey`
  is a second line of defence behind the claim, not the primary one.
- **The sweeper's candidate query must not filter on `DueDate`.** Narrowing to tasks
  already inside their window looks like an obvious optimisation and silently breaks the
  case the re-resolution exists for: a deadline moved *earlier* would never enter the set
  and would never nudge at all. If the cost ever matters, narrow on a generous outer bound
  (`DueDate == null || DueDate <= now + maxLeadTime + slack`), never on the exact window.
- **`ReminderLeadTimeMinutes` is minutes, and the builder's day field is `double?`.** EF
  Core maps `TimeSpan` to SQL Server `time(7)`, which caps at 24 hours, so a lead time of a
  day or more would silently truncate — that is why the column is an int. And an `int?`
  day-facing property does integer division on those minutes, so a 12-hour lead time reads
  back as `0` and the next save writes that zero. `TaskEditModel.ReminderLeadTimeDays` is
  `double?` for exactly this reason, and the `MudNumericField` bound to it must be
  `T="double?"` too.
- **`DemoDueDateResolver` keys on the id's shape, not `SubjectType`** — the same
  limitation `DemoInboxClient` has, with the same consequence: the first non-document
  subject type with an integer key inherits an unrelated document's deadline.
- **Two more SQL Server-specific index filters.** The sweeper's
  `(Status, ReminderSentAt)` index uses `HasFilter("[ReminderSentAt] IS NULL")`, joining
  the convergence unique index in `WorkflowModelBuilder.cs`. Supporting another provider
  means conditioning both.
- **`WorkflowEventKind.TaskDueSoon` is last in the enum and must stay there**, like
  `SubWorkflowCancelled` before it. `SchemaGuardTests.Event_kind_ordinals_are_stable` pins
  it at 15; if that test fails, fix the enum, not the test.
- **The subject cache on the engine is per-scope, and depends on a run's subject never
  changing.** `_subjectsByRun` memoises so the funnel does not query per task. Nothing on
  `IWorkflowEngine` can change a run's subject today; anything that ever could must
  invalidate this.
- **`AddReminderProcessing()` needs `AddOutboxProcessing()` to actually deliver
  anything.** The sweeper raises the event, but `workflow.notify` defaults to after-commit
  dispatch, so the outbox is what carries it to the sink. A host that registers only the
  first gets `TriggerExecution` rows and no notifications.
```

- [ ] **Step 5: Commit**

```bash
git add STATE.md docs/superpowers/specs/2026-08-21-deadlines-and-reminders-design.md
git commit -m "Record deadlines and reminders in STATE.md"
```

- [ ] **Step 6: Finish the branch**

Use the superpowers:finishing-a-development-branch skill to decide between merging to
`main`, opening a PR, or leaving the branch in place.

---

## Self-review notes

Checked against the spec, section by section. Where they now disagree, Task 1 amends the
spec first, so nothing downstream carries a contradiction.

**Covered:** the host seam and its null default and failure policy (Task 2); the three
columns and their types (Task 3); the funnel and all eight creation paths (Task 4); the
appended event kind and the system actor (Task 5); the candidate query, the re-resolution,
the conditional claim and every case in the spec's sweeper test list (Task 6); the hosted
service and its options (Task 7); `DueDate` through `InboxTaskSnapshot` → `InboxItem` →
the Due column, with row order deliberately unchanged (Task 8); the runner (Task 9); the
builder field and its round-trip (Task 10); `DemoDueDateResolver` as
`InternalDueDate ?? DueDate`, the seeded lead time, and the wiring (Task 11); the
migration drift guard (Task 3, re-run in Task 12).

**Deliberately absent, matching the spec's own scope section:** escalation actions,
timers as a primitive, `SetTaskDueDateAsync`, working-day arithmetic, a reminder series,
and bUnit component tests.

**One judgement call worth flagging to the reviewer.** The spec's test list says "skips
completed, cancelled, forked, and archived tasks", and Task 6 writes explicit tests for
completed, cancelled and archived but not for `Forked`. Reaching a `Forked` task requires
forking a task whose *definition* carries a lead time, and the seeded forkable definition
is the one Task 11 arms — so the case is reachable, just fiddlier to set up than the other
three. It is worth adding if the sweep is ever changed; the positive status list in the
candidate query is what makes it safe today, and that list is the thing under test.
