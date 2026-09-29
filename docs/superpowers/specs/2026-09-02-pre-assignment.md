# Pre-assigning work that does not exist yet

**Status:** specification, not yet built.
**Origin:** a host whose managers plan a document's staffing before the work reaches each step.

---

## The requirement

A manager schedules a document, chooses a workflow for it, and while doing so says who will
handle particular steps — before those steps are reached, and before the people ahead of them
have finished. They can also reopen a running workflow, see the whole thing, and set or change
those assignments as it progresses.

So: Bill has the current task; Bob is already named for the follow-up. When Bill finishes, the
follow-up goes to Bob rather than to whoever the step's role would have produced.

And a person can ask what is assigned to them **now or later** — one list spanning work they
hold and work they are promised.

## Why this cannot be done in the host

The tempting implementation is a host table plus a check in `IWorkflowAssignmentResolver`. It
does not work, for two reasons in the engine as it stands.

**The resolver is not called when a step has no role key:**

```csharp
if (string.IsNullOrWhiteSpace(definition.AssignmentRoleKey))
{
    return current;   // the resolver never runs
}
```

A step with no role — an entry step, a closing step — cannot be pre-assigned through that seam
at all.

**And when it does run, it cannot tell which step it is assigning.**
`ResolveAsync(roleKey, current, task, ct)` receives a `WorkflowTaskSnapshot` which, on the
routing path, is the task that just *completed* — not the one being created. Two definitions
sharing a role key are indistinguishable to it.

> **A related inconsistency, worth fixing whether or not this is built.** At run start the
> engine passes `Placeholder(definition)`, whose `TaskDefinitionId` **is** the task being
> created; on the routing path it passes the completed task's snapshot. The same parameter
> means "the task being assigned" on one path and "the task that just finished" on the other.
> Nothing depends on the difference today, which is why it has gone unnoticed.

**The deeper reason:** the follow-up task does not exist. Tasks are created when routed to, so
there is no row to assign. Pre-assignment must be a stored *intention* consulted at task
creation — which means a table the engine owns and a hook inside the creation path.

## Decisions taken

Answered by the host before speccing, and none of them are technical:

| Question | Answer |
|---|---|
| Does a pre-assignment beat the step's role default? | **Always.** If Bob is named, Bob gets it, whatever the role says. |
| Can it be changed afterwards? | Yes — the task is his, and ordinary reassignment applies once it exists. |
| Forks? | **Not applicable.** Mainline steps only. |
| Rework — a step that runs twice? | The pre-assignment **stands for the run**, so a recurring step goes to the same person again. *(Read from "that task is his"; correct this if a one-shot was meant.)* |
| Who may do it? | A **separate permission** from acting on your own work. |

## Data

One table, `TaskRouterPreAssignments`:

| Column | |
|---|---|
| `WorkflowRunId` | Which run this applies to. Pre-assignment is per run, not per definition — the next document does not inherit it. |
| `TaskDefinitionId` | Which step. |
| `ActorId` | Who gets it. |
| `BranchKey` | Optional. Null keeps the run's own org unit, which is the normal case. |
| Audit columns | As every other entity. |

**Unique on `(WorkflowRunId, TaskDefinitionId)`** — one standing answer per step per run.
Setting it again replaces it, which is what "go back in and change it" means.

Forks need no dimension here because they are out of scope by decision.

## Behaviour

**At task creation**, before the role resolver runs:

```
if a pre-assignment exists for (run, definition) and this task is not a fork branch:
    use it, and do not call the assignment resolver
otherwise:
    resolve as today
```

The resolver is skipped rather than consulted-and-overridden, because calling it and discarding
its answer would run host code — potentially a database lookup — for a result already decided.

**The pre-assignment is not consumed.** It stands until removed, so a step re-created by
rework goes to the same person. The row is the manager's standing instruction about this run,
not a one-shot token.

**Fork branches ignore it.** Out of scope by decision, and silently ignoring it would be
worse than refusing — see validation.

## API

```csharp
/// <summary>
/// Names who will handle a step before it is reached. Beats the step's assignment role when
/// the task is eventually created, and stands until removed — a step re-created by rework
/// goes to the same person.
///
/// <para>Gated as <see cref="WorkflowOperation.PreAssignTask"/>, which is deliberately its own
/// operation: naming somebody else's future work is a different act from doing your own, and a
/// host will want a different rule for it.</para>
///
/// <para><b>Reassignment deliberately is not.</b> <see cref="WorkflowOperation.ReassignTask"/>
/// sits with completing and cancelling, because handing an existing task to someone else is
/// ordinary work on work that already exists — anyone who may act on a task may pass it on. The
/// distinction being drawn here is between acting on real work and committing someone to work
/// that does not exist yet, not between doing a thing and delegating it.</para>
/// </summary>
Task<Result<Unit>> PreAssignAsync(
    int workflowRunId,
    int taskDefinitionId,
    WorkflowAssignment assignment,
    string actorId,
    CancellationToken ct = default);

/// <summary>Removes a standing pre-assignment. The step falls back to its role default.</summary>
Task<Result<Unit>> RemovePreAssignmentAsync(
    int workflowRunId,
    int taskDefinitionId,
    string actorId,
    CancellationToken ct = default);

/// <summary>
/// Every standing pre-assignment on a run — what a manager reopening a workflow needs in order
/// to see the whole thing and change it.
/// </summary>
Task<Result<IReadOnlyList<PreAssignmentSnapshot>>> GetPreAssignmentsForRunAsync(
    int workflowRunId, CancellationToken ct = default);

/// <summary>
/// Work an actor is promised but does not yet hold: pre-assignments on runs that are still
/// running, for steps whose task has not been created.
///
/// <para>Deliberately separate from <see cref="GetOpenTasksForActorAsync"/> rather than merged
/// into it. An inbox is work you can act on now; this is work you cannot. Merging them would
/// mean every existing caller suddenly showing rows with no task to open, and every one of
/// them would need to learn the difference. A host that wants one list concatenates two.</para>
/// </summary>
Task<Result<IReadOnlyList<UpcomingTaskSnapshot>>> GetUpcomingTasksForActorAsync(
    string actorId, CancellationToken ct = default);

/// <summary>A step somebody is promised, with enough to navigate to it.</summary>
public sealed record UpcomingTaskSnapshot(
    int WorkflowRunId,
    WorkflowSubject Subject,
    string WorkflowName,
    int TaskDefinitionId,
    string Label,
    string? BranchKey);
```

`WorkflowOperation` gains `PreAssignTask`. The enum is documented **append only** — a host may
have persisted these names — so it goes on the end.

## Validation

Refuse, rather than accept-and-ignore, when:

- **The definition does not belong to the run's version.** A pre-assignment for a step that
  cannot occur is a mistake worth reporting.
- **The definition is forkable.** Out of scope by decision, and a manager who pre-assigns a
  forkable step and sees nothing happen has been told nothing. Refusing says why.
- **The task already exists and is open.** That is a reassignment, and `ReassignTaskAsync` is
  the operation for it. Two ways to do one thing, with different permissions, is how they drift.

Permit, quietly:

- **A step already completed** — it may recur through rework, and refusing would make the
  manager's intent depend on where the run happens to be.
- **A step that turns out never to be reached**, because a conditional route went the other
  way. Nothing can know that in advance.

## Open questions

1. **Should a pre-assignment survive into a new version?** A run pins to a version, so this
   never arises for one run. But `CreateDraftVersionAsync` copies a definition and its task
   definitions get **new ids** — so pre-assignments do not follow, which is correct, and worth
   stating so nobody "fixes" it.

2. **Does the manager see pre-assignments in the builder, or only in a run view?** The builder
   edits definitions and knows nothing about runs; pre-assignment is per run. So it belongs in
   a run-oriented view, not in the builder — but the requirement says "go back into the
   workflow and see the whole thing", which sounds like the builder's graph. **Likely a third
   view: the definition graph annotated with a run's actual and promised assignments.** Nothing
   in the library prevents a host composing that today from `GetRunAsync`,
   `GetPreAssignmentsForRunAsync` and the definition; whether the library should offer it as
   one read is a question for after a host has drawn it.

3. **Notification.** Bob is promised work he cannot yet see in his inbox. Is he told at
   pre-assignment time, at creation time, or both? Creation already fires `TaskCreated`, so the
   second is free. The first would be a new event, and is only worth adding if a host wants it.

## Testing

- A pre-assigned step goes to that actor when created, **and the resolver is not called** —
  assert with a resolver that records invocations, since "the right answer for the wrong
  reason" is the likely defect.
- A pre-assigned step with a **role key** still goes to the pre-assigned actor, proving it
  beats the default rather than merely filling a gap.
- A pre-assigned step with **no role key** is assigned — the case the host seam could not reach
  at all, and the reason this is in the engine.
- **Rework**: reject back to a pre-assigned step; the re-created task goes to the same actor.
- **Removal**: after removal the step falls back to its role default.
- **Replacement**: pre-assigning twice leaves one row and the later actor.
- A **fork branch** ignores it — and pre-assigning a forkable definition is refused up front.
- `GetUpcomingTasksForActorAsync` returns promised steps, **excludes** ones whose task now
  exists (it is in the inbox instead, and showing both is a double count), and excludes runs
  that have finished.
- Authorization: an actor without `PreAssignTask` is refused, **and** an actor with it but
  without `CompleteTask` can still pre-assign — proving the permissions are genuinely separate
  rather than one implying the other.
