# Task Inbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.

**Goal:** Give a person one place that answers "what is waiting on me?", with a link to the document where each piece of work happens.

**Architecture:** Three layers, matching what the builder and the runner already do. An engine query (`GetOpenTasksForActorAsync`) owns the predicate that is easy to get wrong. A host seam (`IWorkflowInboxClient`) turns opaque `WorkflowSubject`s into labelled, linkable rows by joining to the host's own tables. A component (`WorkflowInbox.razor`) renders them and navigates on click. The demo implements the seam as the worked example.

**Tech Stack:** .NET 10, EF Core against SQL Server, MSTest integration tests, Blazor Server + MudBlazor 9.7.0.

**Spec:** `docs/superpowers/specs/2026-08-20-task-inbox-design.md`

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

Baseline before you touch anything: `dotnet build` clean, `dotnet test` green at **148 tests**.

### Three standing hazards in this repo

1. **The namespace trap.** Any `using` of `MudBlazor` inside `Workflow.MudBlazor`, or of
   `Workflow.*` inside `DemoDocuments.Server`, must be written `@using global::…`. A bare
   using binds to the enclosing namespace and fails as CS0246 on hand-written `@inject`
   / `@code` while markup still compiles. See `src/Workflow.MudBlazor/_Imports.razor`.
2. **Never call `BeginTransaction`.** Use `WorkflowTransaction.ExecuteAsync`. Nothing in
   this plan writes, so this should not come up — if you find yourself reaching for a
   transaction, you have gone off-plan.
3. **A DbContext permits one operation at a time**, and Blazor components re-render
   concurrently. The component in Task 6 copies the runner's semaphore for this reason.

### Facts you will need, already verified

- Seeded sections: `C100` Electrical, `C200` Mechanical, `C300` Structural — all under
  one Branch whose head is `user-branch-head`.
- Seeded people: `user-originator` (no section), `user-section-lead-c200` = Sam Section Lead (Mechanical),
  `user-worker-c200` = Drew Novak (Mechanical), `user-worker-c100` = Casey Ellis
  (Electrical), `user-unassigned` = Sky Vance (no section).
- `StartRunAsync(subject, definitionId, "user-originator")` creates the `enter-record`
  entry task assigned to `user-originator` with a **null** branch key. (`enter-record`
  declares the `originator` role, and `DemoAssignmentResolver` returns the current
  assignment unchanged for it.)
- The seeder creates **no documents**. Tests create their own, as
  `DocumentTaskServiceTests.cs:28` does.
- `DocumentBase.DocumentType` is `Ignore`d in EF — it is computed from the concrete
  type — so **you cannot filter on it in a LINQ query**. Join on `Id` only.

## File structure

| File | Responsibility |
|---|---|
| Create: `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs` | `InboxTaskSnapshot` and the one query that owns the predicate |
| Modify: `src/Workflow.Persistence.EF/IWorkflowEngine.cs` | Declare `GetOpenTasksForActorAsync` |
| Create: `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs` | The host seam and `InboxItem` |
| Create: `src/Workflow.MudBlazor/WorkflowInbox.razor` | The table, the chips, the navigation |
| Modify: `src/Workflow.MudBlazor/_Imports.razor` | `@using global::Workflow.Core.Inbox` |
| Create: `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs` | Worked example: engine rows joined to documents |
| Create: `samples/DemoDocuments.Server/Components/Pages/Inbox.razor` | The demo page |
| Modify: `samples/DemoDocuments.Server/Components/_Imports.razor` | `@using global::Workflow.Core.Inbox` |
| Modify: `samples/DemoDocuments.Server/Components/Layout/MainLayout.razor` | Nav button |
| Modify: `samples/DemoDocuments.Server/Program.cs` | Register the client; rewrite `/tasks/assigned/{actorId}` |
| Create: `tests/Workflow.Tests/InboxTests.cs` | The engine predicate, nine cases |
| Create: `tests/Workflow.Tests/InboxClientTests.cs` | Label, subtitle, URL, orphan row |
| Modify: `STATE.md` | Record what shipped and the two new hazards |

Tasks 1–2 build the engine query. Task 3 adds the seam. Tasks 4–5 build and test the demo
client. Task 6 builds the component. Task 7 wires the demo. Task 8 verifies and records.

---

## Task 1: The engine query — assignment, status and test runs

**Files:**
- Create: `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs`
- Modify: `src/Workflow.Persistence.EF/IWorkflowEngine.cs`
- Create: `tests/Workflow.Tests/InboxTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Workflow.Tests/InboxTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

using Workflow.Core.Model;
using Workflow.Persistence.EF;

namespace Workflow.Tests;

/// <summary>
/// The inbox predicate.
///
/// This is the one piece of the inbox that must live in the library: every host asking
/// "what is waiting on me?" has to exclude forked ghosts and the builder's test runs,
/// and the hand-written version this replaces forgot the second one. Each test here is
/// a way a host writing the query itself would get it wrong.
/// </summary>
[TestClass]
public class InboxTests
{
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Starts a run and hands back its single entry task, so a test can put the
    /// assignment it cares about on a real task rather than a hand-built row.
    /// </summary>
    private async Task<WorkflowTask> StartAndGetEntryAsync(string subjectId)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), _definitionId, "user-originator")).Unwrap();

        return await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);
    }

    [TestMethod]
    public async Task It_returns_a_task_assigned_to_the_actor()
    {
        var task = await StartAndGetEntryAsync("1");

        // StartRunAsync assigns the entry task to the starting actor with no branch key.
        Assert.AreEqual("user-originator", task.AssignedToActorId);

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", [])).Unwrap();

        var row = rows.Single(r => r.TaskId == task.Id);
        Assert.AreEqual("Enter Record", row.Label);
        Assert.AreEqual("enter-record", row.TaskTypeKey);
        Assert.AreEqual("ChangeRequest", row.Subject.SubjectType);
        Assert.AreEqual("1", row.Subject.SubjectId);
        Assert.AreEqual("ChangeRequest Document Review", row.WorkflowName);
        Assert.IsFalse(row.IsUnclaimed);
    }

    [TestMethod]
    public async Task It_does_not_return_a_task_assigned_to_somebody_else()
    {
        var task = await StartAndGetEntryAsync("1");

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-worker-c200", [])).Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_returns_unclaimed_work_in_one_of_the_actors_units()
    {
        var task = await StartAndGetEntryAsync("1");

        // The state DemoAssignmentResolver produces when a section has nobody in the
        // role: a branch key, and no owner.
        task.AssignedToActorId = null;
        task.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C200"])).Unwrap();

        var row = rows.Single(r => r.TaskId == task.Id);
        Assert.IsTrue(row.IsUnclaimed);
        Assert.AreEqual("C200", row.AssignedBranchKey);
    }

    [TestMethod]
    public async Task It_does_not_return_unclaimed_work_in_another_unit()
    {
        var task = await StartAndGetEntryAsync("1");

        task.AssignedToActorId = null;
        task.AssignedBranchKey = "C300";
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C200"])).Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_does_not_return_somebody_elses_task_in_the_actors_own_unit()
    {
        var task = await StartAndGetEntryAsync("1");

        task.AssignedToActorId = "user-section-lead-c200";
        task.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        // Unclaimed means unowned, not "owned by a colleague". An inbox that showed
        // a section's whole workload would be a supervisor view, not an inbox.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            "user-worker-c200", ["C200"])).Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }

    [TestMethod]
    public async Task It_excludes_completed_cancelled_and_forked_tasks()
    {
        foreach (var status in new[]
                 {
                     WorkflowTaskStatus.Completed,
                     WorkflowTaskStatus.Cancelled,
                     WorkflowTaskStatus.Forked
                 })
        {
            var task = await StartAndGetEntryAsync($"status-{status}");
            task.Status = status;
            await _host.Db.SaveChangesAsync();

            var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", []))
                .Unwrap();

            Assert.IsFalse(
                rows.Any(r => r.TaskId == task.Id),
                $"A {status} task must not appear in an inbox.");
        }
    }

    [TestMethod]
    public async Task It_excludes_tasks_in_a_test_run()
    {
        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _definitionId && v.IsPublished && v.IsLatest)
            .Select(v => v.Id)
            .SingleAsync();

        var testRun = (await _host.Engine.StartRunOnVersionAsync(
            new WorkflowSubject("WorkflowTest", "1"), versionId, "user-originator",
            isTest: true)).Unwrap();

        // The regression this whole feature exists to prevent: an admin trying a draft
        // out must not put tasks in real people's inboxes.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", []))
            .Unwrap();

        Assert.IsFalse(rows.Any(r => r.WorkflowRunId == testRun.Id));
    }

    [TestMethod]
    public async Task It_excludes_archived_tasks()
    {
        var task = await StartAndGetEntryAsync("1");
        task.IsArchived = true;
        await _host.Db.SaveChangesAsync();

        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-originator", []))
            .Unwrap();

        Assert.IsFalse(rows.Any(r => r.TaskId == task.Id));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~InboxTests"`
Expected: FAIL to compile — `IWorkflowEngine` has no `GetOpenTasksForActorAsync`.

- [ ] **Step 3: Declare the method on the interface**

In `src/Workflow.Persistence.EF/IWorkflowEngine.cs`, add after `GetRunsForSubjectAsync`
(around line 57, before `ForkTaskAsync`):

```csharp
    /// <summary>
    /// Open tasks for one actor: those assigned to them, plus unclaimed work in the org
    /// units they belong to.
    ///
    /// The host passes its own <paramref name="branchKeys"/> because org membership is
    /// host knowledge — the engine has no user model and no directory, and a membership
    /// seam would be a third way to ask a question the host can already answer.
    /// </summary>
    Task<Result<IReadOnlyList<InboxTaskSnapshot>>> GetOpenTasksForActorAsync(
        string actorId,
        IReadOnlyList<string> branchKeys,
        CancellationToken ct = default);
```

- [ ] **Step 4: Write the query**

Create `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

using Workflow.Core.Model;
using Workflow.Core.Results;

namespace Workflow.Persistence.EF;

/// <summary>
/// One row of somebody's inbox: a task that is open, theirs, and describes the subject
/// it belongs to so a host can turn it into a link.
/// </summary>
public sealed record InboxTaskSnapshot(
    int TaskId,
    int WorkflowRunId,
    WorkflowSubject Subject,
    string TaskTypeKey,
    string Label,
    string WorkflowName,
    WorkflowTaskStatus Status,
    string? AssignedToActorId,
    string? AssignedBranchKey,
    bool IsUnclaimed,
    bool IsBlocked,
    int? SubWorkflowInstanceId,
    DateTime Created);

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

            // Materialised so it translates to an IN (...) rather than closing over the
            // parameter type; an empty list short-circuits the unclaimed clause entirely.
            var units = branchKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct().ToList();

            var rows = await db.WorkflowTasks
                .AsNoTracking()
                .Include(t => t.TaskDefinition!).ThenInclude(d => d.TaskType)
                .Include(t => t.Run!).ThenInclude(r => r.DefinitionVersion!)
                    .ThenInclude(v => v.WorkflowDefinition)
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
                .OrderBy(t => t.Created)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return (IReadOnlyList<InboxTaskSnapshot>)[.. rows.Select(t => new InboxTaskSnapshot(
                TaskId: t.Id,
                WorkflowRunId: t.WorkflowRunId,
                Subject: t.Run!.Subject,
                TaskTypeKey: t.TaskDefinition?.TaskType?.Key ?? string.Empty,
                Label: t.TaskDefinition?.DisplayName
                       ?? t.TaskDefinition?.TaskType?.DisplayName
                       ?? string.Empty,
                WorkflowName: t.Run.DefinitionVersion?.WorkflowDefinition?.Name ?? string.Empty,
                Status: t.Status,
                AssignedToActorId: t.AssignedToActorId,
                AssignedBranchKey: t.AssignedBranchKey,
                IsUnclaimed: t.AssignedToActorId == null,
                IsBlocked: false,       // Task 2
                SubWorkflowInstanceId: t.SubWorkflowInstanceId,
                Created: t.Created))];
        });
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~InboxTests"`
Expected: PASS, 8 tests.

(`"ChangeRequest Document Review"` is `DemoWorkflowSeeder.LarWorkflowName`, verified against the
seeder — not a guess.)

- [ ] **Step 6: Commit**

```bash
git add src/Workflow.Persistence.EF tests/Workflow.Tests/InboxTests.cs
git commit -m "Add the inbox query, owning the predicate hosts get wrong"
```

---

## Task 2: `IsBlocked`, and delegated work

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs`
- Modify: `tests/Workflow.Tests/InboxTests.cs`

A task held by a blocking sub-workflow is flagged, **not** filtered. It is genuinely the
actor's and genuinely waiting; hiding it makes work vanish from the only place anyone
looks, and the runner explains what it is waiting on once you arrive.

- [ ] **Step 1: Write the failing tests**

Append to `InboxTests`:

```csharp
    // ─────────────────────── Delegated and blocked work ───────────────────────

    /// <summary>
    /// Drives the seeded run to Provide Input, which is what the "Technical Review"
    /// sub-workflow is attached to, and returns that task's id.
    /// </summary>
    private async Task<int> DriveToProvideInputAsync(string subjectId)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", subjectId), _definitionId, "user-originator")).Unwrap();

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, "approved", "user-originator")).Unwrap();

        return await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .SingleAsync();
    }

    [TestMethod]
    public async Task It_includes_the_tasks_of_a_delegated_sub_workflow()
    {
        var parentTaskId = await DriveToProvideInputAsync("1");

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignment: new Workflow.Core.Abstractions.WorkflowAssignment(
                "user-worker-c200", "C200"))).Unwrap();

        // Delegated work is real work. A sub-workflow's tasks are ordinary tasks in the
        // parent's run, so the inbox needs no knowledge of sub-workflows to show them.
        var rows = (await _host.Engine.GetOpenTasksForActorAsync("user-worker-c200", ["C200"]))
            .Unwrap();

        var row = rows.Single(r => r.SubWorkflowInstanceId == instance.Id);
        Assert.AreEqual("Technical Review — Get Info", row.Label);
        Assert.IsFalse(row.IsBlocked, "The delegated task is the work, not the thing waiting.");
    }

    [TestMethod]
    public async Task It_flags_a_task_held_by_a_blocking_sub_workflow()
    {
        var parentTaskId = await DriveToProvideInputAsync("1");

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator",
            assignment: new Workflow.Core.Abstractions.WorkflowAssignment(
                "user-worker-c200", "C200"))).Unwrap();

        var parent = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == parentTaskId);
        var owner = parent.AssignedToActorId!;
        var unit = parent.AssignedBranchKey;

        var rows = (await _host.Engine.GetOpenTasksForActorAsync(
            owner, unit is null ? [] : [unit])).Unwrap();

        var row = rows.Single(r => r.TaskId == parentTaskId);

        // Flagged, not filtered: it is still this person's task, and it is still waiting.
        Assert.IsTrue(row.IsBlocked);
    }
```

(The parameter really is named `assignment` — `IWorkflowEngine.cs:94`. Supplying it is
what makes the chain resolve against the delegatee's section rather than the parent's.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~InboxTests"`
Expected: `It_flags_a_task_held_by_a_blocking_sub_workflow` FAILS — `IsBlocked` is
hardcoded `false`. `It_includes_the_tasks_of_a_delegated_sub_workflow` should already
PASS, which is the point: sub-workflow tasks need no special handling.

- [ ] **Step 3: Compute `IsBlocked`**

In `WorkflowEngine.Inbox.cs`, after the `rows` query and before the projection, add a
second query and use it. Replace `IsBlocked: false,       // Task 2` with
`IsBlocked: blockedTaskIds.Contains(t.Id),` and insert this above the `return`:

```csharp
            // One extra query rather than a correlated subquery per row: the set is
            // small (running blocking instances only) and this keeps the projection
            // free of database work.
            var candidateIds = rows.Select(t => t.Id).ToList();

            var blockedTaskIds = await db.WorkflowSubWorkflowInstances
                .AsNoTracking()
                .Where(i => i.Status == SubWorkflowStatus.Running
                            && i.IsBlocking
                            && candidateIds.Contains(i.ParentTaskId))
                .Select(i => i.ParentTaskId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~InboxTests"`
Expected: PASS, 10 tests.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS, 158 tests (148 + 10).

- [ ] **Step 6: Commit**

```bash
git add src/Workflow.Persistence.EF tests/Workflow.Tests/InboxTests.cs
git commit -m "Flag inbox tasks held by a blocking sub-workflow"
```

---

## Task 3: The host seam

**Files:**
- Create: `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs`

No test of its own — it is an interface and a record. Task 5 tests the implementation.

- [ ] **Step 1: Write the seam**

Create `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs`:

```csharp
namespace Workflow.Core.Inbox;

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
    /// Everything open and waiting on this actor, oldest first. The host decides which
    /// org units the actor belongs to before asking the engine — the component never
    /// learns what an org unit is.
    /// </summary>
    Task<IReadOnlyList<InboxItem>> GetInboxAsync(string actorId, CancellationToken ct = default);
}

/// <summary>
/// One row of the inbox, already resolved to something a person can read and click.
/// </summary>
/// <param name="SubjectUrl">
/// Where the work happens. A plain string the host builds — there is no routing
/// abstraction, because the component must not know what a document is. Empty when the
/// host cannot resolve the subject, which leaves the row visible but not clickable.
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
    DateTime Created);
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/Workflow.Core/Inbox
git commit -m "Add the inbox host seam"
```

---

## Task 4: `DemoInboxClient`

**Files:**
- Create: `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs`
- Create: `tests/Workflow.Tests/InboxClientTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Workflow.Tests/InboxClientTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using Workflow.Core.Model;

namespace Workflow.Tests;

/// <summary>
/// The host half of the inbox: turning an opaque WorkflowSubject into something a person
/// can read and click.
///
/// This is the seam's whole reason for existing. The engine knows a run is about
/// "ChangeRequest:42"; only the host knows what that is called and where it lives.
/// </summary>
[TestClass]
public class InboxClientTests
{
    private TestHost _host = null!;
    private DemoInboxClient _client = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync();
        _client = new DemoInboxClient(_host.Db, _host.Engine, new DemoOrg(_host.Db));

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<int> CreateDocumentAsync(string number, string title)
    {
        var changeRequest = new ChangeRequest
        {
            Title = title,
            DocNumber = number,
            Originator = "user-originator",
            CreatorId = "user-originator"
        };

        _host.Db.Documents.Add(changeRequest);
        await _host.Db.SaveChangesAsync();
        return changeRequest.Id;
    }

    private async Task<int> StartRunOnAsync(int documentId)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", documentId.ToString()),
            _definitionId,
            "user-originator")).Unwrap();

        return run.Id;
    }

    [TestMethod]
    public async Task It_resolves_the_document_label_subtitle_and_url()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        await StartRunOnAsync(documentId);

        var items = await _client.GetInboxAsync("user-originator");

        var item = items.Single();
        Assert.AreEqual("Enter Record", item.TaskLabel);
        Assert.AreEqual("CR-2026-0042", item.SubjectLabel);
        Assert.AreEqual("Pump room rewire", item.SubjectSubtitle);
        Assert.AreEqual($"/documents/{documentId}", item.SubjectUrl);
    }

    [TestMethod]
    public async Task It_returns_one_row_per_task_when_a_document_has_several()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        // A second open task on the same document, standing in for a fork branch or an
        // ad-hoc addition. Built directly because what is under test is the join, not
        // how the second task came to exist.
        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);

        _host.Db.WorkflowTasks.Add(new WorkflowTask
        {
            WorkflowRunId = runId,
            TaskDefinitionId = entry.TaskDefinitionId,
            Status = WorkflowTaskStatus.NotStarted,
            AssignedToActorId = "user-originator",
            CreatorId = "user-originator",
            ModifierId = "user-originator"
        });
        await _host.Db.SaveChangesAsync();

        var items = await _client.GetInboxAsync("user-originator");

        Assert.AreEqual(2, items.Count);
        Assert.IsTrue(items.All(i => i.SubjectLabel == "CR-2026-0042"));
    }

    [TestMethod]
    public async Task It_keeps_a_row_whose_document_is_missing_and_gives_it_no_url()
    {
        // No document with id 9999. An orphaned task is a defect worth seeing, so the
        // row survives with the raw subject and nothing to click — the same reasoning
        // as not filtering the query on run status.
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "9999"), _definitionId, "user-originator")).Unwrap();

        var items = await _client.GetInboxAsync("user-originator");

        var item = items.Single(i => i.TaskId == run.Tasks.Single().Id);
        Assert.AreEqual("ChangeRequest:9999", item.SubjectLabel);
        Assert.IsNull(item.SubjectSubtitle);
        Assert.AreEqual(string.Empty, item.SubjectUrl);
    }

    [TestMethod]
    public async Task It_asks_the_engine_for_the_actors_own_section()
    {
        var documentId = await CreateDocumentAsync("CR-2026-0042", "Pump room rewire");
        var runId = await StartRunOnAsync(documentId);

        var entry = await _host.Db.WorkflowTasks.SingleAsync(t => t.WorkflowRunId == runId);
        entry.AssignedToActorId = null;
        entry.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();

        // Drew Novak is in C200, so this unclaimed task is theirs to see. Casey Ellis is
        // in C100 and must not see it — which is the client deriving the branch keys
        // from the person rather than being told them.
        var drew = await _client.GetInboxAsync("user-worker-c200");
        var casey = await _client.GetInboxAsync("user-worker-c100");

        Assert.AreEqual(1, drew.Count);
        Assert.IsTrue(drew.Single().IsUnclaimed);
        Assert.AreEqual(0, casey.Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~InboxClientTests"`
Expected: FAIL to compile — `DemoInboxClient` does not exist.

- [ ] **Step 3: Write the client**

Create `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using Workflow.Core.Inbox;
using Workflow.Persistence.EF;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// The in-process half of the inbox, and the third worked example of a host seam.
///
/// Two things it does that the engine will not, both for the same reason — the engine's
/// subject is deliberately opaque:
///
///  - **Which org units the actor is in.** Derived from the person, through
///    <see cref="DemoOrg"/>, so the caller never has to know.
///  - **What a subject is called and where it lives.** One join against the host's own
///    Documents table, which is possible at all because engine tables and host tables
///    share a DbContext by design.
/// </summary>
public sealed class DemoInboxClient(
    DemoDbContext db,
    IWorkflowEngine engine,
    DemoOrg org) : IWorkflowInboxClient
{
    public async Task<IReadOnlyList<InboxItem>> GetInboxAsync(
        string actorId, CancellationToken ct = default)
    {
        // A person is in at most one section here. The engine takes a list because a
        // host whose people sit in several is already accommodated.
        var assignment = await org.AssignmentForAsync(actorId, ct).ConfigureAwait(false);
        string[] units = assignment.BranchKey is null ? [] : [assignment.BranchKey];

        var rows = (await engine.GetOpenTasksForActorAsync(actorId, units, ct).ConfigureAwait(false))
            .Unwrap();

        if (rows.Count == 0)
        {
            return [];
        }

        // One query for every document mentioned, rather than one per row. Subject ids
        // are strings because the engine cannot assume a key type; this host's are ints,
        // so anything unparseable is simply a subject that is not one of our documents.
        var documentIds = rows
            .Select(r => int.TryParse(r.Subject.SubjectId, out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct()
            .ToList();

        var documents = await db.Documents
            .AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocNumber, d.Title })
            .ToDictionaryAsync(d => d.Id, ct)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(r =>
            {
                var found = int.TryParse(r.Subject.SubjectId, out var id)
                            && documents.TryGetValue(id, out var doc)
                    ? doc
                    : null;

                return new InboxItem(
                    TaskId: r.TaskId,
                    TaskLabel: r.Label,
                    WorkflowName: r.WorkflowName,
                    // A subject with no document keeps its row, labelled with the raw
                    // key and nothing to click. An orphaned task is a defect worth
                    // seeing, not worth hiding.
                    SubjectLabel: found?.DocNumber ?? r.Subject.ToString(),
                    SubjectSubtitle: found?.Title,
                    SubjectUrl: found is null ? string.Empty : $"/documents/{found.Id}",
                    BranchKey: r.AssignedBranchKey,
                    IsUnclaimed: r.IsUnclaimed,
                    IsBlocked: r.IsBlocked,
                    Created: r.Created);
            })
        ];
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~InboxClientTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs tests/Workflow.Tests/InboxClientTests.cs
git commit -m "Add DemoInboxClient, resolving subjects to documents in one query"
```

---

## Task 5: Register the client and rewrite the endpoint

**Files:**
- Modify: `samples/DemoDocuments.Server/Program.cs:72` and `:269`

The point of this task is that the predicate ends up in the repository **once**.

- [ ] **Step 1: Register the client**

In `Program.cs`, beside the other two seams (around line 72):

```csharp
builder.Services.AddScoped<IWorkflowInboxClient, DemoInboxClient>();
```

Add `using Workflow.Core.Inbox;` to the file's usings if it is not already there.

- [ ] **Step 2: Rewrite the endpoint on the engine query**

Replace the whole `app.MapGet("/tasks/assigned/{actorId}", ...)` block (starts at
`Program.cs:269`, ends at the `.ToListAsync());` before
`app.MapGet("/workflow/definitions", ...)`) with:

```csharp
/// "My open tasks" — the query a task-driven system lives on.
///
/// Backed by IWorkflowInboxClient rather than a LINQ query of its own. The hand-written
/// version returned run ids rather than documents and forgot to exclude test runs, which
/// is the argument for the predicate living in the engine and being used from one place.
app.MapGet("/tasks/assigned/{actorId}", async (string actorId, IWorkflowInboxClient inbox) =>
    await inbox.GetInboxAsync(actorId));
```

- [ ] **Step 3: Verify it builds and nothing regressed**

Run: `dotnet build && dotnet test`
Expected: Build succeeded; PASS, 162 tests (158 + 4).

- [ ] **Step 4: Commit**

```bash
git add samples/DemoDocuments.Server/Program.cs
git commit -m "Back the assigned-tasks endpoint with the inbox seam"
```

---

## Task 6: The `WorkflowInbox` component

**Files:**
- Create: `src/Workflow.MudBlazor/WorkflowInbox.razor`
- Modify: `src/Workflow.MudBlazor/_Imports.razor`

No test. bUnit is deliberately deferred while the UI is still changing shape — see
`STATE.md`. Task 8 verifies this by running the app.

- [ ] **Step 1: Add the import**

Append to `src/Workflow.MudBlazor/_Imports.razor`:

```razor
@using global::Workflow.Core.Inbox
```

The `global::` qualifier is required, for the reason the comment above it in that file
explains. Do not write it bare.

- [ ] **Step 2: Write the component**

Create `src/Workflow.MudBlazor/WorkflowInbox.razor`:

```razor
@using global::Workflow.Core.Inbox
@implements IDisposable

@inject IWorkflowInboxClient Client
@inject NavigationManager Nav

@*
    What is waiting on one person, and where to go and do it.

    The third component of the set, after the builder and the runner. It shows one row
    per task rather than per document so it can be sorted by age: the thing that has
    waited longest is the thing to do.
*@

<MudStack Spacing="3">
    @if (_error is not null)
    {
        <MudAlert Severity="Severity.Error" Dense="true"
                  ShowCloseIcon="true" CloseIconClicked="@(() => _error = null)">
            @_error
        </MudAlert>
    }

    @if (_loading)
    {
        <MudProgressCircular Indeterminate="true" />
    }
    else if (_items.Count == 0)
    {
        <MudAlert Severity="Severity.Success" Dense="true">
            Nothing is waiting on you.
        </MudAlert>
    }
    else
    {
        <MudTable Items="_items" Dense="true" Hover="true" Elevation="1"
                  AllowUnsorted="false" T="InboxItem">
            <HeaderContent>
                <MudTh><MudTableSortLabel SortBy="@(new Func<InboxItem, object?>(i => i.TaskLabel))" T="InboxItem">Task</MudTableSortLabel></MudTh>
                <MudTh><MudTableSortLabel SortBy="@(new Func<InboxItem, object?>(i => i.SubjectLabel))" T="InboxItem">Document</MudTableSortLabel></MudTh>
                <MudTh><MudTableSortLabel SortBy="@(new Func<InboxItem, object?>(i => i.BranchKey))" T="InboxItem">Section</MudTableSortLabel></MudTh>
                @* Oldest first by default: sorting on Created ascending puts the
                   longest wait at the top, which is the order to work in.

                   SortBy must tiebreak on TaskId, not sort on Created alone. Every
                   branch of a fork is stamped with one DateTime.UtcNow, so Created is
                   not a total order — the engine query adds .ThenBy(t => t.Id) for
                   exactly this reason (see WorkflowEngine.Inbox.cs). A client-side
                   re-sort on Created alone throws that tiebreak away and reintroduces
                   the reshuffle-between-refreshes the engine went out of its way to
                   prevent. Found in the Tasks 3-4 review, 2026-08-20. *@
                <MudTh><MudTableSortLabel SortBy="@(new Func<InboxItem, object?>(i => i.Created))" T="InboxItem"
                                          InitialDirection="SortDirection.Ascending">Waiting</MudTableSortLabel></MudTh>
            </HeaderContent>
            <RowTemplate>
                <MudTd DataLabel="Task">
                    <MudStack Row="true" Spacing="1" AlignItems="AlignItems.Center">
                        <MudLink Href="@context.SubjectUrl" Underline="Underline.Hover"
                                 Disabled="@string.IsNullOrEmpty(context.SubjectUrl)">
                            @context.TaskLabel
                        </MudLink>
                        @if (context.IsUnclaimed)
                        {
                            <MudChip T="string" Size="Size.Small" Color="Color.Warning"
                                     Variant="Variant.Outlined"
                                     title="In your section, with nobody assigned. Open the document and reassign it to take it.">
                                Unclaimed
                            </MudChip>
                        }
                        @if (context.IsBlocked)
                        {
                            <MudChip T="string" Size="Size.Small" Color="Color.Default"
                                     Variant="Variant.Outlined"
                                     title="Waiting on a sub-workflow. Open it to see what.">
                                Blocked
                            </MudChip>
                        }
                    </MudStack>
                    <MudText Typo="Typo.caption" Class="mud-text-secondary">@context.WorkflowName</MudText>
                </MudTd>
                <MudTd DataLabel="Document">
                    <MudText Typo="Typo.body2">@context.SubjectLabel</MudText>
                    @if (!string.IsNullOrWhiteSpace(context.SubjectSubtitle))
                    {
                        <MudText Typo="Typo.caption" Class="mud-text-secondary">@context.SubjectSubtitle</MudText>
                    }
                </MudTd>
                <MudTd DataLabel="Section">@(context.BranchKey ?? "—")</MudTd>
                <MudTd DataLabel="Waiting">@Waited(context.Created)</MudTd>
            </RowTemplate>
        </MudTable>
    }
</MudStack>

@code {
    /// <summary>Whose inbox this is. A host with authentication passes the signed-in
    /// user; the demo passes a picker's selection.</summary>
    [Parameter, EditorRequired] public string ActorId { get; set; } = string.Empty;

    /// <summary>Raised with the row count after every load, so a host can badge a tab.</summary>
    [Parameter] public EventCallback<int> OnCountChanged { get; set; }

    private IReadOnlyList<InboxItem> _items = [];
    private string? _error;
    private bool _loading = true;
    private string _loadedFor = string.Empty;

    /// <summary>
    /// One load at a time — the same hazard the runner has. A parent that resolves the
    /// signed-in actor asynchronously re-renders when it finishes, which fires
    /// OnParametersSetAsync again while the first load is still awaiting; two concurrent
    /// loads against an EF DbContext throw, because it permits one operation at a time.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == ActorId)
        {
            return;
        }

        _loadedFor = ActorId;
        await LoadAsync();
    }

    /// <summary>Re-polls. Public so a host can refresh after something elsewhere on the
    /// page changes a run.</summary>
    public async Task RefreshAsync() => await LoadAsync();

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(ActorId))
        {
            _items = [];
            _loading = false;
            return;
        }

        await _gate.WaitAsync();
        try
        {
            _loading = true;
            _error = null;
            _items = await Client.GetInboxAsync(ActorId);
        }
        catch (Exception ex)
        {
            // Unlike the runner, this seam's read returns a bare list rather than a
            // Result, so a failure arrives as an exception and has nowhere else to go.
            _error = ex.Message;
            _items = [];
        }
        finally
        {
            _loading = false;
            _gate.Release();
        }

        await OnCountChanged.InvokeAsync(_items.Count);
    }

    private static string Waited(DateTime created)
    {
        var age = DateTime.UtcNow - created;

        return age.TotalDays >= 1 ? $"{(int)age.TotalDays}d"
            : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h"
            : $"{Math.Max(1, (int)age.TotalMinutes)}m";
    }

    public void Dispose() => _gate.Dispose();
}
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

If you get CS0246 on `IWorkflowInboxClient` or `NavigationManager`, you wrote a `using`
without `global::`. Re-read Step 1.

- [ ] **Step 4: Commit**

```bash
git add src/Workflow.MudBlazor
git commit -m "Add the WorkflowInbox component"
```

---

## Task 7: The demo page

**Files:**
- Create: `samples/DemoDocuments.Server/Components/Pages/Inbox.razor`
- Modify: `samples/DemoDocuments.Server/Components/_Imports.razor`
- Modify: `samples/DemoDocuments.Server/Components/Layout/MainLayout.razor`

- [ ] **Step 1: Add the import**

Append to `samples/DemoDocuments.Server/Components/_Imports.razor`:

```razor
@using global::Workflow.Core.Inbox
```

- [ ] **Step 2: Write the page**

Create `samples/DemoDocuments.Server/Components/Pages/Inbox.razor`:

```razor
@page "/inbox"

@inject IDbContextFactory<DemoDbContext> DbFactory

<PageTitle>My tasks</PageTitle>

<MudStack Spacing="4">
    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="4">
        <MudText Typo="Typo.h5" HtmlTag="h1">My tasks</MudText>
        <MudBadge Content="_count" Color="Color.Info" Overlap="false" Visible="@(_count > 0)" />
        <MudSpacer />
        @* The demo has no authentication, so who you are is a picker. A real host
           passes its signed-in user and shows no control at all. *@
        <MudSelect T="string" @bind-Value="_actorId" Label="Acting as" Dense="true"
                   Style="min-width: 260px;">
            @foreach (var person in _people)
            {
                <MudSelectItem T="string" Value="@person.ActorId">@person.FullName</MudSelectItem>
            }
        </MudSelect>
    </MudStack>

    <MudText Typo="Typo.body2" Class="mud-text-secondary">
        Everything open and waiting on you, oldest first, including unclaimed work in your
        section. Click a task to open the document and act on it.
    </MudText>

    <WorkflowInbox ActorId="@_actorId" OnCountChanged="@(c => _count = c)" />
</MudStack>

@code {
    private List<PersonRow> _people = [];
    private string _actorId = string.Empty;
    private int _count;

    protected override async Task OnInitializedAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();

        _people = await db.People
            .AsNoTracking()
            .OrderBy(p => p.FullName)
            .Select(p => new PersonRow(p.ActorId, p.FullName))
            .ToListAsync();

        _actorId = _people.FirstOrDefault()?.ActorId ?? "demo-user";
    }

    private sealed record PersonRow(string ActorId, string FullName);
}
```

- [ ] **Step 3: Add the nav button**

In `samples/DemoDocuments.Server/Components/Layout/MainLayout.razor`, before the
Documents button:

```razor
        <MudButton Href="/inbox" Color="Color.Inherit"
                   StartIcon="@Icons.Material.Filled.Inbox">My tasks</MudButton>
```

- [ ] **Step 4: Verify it builds and nothing regressed**

Run: `dotnet build && dotnet test`
Expected: Build succeeded; PASS, 162 tests.

- [ ] **Step 5: Commit**

```bash
git add samples/DemoDocuments.Server/Components
git commit -m "Add the demo inbox page"
```

---

## Task 8: Verify in the app, then record it

**Files:**
- Modify: `STATE.md`

- [ ] **Step 1: Start the demo on a fresh database**

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$DOTNET_ROOT/tools:$PATH"
source "$WORKFLOW_DEV_ENV"
export ConnectionStrings__Demo="Server=localhost,1433;Database=WorkflowDemo;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;Encrypt=False"
cd samples/DemoDocuments.Server && dotnet run --urls http://localhost:5199
```

Note the pid. **Stop the server by pid, not `pkill -f`.**

- [ ] **Step 2: Create a document with a run on it**

The document API lives under `/api` because the Blazor UI owns `/documents`. This one
call creates the ChangeRequest, its work items, and starts the workflow on it:

```bash
curl -s -X POST http://localhost:5199/api/documents/change-requests \
  -H 'Content-Type: application/json' \
  -d '{"title":"Pump room rewire","docNumber":"CR-2026-0042",
       "originator":"user-originator","actorId":"user-originator",
       "sectionCodes":["C100","C200"]}'
```

- [ ] **Step 3: Check the five expectations**

Open `http://localhost:5199/inbox`.

1. Acting as **Pat Originator**, the Enter Record task appears, with CR-2026-0042 and
   "Pump room rewire" in the Document column
2. Clicking it opens `/documents/{id}`
3. Acting as somebody else, the list is empty and says "Nothing is waiting on you."
4. From the document page, start the Technical Review sub-workflow on Provide Input and
   delegate it to **Drew Novak (Mechanical)**. Get Info then appears in Drew's inbox
5. The parent task appears in its owner's inbox flagged **Blocked**

If browser automation is unavailable on this machine, say so plainly and record which
expectations were verified through `InboxClientTests` instead rather than claiming a
click-through that did not happen.

- [ ] **Step 4: Stop the server**

```bash
kill <pid>
```

- [ ] **Step 5: Update STATE.md**

In the **Done** section, after the sub-workflow bullet, add:

```markdown
- **The task inbox.** `GetOpenTasksForActorAsync` on the engine owns the predicate —
  open statuses as a positive list, `!IsTest`, and mine-or-unclaimed-in-my-units — and
  `IWorkflowInboxClient` is the third host seam, turning an opaque subject into a
  labelled, linkable row. `WorkflowInbox` renders it and drops into a `MudTabPanel`.
```

In **Not done**, delete the `**No task inbox UI.**` bullet entirely.

In **Suggested next step**, delete the task-inbox mention if one is present, leaving
timers/SLA escalation, authorization and `Workflow.AspNetCore`.

In **Things to be careful about**, add:

```markdown
- **The inbox shows nothing that is wholly unassigned.** Both halves of the predicate
  need something to match on, so a task with neither an actor nor a branch key is in
  nobody's inbox. That state is reachable — `IWorkflowAssignmentResolver` may return
  `WorkflowAssignment.Unassigned` — and finding those is an admin query, not an inbox one.
- **Unclaimed means unowned, not "owned by a colleague".** The inbox shows tasks in your
  units only when `AssignedToActorId` is null. Widening it to everything in your section
  turns an inbox into a supervisor's report, and the count on the tab stops meaning
  "work I must do".
```

- [ ] **Step 6: Commit**

```bash
git add STATE.md
git commit -m "Record the task inbox in STATE.md"
```

---

## Done when

- [ ] `dotnet build` clean, `dotnet test` green — **162 tests** (148 + 10 + 4)
- [ ] A run started from the builder's Try it pane appears in nobody's inbox
- [ ] A task assigned to a colleague in your section is not in your inbox; an unowned one is
- [ ] A `Forked` task never appears
- [ ] Delegating Technical Review to Drew Novak puts Get Info in Drew's inbox, linked to the document
- [ ] The parent task shows as Blocked while the chain runs
- [ ] `/tasks/assigned/{actorId}` and the UI go through the same predicate — one LINQ query in the repository, not two
