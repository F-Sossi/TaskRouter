# The runner and the inbox, for a host that is not the demo

**Status:** specification. Not yet built.
**Follows:** `2026-09-02-builder-over-http.md`, which did the same job for the builder.

---

## The gap

`WorkflowRunner.razor`, `WorkflowTestPane.razor` and `WorkflowInbox.razor` inject
`IWorkflowRunnerClient` and `IWorkflowInboxClient`. **The library ships no implementation of
either.** The only ones that exist are `DemoRunnerClient` (700 lines) and `DemoInboxClient`
(97 lines), in the sample.

So the components are unusable outside the demo without a host writing 800 lines first, and a
WebAssembly host cannot write them at all — they need a `DbContext`, which a browser has not
got. This is the same hole the builder was in before `2026-09-02`, and it has the same two
halves: **ship the implementation**, then **ship a transport**.

## What is actually host knowledge

Checked rather than assumed, because that check is what made the builder plan correct — its
858 lines moved into the library untouched because not one of them referenced a demo type.

`DemoRunnerClient` is *mostly* library-shaped: **24 of its 27 `db.` uses are TaskRouter's own
tables**, reachable through `IWorkflowDbContext`. Three are not. `DemoInboxClient` has exactly
one. All four are the same kind of question:

| Question | Where | Answered today by |
|---|---|---|
| What is this actor called? | runner, 4 sites | `IWorkflowActorResolver.GetDisplayNameAsync` |
| Who can I assign to? | runner reassign + delegate pickers | `db.People` |
| What org units exist? | runner fork picker | `db.Sections` |
| Which org unit is this actor in? | runner **and inbox** | `DemoOrg.AssignmentForAsync` |
| **What is this subject called, and where does it live?** | **inbox** | **`db.Documents`** |

Nothing else. Everything else the runner does is engine state.

> **The last row was missed on the first pass and is the one that changes the shape.** An
> initial grep of `DemoInboxClient` looked for the runner's three host calls and found one,
> which read as "96 of its 97 lines are library code". That was wrong: the inbox's *whole
> reason to exist* is the row it missed. Its own interface documents this — "the engine knows
> a run is about `ChangeRequest:42`; only the host knows that is 'CR-2026-0042, Pump room
> rewire' and that it lives at `/documents/42`." The runner's three-seam finding was checked
> with a wider pattern and stands.

## Decision: `IWorkflowActorResolver` grows into the seam

Those four are one responsibility — *the host's directory of people and org units* — and the
first of them is already an interface. So it takes the other three:

```csharp
public interface IWorkflowActorResolver
{
    Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct = default);

    /// <summary>Actors that may be assigned to, for the reassign and delegate pickers.</summary>
    Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default);

    /// <summary>Every org unit a fork could branch across. Not filtered — see below.</summary>
    Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(CancellationToken ct = default);

    /// <summary>An actor's org unit, as a complete assignment.</summary>
    Task<WorkflowAssignment> GetAssignmentForAsync(string? actorId, CancellationToken ct = default);
}
```

**Why this interface rather than a new one.** Spike finding 4 was that
`IWorkflowActorResolver` is registered by `AddActorResolver<T>()` and consumed by *nothing* in
the library — the conclusion recorded on 2026-08-29 was "give it a purpose or remove it before
publication rather than ship it as decoration". This is the purpose. A host registers one
thing for one concept, and the library's own runner client is the consumer that was missing.

**It is a breaking change** to anyone implementing the one-method version. That is free today
and is not free after the first publish, which is the argument for doing it now rather than
discovering the need later.

**The failure policy is the existing one.** Resolvers fail soft: a thrower is logged and
treated as no answer. A directory that is down should empty a picker, not take down a run
view. `GetAssignmentForAsync` returning `WorkflowAssignment.Unassigned` for an unknown actor
is a real answer and not an error — `DemoOrg` already documents why, and the assignment
resolver already treats a missing branch key as "keep the current assignment".

### The one design point worth stating

**`GetBranchOptionsAsync` takes no task id and does no filtering.** The demo's version takes
one and excludes org units already branched on, but that exclusion is two queries against
`WorkflowTasks` — *engine* knowledge, and the engine rejects a duplicate branch anyway. Split
at the seam instead:

- the **host** lists its org units, which is a one-line projection it cannot get wrong;
- the **library's** `EfWorkflowRunnerClient` removes the ones already taken.

The alternative obliges every host to reimplement a fork-manifest rule to make its picker
correct, and a host that gets it wrong gets an exception from the engine at fork time. The
`IWorkflowRunnerClient.GetBranchOptionsAsync(int taskId)` signature is unchanged — the
filtering simply moves behind it.

### `ActorOption` and `BranchOption` move to `TaskRouter.Core.Abstractions`

They are declared in `TaskRouter.Core.Runner` today, beside the runner client. A host seam
must not return types from a namespace named for a UI surface — a directory is not a runner
concept, and `Abstractions` is where `WorkflowAssignment` already lives. Same assembly, so the
change is a namespace and some `using` lines, and it is breaking in the same free way.

## Decision: subject resolution becomes its own seam

The directory answers questions about *people and org units*. What a subject is called is a
different concept and cannot be forced into the same interface, so it is a second one:

```csharp
/// <summary>
/// What the host's subjects are called and where they live. The engine stores an opaque
/// (type, id) pair and can say nothing more about it.
/// </summary>
public interface IWorkflowSubjectResolver
{
    Task<IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor>> ResolveAsync(
        IReadOnlyList<WorkflowSubject> subjects, CancellationToken ct = default);
}

/// <summary>A null field means the host could not resolve it.</summary>
public sealed record SubjectDescriptor(string? Label, string? Subtitle, string? Url);
```

**Batched, not one call per row.** `DemoInboxClient` deliberately issues one query for every
document in the inbox rather than one per row, and a per-subject signature would make N+1 the
default for every host that implements it. The batch is the whole point of the signature.

**A missing subject keeps its row.** `SubjectDescriptor` with a null `Label` falls back to the
raw key (`"ChangeRequest:42"`) and a null `Url` renders unclickable, which is what
`DemoInboxClient` already does and for the reason it already gives: an orphaned task is a
defect worth seeing, not worth hiding. A host returning no entry at all for a subject means
the same thing.

**`WorkflowSubject` is a valid dictionary key** — it implements `IEquatable<WorkflowSubject>`
with a matching `GetHashCode`. It is a mutable class rather than a record, so nothing on this
path may mutate one after it is used as a key; nothing does.

**Failure policy is the resolvers' — soft.** A subject resolver that throws should leave rows
labelled with their raw keys, not empty the inbox. This is the opposite of what
`DemoInboxClient` does with the *engine* call, and deliberately: an empty inbox renders as
"Nothing is waiting on you", which is a lie, so an engine failure must surface. Unresolved
labels are visibly degraded rather than false.

## What gets built

| | |
|---|---|
| `EfWorkflowRunnerClient` | `DemoRunnerClient` less its three host calls, in `TaskRouter.EntityFrameworkCore` |
| `EfWorkflowInboxClient` | `DemoInboxClient` with both host calls behind seams |
| `RunnerEndpoints` / `InboxEndpoints` | routes, in the style of `BuilderEndpoints` |
| `HttpWorkflowRunnerClient` / `HttpWorkflowInboxClient` | `TaskRouter.Blazor.Http` |
| `AddWorkflowRunner()` | registration, beside `AddWorkflowBuilder()` |

`GET /workflow/inbox` **already exists** in `InboxEndpoints.cs` but is *not* enough: it
returns the engine's raw rows, with subjects unresolved. The inbox component needs
`InboxItem`, so a second route returning that is required. Worth keeping both — the raw one is
the honest engine read, and a host may want it.

## Carried forward from the builder, because it cost a session

Every minimal-API handler taking a library interface **must** mark it `[FromServices]`.
Without it an unregistered type is inferred as a *body* parameter, a GET may not have one, and
the host fails to start with an error naming neither the service nor the route. There is a
regression test for the builder's half; the runner needs its own.

## Open, and to be settled while building

1. **Does the demo keep its clients?** The builder's demo client was deleted outright once the
   library shipped one. The runner's and inbox's cannot be deleted wholesale — something must
   still implement the two seams — so the 800 lines should shrink to a `DemoDirectory`
   (`IWorkflowActorResolver`, four short methods over `db.People` and `db.Sections`) and a
   `DemoSubjectResolver` (`IWorkflowSubjectResolver`, one batched query over `db.Documents`).
   **If they do not shrink to roughly that, the seams are wrong and this spec is wrong** —
   that is the check, and it is the same one that validated the builder plan.
2. **`StartTestRunAsync`'s subject type.** `DemoRunnerClient` declares
   `public const string TestSubjectType = "WorkflowTest"` and the comment says nothing in the
   engine treats it specially. Moving the client into the library makes that constant part of
   the public API, so decide whether it is a library constant or a host's choice.
3. **Authorization.** The runner routes mutate runs, so unlike the builder they map onto
   operations the engine's own policy already gates. Confirm the endpoint layer adds nothing
   and defers, rather than inventing a second gate.

## Testing

The builder's shape applies, and the second item is the one that matters:

- **Serialization first**, before any endpoint is built on the assumption. `RunDetail`,
  `RunTaskView` and `ForkInfo` are deeper than anything the builder sends, and `RunTaskView`
  has 25 positional parameters — a wire format that silently drops one is the hazard.
- **Against a real server, not a mocked `HttpMessageHandler`.** Every failure these clients can
  have is a disagreement with the server, and a mock agrees with whatever the test author
  believed the routes were.
- **A host that never adopted the runner still starts** — the `[FromServices]` regression.
- **Both resolvers fail soft**: a directory that throws empties a picker without failing the
  run view; a subject resolver that throws leaves inbox rows labelled with raw keys. And
  **the engine call in the inbox does not** — assert that a failing engine read surfaces,
  because an empty inbox reads as "nothing is waiting on you", which is the opposite of the
  truth.
- **The branch filter is the library's**: fork a task, then assert the remaining options
  exclude the taken branch — with a directory that returns everything, so the test fails if
  the filtering is ever pushed back onto the host.
