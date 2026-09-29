# Sub-workflow delegation, and attachments in the builder

**Date:** 2026-08-19
**Status:** Approved, not yet implemented

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.

## What this is for

A person working a mainline task needs to hand a short chain of work to somebody in
another section and, usually, wait for it. The chain is typically

```
get info  →  section-lead review  →  branch review
```

where *get info* goes to a named individual, and the two reviews go to that
individual's Section Lead and then that section's branch head — looked up, not chosen. Every
step is assigned to an individual; nothing is assigned to a section.

Almost all of this exists. What follows is the gap between it and the intent.

## What already works

- **The chain.** A sub-workflow is an ordinary `WorkflowDefinition` with
  `IsSubWorkflow` set, so a three-step chain is three task definitions and two routes,
  built in the same builder and checked by the same validator. The demo already seeds
  `section-review → division-review` (`DemoWorkflowSeeder.cs:362`).
- **The org lookup.** `DemoAssignmentResolver` maps `DemoRoles.SectionLead` to
  `section.SectionLeadActorId` and `DemoRoles.DivisionHead` to
  `section.Division.DivisionHeadActorId`. The demo's org model already carries
  `Person.SectionId`, `Section.SectionLeadActorId`, `Section.BranchId` and
  `Division.DivisionHeadActorId`.
- **Propagation.** Routing inherits assignment from the previous task
  (`WorkflowEngine.cs:264`), so getting the *entry* task right is enough — the rest of
  the chain follows without new code.

## The three gaps

### 1. The delegatee never reaches the engine

`StartSubWorkflowAsync(parentTaskId, subWorkflowDefinitionId, actorId, notes)` takes no
assignment, unlike `AddAdHocTaskAsync`, which has had `WorkflowAssignment? assignment =
null` all along. So the entry task inherits the **parent's** actor and branch key.

`DemoAssignmentResolver` then resolves the Section Lead from `current.BranchKey` — the section
the task already sits in — and bails out entirely when it is blank:

```csharp
var branchKey = current.BranchKey;
if (string.IsNullOrWhiteSpace(branchKey)) return current;   // HostAdapters.cs:39
```

The result is that `section-review` resolves to **the delegator's Section Lead, not the
delegatee's**. It fails silently and plausibly.

The rule underneath: **`AssignedToActorId` and `AssignedBranchKey` must move together.**
They do today everywhere, because the engine inherits them as a pair. Delegation is the
first operation that would move the actor without moving the section, and that desync is
the misrouting.

### 2. An attachment can only name one task

`SubWorkflowAttachment` is one row per (mainline task definition, sub-workflow
definition). "Available at any point in the workflow" therefore means a row per task,
and a task added later silently lacks it.

### 3. Saving a draft destroys attachments

`SubWorkflowAttachment.TaskDefinitionId` has `OnDelete(DeleteBehavior.Cascade)`
(`WorkflowModelBuilder.cs:116`), and `ClearGraphAsync` deletes every task definition on
a draft re-save (`DemoBuilderClient.cs:531`). `LoadGraphAsync` never reads attachments
and `WorkflowEditModel` has no field for them, so `CreateDraftVersionAsync` cannot carry
them forward either.

**Saving a draft in the builder silently deletes its attachments.** The seeded
attachment survives only because nothing has re-saved that version through the UI. This
is a data-loss bug sitting under the feature gap, not merely a missing editor.

## Design

### Model — `Workflow.Core`

`SubWorkflowAttachment` gains a version anchor and loses its required task:

```csharp
public required int WorkflowDefinitionVersionId { get; set; }   // new — the scope
public WorkflowDefinitionVersion? DefinitionVersion { get; set; }

public int? TaskDefinitionId { get; set; }   // was required; null means any task
```

The version is now what scopes an attachment; the task definition only narrows it. The
anchor has to exist because a version-wide attachment has no task definition to infer a
version from.

The unique index becomes `(WorkflowDefinitionVersionId, TaskDefinitionId,
SubWorkflowDefinitionId)`. SQL Server treats NULLs as equal for uniqueness, which gives
exactly the wanted rule: one version-wide attachment per sub-workflow per version.

Both foreign keys cascade: an attachment cannot outlive the version that scopes it, and
one narrowed to a task cannot outlive that task — promoting it to version-wide because
its task was deleted would silently widen what an author wrote. `ClearGraphAsync` deletes
attachments explicitly anyway, so the cascade is a backstop rather than the mechanism.
`SubWorkflowDefinitionId` stays `Restrict`, unchanged: deleting a sub-workflow that is
still attached somewhere should fail loudly.

`SubWorkflowInstance` is unchanged. The delegatee lives on the entry task, where every
other assignment lives; a second copy on the instance could disagree with it.

### Engine — `Workflow.Persistence.EF`

**Assignment at spawn.** `StartSubWorkflowAsync` gains a parameter in the same position
`AddAdHocTaskAsync` has it, so the two spawn paths read alike:

```csharp
StartSubWorkflowAsync(
    int parentTaskId,
    int subWorkflowDefinitionId,
    string actorId,
    WorkflowAssignment? assignment = null,   // new
    string? notes = null,
    CancellationToken ct = default)
```

In `SpawnAsync` the requested assignment replaces the parent's as the resolver's input:

```csharp
var assignment = await ResolveAssignmentAsync(
    entryDef,
    requested ?? new WorkflowAssignment(parent.AssignedToActorId, parent.AssignedBranchKey),
    parent.ToSnapshot(), ct);
```

An entry task with no `AssignmentRoleKey` inherits the chosen person; one with a role key
still resolves on top of it. Auto-assignment and manual assignment are therefore the same
mechanism with and without a role key, and `ReassignTaskAsync` already covers overriding
an auto-assignment after the fact.

**Attachment lookup.** The version to look in is
`parent.TaskDefinition.WorkflowDefinitionVersionId` — the version that owns the parent
task's *definition*, not the run's pinned version. The two differ exactly when the parent
task is itself inside a sub-workflow instance, and the definition's version is the right
one: a sub-workflow nested under a sub-workflow task should find the attachments its own
author wrote. `LoadTaskAsync` already includes `TaskDefinition`.

Matching version-wide *or* task-specific can return two rows, so
precedence is required: **task-specific wins.** An author who narrowed a sub-workflow to
one task meant it. `SingleOrDefaultAsync` becomes an ordered `FirstOrDefaultAsync` on
`TaskDefinitionId != null` descending, in both `StartSubWorkflowAsync` and
`GetSubWorkflowOptionsAsync`.

**Automatic spawn stays task-specific.** `SpawnAutomaticAsync` keeps querying by exact
task definition. Automatic *and* version-wide would spawn an instance on every task in
the run, which is never what an author means, so the validator rejects the combination
rather than the engine interpreting it.

### Builder seam — `Workflow.Core/Builder`

```csharp
public sealed class SubWorkflowAttachmentEditModel
{
    public int Id { get; set; }
    public int SubWorkflowDefinitionId { get; set; }
    public Guid? TaskLocalId { get; set; }        // null means any task
    public bool IsAutomatic { get; set; }
    public bool IsBlocking { get; set; } = true;
    public bool AllowMultiple { get; set; }

    public SubWorkflowAttachmentEditModel Clone();
    public void CopyFrom(SubWorkflowAttachmentEditModel source);
}
```

`Clone`/`CopyFrom` are not optional: Blazor passes reference types by reference, so a
dialog binding straight to its `Model` parameter cannot be cancelled. This is the same
trap already documented for the three existing dialogs.

It hangs off `WorkflowEditModel`, not `TaskEditModel`, because `TaskLocalId` is nullable
and a version-wide attachment has no task to sit under:

```csharp
public List<SubWorkflowAttachmentEditModel> SubWorkflows { get; set; } = [];
```

`IWorkflowBuilderClient` gains one method, so the picker can be populated:

```csharp
Task<IReadOnlyList<SubWorkflowDefinitionOption>> GetSubWorkflowDefinitionsAsync(
    CancellationToken ct = default);

public sealed record SubWorkflowDefinitionOption(
    int DefinitionId, string Name, bool HasPublishedVersion);
```

### Persistence — `DemoBuilderClient`

This is where gap 3 is fixed, and it costs nothing extra because attachments now travel
the same road as routes:

- `LoadGraphAsync` includes attachments for the version and maps `TaskDefinitionId` to
  the matching `TaskLocalId`.
- `ClearGraphAsync` removes them explicitly, alongside routes, outcomes and triggers,
  rather than relying on the FK cascade.
- `PersistCoreAsync` re-adds them after the task definitions exist, mapping local ids
  back exactly as routes do.
- `CreateDraftVersionAsync` then carries attachments forward for free, because it
  round-trips through `WorkflowEditModel`.

### Validation — `WorkflowDefinitionValidator`

- An automatic attachment must name a task.
- The attached definition must have `IsSubWorkflow` set.
- The attached definition must have a published version — checked at publish, not on a
  draft, matching the existing draft-tolerance rule.
- No duplicate (task, sub-workflow) pairs, version-wide included.

### UI — `Workflow.MudBlazor`

A `Sub-workflows` panel after Tasks, labelled `this version` like the other
version-scoped panel:

```
┌─ Sub-workflows ───────────────── [this version] ─┐
│ Sub-workflow       Scope           Spawn         │
│ Technical Review   any task        on request  ✎✗│
│ Drawing Markup     Provide Input   automatic   ✎✗│
│                                     [+ Attach]   │
└──────────────────────────────────────────────────┘
```

A version-level panel rather than a sub-list on each task, for two reasons: it matches
the builder's existing panel-per-scope rationale, and a version-wide attachment has
nowhere sensible to sit inside a per-task UI without being repeated on every task.

`SubWorkflowAttachDialog` edits a clone and commits with `CopyFrom`.

`RunnerSubWorkflowDialog` gains a person picker fed by the existing `GetActorsAsync` —
the same source the reassign dialog uses.
`IWorkflowRunnerClient.StartSubWorkflowAsync` carries the chosen actor id through.

### Host — `DemoRunnerClient`

The org lookup stays host-side; the engine never learns what a section is:

```csharp
Person(actorId) → SectionId → Section.Code
  ⇒ new WorkflowAssignment(actorId, section.Code)
```

A person with no section yields `new WorkflowAssignment(actorId, null)` and a logged
warning, matching how the resolver already handles a missing section rather than
inventing a second failure style.

**One helper, two call sites.** `AddAdHocTaskAsync` already accepts a
`WorkflowAssignment` and has the same desync hazard — nothing stops a caller passing an
actor with a stale or blank branch key. The lookup goes in one place,
`DemoOrg.AssignmentForAsync(actorId, ct)` alongside the other host adapters, called by
both `StartSubWorkflowAsync` and `AddAdHocAsync` on `DemoRunnerClient`. The ad-hoc path
is then fixed by the same change rather than left as the next instance of this bug.

### Blocking — confirmed, and what is missing around it

A blocking instance must stop its parent completing until it is **either completed or
cancelled**. Half of this is already the engine's behaviour:

```csharp
.Where(i => i.ParentTaskId == taskId
         && i.IsBlocking
         && i.Status == SubWorkflowStatus.Running)   // SubWorkflows.cs:285
```

`SubWorkflowStatus` is `Running / Completed / Cancelled`, so testing for `Running` alone
is what makes cancellation unblock. `SubWorkflowAttachment.IsBlocking` defaults to `true`,
so delegated work blocks unless an author says otherwise, and the flag is copied onto the
instance at spawn so changing the attachment later cannot unblock work already in flight.

This is deliberate rather than incidental — it is the fix for the original engine's finding H1, which
tested `Status != Completed` and therefore blocked a parent forever on a cancelled item.

**But the cancelled half is unreachable.** Correcting an earlier claim in this document:
`SubWorkflowStatus.Cancelled` is set in exactly one place, `CancelInstancesForAsync`
(`SubWorkflows.cs:256`), which is called from exactly one place — `CancelTaskAsync`,
immediately after it cancels the **parent** task (`Queries.cs:42`). So an instance becomes
Cancelled only when its parent already has, and a cancelled parent can never be completed.
Cancellation cascades *down from* a parent; there is no way to abandon a delegated chain and
then complete the task that delegated it.

The guard clause is right, and defensively so. What is missing is a public
`CancelSubWorkflowAsync(instanceId, actorId, reason)` — it reuses the existing cascade, so
the work is an entry point rather than new mechanics, but without it the stated requirement
is only half met. Three further things need doing.

**It is not tested.** The existing `IsBlocking` assertions check the flag is carried, not
that it blocks. The cancellation path in particular — the actual H1 regression — is
guarded by a comment and nothing else.

**The runner offers a Complete button that throws.** `CanComplete` is
`task.Status is NotStarted or InProgress` (`DemoRunnerClient.cs:163`) and says nothing
about blocking, so a blocked task renders an enabled button and the user meets the
engine's exception after clicking. That contradicts the rule the fork and rejection
dialogs already follow: an engine constraint is surfaced before the action, because an
error the user could not have predicted is worse than a disabled control.

`RunningSubWorkflowCount` on `RunTaskView` cannot carry this — it counts every running
instance, blocking or not. It gains a sibling:

```csharp
int RunningSubWorkflowCount,
int BlockingSubWorkflowCount,   // new
```

`WorkflowRunner` disables Complete when `BlockingSubWorkflowCount > 0` and says what it
is waiting for. This stays out of `CanComplete` on purpose: `CanComplete` is host policy
about *who may act*, while blocking is an engine rule about *whether the action is legal
at all*. Folding an engine rule into a host decision would oblige every host to
reimplement it, and a host that forgot would get the thrown error back.

### Cancelling a delegated chain

`CancelSubWorkflowAsync(instanceId, actorId, reason)` cancels one running instance and
everything still open inside it, leaving the parent alone — the counterpart to cancelling a
parent, which takes its instances down with it.

The per-instance body is extracted from `CancelInstancesForAsync` into a shared
`CancelInstanceAsync`, used by both, so the cascade and the standalone cancel cannot drift
apart about what cancelling an instance means. Cancelling one that is not Running is an
error rather than a no-op: it almost always means the caller is working from stale state.

In the runner it appears where it is needed — on the alert telling somebody why they cannot
complete their task, which names each blocking instance and offers to abandon it.

### Demo and migration

Add a `get-info` step in front of the seeded `section-review → division-review` chain, so the
demo exercises the real case: delegate to a person, watch the two reviews resolve to
that person's section. One EF migration in `DemoDocuments.Server`, adding
`WorkflowDefinitionVersionId`, making `TaskDefinitionId` nullable, and replacing the
unique index; existing rows are backfilled from their task definition's version.

## Testing

| Test | What would otherwise break |
|---|---|
| Attachment survives a draft re-save | Gap 3 — the silent data loss |
| Attachment survives `CreateDraftVersionAsync` | Gap 3 across versions |
| Version-wide attachment is offered on every task | Gap 2 |
| Task-specific attachment wins over version-wide | The precedence rule |
| Automatic + no task is rejected by the validator | Spawning on every task in a run |
| Delegated entry task carries the chosen actor *and* their section | Gap 1 |
| `section-review` resolves to the delegatee's Section Lead, not the delegator's | Gap 1, the actual symptom |
| Delegating to a person with no section degrades cleanly | The null-section path |
| Ad-hoc task assignment goes through the same helper | The hazard shared with `AddAdHocTaskAsync` |
| Parent refuses to complete while a blocking instance runs | Blocking not enforced at all |
| Parent completes once the instance completes | Blocking never releasing |
| Parent completes once the instance is **cancelled** | the original engine's finding H1 — and, until `CancelSubWorkflowAsync` exists, unreachable |
| Cancelling an instance cancels its open tasks | Work nobody can reach, still assigned to people |
| An instance cannot be cancelled twice | A second cancel silently re-stamping a closed instance |
| A non-blocking instance never stops the parent | `IsBlocking` ignored |
| `BlockingSubWorkflowCount` excludes non-blocking instances | The runner disabling Complete for nothing |

## Known limits

**The published-version check is not a guarantee.** "This sub-workflow has a published
version" is true when validated and can stop being true afterwards. The engine already
throws at spawn when there is no published version, so it degrades to a runtime error
rather than a silent failure — but the validator cannot promise it.

**Dialogs are still untested as components.** `SubWorkflowAttachDialog` joins three
existing dialogs that render but have never been driven by a test. bUnit stays
deliberately deferred while the builder is changing shape; `Clone`/`CopyFrom` semantics
are covered by `EditModelCloneTests` and the new model should be added there.

## Decisions taken, and what was rejected

| Decision | Rejected alternative | Why |
|---|---|---|
| Delegator picks a **person**; host derives the section | Picking a section, engine-side | Every step is assigned to an individual. A section picker would still leave the actor unset. |
| Person → section resolved in the host client at delegation | A `section-lead-of-assignee` role in the resolver | Fixing it in the resolver leaves `AssignedBranchKey` wrong on the task, so progress scoping, inbox filters and fork branch keys stay wrong — only the Section Lead path gets better. |
| Version-wide attachments, per-task optional | Per-task only, with a checkbox grid | "At any point in the workflow" is the common case, and per-task-only means a task added later silently lacks it. |
| Version-wide attachments, per-task optional | Version-wide only | Loses the ability to say a sub-workflow only makes sense at certain steps, and leaves automatic spawn with no sensible meaning. |
| Attachments ride `WorkflowEditModel` | An editor outside the version graph | Attachments are version-scoped through the task definition, exactly like routes, outcomes and triggers. Anything else needs the FK changed and attachments re-pointed at new task rows after every save. |
| Attachment keyed to a task definition | Keyed to a task *type* | Would apply to every version at once, published ones included, and could not distinguish the same task type used twice. |
