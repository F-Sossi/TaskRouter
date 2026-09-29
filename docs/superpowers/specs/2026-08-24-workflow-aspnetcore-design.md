# `Workflow.AspNetCore`

**Date:** 2026-08-24
**Status:** Implemented 2026-08-25 — see `docs/superpowers/plans/2026-08-24-workflow-aspnetcore.md`

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.

## What this is for

The library can be driven, but only from inside a process. A host that wants HTTP writes
the HTTP itself. `samples/DemoDocuments.Server` does, in about 120 lines of `Program.cs`,
and every one of those lines is a decision someone else will have to make again:

- how a failed `Result<T>` becomes a status code,
- where the actor id comes from,
- what stops a caller putting `workflow:system` in a request body.

The demo answers all three, and answers the last one with a guard that every mutating
route has to remember to call. That is the shape of a thing that wants to be a package.

Authorization made this worth doing now. Before it, an endpoint layer would have been a
liability: thin, generic routes over an engine that gated nothing, handed to hosts who
would reasonably assume the package had thought about access. The engine now enforces at
the only chokepoint every caller passes through, so a ready-made endpoint can be thin
*without* being unsafe. The gate does not move to HTTP; HTTP just stops being the only
thing between a caller and the engine.

## What it is not

**Not a replacement for the demo's routes.** `/documenttasks` is host-domain-shaped —
document ids and section codes, translated by `IDocumentTaskService` into subjects and
branch keys — and it mirrors the legacy `DocumentTasksController` route for route on
purpose. This package is engine-shaped: task ids, branch keys, `WorkflowSubject`. Both
are correct; they answer different questions. The demo stays as the worked example of a
host that wants its own vocabulary, and does not adopt this package.

That means the demo does not validate it. Integration tests against a minimal host do —
see **Testing**.

## Deliberately not in scope

- **Authentication.** The package reads an identity; it never establishes one. A host
  that maps these endpoints without an authentication scheme gets 401 on every mutation,
  which is the correct outcome rather than a gap.
- **Read authorization.** Reads are ungated in the engine, deliberately, and this layer
  does not invent a second policy that the in-process path would not share. STATE.md
  records the reasoning; nothing here changes it.
- **The builder and runner seams.** `IWorkflowBuilderClient`, `IWorkflowRunnerClient` and
  `IWorkflowInboxClient` are host interfaces that resolve engine data into host
  vocabulary — document titles, URLs. A generic package cannot implement them and should
  not pretend to.
- **OpenAPI documents, versioning, rate limits, CORS.** `MapWorkflowEndpoints` returns
  the `RouteGroupBuilder`, so the host applies its own.

## Surface

```csharp
services.AddWorkflowEndpoints();          // the two seams' defaults
app.MapWorkflowEndpoints("/workflow");    // returns RouteGroupBuilder
```

Returning the group is the whole extensibility story. A host writes

```csharp
app.MapWorkflowEndpoints().RequireAuthorization("WorkflowUser").RequireRateLimiting("api");
```

and the package needs to know nothing about schemes, policies or limiter names.

One route per `IWorkflowEngine` member, no more:

| Route | Engine method |
|---|---|
| `POST /runs` | `StartRunAsync` |
| `POST /runs/version` | `StartRunOnVersionAsync` |
| `GET /runs/{runId}` | `GetRunAsync` |
| `GET /runs` | `GetRunsForSubjectAsync` |
| `GET /runs/{runId}/sub-workflow-instances` | `GetSubWorkflowInstancesAsync` |
| `GET /runs/{runId}/fork-manifests` | `GetForkManifestsForRunAsync` |
| `POST /tasks/{taskId}/complete` | `CompleteTaskAsync` |
| `POST /tasks/{taskId}/complete-selective` | `CompleteWithSelectiveRejectionAsync` |
| `POST /tasks/{taskId}/cancel` | `CancelTaskAsync` |
| `POST /tasks/{taskId}/reassign` | `ReassignTaskAsync` |
| `PUT /tasks/{taskId}/notes` | `UpdateTaskNotesAsync` |
| `POST /tasks/{taskId}/fork` | `ForkTaskAsync` |
| `POST /tasks/{taskId}/adhoc` | `AddAdHocTaskAsync` |
| `POST /tasks/{taskId}/sub-workflows` | `StartSubWorkflowAsync` |
| `GET /tasks/{taskId}/sub-workflow-options` | `GetSubWorkflowOptionsAsync` |
| `GET /tasks/{taskId}/outcomes` | `GetValidOutcomesAsync` |
| `GET /tasks/{taskId}/logs` | `GetTaskLogsAsync` |
| `GET /tasks/{taskId}/children` | `GetChildTasksAsync` |
| `GET /tasks/{taskId}/fork-context` | `GetForkContextAsync` |
| `POST /forks/{forkGroupId:guid}/branches` | `AddBranchToForkAsync` |
| `GET /fork-manifests/{manifestId}` | `GetForkManifestAsync` |
| `POST /sub-workflow-instances/{instanceId}/cancel` | `CancelSubWorkflowAsync` |
| `GET /inbox` | `GetOpenTasksForActorAsync` |

Responses are the engine's existing snapshot types, serialized directly. No DTO layer:
`WorkflowRunSnapshot`, `WorkflowTaskSnapshot`, `ForkManifestView` and the rest already
*are* the read model, shaped for a UI, and a parallel set of records would be two things
to keep in step for no gain.

Request bodies are records defined in this package, one per mutating route. They differ
from the demo's in exactly one respect, and it is the important one: **none of them has
an `ActorId`.**

One binding wrinkle worth knowing before implementation: `WorkflowSubject` is a class with
a parameterless constructor and value equality, not a record. It deserializes from a JSON
body without help, but `GET /runs` identifies its subject in the query string, and minimal
APIs will not bind a complex type from a query. That route takes `subjectType` and
`subjectId` as separate string parameters and constructs the subject in the handler.

## Where the actor comes from

```csharp
public interface IWorkflowEndpointActorAccessor
{
    /// Null means the request carries no usable actor. Endpoints return 401.
    string? GetActorId(HttpContext context);
}
```

The default reads a claim — `ClaimTypes.NameIdentifier` unless configured otherwise — off
`context.User`. It never touches the body, the query string or a header.

It reads that claim only from **authenticated** identities, and gates on the same set it
reads from. A `ClaimsPrincipal` can carry several identities, and `context.User.Identity`
is only the primary one while `context.User.FindFirst` searches all of them — so gating on
the first and reading with the second returns claims that no authenticated identity ever
asserted. Enrichment middleware and multi-scheme hosts both produce such principals.

This is the design's load-bearing choice. The demo takes the actor from the request body
because it has no authentication, which makes every caller trivially able to act as
anyone; the demo is a sample and says so. A package cannot ship that. Taking the actor
from the authenticated principal means a caller can assert *what* to do but never *who is
doing it*, and the request contract makes it structural rather than a rule to follow: a
route handler that wanted to trust a body-supplied actor would have to add the property
first.

### The reserved-id guard shrinks to one place

`WorkflowActors.System` short-circuits `AuthorizeAsync` before the policy is consulted, so
`workflow:system` is a root switch. The demo guards it on every mutating route with
`RejectReservedActorId`, and "every route remembers" is precisely the property that fails
the day someone adds route twenty-four.

Here the id can no longer arrive in a body, but it could still arrive in a claim — a host
is free to mint whatever claims it likes, including a careless one. So the check stays,
once, at the single path every route's actor comes through: if the resolved id starts with
`workflow:`, the request is refused with **403** and the event logged. Unbypassable by a
route that forgets, and pinned from the outside by a test that registers a rogue accessor.

403 rather than 401 because the caller *is* authenticated — they simply may not be that
actor — and because a 401 in a cookie-auth host can have `StatusCodePages` bounce an
already-logged-in user to a login page. The missing-actor case stays 401: there, nobody
has said who this is yet.

The comparison is **case-insensitive**, which is not the obvious choice and is worth the
sentence. The engine's own gate compares actor ids with C# `==`, so an ordinal guard would
still stop a root bypass. But every SQL-mediated view of the actor — inbox matching, the
`CreatorId` and `ModifierId` audit columns — compares under the database collation, which
is case-insensitive by default. `WORKFLOW:SYSTEM` would therefore pass an ordinal guard and
then be treated as the system actor by exactly the audit trail the prefix exists to keep
legible. It is also the convention everywhere else in the repo: `TriggerRegistry` and
`OutboxProcessor` both match identifiers with `OrdinalIgnoreCase`.

## Where branch keys come from

`GetOpenTasksForActorAsync` takes `branchKeys` alongside the actor, and its documentation
is explicit that these are host knowledge: the engine has no user model and no directory.

```csharp
public interface IWorkflowEndpointBranchKeyResolver
{
    Task<IReadOnlyList<string>> GetBranchKeysAsync(
        HttpContext context, string actorId, CancellationToken ct);
}
```

The endpoint takes neither the actor nor the keys from the caller. Accepting
`?branchKeys=` would let anyone enumerate another org unit's unclaimed work by guessing —
an inbox that answers questions about other people's inboxes.

With no resolver registered the default returns empty, and the inbox degrades to
directly-assigned tasks only. That is the documented predicate behaviour rather than a
failure: both halves need something to match on. It will be commented as such at the
default, because an empty inbox for a real user is otherwise a puzzling first experience.

`GET /inbox` returns `InboxTaskSnapshot`, the engine's shape — **not** the
`IWorkflowInboxClient.InboxItem` the demo's UI consumes. `InboxItem` carries a
`SubjectLabel` and a `SubjectUrl`, and resolving `ChangeRequest:42` into "CR-2026-0042, Pump room
rewire" at `/documents/42` is host knowledge this package does not have.

## Errors

Every engine refusal today is an `InvalidOperationException` except
`WorkflowAuthorizationException`. So `"Task 99 not found."` and `"Task 99 is already
completed."` are the same type carrying different prose, and an endpoint layer that wants
to distinguish them has only the message to go on. Mapping on message text would make
rewording a sentence a breaking API change.

### `WorkflowNotFoundException`

New in `Workflow.Core/Abstractions`, beside `WorkflowAuthorizationException` and modelled
on it:

```csharp
public sealed class WorkflowNotFoundException(string entityKind, string id)
    : InvalidOperationException($"{entityKind} {id} not found.")
{
    public string EntityKind { get; } = entityKind;
    public string Id { get; } = id;
}
```

Deriving from `InvalidOperationException` is the same trade the authorization exception
made, for the same reason: the engine's idiom is to throw inside a `Try.RunAsync` body, so
deriving leaves every existing call site and every existing test working untouched, while
a host that wants 404 rather than 400 can catch the type. `Id` is a string because a fork
group is identified by a `Guid` and everything else by an `int`.

It replaces thirteen throws:

| File | What |
|---|---|
| `WorkflowEngine.cs:472` | task (`LoadTaskAsync`, the shared loader) |
| `WorkflowEngine.cs:123` | workflow version |
| `WorkflowEngine.cs:341` | fork group |
| `WorkflowEngine.Reads.cs:56, 139` | task |
| `WorkflowEngine.Reads.cs:181` | fork manifest |
| `WorkflowEngine.Reads.cs:243` | fork group (`AddBranchToForkAsync`) |
| `WorkflowEngine.Fork.cs:53` | convergence task definition |
| `WorkflowEngine.Fork.cs:173` | convergence task |
| `WorkflowEngine.Queries.cs:121` | task definition |
| `WorkflowEngine.Queries.cs:163` | run |
| `WorkflowEngine.SubWorkflows.cs:114` | sub-workflow instance |
| `WorkflowEngine.SubWorkflows.cs:449` | task |

The two fork-group sites were found during review, not by the survey that produced the
other eleven: they report the same absence in different prose —
`"No fork manifest for group {id}."` — so a grep for the `"… not found."` shape missed
them. Their messages change, uniquely in this conversion; nothing outside the engine read
them. The lesson is that the rule has to be applied by reading each lookup, not by
matching message text.

The rule is narrow and mechanical: **only "no row with this id" converts.**
`"No published version for workflow definition 3."` stays an `InvalidOperationException` —
the definition exists, it is in a state the caller cannot use. Same for
`"'X' is a sub-workflow. Attach it to a task…"`. Drawing the line at row-lookup keeps the
conversion a search-and-replace rather than a judgement call at each site.

This is a change to a merged, green subsystem, so it lands as its own commit with its own
tests, before any endpoint code exists.

### The mapping

All failures render as `ProblemDetails`.

| Exception | Status | Detail |
|---|---|---|
| `WorkflowAuthorizationException` | 403 | the `Reason`, plus `operation` as an extension |
| `WorkflowNotFoundException` | 404 | the message |
| other `InvalidOperationException` | 409 | the message |
| anything else | 500 | **generic text; the message is logged, not returned** |

409 rather than 400 for the remainder: once denial, absence and bad input are peeled off,
what is left is overwhelmingly "the workflow is not in a state where this is allowed" —
already completed, cancelled, superseded by a fork.

The 400 arm was not in the first draft of this design, and the reason it is here now is
worth recording. That draft argued "a malformed body never reaches the handler; ASP.NET has
already answered 400." That is true of malformed *JSON* and false of well-formed JSON the
engine refuses on its merits. Several such refusals are reachable from mapped routes —
`"Duplicate branch keys are not allowed."` and `"A fork needs at least two branches."`
(`ForkTaskAsync`), `"At least one branch key is required."` (`AddBranchToForkAsync`), and an
`ArgumentNullException` when a body omits `rejectedBranchKeys` entirely. All are
`ArgumentException` or derive from it, so without this arm they answered 500 with the
message withheld: the caller's mistake reported as a server fault, with the one sentence
that would let them fix it deliberately suppressed.

The 500 arm is the only place the package withholds a message, and deliberately: an
unexpected exception is the one class whose text was never written with a caller in mind.

With the 400 arm in place, no caller-reachable engine path is known to land on it — every
refusal surveyed is an `ArgumentException` or an `InvalidOperationException`. That makes it
genuinely hard to test, so its test asserts only that the internal message does not leak,
rather than contriving a way to trigger it.

### On the existence oracle

STATE.md records that a denied actor can already distinguish "not authorized" from "not
found", because `LoadTaskAsync` throws before `AuthorizeAsync` runs, and accepts it: the
policy needs the task loaded to decide anything, and refusing to say *why* a call failed
is worse. 403-versus-404 makes that distinction more legible without widening it. Nothing
new leaks; the same fact is stated in a status code rather than a sentence.

## Testing

`TestHost` builds a raw `ServiceProvider`, not a web host, so HTTP tests need something
new: an `EndpointTestHost` that reuses the existing per-class SQL Server database creation
and mounts `MapWorkflowEndpoints` on a minimal `WebApplication` via
`Microsoft.AspNetCore.TestHost`.

It deliberately does **not** boot the demo's `Program`. The demo would drag its seeder, its
domain and its own routes into a test whose subject is a generic package, and the point of
the first design decision was that this layer stands on its own.

Coverage:

- **Each status-code arm.** A denial from a stub policy gives 403 with the reason; a
  missing id gives 404; completing an already-completed task gives 409; a stub seam that
  throws something unexpected gives 500 *and no message*.
- **The actor seam.** No principal → 401. A principal with no matching claim → 401. A
  principal whose claim is `workflow:system` → 403, in any casing, and — the assertion that
  matters — the engine was never called. A **rogue accessor** registered by the host is
  still refused, which is the guard's whole reason for living outside the accessor.
- **The inbox seam.** With a resolver, unclaimed branch work appears; with none, only
  directly-assigned tasks do; `?branchKeys=` in the query string changes nothing.
- **One happy path per mutating route**, asserting the engine actually moved — the route
  table is where a copy-paste slip hides, and only a call per route finds it.

The existing 261 tests must still pass untouched. If the exception change breaks one, that
is a signal the conversion rule was drawn wrongly, not licence to edit the test.

## What this leaves for later

- **OpenAPI.** The routes are minimal-API endpoints, so `WithOpenApi` metadata could be
  added later without moving anything. Not now: the shape should settle first.
- **Read authorization.** If a host ever needs run-level visibility rules, this layer is
  where the pressure will show up first — every read becomes a page someone can request
  directly. Revisit there, not here.
- **Trigger actions running as the triggering actor.** Unchanged and still documented on
  the seam. An endpoint layer neither helps nor hurts it.
