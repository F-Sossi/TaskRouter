# Sub-workflow Delegation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.

**Goal:** Let a person delegate a short workflow chain to a named individual in another section, so the Section Lead and branch-head steps resolve against *that person's* section — and make sub-workflow attachments editable in the builder without destroying them on save.

**Architecture:** A sub-workflow attachment moves from being scoped by its task definition to being scoped by the workflow *version*, with the task definition as an optional narrowing. `StartSubWorkflowAsync` gains a `WorkflowAssignment` exactly as `AddAdHocTaskAsync` already has one; the host maps a chosen person to `(actorId, sectionCode)` so the existing `DemoAssignmentResolver` resolves the Section Lead correctly with no change. Attachments join routes, outcomes and triggers as children of `WorkflowEditModel`, which is what makes them survive a draft save.

**Tech Stack:** .NET 10, EF Core (SQL Server), MudBlazor / Blazor Server, MSTest.

**Spec:** `docs/superpowers/specs/2026-08-19-sub-workflow-delegation-design.md`

---

## Before you start

Every command below assumes this environment. `dotnet` is **not** on PATH by default on this machine:

```bash
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
source "$WORKFLOW_DEV_ENV"          # provides SA_PASSWORD
cd <repo root>
```

The tests need the SQL Server container running:

```bash
$WORKFLOW_DEV_DB_START
docker ps --format '{{.Names}}' | grep the SQL Server dev container     # expect: the SQL Server dev container
```

Baseline before any change: `dotnet build && dotnet test` → **110/110 passing**.

Three standing hazards in this repo that this plan touches:

1. **Never call `BeginTransaction` directly** — use `WorkflowTransaction.ExecuteAsync`. The provider has `EnableRetryOnFailure` on and refuses user-initiated transactions outside its execution strategy. This has already been got wrong twice.
2. **Dialogs must edit a clone.** Blazor passes reference types by reference, so a dialog bound to its `Model` parameter writes into the caller's object as the user types and Cancel cannot undo it. Use `Clone()` / `CopyFrom()`.
3. **`@using global::MudBlazor`** — a bare `using MudBlazor` inside `namespace Workflow.MudBlazor` binds to the project's own namespace. The `global::` qualifier must stay.

## File structure

| File | Responsibility | Change |
|---|---|---|
| `src/Workflow.Core/Model/Entities.cs` | `SubWorkflowAttachment` scope fields | Modify |
| `src/Workflow.Core/Validation/WorkflowDefinitionValidator.cs` | Attachment rules | Modify |
| `src/Workflow.Core/Builder/EditModels.cs` | `SubWorkflowAttachmentEditModel` | Modify |
| `src/Workflow.Core/Builder/IWorkflowBuilderClient.cs` | `GetSubWorkflowDefinitionsAsync` | Modify |
| `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs` | Blocking count, assignee on start | Modify |
| `src/Workflow.Persistence.EF/WorkflowModelBuilder.cs` | FKs and unique index | Modify |
| `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs` | Lookup precedence, assignment at spawn | Modify |
| `src/Workflow.Persistence.EF/IWorkflowEngine.cs` | `StartSubWorkflowAsync` signature | Modify |
| `src/Workflow.MudBlazor/SubWorkflowAttachDialog.razor` | Attachment editor | **Create** |
| `src/Workflow.MudBlazor/WorkflowBuilder.razor` | Sub-workflows panel | Modify |
| `src/Workflow.MudBlazor/RunnerSubWorkflowDialog.razor` | Person picker | Modify |
| `src/Workflow.MudBlazor/WorkflowRunner.razor` | Disable Complete when blocked | Modify |
| `samples/DemoDocuments.Server/Workflow/DemoOrg.cs` | Person → assignment helper | **Create** |
| `samples/DemoDocuments.Server/Workflow/DemoBuilderClient.cs` | Attachment round-trip | Modify |
| `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs` | Blocking count, assignment | Modify |
| `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs` | Section members, `get-info` step | Modify |
| `tests/Workflow.Tests/SubWorkflowTests.cs` | Blocking, precedence, delegation | Modify |
| `tests/Workflow.Tests/BuilderClientTests.cs` | Attachment survival | Modify |
| `tests/Workflow.Tests/EditModelCloneTests.cs` | Clone/CopyFrom | Modify |

---

## Task 1: Lock in the blocking behaviour with tests — ✅ DONE (`f251100`)

> **Outcome.** Three tests landed, not four. Writing the fourth revealed that **nothing can
> cancel a sub-workflow instance**: `SubWorkflowStatus.Cancelled` is set only by
> `CancelInstancesForAsync`, which runs when the *parent* is cancelled — and a cancelled
> parent can never be completed, so the release is unobservable. Task 2 was added to build
> that missing capability, and the fourth test moved there.
>
> Also corrected: this codebase's `Result<T>` has no `ErrorMessage`. It is
> `UnwrapError().Message`. Every test in this plan has been fixed accordingly.

The engine already behaves correctly — a blocking instance stops its parent until it is completed **or cancelled**. Nothing tests it, and the cancellation path is the exact the original engine's finding H1 regression. These tests are written first because they must pass **before** any other change, and must still pass after all of them.

**Files:**
- Test: `tests/Workflow.Tests/SubWorkflowTests.cs`

- [ ] **Step 1: Write the failing tests**

Append inside the `SubWorkflowTests` class, before the closing brace:

```csharp
    // ─────────────────────────────── Blocking ───────────────────────────────

    /// <summary>
    /// The rule, stated once: a blocking instance holds its parent until it reaches a
    /// terminal state. Both terminal states release it — this is the original engine's finding H1, which
    /// tested `Status != Completed` and so blocked a parent forever on a cancelled item.
    /// </summary>
    [TestMethod]
    public async Task A_blocking_instance_stops_the_parent_completing()
    {
        (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var result = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "Technical Review");
    }

    [TestMethod]
    public async Task Completing_the_instance_releases_the_parent()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        await DriveInstanceToCompletionAsync(instance.Id);

        var result = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsFalse(result.IsError, result.IsError ? result.UnwrapError().Message : null);
    }

    [TestMethod]
    public async Task A_non_blocking_instance_never_stops_the_parent()
    {
        await _host.Db.WorkflowSubWorkflowAttachments
            .Where(a => a.SubWorkflowDefinitionId == _subWorkflowId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsBlocking, false));

        _host.Db.ChangeTracker.Clear();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        Assert.IsFalse(instance.IsBlocking);

        var result = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsFalse(result.IsError, result.IsError ? result.UnwrapError().Message : null);
    }

    /// <summary>Completes every open task in an instance until it closes itself.</summary>
    private async Task DriveInstanceToCompletionAsync(int instanceId)
    {
        for (var guard = 0; guard < 10; guard++)
        {
            var open = await _host.Db.WorkflowTasks
                .Where(t => t.SubWorkflowInstanceId == instanceId
                         && (t.Status == WorkflowTaskStatus.NotStarted
                          || t.Status == WorkflowTaskStatus.InProgress))
                .Select(t => t.Id)
                .ToListAsync();

            if (open.Count == 0)
            {
                return;
            }

            foreach (var id in open)
            {
                (await _host.Engine.CompleteTaskAsync(id, "approved", "user-section-lead-c100")).Unwrap();
            }

            _host.Db.ChangeTracker.Clear();
        }

        Assert.Fail($"Instance {instanceId} did not close after 10 rounds.");
    }
```

- [ ] **Step 2: Check the cancel method's real name**

The test calls `CancelSubWorkflowAsync`. Confirm it exists and matches:

Run: `grep -n "CancelSubWorkflowAsync" src/Workflow.Persistence.EF/IWorkflowEngine.cs`
Expected: one match with signature `(int instanceId, string actorId, string? reason = null, CancellationToken ct = default)`.

If the name or parameter order differs, fix the call in the test to match the real signature. **Do not rename the engine method.**

- [ ] **Step 3: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~SubWorkflowTests"`
Expected: **all pass.** These document behaviour that already works. If any fails, the engine has a real bug — stop and report it rather than changing the test to match.

- [ ] **Step 4: Commit**

```bash
git add tests/Workflow.Tests/SubWorkflowTests.cs
git commit -m "Test that blocking sub-workflows release on completion and cancellation"
```

---

## Task 2: Let a sub-workflow be cancelled on its own

Discovered during Task 1, and the reason the fourth blocking test could not be written:
**nothing can cancel a sub-workflow instance.** `SubWorkflowStatus.Cancelled` is set only by
`CancelInstancesForAsync`, which runs when the *parent task* is cancelled — and a cancelled
parent can never be completed, so "cancelled releases the parent" is unobservable. The
requirement is that a delegated chain can be abandoned and the parent then completed.

This task is engine-only. The runner UI for it is in Task 11, which already rebuilds that
part of the component.

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs`
- Modify: `src/Workflow.Persistence.EF/IWorkflowEngine.cs`
- Test: `tests/Workflow.Tests/SubWorkflowTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `SubWorkflowTests`:

```csharp
    /// <summary>the original engine's finding H1, now actually reachable: cancelling must release.</summary>
    [TestMethod]
    public async Task Cancelling_the_instance_releases_the_parent()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        (await _host.Engine.CancelSubWorkflowAsync(
            instance.Id, "user-originator", "not needed")).Unwrap();

        var result = await _host.Engine.CompleteTaskAsync(
            _parentTaskId, "approved", "user-originator");

        Assert.IsFalse(result.IsError, result.IsError ? result.UnwrapError().Message : null);
    }

    [TestMethod]
    public async Task Cancelling_an_instance_cancels_its_open_tasks()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        (await _host.Engine.CancelSubWorkflowAsync(
            instance.Id, "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();

        var statuses = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId == instance.Id)
            .Select(t => t.Status)
            .ToListAsync();

        // Left open they would be work nobody can reach, assigned to real people.
        CollectionAssert.DoesNotContain(statuses, WorkflowTaskStatus.NotStarted);
        CollectionAssert.DoesNotContain(statuses, WorkflowTaskStatus.InProgress);
    }

    [TestMethod]
    public async Task An_instance_cannot_be_cancelled_twice()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        (await _host.Engine.CancelSubWorkflowAsync(instance.Id, "user-originator")).Unwrap();

        var second = await _host.Engine.CancelSubWorkflowAsync(instance.Id, "user-originator");

        Assert.IsTrue(second.IsError);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SubWorkflowTests"`
Expected: FAIL to compile — `CancelSubWorkflowAsync` does not exist. That is the point of this task.

- [ ] **Step 3: Extract the per-instance cancel from the existing cascade**

In `WorkflowEngine.SubWorkflows.cs`, replace `CancelInstancesForAsync` with a version that
delegates, so the parent-cascade and the standalone cancel cannot drift apart:

```csharp
    /// <summary>
    /// Cancels the instances hanging off a task that is being cancelled.
    ///
    /// Without this, cancelling a parent leaves its sub-workflow tasks assigned to
    /// people and its instances Running forever — work nobody can reach and nothing will
    /// ever close.
    /// </summary>
    private async Task CancelInstancesForAsync(
        int parentTaskId, string actorId, DateTime now, CancellationToken ct)
    {
        var instances = await db.WorkflowSubWorkflowInstances
            .Where(i => i.ParentTaskId == parentTaskId && i.Status == SubWorkflowStatus.Running)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var instance in instances)
        {
            await CancelInstanceAsync(instance, actorId, "Parent task cancelled.", now, ct)
                .ConfigureAwait(false);
        }

        if (instances.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels one instance and everything still open inside it. Shared by the
    /// parent-cancellation cascade and by <see cref="CancelSubWorkflowAsync"/>, so the
    /// two can never disagree about what cancelling an instance means.
    ///
    /// Does not save: callers batch.
    /// </summary>
    private async Task CancelInstanceAsync(
        SubWorkflowInstance instance, string actorId, string reason, DateTime now,
        CancellationToken ct)
    {
        instance.Status = SubWorkflowStatus.Cancelled;
        instance.CompletedDate = now;
        instance.ModifierId = actorId;
        instance.Modified = now;

        var open = await db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId == instance.Id
                     && t.Status != WorkflowTaskStatus.Completed
                     && t.Status != WorkflowTaskStatus.Cancelled)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var task in open)
        {
            task.Status = WorkflowTaskStatus.Cancelled;
            task.ModifierId = actorId;
            task.Modified = now;

            Log(task.Id, "Cancelled", reason, actorId, now);
        }
    }
```

- [ ] **Step 4: Add the public method**

In the same file, beside `StartSubWorkflowAsync`:

```csharp
    /// <summary>
    /// Abandons a running sub-workflow without touching its parent.
    ///
    /// The counterpart to cancelling the parent, which takes its instances down with it.
    /// This is the other direction: the delegated work turned out not to be needed, and
    /// the parent should become completable again. Without it a blocking instance can
    /// only ever be released by finishing it.
    /// </summary>
    public Task<Result<Unit>> CancelSubWorkflowAsync(
        int instanceId,
        string actorId,
        string? reason = null,
        CancellationToken ct = default) =>
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var instance = await db.WorkflowSubWorkflowInstances
                .SingleOrDefaultAsync(i => i.Id == instanceId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Sub-workflow instance {instanceId} not found.");

            if (instance.Status != SubWorkflowStatus.Running)
            {
                throw new InvalidOperationException(
                    $"Sub-workflow instance {instanceId} is already {instance.Status}.");
            }

            var now = DateTime.UtcNow;

            await CancelInstanceAsync(
                instance, actorId, reason ?? "Sub-workflow cancelled.", now, token)
                .ConfigureAwait(false);

            Log(instance.ParentTaskId, "SubWorkflowCancelled",
                reason ?? "Cancelled.", actorId, now);

            await db.SaveChangesAsync(token).ConfigureAwait(false);

            return Unit.Value;
        }, ct));
```

Match the surrounding idiom exactly — check a neighbouring `Task<Result<Unit>>` method such
as `CancelTaskAsync` in `WorkflowEngine.Queries.cs` for how `Try.RunAsync`,
`InTransactionAsync`, `Log` and `Unit.Value` are actually used here, and follow it. In
particular **never call `BeginTransaction` directly** — the provider has
`EnableRetryOnFailure` on and refuses a transaction taken outside its execution strategy.

- [ ] **Step 5: Declare it on the interface**

In `IWorkflowEngine.cs`, beside `StartSubWorkflowAsync`:

```csharp
    /// <summary>
    /// Cancels a running sub-workflow instance and everything still open in it, leaving
    /// the parent task alone. A blocking parent becomes completable again.
    /// </summary>
    Task<Result<Unit>> CancelSubWorkflowAsync(
        int instanceId,
        string actorId,
        string? reason = null,
        CancellationToken ct = default);
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test`
Expected: all passing — 113 existing plus 3 new.

- [ ] **Step 7: Commit**

```bash
git add -A src tests
git commit -m "Let a sub-workflow be cancelled without cancelling its parent"
```

---

## Task 3: Give people a section to be delegated to

No seeded `Person` has a `SectionId`, so there is currently nobody to delegate to. This must land before the delegation tests can mean anything.

**Files:**
- Modify: `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs:456-461`

- [ ] **Step 1: Write the failing test**

Append to `tests/Workflow.Tests/SubWorkflowTests.cs` inside the class:

```csharp
    [TestMethod]
    public async Task Seeded_workers_belong_to_a_section()
    {
        var worker = await _host.Db.People
            .SingleAsync(p => p.ActorId == "user-worker-c100");

        var section = await _host.Db.Sections.SingleAsync(s => s.Id == worker.SectionId);

        Assert.AreEqual("C100", section.Code);
        Assert.AreEqual("user-section-lead-c100", section.SectionLeadActorId);
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Seeded_workers_belong_to_a_section"`
Expected: FAIL — `Sequence contains no elements` (no such person).

- [ ] **Step 3: Seed section members**

In `DemoWorkflowSeeder.cs`, replace the `db.People.AddRange(...)` block in `SeedOrgAsync` with:

```csharp
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var byCode = await db.Sections.ToDictionaryAsync(s => s.Code, s => s.Id, ct)
            .ConfigureAwait(false);

        db.People.AddRange(
            new Person { ActorId = "user-originator", FullName = "Pat Originator" },
            new Person { ActorId = "user-branch-head", FullName = "Robin Branch Head" },

            // Section Leads belong to the section they lead, so delegating to a Section Lead resolves
            // to themselves rather than escaping to another section.
            new Person { ActorId = "user-section-lead-c100", FullName = "Alex Section Lead (Electrical)", SectionId = byCode["C100"] },
            new Person { ActorId = "user-section-lead-c200", FullName = "Sam Section Lead (Mechanical)", SectionId = byCode["C200"] },
            new Person { ActorId = "user-section-lead-c300", FullName = "Jo Section Lead (Structural)", SectionId = byCode["C300"] },

            // Ordinary workers — the people a chain is actually delegated to.
            new Person { ActorId = "user-worker-c100", FullName = "Casey Ellis (Electrical)", SectionId = byCode["C100"] },
            new Person { ActorId = "user-worker-c200", FullName = "Drew Novak (Mechanical)", SectionId = byCode["C200"] },
            new Person { ActorId = "user-worker-c300", FullName = "Ari Benn (Structural)", SectionId = byCode["C300"] },

            // Deliberately sectionless: the degraded path has to stay exercised.
            new Person { ActorId = "user-unassigned", FullName = "Sky Vance (no section)" });
```

Note the extra `SaveChangesAsync` before the dictionary — the sections need database ids before people can reference them.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~Seeded_workers_belong_to_a_section"`
Expected: PASS

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all passing (114 by now).

- [ ] **Step 6: Commit**

```bash
git add samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs tests/Workflow.Tests/SubWorkflowTests.cs
git commit -m "Seed section membership so there is somebody to delegate to"
```

---

## Task 4: Scope attachments by version instead of by task

**Files:**
- Modify: `src/Workflow.Core/Model/Entities.cs:95-114`
- Modify: `src/Workflow.Core/Model/Entities.cs:178` (add nav collection)
- Modify: `src/Workflow.Persistence.EF/WorkflowModelBuilder.cs:107-125`
- Create: a migration under `samples/DemoDocuments.Server/Data/Migrations`

- [ ] **Step 1: Change the entity**

In `Entities.cs`, replace the `SubWorkflowDefinitionId` / `TaskDefinitionId` block of `SubWorkflowAttachment` with:

```csharp
    /// <summary>The <see cref="WorkflowDefinition"/> to instantiate. Must have
    /// <see cref="WorkflowDefinition.IsSubWorkflow"/> set.</summary>
    public required int SubWorkflowDefinitionId { get; set; }
    public WorkflowDefinition? SubWorkflowDefinition { get; set; }

    /// <summary>
    /// The version this attachment belongs to. The scope: an attachment is authored
    /// against one version and does not leak into another, exactly as routes and
    /// triggers do not.
    /// </summary>
    public required int WorkflowDefinitionVersionId { get; set; }
    public WorkflowDefinitionVersion? DefinitionVersion { get; set; }

    /// <summary>
    /// The mainline task definition it may hang off, or null for any task in the
    /// version. Null is the common case — "available at any point" — and means a task
    /// added later inherits it rather than silently lacking it.
    /// </summary>
    public int? TaskDefinitionId { get; set; }
    public WorkflowTaskDefinition? TaskDefinition { get; set; }
```

- [ ] **Step 2: Add the navigation collection**

In `Entities.cs`, in `WorkflowDefinitionVersion`, immediately after `public ICollection<WorkflowTaskDefinition> Tasks { get; set; } = [];` add:

```csharp
    /// <summary>Sub-workflows attachable within this version. On the version rather
    /// than the task because an attachment may apply to any task in it.</summary>
    public ICollection<SubWorkflowAttachment> SubWorkflowAttachments { get; set; } = [];
```

- [ ] **Step 3: Update the EF configuration**

In `WorkflowModelBuilder.cs`, replace the whole `b.Entity<SubWorkflowAttachment>(...)` block with:

```csharp
        b.Entity<SubWorkflowAttachment>(e =>
        {
            e.ToTable("WorkflowSubWorkflowAttachments");

            e.HasOne(x => x.SubWorkflowDefinition)
             .WithMany()
             .HasForeignKey(x => x.SubWorkflowDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);

            // Cascade: an attachment cannot outlive the version that scopes it.
            e.HasOne(x => x.DefinitionVersion)
             .WithMany(v => v.SubWorkflowAttachments)
             .HasForeignKey(x => x.WorkflowDefinitionVersionId)
             .OnDelete(DeleteBehavior.Cascade);

            // NoAction, not Cascade — and not because cascading would be wrong in
            // principle. WorkflowTaskDefinition already cascades from
            // WorkflowDefinitionVersion, so a second cascade here gives SQL Server two
            // paths from version to attachment and it refuses the schema outright
            // ("may cause cycles or multiple cascade paths"). The version FK above is
            // the one worth keeping, since it covers version-wide rows too.
            //
            // Nothing is lost: ClearGraphAsync deletes attachments explicitly before
            // task definitions, so the ordering that matters is enforced in code rather
            // than by the database.
            e.HasOne(x => x.TaskDefinition)
             .WithMany()
             .HasForeignKey(x => x.TaskDefinitionId)
             .OnDelete(DeleteBehavior.NoAction);

            // One attachment of a given sub-workflow per task per version, and — because
            // SQL Server treats NULLs as equal for uniqueness — exactly one version-wide
            // attachment per sub-workflow per version.
            e.HasIndex(x => new
            {
                x.WorkflowDefinitionVersionId,
                x.TaskDefinitionId,
                x.SubWorkflowDefinitionId
            }).IsUnique();
        });
```

- [ ] **Step 4: Fix the seeder so it still compiles**

`WorkflowDefinitionVersionId` is `required`. In `DemoWorkflowSeeder.cs`, the
`db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment { ... })` call near line 414 needs the version. The mainline version variable in scope there is the one owning `attachToTaskDefinitionId`. Add to the initialiser:

```csharp
            WorkflowDefinitionVersionId = mainlineVersionId,
```

Run `grep -n "attachToTaskDefinitionId" samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs` to find how that task definition id is passed in, and thread the owning version id alongside it the same way. If the method only receives the task definition id, look it up at the top of the method:

```csharp
        var mainlineVersionId = await db.WorkflowTaskDefinitions
            .Where(d => d.Id == attachToTaskDefinitionId)
            .Select(d => d.WorkflowDefinitionVersionId)
            .SingleAsync(ct).ConfigureAwait(false);
```

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: succeeds. Fix any other `SubWorkflowAttachment` initialiser the compiler flags — `required` will point at every one.

> **Expect a cascade-path error if you deviate.** If the migration or a test fails with
> *"Introducing FOREIGN KEY constraint ... may cause cycles or multiple cascade paths"*, the
> two FKs above are both cascading. `WorkflowTaskDefinition` already cascades from
> `WorkflowDefinitionVersion`, so version → attachment and version → task → attachment are
> two paths to the same row. Keep the version FK cascading and set the task FK to
> `NoAction`. Do **not** resolve it by dropping the version cascade — version-wide
> attachments have no task to be cleaned up by.

- [ ] **Step 6: Generate the migration**

```bash
dotnet ef migrations add SubWorkflowAttachmentScope \
  --project samples/DemoDocuments.Server --context DemoDbContext --output-dir Data/Migrations
```

- [ ] **Step 7: Add the backfill to the generated migration**

EF scaffolds the column as `nullable: false` with `defaultValue: 0`, which would leave existing rows pointing at version 0. Open the generated `*_SubWorkflowAttachmentScope.cs` and, in `Up`, insert this **immediately after the `AddColumn` for `WorkflowDefinitionVersionId` and before the `CreateIndex`**:

```csharp
            // Existing attachments all name a task, so their version is that task's.
            migrationBuilder.Sql(@"
                UPDATE a
                SET a.WorkflowDefinitionVersionId = d.WorkflowDefinitionVersionId
                FROM WorkflowSubWorkflowAttachments a
                INNER JOIN WorkflowTaskDefinitions d ON d.Id = a.TaskDefinitionId;");
```

Verify the operation order in the generated file is: drop old index → add column → **backfill** → alter `TaskDefinitionId` to nullable → create new index → add foreign key. Move statements if EF ordered them otherwise; an index created before the backfill can collide on the default `0`.

- [ ] **Step 8: Run the migration tests**

Run: `dotnet test --filter "FullyQualifiedName~MigrationTests"`
Expected: PASS. `MigrationTests` fails if the model has drifted from the migrations, so this is what proves the migration is complete.

- [ ] **Step 9: Run the full suite**

Run: `dotnet test`
Expected: all passing.

- [ ] **Step 10: Commit**

```bash
git add -A src samples tests
git commit -m "Scope sub-workflow attachments by version, with the task as an optional narrowing"
```

---

## Task 5: Match version-wide attachments, task-specific first

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs:51-59` and `:296-332`
- Test: `tests/Workflow.Tests/SubWorkflowTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `SubWorkflowTests`:

```csharp
    // ────────────────────────── Attachment scope ──────────────────────────

    [TestMethod]
    public async Task A_version_wide_attachment_is_offered_on_every_task()
    {
        await AttachVersionWideAsync();

        // A task the sub-workflow was never attached to by name.
        var otherTaskId = await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == _runId && t.Id != _parentTaskId)
            .Select(t => t.Id)
            .FirstAsync();

        var options = (await _host.Engine.GetSubWorkflowOptionsAsync(otherTaskId)).Unwrap();

        Assert.IsTrue(options.Any(o => o.SubWorkflowDefinitionId == _subWorkflowId));
    }

    [TestMethod]
    public async Task A_task_specific_attachment_wins_over_a_version_wide_one()
    {
        // Seeded attachment on _parentTaskId is blocking. The version-wide one is not.
        await AttachVersionWideAsync(isBlocking: false);

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        // The narrower row governs, so the instance is blocking.
        Assert.IsTrue(instance.IsBlocking);
    }

    /// <summary>Adds a version-wide attachment of the seeded sub-workflow.</summary>
    private async Task AttachVersionWideAsync(bool isBlocking = true)
    {
        var versionId = await _host.Db.WorkflowRuns
            .Where(r => r.Id == _runId)
            .Select(r => r.WorkflowDefinitionVersionId)
            .SingleAsync();

        _host.Db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            SubWorkflowDefinitionId = _subWorkflowId,
            WorkflowDefinitionVersionId = versionId,
            TaskDefinitionId = null,
            IsAutomatic = false,
            IsBlocking = isBlocking,
            AllowMultiple = true,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~attachment"`
Expected: FAIL — the version-wide row is invisible to both lookups.

- [ ] **Step 3: Add a shared lookup with precedence**

In `WorkflowEngine.SubWorkflows.cs`, add this private helper to the class:

```csharp
    /// <summary>
    /// The attachment governing a sub-workflow on a task, version-wide rows included.
    ///
    /// Task-specific wins: an author who narrowed a sub-workflow to one task meant it,
    /// so a version-wide row is the fallback rather than a competitor. Ordering on
    /// `TaskDefinitionId != null` descending puts the specific row first.
    /// </summary>
    private async Task<SubWorkflowAttachment?> FindAttachmentAsync(
        int versionId, int taskDefinitionId, int subWorkflowDefinitionId, CancellationToken ct) =>
        await db.WorkflowSubWorkflowAttachments
            .Include(a => a.SubWorkflowDefinition)
            .Where(a => a.WorkflowDefinitionVersionId == versionId
                     && a.SubWorkflowDefinitionId == subWorkflowDefinitionId
                     && (a.TaskDefinitionId == null || a.TaskDefinitionId == taskDefinitionId)
                     && !a.IsArchived)
            .OrderByDescending(a => a.TaskDefinitionId != null)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
```

- [ ] **Step 4: Use it in `StartSubWorkflowAsync`**

Replace the existing `var attachment = await db.WorkflowSubWorkflowAttachments.SingleOrDefaultAsync(...) ?? throw ...` block with:

```csharp
            // The version owning the parent's *definition*, not the run's pinned version.
            // They differ when the parent is itself inside a sub-workflow instance, and
            // the definition's version is the right one: a sub-workflow nested under a
            // sub-workflow task should find the attachments its own author wrote.
            var versionId = parent.TaskDefinition!.WorkflowDefinitionVersionId;

            var attachment = await FindAttachmentAsync(
                versionId, parent.TaskDefinitionId, subWorkflowDefinitionId, token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "That sub-workflow is not attached to this task. Attachments are what " +
                    "decide where a sub-workflow may hang, so an unattached one is a " +
                    "configuration error rather than a runtime choice.");
```

- [ ] **Step 5: Widen `GetSubWorkflowOptionsAsync`**

Replace its opening projection and attachment query with:

```csharp
            var task = await db.WorkflowTasks
                .Where(t => t.Id == taskId)
                .Select(t => new
                {
                    t.TaskDefinitionId,
                    VersionId = t.TaskDefinition!.WorkflowDefinitionVersionId
                })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Task {taskId} not found.");

            var attachments = await db.WorkflowSubWorkflowAttachments
                .Include(a => a.SubWorkflowDefinition)
                .Where(a => a.WorkflowDefinitionVersionId == task.VersionId
                         && (a.TaskDefinitionId == null
                          || a.TaskDefinitionId == task.TaskDefinitionId)
                         && !a.IsArchived)
                .ToListAsync(ct).ConfigureAwait(false);

            // One row per sub-workflow, the task-specific one where both exist.
            attachments = [.. attachments
                .GroupBy(a => a.SubWorkflowDefinitionId)
                .Select(g => g.OrderByDescending(a => a.TaskDefinitionId != null).First())
                .OrderBy(a => a.SubWorkflowDefinition!.Name)];
```

Delete the now-unused `definitionId` variable and its `if (definitionId == 0)` guard; the projection above throws instead.

- [ ] **Step 6: Leave `SpawnAutomaticAsync` alone**

Confirm it still queries `a.TaskDefinitionId == task.TaskDefinitionId && a.IsAutomatic`. Automatic **and** version-wide would spawn an instance on every task in the run; Task 7 makes the validator reject that combination rather than the engine interpreting it.

Run: `grep -n "IsAutomatic" src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs`
Expected: the `SpawnAutomaticAsync` query is unchanged.

- [ ] **Step 7: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~SubWorkflowTests"`
Expected: PASS, including the Task 1 blocking tests.

- [ ] **Step 8: Commit**

```bash
git add -A src tests
git commit -m "Match version-wide attachments, with task-specific rows taking precedence"
```

---

## Task 6: Carry the delegatee's assignment into the spawn

**Files:**
- Modify: `src/Workflow.Persistence.EF/IWorkflowEngine.cs:89-96`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs:34-40, 85-90, 133-137`
- Test: `tests/Workflow.Tests/SubWorkflowTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `SubWorkflowTests`:

```csharp
    // ──────────────────────────── Delegation ────────────────────────────

    [TestMethod]
    public async Task The_entry_task_goes_to_the_delegatee_and_their_section()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator",
            new WorkflowAssignment("user-worker-c200", "C200"))).Unwrap();

        var entry = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id);

        Assert.AreEqual("user-worker-c200", entry.AssignedToActorId);
        Assert.AreEqual("C200", entry.AssignedBranchKey);
    }

    /// <summary>
    /// The point of the whole feature: the review goes to the *delegatee's* Section Lead, not
    /// the delegator's. Before this, the entry task inherited the parent's branch key
    /// and the lookup resolved against the wrong section, silently.
    /// </summary>
    [TestMethod]
    public async Task The_review_resolves_to_the_delegatees_Section Lead_not_the_delegators()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator",
            new WorkflowAssignment("user-worker-c300", "C300"))).Unwrap();

        var entry = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id);

        (await _host.Engine.CompleteTaskAsync(entry.Id, "approved", "user-worker-c300")).Unwrap();

        var review = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .Where(t => t.SubWorkflowInstanceId == instance.Id && t.Id != entry.Id)
            .SingleAsync();

        Assert.AreEqual("user-section-lead-c300", review.AssignedToActorId);
        Assert.AreEqual("C300", review.AssignedBranchKey);
    }

    [TestMethod]
    public async Task Delegating_without_an_assignment_still_inherits_the_parent()
    {
        var instance = (await _host.Engine.StartSubWorkflowAsync(
            _parentTaskId, _subWorkflowId, "user-originator")).Unwrap();

        var entry = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.SubWorkflowInstanceId == instance.Id);

        var parent = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == _parentTaskId);

        // Unchanged behaviour when nobody is named — this is not a regression surface.
        Assert.AreEqual(parent.AssignedBranchKey, entry.AssignedBranchKey);
    }
```

Add `using Workflow.Core.Abstractions;` to the file's usings if `WorkflowAssignment` does not resolve.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~delegatee OR FullyQualifiedName~Delegating"`
Expected: FAIL to **compile** — `StartSubWorkflowAsync` has no such overload.

- [ ] **Step 3: Change the interface**

In `IWorkflowEngine.cs`, replace the `StartSubWorkflowAsync` declaration with:

```csharp
    /// <summary>
    /// Starts a sub-workflow against a task, on the sub-workflow's latest published
    /// version. The sub-workflow must be attached to the task's definition.
    ///
    /// <paramref name="assignment"/> is who the chain is delegated to. Supplying it is
    /// what makes a downstream role key resolve against *their* org unit rather than
    /// the parent's; omitting it inherits the parent, as before. The actor and the
    /// branch key must describe the same person — the host is what knows the mapping.
    /// </summary>
    Task<Result<SubWorkflowInstanceSnapshot>> StartSubWorkflowAsync(
        int parentTaskId,
        int subWorkflowDefinitionId,
        string actorId,
        WorkflowAssignment? assignment = null,
        string? notes = null,
        CancellationToken ct = default);
```

- [ ] **Step 4: Thread it through the implementation**

In `WorkflowEngine.SubWorkflows.cs`, change the public method signature to match, then pass it down. The call to `SpawnAsync` becomes:

```csharp
            return await SpawnAsync(parent, attachment, actorId, assignment, notes, token)
                .ConfigureAwait(false);
```

Change `SpawnAsync`'s signature to:

```csharp
    private async Task<SubWorkflowInstanceSnapshot> SpawnAsync(
        WorkflowTask parent,
        SubWorkflowAttachment attachment,
        string actorId,
        WorkflowAssignment? requested,
        string? notes,
        CancellationToken ct)
```

and replace its assignment resolution with:

```csharp
        // The requested assignment replaces the parent's as the resolver's input, which
        // is the whole delegation mechanism: an entry task with no role key inherits the
        // named person, and one with a role key resolves on top of them.
        var assignment = await ResolveAssignmentAsync(
            entryDef,
            requested ?? new WorkflowAssignment(parent.AssignedToActorId, parent.AssignedBranchKey),
            parent.ToSnapshot(),
            ct).ConfigureAwait(false);
```

- [ ] **Step 5: Fix the automatic-spawn caller**

`SpawnAutomaticAsync` now needs the extra argument. Automatic spawns have nobody to ask, so they inherit:

```csharp
            await SpawnAsync(task, attachment, actorId, requested: null, notes: null, ct)
                .ConfigureAwait(false);
```

- [ ] **Step 6: Build and fix positional callers**

Run: `dotnet build`

Any caller passing `notes` positionally as the 4th argument now passes it as `assignment` and will fail to compile. Fix each by naming the argument: `notes: "please review"`. Expect hits in `SubWorkflowTests.cs` and `DemoRunnerClient.cs`.

- [ ] **Step 7: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~SubWorkflowTests"`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add -A src tests
git commit -m "Let a sub-workflow be delegated to a named person in another section"
```

---

## Task 7: Validate the attachment rules

**Files:**
- Modify: `src/Workflow.Core/Validation/WorkflowDefinitionValidator.cs`
- Test: `tests/Workflow.Tests/BuilderClientTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `BuilderClientTests`:

```csharp
    [TestMethod]
    public void An_automatic_attachment_must_name_a_task()
    {
        var version = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = 1,
            Version = 1,
            SubWorkflowAttachments =
            [
                new SubWorkflowAttachment
                {
                    SubWorkflowDefinitionId = 2,
                    WorkflowDefinitionVersionId = 1,
                    TaskDefinitionId = null,
                    IsAutomatic = true
                }
            ]
        };

        var errors = WorkflowDefinitionValidator.Validate(version);

        Assert.IsTrue(errors.Any(e => e.Code == "attachment.automatic-needs-task"),
            "Automatic and version-wide would spawn an instance on every task in the run.");
    }
```

Add `using Workflow.Core.Model;` and `using Workflow.Core.Validation;` if not already present.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~An_automatic_attachment_must_name_a_task"`
Expected: FAIL — no error with that code is produced.

- [ ] **Step 3: Add the rules**

In `WorkflowDefinitionValidator.Validate`, before the final `return errors;`, add:

```csharp
        // Attachments. Automatic + version-wide is the one combination the engine cannot
        // interpret sensibly: it would spawn an instance on every task in the run.
        foreach (var attachment in version.SubWorkflowAttachments.Where(a => !a.IsArchived))
        {
            if (attachment.IsAutomatic && attachment.TaskDefinitionId is null)
            {
                errors.Add(new ValidationError(
                    "attachment.automatic-needs-task",
                    "An automatic sub-workflow must be attached to a specific task. " +
                    "Attached to any task, it would start on every task in the run."));
            }

            if (attachment.TaskDefinitionId is { } taskId
                && version.Tasks.All(t => t.Id != taskId))
            {
                errors.Add(new ValidationError(
                    "attachment.unknown-task",
                    $"Sub-workflow attachment names task {taskId}, which is not in this version.",
                    taskId));
            }

            // Only checkable when the caller loaded the navigation. The validator is a
            // pure function over an entity graph and cannot go to the database, so a
            // caller that did not Include it gets no opinion rather than a false alarm.
            if (attachment.SubWorkflowDefinition is { IsSubWorkflow: false } target)
            {
                errors.Add(new ValidationError(
                    "attachment.not-a-sub-workflow",
                    $"'{target.Name}' is not flagged as a sub-workflow, so it cannot be attached."));
            }
        }

        var duplicates = version.SubWorkflowAttachments
            .Where(a => !a.IsArchived)
            .GroupBy(a => (a.TaskDefinitionId, a.SubWorkflowDefinitionId))
            .Where(g => g.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            errors.Add(new ValidationError(
                "attachment.duplicate",
                "The same sub-workflow is attached twice at the same scope. " +
                "Use AllowMultiple for concurrent instances instead."));
        }
```

> **Where the published-version rule lives, and why not here.** The spec lists "the
> attached definition must have a published version" as a validation rule. It cannot go in
> this validator: `Validate` is a static pure function over one entity graph with no
> database access, and the answer can change after validation anyway. It is enforced in
> two places instead — `SubWorkflowDefinitionOption.HasPublishedVersion` warns in the
> attach dialog (Task 10), and `SpawnAsync` already throws at spawn time. That is a real
> limitation, recorded under "Known limits" in the spec, not an oversight.

- [ ] **Step 4: Run the test**

Run: `dotnet test --filter "FullyQualifiedName~An_automatic_attachment_must_name_a_task"`
Expected: PASS

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all passing. The seeder validates its own sub-workflow, so a rule that is too strict shows up here.

- [ ] **Step 6: Commit**

```bash
git add -A src tests
git commit -m "Validate sub-workflow attachment scope and duplicates"
```

---

## Task 8: Put attachments in the builder's edit model

**Files:**
- Modify: `src/Workflow.Core/Builder/EditModels.cs`
- Modify: `src/Workflow.Core/Builder/IWorkflowBuilderClient.cs`
- Test: `tests/Workflow.Tests/EditModelCloneTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `EditModelCloneTests`:

```csharp
    [TestMethod]
    public void Editing_a_cloned_attachment_leaves_the_original_alone()
    {
        var original = new SubWorkflowAttachmentEditModel
        {
            SubWorkflowDefinitionId = 7,
            TaskLocalId = null,
            IsAutomatic = false,
            IsBlocking = true,
            AllowMultiple = false
        };

        var clone = original.Clone();
        clone.IsBlocking = false;
        clone.SubWorkflowDefinitionId = 9;

        Assert.IsTrue(original.IsBlocking);
        Assert.AreEqual(7, original.SubWorkflowDefinitionId);

        original.CopyFrom(clone);

        Assert.IsFalse(original.IsBlocking);
        Assert.AreEqual(9, original.SubWorkflowDefinitionId);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Editing_a_cloned_attachment"`
Expected: FAIL to compile — no such type.

- [ ] **Step 3: Add the edit model**

In `EditModels.cs`, add before `public sealed record TaskTypeOption(...)`:

```csharp
/// <summary>
/// A sub-workflow attachable within this version.
///
/// On <see cref="WorkflowEditModel"/> rather than on a task, because
/// <see cref="TaskLocalId"/> is nullable and a version-wide attachment has no task to
/// sit under.
/// </summary>
public sealed class SubWorkflowAttachmentEditModel
{
    public int Id { get; set; }
    public int SubWorkflowDefinitionId { get; set; }

    /// <summary>Target task by client-side id, or null for any task in the version.</summary>
    public Guid? TaskLocalId { get; set; }

    public bool IsAutomatic { get; set; }
    public bool IsBlocking { get; set; } = true;
    public bool AllowMultiple { get; set; }

    public SubWorkflowAttachmentEditModel Clone() => new()
    {
        Id = Id,
        SubWorkflowDefinitionId = SubWorkflowDefinitionId,
        TaskLocalId = TaskLocalId,
        IsAutomatic = IsAutomatic,
        IsBlocking = IsBlocking,
        AllowMultiple = AllowMultiple
    };

    public void CopyFrom(SubWorkflowAttachmentEditModel source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Id = source.Id;
        SubWorkflowDefinitionId = source.SubWorkflowDefinitionId;
        TaskLocalId = source.TaskLocalId;
        IsAutomatic = source.IsAutomatic;
        IsBlocking = source.IsBlocking;
        AllowMultiple = source.AllowMultiple;
    }
}
```

- [ ] **Step 4: Hang it off the workflow model**

In `WorkflowEditModel`, after `public List<TaskEditModel> Tasks { get; set; } = [];` add:

```csharp
    /// <summary>Sub-workflows attachable in this version. Version-scoped like routes and
    /// triggers, so it must round-trip or a draft save silently drops it.</summary>
    public List<SubWorkflowAttachmentEditModel> SubWorkflows { get; set; } = [];
```

- [ ] **Step 5: Extend the client seam**

In `IWorkflowBuilderClient.cs`, add before `ValidateAsync`:

```csharp
    /// <summary>Definitions flagged as sub-workflows, for the attachment picker.</summary>
    Task<IReadOnlyList<SubWorkflowDefinitionOption>> GetSubWorkflowDefinitionsAsync(
        CancellationToken ct = default);
```

and after the `SaveResult` record:

```csharp
/// <summary><paramref name="HasPublishedVersion"/> because an attachment to a
/// sub-workflow with no published version cannot spawn — the engine throws at spawn
/// time, so the builder warns rather than letting it be discovered in production.</summary>
public sealed record SubWorkflowDefinitionOption(
    int DefinitionId, string Name, bool HasPublishedVersion);
```

- [ ] **Step 6: Run the test**

Run: `dotnet test --filter "FullyQualifiedName~Editing_a_cloned_attachment"`
Expected: FAIL to compile — `DemoBuilderClient` does not implement the new member yet. That is Task 9; add a temporary throwing stub so the suite compiles:

```csharp
    public Task<IReadOnlyList<SubWorkflowDefinitionOption>> GetSubWorkflowDefinitionsAsync(
        CancellationToken ct = default) => throw new NotImplementedException();
```

Re-run. Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A src samples tests
git commit -m "Add the sub-workflow attachment edit model to the builder seam"
```

---

## Task 9: Round-trip attachments through save — the data-loss fix

Today, `ClearGraphAsync` deletes task definitions and the FK cascade takes attachments with them, while nothing reloads or rewrites them. Saving a draft silently destroys them.

**Files:**
- Modify: `samples/DemoDocuments.Server/Workflow/DemoBuilderClient.cs`
- Test: `tests/Workflow.Tests/BuilderClientTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `BuilderClientTests`:

```csharp
    [TestMethod]
    public async Task An_attachment_survives_a_draft_save()
    {
        var versionId = await DraftWithAttachmentAsync();

        var model = await _client.GetWorkflowAsync(versionId);
        Assert.AreEqual(1, model!.SubWorkflows.Count, "Attachment not loaded into the model.");

        var saved = await _client.SaveAsync(model);
        Assert.IsTrue(saved.Success, string.Join("; ", saved.Errors.Select(e => e.Message)));

        var reloaded = await _client.GetWorkflowAsync(saved.VersionId);

        Assert.AreEqual(1, reloaded!.SubWorkflows.Count,
            "Saving a draft destroyed its attachments.");
        Assert.IsNull(reloaded.SubWorkflows[0].TaskLocalId, "Version-wide scope was lost.");
    }

    [TestMethod]
    public async Task An_attachment_survives_a_new_draft_version()
    {
        var versionId = await DraftWithAttachmentAsync();

        var draftId = await _client.CreateDraftVersionAsync(versionId);
        var draft = await _client.GetWorkflowAsync(draftId);

        Assert.AreEqual(1, draft!.SubWorkflows.Count,
            "A new version did not carry its attachments forward.");
    }

    [TestMethod]
    public async Task A_task_scoped_attachment_keeps_pointing_at_its_task()
    {
        var versionId = await DraftWithAttachmentAsync();
        var model = await _client.GetWorkflowAsync(versionId);

        var targetLocalId = model!.Tasks[0].LocalId;
        model.SubWorkflows[0].TaskLocalId = targetLocalId;

        var saved = await _client.SaveAsync(model);
        var reloaded = await _client.GetWorkflowAsync(saved.VersionId);

        // Task definitions are recreated wholesale on save, so this only holds if the
        // local id was mapped back to the *new* row rather than the old database id.
        Assert.AreEqual(targetLocalId, reloaded!.SubWorkflows[0].TaskLocalId);
    }
```

Write `DraftWithAttachmentAsync` as a private helper in the same class. It must produce a **draft** version carrying one version-wide attachment. Follow whatever the file's existing tests do to obtain a draft — run `grep -n "CreateDraftVersionAsync\|_client" tests/Workflow.Tests/BuilderClientTests.cs` and reuse that setup rather than inventing a second one. The attachment is added directly:

```csharp
    private async Task<int> DraftWithAttachmentAsync()
    {
        var mainlineId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var publishedId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == mainlineId && v.IsPublished)
            .Select(v => v.Id).FirstAsync();

        var draftId = await _client.CreateDraftVersionAsync(publishedId);

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        _host.Db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            SubWorkflowDefinitionId = subWorkflowId,
            WorkflowDefinitionVersionId = draftId,
            TaskDefinitionId = null,
            IsBlocking = true,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        return draftId;
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~attachment_survives"`
Expected: FAIL — `SubWorkflows.Count` is 0, because nothing loads them.

- [ ] **Step 3: Load attachments**

In `DemoBuilderClient.LoadGraphAsync`, add to the query chain:

```csharp
            .Include(v => v.SubWorkflowAttachments)
```

- [ ] **Step 4: Map them into the model**

In `ToEditModel` (around line 558), immediately **after** the `foreach (var task in tasks)` loop closes and before `return model;`, add:

```csharp
        // `localIds` is the same database-id → local-id map the routes use, so an
        // attachment ends up pointing at exactly the task a route would.
        model.SubWorkflows =
        [
            .. version.SubWorkflowAttachments
                .Where(a => !a.IsArchived)
                .Select(a => new SubWorkflowAttachmentEditModel
                {
                    Id = a.Id,
                    SubWorkflowDefinitionId = a.SubWorkflowDefinitionId,
                    TaskLocalId = a.TaskDefinitionId is { } id
                                  && localIds.TryGetValue(id, out var local)
                        ? local
                        : null,
                    IsAutomatic = a.IsAutomatic,
                    IsBlocking = a.IsBlocking,
                    AllowMultiple = a.AllowMultiple
                })
        ];
```

`localIds` is already in scope — it is built at the top of `ToEditModel` as
`tasks.ToDictionary(t => t.Id, _ => Guid.NewGuid())`.

- [ ] **Step 5: Delete them explicitly on clear**

In `ClearGraphAsync`, before `db.WorkflowTaskDefinitions.RemoveRange(...)`, add:

```csharp
        // Explicit rather than by FK cascade: version-wide attachments name no task and
        // so would not cascade, and relying on the cascade is what silently destroyed
        // task-scoped ones on every save.
        db.WorkflowSubWorkflowAttachments.RemoveRange(
            await db.WorkflowSubWorkflowAttachments
                .Where(a => a.WorkflowDefinitionVersionId == version.Id)
                .ToListAsync(ct).ConfigureAwait(false));
```

- [ ] **Step 6: Write them back on persist**

In `PersistCoreAsync`, after the routes-and-triggers loop (every task definition now exists in `byLocalId`), add:

```csharp
        // After the task loop, so a task-scoped attachment resolves to the row created
        // in *this* save rather than the one just deleted.
        foreach (var attachment in model.SubWorkflows)
        {
            WorkflowTaskDefinition? target = null;

            if (attachment.TaskLocalId is { } localId
                && !byLocalId.TryGetValue(localId, out target))
            {
                continue;   // names a task that no longer exists; drop it
            }

            db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
            {
                SubWorkflowDefinitionId = attachment.SubWorkflowDefinitionId,
                WorkflowDefinitionVersionId = 0,
                DefinitionVersion = version,
                TaskDefinitionId = null,
                TaskDefinition = target,
                IsAutomatic = attachment.IsAutomatic,
                IsBlocking = attachment.IsBlocking,
                AllowMultiple = attachment.AllowMultiple,
                CreatorId = Actor,
                ModifierId = Actor,
                Created = now,
                Modified = now
            });
        }
```

Setting the navigation properties and leaving the ids at 0 is the same trick the routes use: EF fixes the foreign keys up on save, when the new rows finally have ids.

- [ ] **Step 7: Implement the picker query**

Replace the `NotImplementedException` stub from Task 8 with:

```csharp
    public async Task<IReadOnlyList<SubWorkflowDefinitionOption>> GetSubWorkflowDefinitionsAsync(
        CancellationToken ct = default) =>
        await db.WorkflowDefinitions
            .AsNoTracking()
            .Where(d => d.IsSubWorkflow && !d.IsArchived)
            .OrderBy(d => d.Name)
            .Select(d => new SubWorkflowDefinitionOption(
                d.Id,
                d.Name,
                db.WorkflowDefinitionVersions.Any(v =>
                    v.WorkflowDefinitionId == d.Id && v.IsPublished && !v.IsArchived)))
            .ToListAsync(ct).ConfigureAwait(false);
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test --filter "FullyQualifiedName~BuilderClientTests"`
Expected: PASS

- [ ] **Step 9: Run the full suite**

Run: `dotnet test`
Expected: all passing.

- [ ] **Step 10: Commit**

```bash
git add -A samples tests
git commit -m "Round-trip sub-workflow attachments through save, fixing silent loss"
```

---

## Task 10: The Sub-workflows panel in the builder

**Files:**
- Create: `src/Workflow.MudBlazor/SubWorkflowAttachDialog.razor`
- Modify: `src/Workflow.MudBlazor/WorkflowBuilder.razor`

- [ ] **Step 1: Create the dialog**

```razor
@using Workflow.Core.Builder

@*  Edits a clone and commits with CopyFrom. Blazor passes reference types by
    reference, so binding straight to Model would write into the caller's object as
    the user types and Cancel could not undo it. *@

<MudDialog>
    <DialogContent>
        <MudStack Spacing="3" Style="min-width:460px;">

            <MudSelect T="int" @bind-Value="_edit.SubWorkflowDefinitionId"
                       Label="Sub-workflow" Variant="Variant.Outlined">
                @foreach (var option in Available)
                {
                    <MudSelectItem T="int" Value="@option.DefinitionId">
                        @option.Name @(option.HasPublishedVersion ? "" : "(no published version)")
                    </MudSelectItem>
                }
            </MudSelect>

            @if (Selected is { HasPublishedVersion: false })
            {
                <MudAlert Severity="Severity.Warning" Dense="true">
                    This sub-workflow has no published version, so starting it will fail
                    until one is published.
                </MudAlert>
            }

            <MudSelect T="Guid?" @bind-Value="_edit.TaskLocalId"
                       Label="Available on" Variant="Variant.Outlined"
                       HelperText="Any task means tasks added later get it too.">
                <MudSelectItem T="Guid?" Value="@((Guid?)null)">Any task</MudSelectItem>
                @foreach (var task in Tasks)
                {
                    <MudSelectItem T="Guid?" Value="@((Guid?)task.LocalId)">@task.Label</MudSelectItem>
                }
            </MudSelect>

            <MudSwitch T="bool" @bind-Value="_edit.IsBlocking" Color="Color.Warning"
                       Label="Blocks the parent task" />
            <MudText Typo="Typo.caption" Class="mud-text-secondary">
                The parent cannot be completed until this is completed or cancelled.
            </MudText>

            <MudSwitch T="bool" @bind-Value="_edit.IsAutomatic" Color="Color.Info"
                       Label="Start automatically" />

            <MudSwitch T="bool" @bind-Value="_edit.AllowMultiple" Color="Color.Info"
                       Label="Allow several at once" />

            @if (_error is not null)
            {
                <MudAlert Severity="Severity.Error" Dense="true">@_error</MudAlert>
            }
        </MudStack>
    </DialogContent>
    <DialogActions>
        <MudButton OnClick="@(() => Dialog.Cancel())">Cancel</MudButton>
        <MudButton Color="Color.Primary" Variant="Variant.Filled" OnClick="Save">Save</MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] private IMudDialogInstance Dialog { get; set; } = default!;

    [Parameter] public SubWorkflowAttachmentEditModel Model { get; set; } = new();
    [Parameter] public IReadOnlyList<SubWorkflowDefinitionOption> Available { get; set; } = [];
    [Parameter] public IReadOnlyList<TaskEditModel> Tasks { get; set; } = [];

    private SubWorkflowAttachmentEditModel _edit = new();
    private string? _error;

    private SubWorkflowDefinitionOption? Selected =>
        Available.FirstOrDefault(o => o.DefinitionId == _edit.SubWorkflowDefinitionId);

    protected override void OnInitialized()
    {
        _edit = Model.Clone();

        if (_edit.SubWorkflowDefinitionId == 0)
        {
            _edit.SubWorkflowDefinitionId = Available.FirstOrDefault()?.DefinitionId ?? 0;
        }
    }

    private void Save()
    {
        if (_edit.SubWorkflowDefinitionId == 0)
        {
            _error = "Choose a sub-workflow.";
            return;
        }

        // The engine cannot interpret automatic + any task: it would start an instance
        // on every task in the run. Refused here so it is not a validation error later.
        if (_edit.IsAutomatic && _edit.TaskLocalId is null)
        {
            _error = "An automatic sub-workflow must be attached to a specific task.";
            return;
        }

        Model.CopyFrom(_edit);
        Dialog.Close(DialogResult.Ok(Model));
    }
}
```

- [ ] **Step 2: Add the panel to the builder**

In `WorkflowBuilder.razor`, after the closing `</MudPaper>` of the Tasks panel and before the validation/save area, add:

```razor
        <MudPaper Class="pa-4" Elevation="1">
            <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2" Class="mb-2">
                <MudIcon Icon="@Icons.Material.Filled.AccountTree" Size="Size.Small" />
                <MudText Typo="Typo.subtitle2">Sub-workflows</MudText>
                <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined">this version</MudChip>
                <MudSpacer />
                <MudButton Size="Size.Small" StartIcon="@Icons.Material.Filled.Add"
                           OnClick="AddAttachment">Attach</MudButton>
            </MudStack>

            @if (_model.SubWorkflows.Count == 0)
            {
                <MudText Typo="Typo.body2" Class="mud-text-secondary">
                    Nothing attached. A sub-workflow hands a short chain of work to
                    somebody else and, if it blocks, holds the parent task until it is done.
                </MudText>
            }
            else
            {
                <MudTable Items="_model.SubWorkflows" Dense="true" Elevation="0" Hover="true">
                    <HeaderContent>
                        <MudTh>Sub-workflow</MudTh>
                        <MudTh>Scope</MudTh>
                        <MudTh>Spawn</MudTh>
                        <MudTh>Blocking</MudTh>
                        <MudTh />
                    </HeaderContent>
                    <RowTemplate>
                        <MudTd>@SubWorkflowName(context.SubWorkflowDefinitionId)</MudTd>
                        <MudTd>@ScopeLabel(context)</MudTd>
                        <MudTd>@(context.IsAutomatic ? "automatic" : "on request")</MudTd>
                        <MudTd>@(context.IsBlocking ? "yes" : "no")</MudTd>
                        <MudTd>
                            <MudIconButton Icon="@Icons.Material.Filled.Edit" Size="Size.Small"
                                           OnClick="@(() => EditAttachment(context))" />
                            <MudIconButton Icon="@Icons.Material.Filled.Delete" Size="Size.Small"
                                           OnClick="@(() => _model.SubWorkflows.Remove(context))" />
                        </MudTd>
                    </RowTemplate>
                </MudTable>
            }
        </MudPaper>
```

- [ ] **Step 3: Add the supporting code**

In the `@code` block of `WorkflowBuilder.razor`:

```csharp
    private IReadOnlyList<SubWorkflowDefinitionOption> _subWorkflows = [];

    private string SubWorkflowName(int definitionId) =>
        _subWorkflows.FirstOrDefault(s => s.DefinitionId == definitionId)?.Name
        ?? $"#{definitionId}";

    private string ScopeLabel(SubWorkflowAttachmentEditModel attachment) =>
        attachment.TaskLocalId is { } localId
            ? _model?.Find(localId)?.Label ?? "(missing task)"
            : "any task";

    private Task AddAttachment() =>
        ShowAttachmentDialog(new SubWorkflowAttachmentEditModel(), isNew: true);

    private Task EditAttachment(SubWorkflowAttachmentEditModel attachment) =>
        ShowAttachmentDialog(attachment, isNew: false);

    private async Task ShowAttachmentDialog(
        SubWorkflowAttachmentEditModel attachment, bool isNew)
    {
        var parameters = new DialogParameters<SubWorkflowAttachDialog>
        {
            { x => x.Model, attachment },
            { x => x.Available, _subWorkflows },
            { x => x.Tasks, _model!.Tasks.Where(t => !t.IsAdHoc).ToList() }
        };

        var dialog = await Dialogs.ShowAsync<SubWorkflowAttachDialog>(
            isNew ? "Attach a sub-workflow" : "Edit attachment", parameters);

        var result = await dialog.Result;

        if (result is { Canceled: false } && isNew)
        {
            _model.SubWorkflows.Add(attachment);
        }

        StateHasChanged();
    }
```

Load the options wherever the builder loads its other lookups — find that with
`grep -n "GetTriggerDescriptorsAsync\|GetTaskTypesAsync" src/Workflow.MudBlazor/WorkflowBuilder.razor` and add alongside:

```csharp
        _subWorkflows = await Client.GetSubWorkflowDefinitionsAsync();
```

- [ ] **Step 3b: Collapse the doubled duplicate error**

A duplicate attachment is reported twice by `ValidateAsync` — `ATTACHMENT_DUPLICATE` from
`StructuralErrors` and `attachment.duplicate` from the validator. Both are deliberate: the
structural one makes `SaveAsync` fail cleanly instead of throwing a raw `DbUpdateException`, and
the validator one covers the persisted path that `PublishAsync` re-validates. Neither should be
removed to tidy the other.

Wherever the builder renders validation errors, de-duplicate for display only — by message
text, not by code, since the two codes differ by design:

```csharp
    private IEnumerable<ValidationError> ForDisplay(IEnumerable<ValidationError> errors) =>
        errors.DistinctBy(e => e.Message);
```

Showing the same sentence twice reads as a bug in the builder rather than two layers agreeing.

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: succeeds. On CS0246 for `IMudDialogInstance` or `DialogParameters`, check `_Imports.razor` still says `@using global::MudBlazor` — see the namespace hazard at the top of this plan.

- [ ] **Step 5: Run the app and check the panel renders**

```bash
export ConnectionStrings__Demo="Server=localhost,1433;Database=WorkflowDemo;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;Encrypt=False"
cd samples/DemoDocuments.Server && setsid nohup dotnet run --urls http://localhost:5199 > /tmp/demo.log 2>&1 &
```

Open `http://localhost:5199/workflows/1`. Expected: a **Sub-workflows** panel listing "Technical Review" scoped to a named task. Attach a second one scoped to *any task*, save, reload the page, and confirm both are still there — that is the data-loss fix visible end to end.

Stop the server by pid (**not** `pkill -f DemoDocuments.Server`, which also kills the shell that started it):

```bash
pgrep -f DemoDocuments.Server | head -1 | xargs kill
```

- [ ] **Step 6: Commit**

```bash
git add -A src
git commit -m "Add the sub-workflows panel and attachment dialog to the builder"
```

---

## Task 11: Stop the runner offering Complete on a blocked task

**Files:**
- Modify: `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs:161`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs:91-97, 140`
- Modify: `src/Workflow.MudBlazor/WorkflowRunner.razor:189`
- Test: `tests/Workflow.Tests/RunnerClientTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `RunnerClientTests`:

```csharp
    [TestMethod]
    public async Task A_blocked_task_reports_its_blocking_instances()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator")).Unwrap();

        var detail = await _client.GetRunAsync(_runId);
        var parent = detail!.Tasks.Single(t => t.Id == parentTaskId);

        Assert.AreEqual(1, parent.BlockingSubWorkflowCount);
        Assert.AreEqual(1, parent.RunningSubWorkflowCount);
    }

    [TestMethod]
    public async Task A_non_blocking_instance_is_running_but_not_blocking()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        await _host.Db.WorkflowSubWorkflowAttachments
            .Where(a => a.SubWorkflowDefinitionId == subWorkflowId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsBlocking, false));

        _host.Db.ChangeTracker.Clear();

        var parentTaskId = await ParentTaskIdAsync();

        (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator")).Unwrap();

        var detail = await _client.GetRunAsync(_runId);
        var parent = detail!.Tasks.Single(t => t.Id == parentTaskId);

        Assert.AreEqual(0, parent.BlockingSubWorkflowCount);
        Assert.AreEqual(1, parent.RunningSubWorkflowCount);
    }
```

Add this self-contained fixture to `RunnerClientTests` — it does not reuse `_runId`, which
is set only by the fork helpers and would couple these tests to those:

```csharp
    private int _delegationRunId;

    /// <summary>Starts a run and drives it to the task the sub-workflow attaches to.</summary>
    private async Task<int> ParentTaskIdAsync()
    {
        var definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "delegation"), definitionId, "user-originator")).Unwrap();

        _delegationRunId = run.Id;

        (await _host.Engine.CompleteTaskAsync(
            run.Tasks.Single().Id, "approved", "user-originator")).Unwrap();

        return await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == run.Id && t.Status == WorkflowTaskStatus.NotStarted)
            .Select(t => t.Id)
            .SingleAsync();
    }
```

The two tests above call `GetRunAsync(_runId)` — change both to `GetRunAsync(_delegationRunId)`,
and call `ParentTaskIdAsync()` **before** reading it, since that is what assigns it.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~blocking_instances"`
Expected: FAIL to compile — `BlockingSubWorkflowCount` does not exist.

- [ ] **Step 3: Add the field to the view**

In `IWorkflowRunnerClient.cs`, in `RunTaskView`, replace the `RunningSubWorkflowCount` line with:

```csharp
    /// <summary>Sub-workflow instances running against this task.</summary>
    int RunningSubWorkflowCount,

    /// <summary>
    /// Of those, the ones that stop it completing. A separate count because the runner
    /// must disable Complete for exactly these, and `CanComplete` cannot carry it:
    /// `CanComplete` is host policy about *who may act*, while blocking is an engine
    /// rule about whether the action is legal at all. Folding one into the other would
    /// oblige every host to reimplement the rule, and a host that forgot would get the
    /// engine's exception back.
    /// </summary>
    int BlockingSubWorkflowCount,
```

- [ ] **Step 4: Populate it**

In `DemoRunnerClient.cs`, replace the `runningSubWorkflows` query with:

```csharp
        var subWorkflowCounts = await db.WorkflowSubWorkflowInstances
            .AsNoTracking()
            .Where(i => i.WorkflowRunId == runId && i.Status == SubWorkflowStatus.Running)
            .GroupBy(i => i.ParentTaskId)
            .Select(g => new
            {
                ParentTaskId = g.Key,
                Running = g.Count(),
                Blocking = g.Count(i => i.IsBlocking)
            })
            .ToDictionaryAsync(g => g.ParentTaskId, g => g, ct)
            .ConfigureAwait(false);
```

and the two view fields with:

```csharp
                    RunningSubWorkflowCount: subWorkflowCounts.GetValueOrDefault(t.Id)?.Running ?? 0,
                    BlockingSubWorkflowCount: subWorkflowCounts.GetValueOrDefault(t.Id)?.Blocking ?? 0,
```

- [ ] **Step 5: Disable Complete in the runner**

In `WorkflowRunner.razor`, change the `@if (task.CanComplete)` guard at line 189 to also require nothing blocking, and explain when it does:

```razor
                    @if (task.CanComplete && task.BlockingSubWorkflowCount == 0)
                    {
```

and immediately after that block's closing brace add:

```razor
                    @if (task.CanComplete && task.BlockingSubWorkflowCount > 0)
                    {
                        @* An engine rule surfaced before the action, not as an error
                           after it — the same standard the fork and rejection dialogs
                           already hold to. *@
                        <MudAlert Severity="Severity.Info" Dense="true">
                            Waiting on @task.BlockingSubWorkflowCount blocking
                            sub-workflow@(task.BlockingSubWorkflowCount == 1 ? "" : "s").
                            Complete or cancel @(task.BlockingSubWorkflowCount == 1 ? "it" : "them")
                            to finish this task.
                        </MudAlert>
                    }
```

- [ ] **Step 5b: Make the demo notice version-wide attachments**

`DemoRunnerClient` pre-computes which task definitions have a sub-workflow attachable, and
since Task 4 made `TaskDefinitionId` nullable that set is wrong in two ways: a version-wide
row contributes `null`, which never equals a task's id, so the UI silently never offers it;
and there is no version filter at all, so an attachment on *any* version marks a task
attachable.

Replace the `attachable` query:

```csharp
        var attachments = await db.WorkflowSubWorkflowAttachments
            .AsNoTracking()
            .Where(a => !a.IsArchived)
            .Select(a => new { a.WorkflowDefinitionVersionId, a.TaskDefinitionId })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Which version each task definition in this run belongs to. Needed because an
        // attachment is scoped by version, and a run's tasks can span two of them once
        // sub-workflow instances are involved.
        var versionByTaskDefinition = await db.WorkflowTaskDefinitions
            .AsNoTracking()
            .Where(d => run.Tasks.Select(t => t.TaskDefinitionId).Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.WorkflowDefinitionVersionId, ct)
            .ConfigureAwait(false);
```

and the `HasSubWorkflowOptions` argument:

```csharp
                    HasSubWorkflowOptions: attachments.Any(a =>
                        versionByTaskDefinition.TryGetValue(t.TaskDefinitionId, out var v)
                        && a.WorkflowDefinitionVersionId == v
                        && (a.TaskDefinitionId is null
                         || a.TaskDefinitionId == t.TaskDefinitionId)),
```

This mirrors the precedence-free half of the engine's `GetSubWorkflowOptionsAsync` — it only
answers "is there anything at all here", so it needs the same *matching* but not the
task-specific-wins ordering.

Add a test to `RunnerClientTests`:

```csharp
    [TestMethod]
    public async Task A_version_wide_attachment_is_offered_in_the_runner()
    {
        var parentTaskId = await ParentTaskIdAsync();

        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        // A task the sub-workflow is not attached to by name.
        var otherTask = await _host.Db.WorkflowTasks
            .Where(t => t.WorkflowRunId == _delegationRunId && t.Id != parentTaskId)
            .Select(t => new { t.Id, t.TaskDefinitionId })
            .FirstAsync();

        var versionId = await _host.Db.WorkflowTaskDefinitions
            .Where(d => d.Id == otherTask.TaskDefinitionId)
            .Select(d => d.WorkflowDefinitionVersionId)
            .SingleAsync();

        _host.Db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
        {
            SubWorkflowDefinitionId = subWorkflowId,
            WorkflowDefinitionVersionId = versionId,
            TaskDefinitionId = null,
            IsBlocking = true,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        var detail = await _client.GetRunAsync(_delegationRunId);

        Assert.IsTrue(detail!.Tasks.Single(t => t.Id == otherTask.Id).HasSubWorkflowOptions,
            "A version-wide attachment must be offered on a task it does not name.");
    }
```

- [ ] **Step 6: Expose the instances so they can be cancelled**

The runner shows only a *count* of running instances, so there is nothing to hang a cancel
control on. Add the list and the cancel call to the seam.

In `IWorkflowRunnerClient.cs`:

```csharp
    /// <summary>Sub-workflow instances in a run, so the runner can show and cancel them.</summary>
    Task<IReadOnlyList<SubWorkflowInstanceView>> GetSubWorkflowInstancesAsync(
        int runId, CancellationToken ct = default);

    /// <summary>Abandons a running instance, leaving its parent task alone.</summary>
    Task<RunnerResult> CancelSubWorkflowAsync(
        int instanceId, string actorId, string? reason, CancellationToken ct = default);
```

and beside the other view records:

```csharp
public sealed record SubWorkflowInstanceView(
    int Id, int ParentTaskId, string Name, bool IsBlocking, bool IsRunning);
```

In `DemoRunnerClient`, implement both by delegating to the engine, which already has
`GetSubWorkflowInstancesAsync(runId)` returning `SubWorkflowInstanceSnapshot` and — as of
Task 2 — `CancelSubWorkflowAsync`:

```csharp
    public async Task<IReadOnlyList<SubWorkflowInstanceView>> GetSubWorkflowInstancesAsync(
        int runId, CancellationToken ct = default)
    {
        var result = await engine.GetSubWorkflowInstancesAsync(runId, ct).ConfigureAwait(false);

        return result.IsError
            ? []
            : [.. result.Unwrap().Select(i => new SubWorkflowInstanceView(
                i.Id, i.ParentTaskId, i.Name, i.IsBlocking,
                i.Status == SubWorkflowStatus.Running))];
    }

    public async Task<RunnerResult> CancelSubWorkflowAsync(
        int instanceId, string actorId, string? reason, CancellationToken ct = default)
    {
        var result = await engine
            .CancelSubWorkflowAsync(instanceId, actorId, reason, ct).ConfigureAwait(false);

        return result.IsOk ? RunnerResult.Ok : RunnerResult.Failed(result.UnwrapError().Message);
    }
```

- [ ] **Step 7: Show running instances with a cancel control**

In `WorkflowRunner.razor`, load the instances wherever the run detail is loaded, into a field:

```csharp
    private IReadOnlyList<SubWorkflowInstanceView> _instances = [];
```

Then replace the blocking alert added in Step 5 with one that lists what is being waited on
and offers to abandon each:

```razor
                    @if (task.CanComplete && task.BlockingSubWorkflowCount > 0)
                    {
                        @* An engine rule surfaced before the action, not as an error after
                           it — the same standard the fork and rejection dialogs hold to. *@
                        <MudAlert Severity="Severity.Info" Dense="true">
                            <MudStack Spacing="1">
                                <div>Waiting on:</div>
                                @foreach (var instance in _instances.Where(i =>
                                    i.ParentTaskId == task.Id && i.IsRunning && i.IsBlocking))
                                {
                                    <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                                        <span>@instance.Name</span>
                                        <MudButton Size="Size.Small" Color="Color.Error"
                                                   Variant="Variant.Text"
                                                   OnClick="@(() => CancelInstance(instance))">
                                            Cancel it
                                        </MudButton>
                                    </MudStack>
                                }
                            </MudStack>
                        </MudAlert>
                    }
```

and the handler, following the file's existing `PerformAsync` idiom:

```csharp
    private Task CancelInstance(SubWorkflowInstanceView instance) =>
        PerformAsync(() => Client.CancelSubWorkflowAsync(
            instance.Id, ActorId, "Cancelled from the runner."));
```

Use whatever this file already calls the acting actor id — check a neighbouring
`PerformAsync` call and match it rather than assuming `ActorId`.

- [ ] **Step 8: Verify the loop closes**

Add to `RunnerClientTests`:

```csharp
    [TestMethod]
    public async Task Cancelling_a_blocking_instance_unblocks_the_task()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var instance = (await _host.Engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator")).Unwrap();

        var cancelled = await _client.CancelSubWorkflowAsync(
            instance.Id, "user-originator", "not needed");

        Assert.IsTrue(cancelled.Success, cancelled.Error);

        var detail = await _client.GetRunAsync(_delegationRunId);
        var parent = detail!.Tasks.Single(t => t.Id == parentTaskId);

        Assert.AreEqual(0, parent.BlockingSubWorkflowCount);
    }
```

- [ ] **Step 9: Run the tests**

Run: `dotnet test`
Expected: all passing.

- [ ] **Step 10: Commit**

```bash
git add -A src samples tests
git commit -m "Show what a blocked task is waiting on, and let it be cancelled"
```

---

## Task 12: Pick a person when delegating

**Files:**
- Create: `samples/DemoDocuments.Server/Workflow/DemoOrg.cs`
- Modify: `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs:57`
- Modify: `src/Workflow.MudBlazor/RunnerSubWorkflowDialog.razor`
- Modify: `src/Workflow.MudBlazor/WorkflowRunner.razor:524-527, 707`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs`
- Test: `tests/Workflow.Tests/RunnerClientTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `RunnerClientTests`:

```csharp
    [TestMethod]
    public async Task Delegating_through_the_client_fills_in_the_persons_section()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var result = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator", "user-worker-c200", "please review");

        Assert.IsTrue(result.Success, result.Error);

        var entry = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId != null)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        // The caller named a person; the host supplied the section. Setting the actor
        // without the section is what misroutes the Section Lead lookup downstream.
        Assert.AreEqual("user-worker-c200", entry.AssignedToActorId);
        Assert.AreEqual("C200", entry.AssignedBranchKey);
    }

    [TestMethod]
    public async Task Delegating_to_somebody_with_no_section_still_works()
    {
        var subWorkflowId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).SingleAsync();

        var parentTaskId = await ParentTaskIdAsync();

        var result = await _client.StartSubWorkflowAsync(
            parentTaskId, subWorkflowId, "user-originator", "user-unassigned", null);

        Assert.IsTrue(result.Success, result.Error);

        var entry = await _host.Db.WorkflowTasks
            .Where(t => t.SubWorkflowInstanceId != null)
            .OrderByDescending(t => t.Id)
            .FirstAsync();

        Assert.AreEqual("user-unassigned", entry.AssignedToActorId);
        Assert.IsNull(entry.AssignedBranchKey);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Delegating_t"`
Expected: FAIL to compile — the client method takes no assignee.

- [ ] **Step 3: Create the host helper**

`samples/DemoDocuments.Server/Workflow/DemoOrg.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using Workflow.Core.Abstractions;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Turns a person into a complete assignment.
///
/// One helper rather than a lookup at each call site, because
/// <c>AssignedToActorId</c> and <c>AssignedBranchKey</c> have to move together. They do
/// everywhere else, since the engine inherits them as a pair; delegation and ad-hoc
/// tasks are the only operations that set an actor directly, and an actor set without
/// their section is what makes a downstream Section Lead lookup resolve against the wrong one.
/// </summary>
public sealed class DemoOrg(DemoDbContext db)
{
    public async Task<WorkflowAssignment> AssignmentForAsync(
        string? actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return WorkflowAssignment.Unassigned;
        }

        var sectionCode = await db.People
            .AsNoTracking()
            .Where(p => p.ActorId == actorId)
            .Select(p => p.SectionId == null
                ? null
                : db.Sections.Where(s => s.Id == p.SectionId).Select(s => s.Code).FirstOrDefault())
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // A null section is a real state, not an error: the assignment resolver already
        // treats a missing branch key as "keep the current assignment" rather than
        // failing, and inventing a second failure style here would be worse.
        return new WorkflowAssignment(actorId, sectionCode);
    }
}
```

Register it in `samples/DemoDocuments.Server/Program.cs` alongside the other host services:

```csharp
builder.Services.AddScoped<DemoOrg>();
```

- [ ] **Step 4: Widen the runner client seam**

In `IWorkflowRunnerClient.cs`:

```csharp
    /// <summary>
    /// Starts a sub-workflow, delegated to <paramref name="assignToActorId"/>. The host
    /// resolves that person's org unit — the component neither knows nor should know
    /// what an org unit is.
    /// </summary>
    Task<RunnerResult> StartSubWorkflowAsync(
        int parentTaskId, int subWorkflowDefinitionId, string actorId,
        string? assignToActorId, string? notes,
        CancellationToken ct = default);
```

- [ ] **Step 5: Implement it**

In `DemoRunnerClient`, add `DemoOrg org` to the primary constructor parameter list and change the method to:

```csharp
    public async Task<RunnerResult> StartSubWorkflowAsync(
        int parentTaskId, int subWorkflowDefinitionId, string actorId,
        string? assignToActorId, string? notes, CancellationToken ct = default)
    {
        var assignment = assignToActorId is null
            ? null
            : await org.AssignmentForAsync(assignToActorId, ct).ConfigureAwait(false);

        var result = await engine.StartSubWorkflowAsync(
            parentTaskId, subWorkflowDefinitionId, actorId, assignment, notes, ct)
            .ConfigureAwait(false);

        return ToRunnerResult(result);
    }
```

Use whatever this file already calls its `Result` → `RunnerResult` conversion; find it with
`grep -n "RunnerResult" samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs` and match the existing pattern rather than adding a second one.

**Leave `AddAdHocAsync` alone.** Its signature has no assignee, and it calls
`AddAdHocTaskAsync(..., notes: notes, ct: ct)` without an assignment — so the engine
inherits the parent's pair and nothing desyncs today. The hazard there is *latent*: the
moment somebody adds an assignee parameter, they must route it through
`DemoOrg.AssignmentForAsync` rather than passing a bare actor id. Task 13 records that in
`STATE.md`. Widening the ad-hoc dialog now would be scope this feature did not ask for.

- [ ] **Step 6: Update every place that constructs the client**

`DemoRunnerClient` is constructed by hand in **two** test files, not one. Both need the
new argument:

```bash
grep -rn "new DemoRunnerClient" tests/
```

In `tests/Workflow.Tests/RunnerClientTests.cs` (`Setup`), change:

```csharp
        _client = new DemoRunnerClient(_host.Db, _host.Engine, new DemoActorResolver(_host.Db));
```

to:

```csharp
        _client = new DemoRunnerClient(
            _host.Db, _host.Engine, new DemoActorResolver(_host.Db), new DemoOrg(_host.Db));
```

Apply the same change to any hit in `TestHost.cs`, using whatever context variable that file
already passes to `DemoAssignmentResolver` (it uses `plainDb`, not `_host.Db`).

- [ ] **Step 7: Add the person picker to the dialog**

In `RunnerSubWorkflowDialog.razor`, add a parameter and a select. After the sub-workflow `MudSelect`, insert:

```razor
            <MudSelect T="string" @bind-Value="_assignToActorId" Label="Assign to"
                       Variant="Variant.Outlined" Clearable="true"
                       HelperText="Reviews further down the chain go to this person's Section Lead.">
                @foreach (var actor in Actors)
                {
                    <MudSelectItem T="string" Value="@actor.ActorId">@actor.DisplayName</MudSelectItem>
                }
            </MudSelect>
```

and in `@code`:

```csharp
    [Parameter] public IReadOnlyList<ActorOption> Actors { get; set; } = [];

    private string? _assignToActorId;
```

Change the close call to carry it:

```csharp
        Dialog.Close(DialogResult.Ok(
            new WorkflowRunner.SubWorkflowRequest(_definitionId, _assignToActorId, _notes)));
```

- [ ] **Step 8: Update the runner's wiring**

In `WorkflowRunner.razor`, change the record at line 707 to:

```csharp
    public sealed record SubWorkflowRequest(
        int SubWorkflowDefinitionId, string? AssignToActorId, string? Notes);
```

pass the actors when showing the dialog (mirroring how the reassign dialog at line 580 does it):

```csharp
            { x => x.Actors, await Client.GetActorsAsync() },
```

and change the call at line 526 to:

```csharp
            await PerformAsync(() => Client.StartSubWorkflowAsync(
                task.Id, request.SubWorkflowDefinitionId, ActorId,
                request.AssignToActorId, request.Notes));
```

Use whatever the surrounding code already calls the acting actor id — check the neighbouring `PerformAsync` calls and match.

- [ ] **Step 9: Run the tests**

Run: `dotnet test`
Expected: all passing.

- [ ] **Step 10: Commit**

```bash
git add -A src samples tests
git commit -m "Delegate a sub-workflow to a named person, with the host supplying their section"
```

---

## Task 13: Make the demo show the real chain

**Files:**
- Modify: `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs:362`
- Modify: `STATE.md`

- [x] **Step 1: Write the failing test**

Append to `SubWorkflowTests`:

```csharp
    [TestMethod]
    public async Task The_seeded_sub_workflow_starts_with_a_get_info_step()
    {
        var version = await _host.Db.WorkflowDefinitionVersions
            .Include(v => v.Tasks).ThenInclude(t => t.TaskType)
            .SingleAsync(v => v.WorkflowDefinitionId == _subWorkflowId && v.IsPublished);

        var entry = version.Tasks.Single(t => t.Id == version.EntryTaskDefinitionId);

        Assert.AreEqual("get-info", entry.TaskType!.Key);

        // No role key: it goes to whoever the chain was delegated to, and the two
        // reviews above resolve from them.
        Assert.IsNull(entry.AssignmentRoleKey);
    }
```

- [x] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~get_info_step"`
Expected: FAIL — the entry task is `section-review`.

- [x] **Step 3: Add the step**

> Plan gap found in execution: there was no `get-info` task type. Added
> `("get-info", "Get Info")` to the seeder's task-type list — `types["get-info"]`
> would otherwise have thrown.

In `DemoWorkflowSeeder.cs`, in the sub-workflow seeding block, add a task before `section-lead` and route it in. Replace the `var sectionLead = Task(...)` line with:

```csharp
        // No assignment role: the entry task goes to whoever the chain was delegated to,
        // and the two reviews resolve from that person's section.
        // role: null — the entry task goes to whoever the chain was delegated to.
        // Note this local Task() helper requires the role positionally; it is not the
        // same helper the mainline seeding uses.
        var getInfo = Task("get-info", "Technical Review — Get Info", null);
        var sectionLead = Task("section-review", "Technical Review — Section Lead", DemoRoles.SectionLead);
```

Add its outcome to the outcomes array — insert into the existing `new[] { ... }` collection, before the `section-lead` entries:

```csharp
            (getInfo, Outcomes.Approved, "Provided", 1),
```

Add a route from `getInfo` to `section-lead` alongside the existing route construction (match the surrounding `new TaskRoute { ... }` shape exactly — find it with `grep -n "new TaskRoute" samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs`):

```csharp
            new TaskRoute
            {
                TaskDefinitionId = 0,
                TaskDefinition = getInfo,
                OutcomeKey = Outcomes.Approved,
                NextTaskDefinitionId = 0,
                NextTaskDefinition = sectionLead,
                IsDefault = true,
                Order = 1,
                CreatorId = actor,
                ModifierId = actor,
                Created = now,
                Modified = now
            },
```

Change the entry task from `section-lead` to `getInfo`:

```csharp
        version.EntryTaskDefinitionId = getInfo.Id;
```

- [x] **Step 3b: Re-point the Task 6 delegation tests, which this step changes**

Task 6 could only assert half the mechanism, because the seeded chain's *entry* task was the
Section Lead review and carried a role key — so it resolved rather than inheriting. Adding a role-less
`get-info` in front makes the other half observable, and moves what each test should look at.

In `tests/Workflow.Tests/SubWorkflowTests.cs`:

- `The_entry_task_is_resolved_against_the_delegatees_section` — the entry is now `get-info`
  with no role key, so the delegated person survives **verbatim**. Assert
  `entry.AssignedToActorId == "user-worker-c200"` and `entry.AssignedBranchKey == "C200"`,
  and rename it to `The_entry_task_goes_to_the_delegatee_when_it_declares_no_role`.
- `The_review_resolves_to_the_delegatees_Section Lead_not_the_delegators` — keep the name, it is still
  exactly what this tests. Move the Section Lead assertion off the entry task and onto the task that
  follows it, since `section-review` is no longer the entry. Complete `get-info` first.

Both must still fail without the `requested ??` in `SpawnAsync` — re-run that check after
re-pointing them, exactly as Task 6 did.

- [x] **Step 4: Fix the tests the new step breaks**

`A_sub_workflow_starts_as_tasks_inside_the_parent_run` asserts the entry task is
`"Technical Review — Section Lead"`. It is now `"Technical Review — Get Info"`. Update that assertion. The delegation tests from Task 6 already expect `get-info` first and will start passing for the right reason.

- [x] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all passing.

- [x] **Step 6: Verify the whole feature in the app** — *partly. No browser automation
  is available on this box, so the five expectations below were verified through
  `DemoRunnerClient` instead (`RunnerClientTests
  .Delegating_through_the_runner_client_moves_the_whole_chain_to_the_delegatees_section`),
  which is the layer the dialog calls. The demo was run on a fresh database and its
  pages served 200 with the three-step chain seeded, but nobody clicked through it.*

Start the demo as in Task 10, open a document, start the sub-workflow on a task, and delegate it to **Drew Novak (Mechanical)**. Expected:

1. Get Info is assigned to Drew Novak
2. The parent task shows "Waiting on 1 blocking sub-workflow" and has no Complete button
3. Completing Get Info sends Section Lead review to **Sam Section Lead (Mechanical)** — not to the delegator's Section Lead
4. Completing that sends Branch review to Robin Branch Head
5. The parent's Complete button returns once the chain finishes

Stop the server by pid, not `pkill -f`.

- [x] **Step 7: Update STATE.md**

Under "Suggested next step", remove the sub-workflow-attachments bullet — it is done. Add to "Things to be careful about":

```markdown
- **An assignment's actor and branch key must move together.** The engine inherits them
  as a pair, so they only desync where a host sets an actor directly — delegating a
  sub-workflow, and ad-hoc tasks. `DemoOrg.AssignmentForAsync` is the single place that
  maps a person to both; setting one without the other makes a downstream role key
  resolve against the wrong org unit, silently and plausibly.
- **The runner offers a start icon for a sub-workflow that cannot start.**
  `HasSubWorkflowOptions` accounts for scope but not `AllowMultiple`, so a task with a
  running non-multiple attachment still shows the icon; the dialog then says
  "Everything attachable here is already running." A dead-end click that explains
  itself, rather than the exception the blocking case used to produce — but the same
  class of problem, and the runner otherwise holds to surfacing engine rules before the
  action rather than after.
- **Attachments are version-scoped and must round-trip through `WorkflowEditModel`.**
  They are rewritten wholesale on every save, exactly like routes and triggers. Before
  2026-08-19 they were neither loaded nor rewritten, and the FK cascade from
  `ClearGraphAsync` deleted them on every draft save.
```

- [x] **Step 8: Commit**

```bash
git add -A samples tests STATE.md
git commit -m "Seed the get-info step and record the delegation rules in STATE.md"
```

---

## Done when

- [x] `dotnet build` clean, `dotnet test` green — **148 tests**
- [x] `MigrationTests` passes, proving the model matches the migrations
- [x] An attachment survives a draft save and a new version
- [x] Delegating to a person in Section C200 sends the Section Lead review to `user-section-lead-c200`
- [x] A task held by a blocking sub-workflow shows no Complete button
- [x] `STATE.md` records both new standing hazards
