# Authorization

**Date:** 2026-08-23
**Status:** Implemented 2026-08-23 — see `docs/superpowers/plans/2026-08-23-authorization.md`

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.

## What this is for

Nothing stops anyone from doing anything.

`RunTaskView.CanComplete` is computed by the host and consulted by the runner, which
hides a button when it is false. That is the whole of it. The engine will complete any
task for any actor id that asks, and a caller who reaches `IWorkflowEngine` directly —
another page, a background job, a host that maps its own routes, which the demo does —
never passes the button at all.

So the current state is not "authorization is the host's job". It is "authorization is
advisory, and the advice is only taken by one component". Every mutating engine method
already accepts an `actorId`:

| Method | Takes `actorId` | Checks it |
|---|---|---|
| `StartRunAsync` | yes | no |
| `StartRunOnVersionAsync` | yes | no |
| `CompleteTaskAsync` | yes | no |
| `CompleteWithSelectiveRejectionAsync` | yes | no |
| `CancelTaskAsync` | yes | no |
| `ReassignTaskAsync` | yes | no |
| `ForkTaskAsync` | yes | no |
| `AddBranchToForkAsync` | yes | no |
| `AddAdHocTaskAsync` | yes | no |
| `StartSubWorkflowAsync` | yes | no |
| `CancelSubWorkflowAsync` | yes | no |
| `UpdateTaskNotesAsync` | yes | no |

The engine already knows who is asking. It has simply never asked whether they may.

Twelve mutating methods, and they map to eleven operations: `StartRunAsync` and
`StartRunOnVersionAsync` are the same act on a different version and gate as one.

None of the five design documents in `Dev/` covers authorization, so unlike
sub-workflows, triggers and deadlines, there is no prior reasoning to inherit and no
the original engine's finding driving the shape. This one is designed from the codebase's own
conventions.

## The decision this rests on

**The engine enforces; the host decides.** The rule itself is host knowledge — who may
cancel a task depends on an org model the library does not have, exactly as escalation
depends on one — but *asking* the question has to be the engine's job, because the
engine owns the only chokepoint every caller passes through.

This is a deliberate departure from how `CanComplete` was framed. That comment says the
engine "has no opinion on it", and treats the question and the answer as one thing. They
are not: the engine can hold no opinion about the answer while still insisting the
question be asked. Escalation drew the same line — the engine raises `TaskOverdue` and
has no notion of a supervisor — and this is that pattern applied to a gate rather than
an event.

The alternative, leaving enforcement in the host, was rejected because it leaves the
hole exactly as it is today. A host that wants to be safe already can be; what it
cannot do is be safe *by default*, or be sure it covered every method.

## Deliberately not in scope

- **Read gating.** `GetRunAsync`, `GetTaskLogsAsync`, `GetForkManifestAsync` and the
  rest stay open. Reads are how a host builds the page it then decides whether to show,
  the inbox already answers a similar question through its own predicate, and gating
  reads would put a policy call on every per-page load. Recorded in STATE.md as a known
  gap rather than left unsaid.
- **A role or permission model.** No `WorkflowRole`, no permission strings, no
  role-assignment tables. The policy is a function, not a schema.
- **Persisted denials.** A refused operation is not written anywhere. The task log
  records what happened, and nothing happened.
- **Delegation or "act on behalf of".** One actor id per call, as now.
- **Authentication.** The actor id is whatever the host says it is. A host that lets an
  end user choose their own actor id has already lost, and no gate here recovers that.

## The seam

New in `src/Workflow.Core/Abstractions/Authorization.cs`, beside the other host seams in
`HostServices.cs` — its own file because that one is already seven interfaces long and this
is one cohesive feature:

```csharp
public enum WorkflowOperation
{
    StartRun,
    CompleteTask,
    CompleteWithSelectiveRejection,
    CancelTask,
    ReassignTask,
    ForkTask,
    AddBranchToFork,
    AddAdHocTask,
    StartSubWorkflow,
    CancelSubWorkflow,
    UpdateTaskNotes,
}

public sealed record WorkflowAuthorizationContext(
    string ActorId,
    WorkflowTaskSnapshot? Task,
    WorkflowSubject? Subject,
    int? WorkflowDefinitionId);

public sealed record WorkflowAuthorizationResult(bool IsAllowed, string? Reason)
{
    public static readonly WorkflowAuthorizationResult Allowed = new(true, null);

    public static WorkflowAuthorizationResult Denied(string reason) => new(false, reason);
}

public interface IWorkflowAuthorizationPolicy
{
    Task<WorkflowAuthorizationResult> EvaluateAsync(
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        CancellationToken ct = default);
}
```

One method taking an operation enum, not a method per operation. Adding a gated
operation is then an enum member rather than a breaking change to every implementation
— the same reasoning that made the trigger seam take a `WorkflowEventKind` instead of
growing a method per event.

`Task` is null exactly when the operation is `StartRun`, which has no task yet; `Subject`
and `WorkflowDefinitionId` are set exactly then and null otherwise. The record carries
both rather than splitting into two context types because a host switching on the
operation already knows which it is holding, and two types would mean two interface
methods.

`CompleteWithSelectiveRejection` is its own member rather than folding into
`CompleteTask`. It completes a convergence task *and* sends branches back for rework,
which a host may reasonably gate as a management act. The demo treats the two alike; the
distinction costs nothing and cannot be recovered later without a breaking change.

`Reason` exists so a denial can say why. It reaches the caller in the exception message
and therefore in the `Result` error, because a workflow UI telling somebody "you cannot
do this" without saying why is the kind of thing that generates support tickets. This is
an internal line-of-business tool; the org chart it would leak is on the wall.

## Failure policy: fail closed

**A policy that throws denies, and logs.**

This is the opposite of the rule the neighbouring seams document.
`IWorkflowAssignmentResolver` and `IWorkflowDueDateResolver` both say a resolver that
throws is logged and treated as null, never fatal — and that is right for them, because
the fallback is a lesser answer: an unrouted assignment, or no deadline. Work continues
in a diminished state that somebody can see and fix.

For a gate there is no lesser answer. The fallback is *no gate*, and a policy that
throws under load would silently open every operation to everybody at exactly the moment
nobody is watching. So this seam fails closed, and its doc comment says explicitly that
it differs from the two interfaces above it, because a reader who has just read those
will otherwise assume the convention holds.

## Default: allow, and say so

`AddWorkflowEngine()` registers a sealed `AllowAllAuthorizationPolicy` through
`TryAddScoped` — scoped, matching `NullAssignmentResolver` and `NullDueDateResolver`
beside it, and because a real policy will want the request's `DbContext` — so a host that
registers its own wins and a host that registers none behaves exactly as the library does
today. That is what keeps the existing 239 tests
green without edits, and what stops this being a breaking change for anyone already
consuming the package.

It is a named class rather than a null check at the call site: "this system has no
authorization" should be a greppable object, not an absence. It logs once, on first use,
that no policy is registered and every operation is permitted — not at startup, since a
startup log would fire even for a host that goes on to register a real policy through the
builder — and an unauthorized system is a legitimate configuration, but it should never be
a silent one.

**`WorkflowActors.System` bypasses the gate**, short-circuited in the engine before the
policy is consulted. The deadline sweeper and the trigger dispatcher act under that id.
If each host had to remember to allow it, a host that forgot would break its own sweep,
and the failure would surface as tasks mysteriously not escalating rather than as an
authorization error. The bypass belongs in the one place that knows the id is the
engine's own.

## Where the gate runs

One private helper on `WorkflowEngine`:

```csharp
private async Task AuthorizeAsync(
    WorkflowOperation operation,
    WorkflowTask? task,
    string actorId,
    CancellationToken ct)
```

called inside each mutating method's existing `Try.RunAsync(() => InTransactionAsync(…))`
body, immediately after the task is loaded and **before any validation**:

```csharp
var task = await LoadTaskAsync(taskId, token).ConfigureAwait(false);
await AuthorizeAsync(WorkflowOperation.CompleteTask, task, actorId, token).ConfigureAwait(false);
await ValidateCompletableAsync(task, outcomeKey, token).ConfigureAwait(false);
```

Three properties follow from that placement, and each is pinned by a test:

- **Before validation**, so a denied actor learns nothing about task state. Told "task 42
  is already completed", they have learned that task 42 exists and what happened to it.
- **Before mutation**, so a denial writes nothing — no status change, no log row, no
  trigger fired.
- **Inside the transaction**, so even if that reasoning is wrong somewhere, the rollback
  covers it.

The two `StartRun` methods have no task to load, so they authorize on the subject and
definition id as their first act inside the transaction, before the definition is
resolved or a version pinned. `StartRunOnVersionAsync` authorizes with the same
`StartRun` operation and a null `Task`; the version it targets is not part of the
context, because whether a run may start is not a question about which version it
starts on.

On denial it throws `WorkflowAuthorizationException`, which **derives from
`InvalidOperationException`**. The engine's existing idiom is to throw
`InvalidOperationException` inside a `Try.RunAsync` body and let it become a failed
`Result`; deriving keeps every one of those paths working untouched, while a host that
wants to map a denial to HTTP 403 rather than 400 can catch the specific type. It
carries the operation, the actor id and the reason as properties, so a host reading it
does not have to parse the message.

## Demo host

`DemoAuthorizationPolicy` implements the split the runner needs, walking the same org
chain `DemoEscalationTrigger` already walks — `Person.SectionId` → `Section.Code`
(the engine's branch key) → `Section.SectionLeadActorId` → `Division.DivisionHeadActorId`:

| Operation | Who may |
|---|---|
| `CompleteTask`, `CompleteWithSelectiveRejection`, `UpdateTaskNotes`, `AddAdHocTask`, `StartSubWorkflow` | the assignee, or any member of the section when the task is unclaimed |
| `CancelTask`, `ReassignTask`, `ForkTask`, `AddBranchToFork`, `CancelSubWorkflow` | the section's Section Lead, or the branch head above it |
| `StartRun` | any known person |
| anything, unknown actor | nobody |

"Unclaimed" means the task's `AssignedBranchKey` matches the actor's section code and
`AssignedToActorId` is null — the same shape as the inbox's unclaimed half, deliberately,
so the demo cannot show somebody work in their inbox that it then refuses to let them do.

The split earns the operation enum. A demo where every operation answered identically
would suggest the enum was unnecessary, and would teach the next host to write a policy
that ignores its first argument.

## `CanComplete` stops being hand-written

`RunTaskView.CanComplete` stays host-computed — it is the host's seam and the runner
must not acquire an opinion — but `DemoRunnerClient` derives it by asking the policy
rather than hand-writing `Status is NotStarted or InProgress`. The rule then lives in one
place, and the button and the gate cannot disagree.

The status check does not disappear; it moves. A completed task is not *authorized*
differently, it is simply not actionable, so `CanComplete` stays the conjunction of the
two:

```csharp
task.Status is WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress
&& (await policy.EvaluateAsync(WorkflowOperation.CompleteTask, ctx, ct)).IsAllowed
```

This costs one policy call per task in the run detail's list, where the projection is
currently synchronous and will become a loop. In-process, against the small lists a
single run produces, that is acceptable; a batching seam would be speculative and is not
built. The N+1 is noted in the code so the next person meets it as a known trade rather
than a bug.

Two doc comments assert that the engine has no opinion on authorization —
`src/Workflow.Core/Runner/IWorkflowRunnerClient.cs:152` and
`samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs:22`. Both become untrue and are
corrected as part of this work. The comment at `IWorkflowRunnerClient.cs:181`, which
distinguishes `CanComplete` from `BlockingSubWorkflowCount`, stays accurate and is left
alone.

## Testing

A new `tests/Workflow.Tests/AuthorizationTests.cs`, against SQL Server like the rest:

**The seam**

- With no policy registered, every operation still succeeds — the guard on the existing
  suite, asserted directly rather than inferred from the other 239 passing.
- A denying policy blocks `CompleteTaskAsync`, and the task is byte-for-byte unchanged:
  status, outcome, modifier, log rows, and no trigger execution.
- The denial reason reaches the `Result` error message.
- A denied actor on an *already completed* task gets the authorization error, not
  "already completed" — pins the ordering, and would fail if the gate drifted below
  validation.
- A policy that throws denies rather than allows.
- `WorkflowActors.System` bypasses the policy, including a policy that denies everything.
- `WorkflowAuthorizationException` is catchable as itself and as
  `InvalidOperationException`.

**Coverage**

- Table-driven over all **twelve mutating methods**, not the eleven operations: a
  deny-everything policy makes every one of them fail. Driving it from the methods is
  the point — an operation-driven table would pass while `StartRunOnVersionAsync` sat
  ungated, since `StartRunAsync` already covers `StartRun`. This is the test that fails
  the day someone adds a mutating method and forgets the gate, which no hand-written
  per-method test would.

**The demo policy**

- The assignee may complete; a stranger may not.
- A section member may complete unclaimed work in their section; a member of another
  section may not.
- A non-Section Lead may not cancel; the section's Section Lead may; the branch head above may.
- An unknown actor is refused a `StartRun`.
- `DemoRunnerClient` reports `CanComplete: false` for exactly the actors the policy
  denies — the assertion that the button and the gate agree.

## What this leaves for later

- **Read gating**, as above.
- **A policy that can explain a whole task at once.** If a UI ever needs "which of these
  eleven may I do", it will want one call rather than eleven. The enum shape allows a
  batch overload to be added later without breaking the single-operation one.
- **Caching.** Each call re-queries the demo org. Fine in-process; a host with a remote
  directory would want memoization per request, and the seam does not prevent it.
