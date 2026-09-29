# Reading workflow state for many subjects at once

**Status:** built 2026-09-02. See `WorkflowEngine.SubjectState.cs` and `SubjectStateTests.cs`.
**Origin:** the integration spike, from a host building a document list view.

---

## The problem

Every read on `IWorkflowEngine` is single-entity. By run, by task, by manifest — or by exactly
one subject:

```csharp
Task<Result<IReadOnlyList<WorkflowRunSnapshot>>> GetRunsForSubjectAsync(
    WorkflowSubject subject, CancellationToken ct = default);
```

There is no way to ask about a *set* of subjects. That matters because the most-used screen in
a document-driven system is a list of documents, and every row wants the same three things:
what step it is on, who has it, what state it is in.

With today's API that is one call per row. A fifty-row page is fifty queries, each with an
`Include`. Not pathological — they are plain reads rather than transactions, and
`(SubjectType, SubjectId)` is indexed — but not something to put behind a grid.

**The consequence is worse than the cost.** A host that needs a list view reaches past
`IWorkflowEngine` to `IWorkflowDbContext` and queries `WorkflowTasks` and `WorkflowRuns`
directly. That works and is legitimate — reads are ungated by design and a host owns its own
queries — but it means the first thing a real host does with the read API is bypass it, and it
couples that host to the schema rather than the interface.

## Two things a list view needs that the current model does not express

Both came from a host describing its screens, and both invalidate the obvious design.

### A forked step's current task is the origin, not the branches

When a task is forked the origin becomes `WorkflowTaskStatus.Forked`, `IsForkOrigin`, and keeps
the new `ForkGroupId`; the branches are created open, sharing that group. So filtering to open
tasks returns **the branches and not the origin.**

For an inbox that is exactly right — each person needs their own branch, and the origin is not
actionable by anyone. For a list of documents it is exactly wrong. A row should read
"Provide Response", once, because that is the step the document is on. Returning the branches
gives three rows all named "Provide Response", differing only by section.

So **"open and actionable" and "the step this is on" are different questions**, and this API is
the second one. A naive `GetOpenTasksForSubjectsAsync` answers the first and silently gives a
grid the wrong answer.

### A subject can carry several workflows, and they must be separable

A document may have more than one workflow running at once, and the host wants them presented
separately — one grid or tab each. That needs the result **grouped by run**, and each group
**named**, because a tab with no label is not a tab. `WorkflowTaskSnapshot` carries
`WorkflowRunId` but no workflow name, so a flat list of tasks cannot be grouped into labelled
tabs without a second query.

## Proposed API

```csharp
/// <summary>How a forked step is represented.</summary>
public enum ForkView
{
    /// <summary>
    /// The open branch tasks, one per branch. What an inbox wants: each person's own work.
    /// </summary>
    Branches = 0,

    /// <summary>
    /// The fork origin instead of its branches, collapsing a fanned-out step back to the one
    /// step it is. What a list view wants: "this document is on Provide Response", once,
    /// however many sections are working it.
    ///
    /// <para>The origin is <see cref="WorkflowTaskStatus.Forked"/> and not actionable, which is
    /// the point — it names the position, it is not something to click.</para>
    /// </summary>
    Origin = 1,
}

/// <summary>
/// A step a subject is currently on.
///
/// <para><paramref name="OpenBranchCount"/> is how many branches of a forked step are still
/// open, and is <c>0</c> for anything that is not a fork origin — including every task under
/// <see cref="ForkView.Branches"/>, where the branches come back individually and counting them
/// is the caller's own <c>Count</c>.</para>
///
/// <para>It exists because <see cref="ForkView.Origin"/> deliberately withholds the branches, so
/// without it a caller has no way to know a step fanned out at all, let alone how widely. The
/// origin's own <c>AssignedToActorId</c> is returned unchanged — whoever held the step before it
/// forked — so a host can render "J. Smith" or "3 sections" or both, and decide for itself which
/// of those its users need.</para>
/// </summary>
public sealed record CurrentTask(
    WorkflowTaskSnapshot Task,
    int OpenBranchCount);

/// <summary>One workflow running on a subject, and where it has got to.</summary>
public sealed record SubjectWorkflowState(
    int WorkflowRunId,
    string WorkflowName,
    int Version,
    WorkflowRunStatus Status,
    IReadOnlyList<CurrentTask> CurrentTasks);

/// <summary>
/// Where each of many subjects has got to, for a list view: one row per document, showing what
/// step it is on and who has it.
///
/// <para>Grouped by run rather than flattened, because a subject may carry several workflows at
/// once and a host showing them separately needs them separable — and named, which a bare
/// <see cref="WorkflowTaskSnapshot"/> is not.</para>
///
/// <para>Current means <see cref="WorkflowTaskStatus.NotStarted"/> or
/// <see cref="WorkflowTaskStatus.InProgress"/>, plus — under
/// <see cref="ForkView.Origin"/> — the <see cref="WorkflowTaskStatus.Forked"/> origin of any
/// fork group that still has open branches. A fork whose branches have all finished is history,
/// not position, and is excluded. Archived runs are excluded.</para>
///
/// <para>A subject with nothing running is absent from the result rather than present with an
/// empty list, so a caller can tell "nothing open" from "never asked".</para>
///
/// <para><b>A run can still have several current tasks</b> even under
/// <see cref="ForkView.Origin"/> — a workflow may legitimately have two unrelated steps open at
/// once. The result does not pretend otherwise, and a caller wanting one value per row needs its
/// own rule.</para>
/// </summary>
Task<Result<IReadOnlyDictionary<WorkflowSubject, IReadOnlyList<SubjectWorkflowState>>>>
    GetCurrentStateForSubjectsAsync(
        IReadOnlyList<WorkflowSubject> subjects,
        ForkView forkView = ForkView.Branches,
        CancellationToken ct = default);
```

### Why these choices

**Named for position, not for actionability.** An earlier draft called this
`GetOpenTasksForSubjectsAsync`, for symmetry with `GetOpenTasksForActorAsync`. That name is a
lie under `ForkView.Origin`, which returns a `Forked` task. Honesty beats symmetry: the two
methods answer genuinely different questions, and naming them alike would hide that.

**`ForkView` rather than always-one-behaviour.** Both views are legitimate and the same
application needs both — its inbox wants branches, its document list wants origins. A flag is
better than making every caller reconstruct one from the other, which requires the origin the
open-tasks filter has already discarded.

**Grouped by run, with the name.** Directly serves "one grid per workflow". A flat list would
make every caller regroup and then issue a second query for the names.

**`WorkflowSubject` as the dictionary key.** It already implements `IEquatable<WorkflowSubject>`
and overrides `GetHashCode`. No new type.

**`WorkflowTaskSnapshot` for the tasks.** The established currency of the read side. It does not
carry the definition flags (`IsBlocking`, `IsForkable`, `IsConvergencePoint`) — a separate,
already-recorded gap that should be fixed there rather than worked around here.

**No `includeSubWorkflowTasks` flag.** A sub-workflow's tasks are ordinary tasks in the parent's
run, distinguished by `SubWorkflowInstanceId`, which the snapshot carries. A caller wanting the
mainline step filters on `is null` without the signature growing a policy.

**Absent rather than empty.** Cheaper, and it distinguishes "asked, nothing running" from "not
asked" without a sentinel.

## What a list view shows — still the caller's decision

The engine reports position; turning that into one cell is domain-specific:

| Situation | Under `ForkView.Origin` | Caller still decides |
|---|---|---|
| One open task | One task | Show it |
| Forked step | The origin, once, with `OpenBranchCount` | How to render it: the origin's assignee, the count, or both |
| Two unrelated open steps | Two tasks | Which to show, or both |
| Several runs on the subject | One `SubjectWorkflowState` each | Tab, or pick the mainline |
| Sub-workflow running | Parent task **and** the sub-workflow's tasks | Filter `SubWorkflowInstanceId is null` |

## Implementation notes

**Group by subject type.** Subjects are `(type, id)` pairs and a list view is almost always one
type with many ids. Grouping and emitting one query per distinct type keeps the predicate to
`SubjectType = @t AND SubjectId IN (…)`, which uses the existing `(SubjectType, SubjectId)`
index. A flat `OR` over pairs would not.

**Chunk the ids.** SQL Server caps parameters near 2,100 and a caller may hand over a large
page. Chunk each type's ids well under that and concatenate.

**Resolving `ForkView.Origin` needs the fork group, not just the task.** An origin is current
only while its group still has open branches. Both origin and branches share `ForkGroupId`, so
one pass over the run's tasks answers it: for each group with an open branch, take the
`IsForkOrigin` task and drop the branches. Do this in memory over tasks already fetched — not as
a second query.

**`OpenBranchCount` falls out of that same pass** and costs nothing extra: it is the number of
open tasks in the group that produced the origin. Do not add a query for it.

**One query per subject type, not per subject.** Join tasks to runs, project the subject
alongside, and group in memory. `WorkflowEngine.Inbox.cs` already does this shape — projecting
the owned subject as two scalars and rebuilding it — and should be reused.

**Not transactional, not gated.** Consistent with the rest of the read side.

## Built — what changed on the way

**`SubjectWorkflowState` gained `IsTest`.** The spec said nothing about test runs.
`GetRunsForSubjectAsync`, the single-subject read this bulks up, does not filter them out, so
neither does this — but a document list almost certainly wants to. Exposing the flag keeps the
two reads consistent and lets the caller decide, rather than this one quietly disagreeing with
its sibling.

**The fork origins are fetched under both views.** Under `ForkView.Branches` they are dropped
again, but they have to be *fetched*, because a group's open-branch count is computed from the
same rows. Fetching them only under `Origin` would mean two different queries for one method.

**A run with no current position is omitted, not returned empty.** A run whose only rows were
fork origins with nothing still open is between steps; it contributes no
`SubjectWorkflowState`, and a subject whose runs all do that is absent entirely, which is the
same rule as "nothing running".

## Open questions

1. ~~**Who is a forked step "assigned to"?**~~ **Decided 2026-08-31: returned as-is, with a
   branch count alongside.** The origin's `AssignedToActorId` is whoever held the step before it
   forked, and it is not nulled out — the host renders what its users need. That required
   `CurrentTask.OpenBranchCount`, because `ForkView.Origin` withholds the branches and a caller
   would otherwise have no way to know the step fanned out, or how widely.

   Rejected: nulling the assignee on a fork origin. It throws away true information — that
   person *did* hold it — to prevent a misreading that a label solves more cheaply.

2. **Should `GetRunsForSubjectAsync` bulk up too?** `GetRunsForSubjectsAsync` would be the same
   shape for callers wanting run-level state without tasks. Cheap alongside. Worth doing only
   if a host asks.

3. **Is a cap wanted?** Chunking makes an unbounded list work rather than fail, which may be the
   wrong kindness — a caller passing 50,000 subjects has a paging bug, and an explicit limit
   would surface it. **Leaning: chunk, document that callers page, do not throw.**

## Testing

Written test-first against SQL Server, as the rest of the suite is:

- Several subjects, one open task each — every subject present with its task.
- A subject with nothing running — absent, not present-and-empty.
- A subject whose tasks are all completed — absent.
- **`ForkView.Branches` on a forked step** — one entry per branch, origin absent.
- **`ForkView.Origin` on the same data** — the origin once, branches absent.
- **`ForkView.Origin` after the branches have converged** — the origin absent, because a
  finished fork is history and not position. This is the assertion that stops the fork rule
  degenerating into "always show origins".
- **`ForkView.Origin` reports the open branch count** — fork into three, complete one branch,
  and the origin comes back with `OpenBranchCount` of two. Pins both that the count is of *open*
  branches rather than of the fork's original width, and that it is not a second query.
- **`OpenBranchCount` is 0 for an ordinary task**, under either view.
- **The origin's assignee is returned unchanged** — the person who held the step before it
  forked, not null, because a host may want to show them.
- Two concurrent runs on one subject — two `SubjectWorkflowState` entries, each named, each with
  its own tasks.
- A sub-workflow in progress — the parent's task and the sub-workflow's tasks both present,
  distinguishable by `SubWorkflowInstanceId`.
- An archived run — excluded.
- A subject not passed in — absent though it has open work, proving the filter binds.
- More ids than one chunk holds — proving the batching is not a rounding error.
- One query per subject type, not per subject. The N+1 being gone is the entire point, and
  nothing else in the suite would notice if it came back.
