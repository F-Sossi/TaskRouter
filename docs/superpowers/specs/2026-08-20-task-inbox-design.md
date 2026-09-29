# The task inbox

**Date:** 2026-08-20
**Status:** Approved, not yet implemented

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.

## What this is for

A person needs one place that answers "what is waiting on me?", and a way to get from
that answer to the document where the work happens. It is the last of the four things
the library exists to do, and the only one with nothing behind it.

The other three are built:

| Intent | Where it lives |
|---|---|
| An admin builds a workflow per document type | The builder, `/workflows/{versionId}` |
| A document shows its workflow, users mark steps complete | `WorkflowRunner`, used in two places |
| Tasks are assigned to people | `AssignedToActorId` + `AssignedBranchKey`, resolved through `IWorkflowAssignmentResolver` |
| **A list of what is waiting on you** | **Nothing. This spec.** |

## What exists today, and why it is not enough

One hand-written endpoint in the demo host, `Program.cs:269`:

```csharp
app.MapGet("/tasks/assigned/{actorId}", async (string actorId, DemoDbContext db) =>
    await db.WorkflowTasks
        .Where(t => t.AssignedToActorId == actorId
                 && t.Status != WorkflowTaskStatus.Completed
                 && t.Status != WorkflowTaskStatus.Cancelled
                 && t.Status != WorkflowTaskStatus.Forked)
        ...
```

Three problems, in order of severity:

1. **It is not a library capability.** There is no inbox method on `IWorkflowEngine`.
   A consuming host gets nothing and writes this itself, which means writing the
   predicate below itself, which means getting it wrong in a new way per host.
2. **It never returns the document.** It projects `WorkflowRunId`. The user's question
   is "which documents", and this cannot answer it.
3. **It does not exclude test runs.** `WorkflowRun.IsTest` exists precisely so hosts can
   keep the builder's throwaway runs out of inboxes, and this forgets it. An admin trying
   a draft out puts tasks in real people's inboxes.

There is also a fourth, latent: the status filter is a **negative** list. Any status
added to `WorkflowTaskStatus` later is admitted silently.

## Design

Three layers, matching what the builder and the runner already do: an engine query, a
host seam, a component. The demo implements the seam as the worked example.

### 1. The engine query

New partial, `src/Workflow.Persistence.EF/WorkflowEngine.Inbox.cs`, rather than growing
`WorkflowEngine.Queries.cs`.

```csharp
/// <summary>
/// Open tasks for one actor: those assigned to them, plus unclaimed work in the org
/// units they belong to. The host passes its own branch keys — org membership is host
/// knowledge, and a membership seam would be a third way to ask the same question.
/// </summary>
Task<Result<IReadOnlyList<InboxTaskSnapshot>>> GetOpenTasksForActorAsync(
    string actorId,
    IReadOnlyList<string> branchKeys,
    CancellationToken ct = default);
```

The predicate is the reason this belongs in the library:

```csharp
t.Status is WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress
&& !t.IsArchived
&& !t.Run!.IsTest
&& (t.AssignedToActorId == actorId
    || (t.AssignedToActorId == null && branchKeys.Contains(t.AssignedBranchKey)))
```

- **A positive status list, not a negative one.** `Forked` is the value a host writing
  this by hand forgets, and a forked task is a superseded ghost — showing it puts work in
  an inbox that can never be completed.
- **`!IsTest`** is the regression that motivated this whole piece.
- **Unclaimed means `AssignedToActorId == null`,** not "assigned to someone else in my
  unit". `DemoAssignmentResolver` returns `current` unchanged when a section has nobody
  in the role (`HostAdapters.cs:59`), which is exactly how a task ends up carrying a
  branch key and no owner. Without this clause that work is invisible to everyone.
- **Run status is deliberately not filtered.** Task status is the authority on whether
  work exists. A `Completed` run holding an open task is a defect, and hiding it would
  conceal the defect in the one place somebody would notice it.

```csharp
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
```

`IsBlocked` — the task is held by a blocking sub-workflow — is a **flag, not a filter**.
The task is genuinely the actor's and genuinely waiting; hiding it makes work vanish from
the only place anyone looks, and the runner already explains what it is waiting on once
you arrive.

`Label` follows the existing convention: `TaskDefinition.DisplayName` falling back to
`TaskType.DisplayName`, as `SnapshotMapper` does.

### 2. The host seam

New file, `src/Workflow.Core/Inbox/IWorkflowInboxClient.cs`. Sibling of
`IWorkflowBuilderClient` and `IWorkflowRunnerClient`, and separate for the same reason: a
Blazor Server host implements it in-process, a WebAssembly host over HTTP, and the
component cares about neither.

```csharp
public interface IWorkflowInboxClient
{
    Task<IReadOnlyList<InboxItem>> GetInboxAsync(string actorId, CancellationToken ct = default);
}

public sealed record InboxItem(
    int TaskId,
    string TaskLabel,          // "Technical Review — Section Lead"
    string WorkflowName,
    string SubjectLabel,       // "CR-2026-0042"
    string? SubjectSubtitle,   // "Pump room rewire"
    string SubjectUrl,         // "/documents/42"
    string? BranchKey,
    bool IsUnclaimed,
    bool IsBlocked,
    DateTime Created);
```

The host resolves subject → document by **joining the engine's rows to its own tables in
one query**. Engine tables and host tables share a `DbContext` by design — that is what
lets a task completion and a domain update commit together — so this is a join, not an
N+1 resolve, and no `IWorkflowSubjectResolver` seam is needed.

`SubjectUrl` is a plain string the host builds. No routing abstraction; the component
stays ignorant of what a document is.

### 3. The component

`src/Workflow.MudBlazor/WorkflowInbox.razor`.

```csharp
[Parameter, EditorRequired] public string ActorId { get; set; } = string.Empty;

/// <summary>Raised with the row count after every load, so a host can badge a tab.</summary>
[Parameter] public EventCallback<int> OnCountChanged { get; set; }

public Task RefreshAsync();
```

`OnCountChanged` exists to match what the target host already does: the original system's
`MyDashboard.razor` badges its tabs with
`<WorkItemListGraphQL ReportTasksCount="UpdateTasksCount" />`. Matching that shape makes
this a drop-in for a new `MudTabPanel` rather than a port.

`RefreshAsync` is public so a host can re-poll after something elsewhere on the page
changes the run.

Behaviour:

- `MudTable<InboxItem>`, sortable on Task / Document / Section / Waiting.
- **Default sort oldest-first.** The thing that has waited longest is the thing to do.
- `IsUnclaimed` and `IsBlocked` render as `MudChip`s.
- Row click → `Nav.NavigateTo(item.SubjectUrl)`. A row with no URL is not clickable.
- Empty state is a `MudAlert`, not a blank table.

It reuses the runner's `SemaphoreSlim _gate` + `_loadedFor` pattern verbatim
(`WorkflowRunner.razor:342`), because the hazard is identical: a parent that resolves the
signed-in actor asynchronously re-fires `OnParametersSetAsync` mid-load, and a
`DbContext` permits one operation at a time.

One difference from the runner: `GetInboxAsync` returns a list rather than a `Result`, so
a failure arrives as an exception. The component catches it and renders `_error`. The
runner never needed this because every one of its failures comes back through
`RunnerResult`.

### 4. Demo wiring

- **`DemoInboxClient`** in `samples/DemoDocuments.Server/Workflow/`. Gets the person's
  section code from `DemoOrg.AssignmentForAsync` — the existing single place that maps a
  person to their unit, so no new method — calls the engine, then runs one query against
  `Documents` to build label, subtitle and `/documents/{id}`.
- **`/inbox` page** using the same "Acting as" picker `DocumentDetail` uses, plus a nav
  button in `MainLayout`.
- **`Program.cs:269` is rewritten on top of the engine query**, so the predicate exists
  exactly once in the repository instead of twice with different bugs.

**A subject whose document is missing keeps its row**, labelled with the raw `ChangeRequest:42` and
carrying no URL. Same reasoning as not filtering on run status: an orphaned task is a
defect worth seeing.

## Testing

`InboxTests`, integration against SQL Server like the rest of the suite:

1. Returns tasks assigned to the actor
2. Returns unclaimed tasks whose branch key is one the actor passed
3. Does **not** return unclaimed tasks in another unit
4. Does **not** return a task assigned to somebody else in the actor's unit
5. Excludes `Completed`, `Cancelled` and `Forked`
6. **Excludes tasks in a test run** — the regression that started this
7. Includes tasks belonging to a sub-workflow instance; delegated work is real work
8. Sets `IsBlocked` on a task held by a blocking sub-workflow
9. Sets `IsUnclaimed` only on the unowned rows

`InboxClientTests`, against `DemoInboxClient`: label, subtitle and URL resolution;
several tasks on one document; and the orphaned-subject row.

**No bUnit.** Consistent with the standing deferral recorded in `STATE.md` — the UI is
still changing shape, and component assertions cost more than they catch right now.

## Known limits

- **No claim action.** Unclaimed work is flagged, not takeable from the inbox; you open
  the document and use the runner's existing Reassign. Claiming is an authorization
  decision, and this layer has no authorization at all yet.
- **A task with neither an actor nor a branch key is in nobody's inbox.** Both clauses of
  the predicate need something to match on, so a wholly unassigned task is invisible. That
  state is reachable — `IWorkflowAssignmentResolver` may return `WorkflowAssignment.Unassigned`
  — and finding those is an admin query, not an inbox one. Worth knowing rather than
  worth fixing here.
- **No paging.** The query returns every open row for the actor. A real deployment with
  thousands will want paging, and the signature can take it later without breaking the
  seam.
- **No polling or push.** The count updates when the component loads or a host calls
  `RefreshAsync`. Live updates would need something the library does not ship.
- **One org unit per person in the demo.** The engine signature takes a list, so a host
  with people in several units is already accommodated; `DemoOrg` simply has nothing to
  put in it.

## Decisions taken, and what was rejected

| Decision | Rejected alternative | Why |
|---|---|---|
| One row per task, document as a column | One row per document with nested tasks | Sortable by age and task; the badge count is just the row count |
| Mine + unclaimed in my units | Mine only; or everything in my unit | Mine-only strands the work `DemoAssignmentResolver` leaves unowned; everything-in-my-unit is a supervisor view that makes the inbox noisy |
| Flag unclaimed, no claim button | Claim button in the inbox | Claiming is authorization, and nothing authorizes anything yet |
| Host joins subjects to documents | An `IWorkflowSubjectResolver` seam | The host already owns the schema both tables live in; a resolver would turn a join into an N+1 |
| Engine query + seam + component | Engine query only, hosts build their own UI | The predicate would then be rewritten per host, which is the problem being fixed |
