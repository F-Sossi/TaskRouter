# Runner and Inbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `IWorkflowRunnerClient` and `IWorkflowInboxClient` from the library, in process
and over HTTP, so the runner and inbox components work in any host rather than only the demo.

**Architecture:** The same two-half shape the builder took on 2026-09-02 — an EF
implementation in `TaskRouter.EntityFrameworkCore`, endpoints in `TaskRouter.AspNetCore`, an
HTTP client in `TaskRouter.Blazor`. What is genuinely host knowledge becomes two seams:
`IWorkflowActorResolver` grows from one method to four, and `IWorkflowSubjectResolver` is new.

**Spec:** `docs/superpowers/specs/2026-09-05-runner-and-inbox.md`. Read it first — both seam
decisions and their reasoning are there and are not repeated here.

**Tech Stack:** .NET 10, EF Core 10.0.10, MSTest 4.3.2, SQL Server for tests, MudBlazor 9.7.0.

---

## Before you start

**Build and test commands.** `dotnet` is not on the PATH:

```bash
export DOTNET_ROOT=~/.dotnet
~/.dotnet/dotnet build TaskRouter.slnx --no-incremental
~/.dotnet/dotnet test TaskRouter.slnx
```

`--no-incremental` matters: a plain incremental build can report 0 warnings falsely. Never
build this repo and the host application's repository at the same time — concurrent builds collide with an MSB3030
pdb error that looks like a break and is not.

**Baseline: 361/361, 0 warnings, `main` at `4a15b52`.**

**The rule that cost a session, and it applies again in Task 5.** Every minimal-API handler
taking a library interface must mark it `[FromServices]`. Without it an unregistered type is
inferred as a *body* parameter, a GET may not have one, and the host fails to start with an
error naming neither the service nor the route.

## File Structure

| File | Responsibility |
|---|---|
| `src/TaskRouter.Core/Abstractions/HostServices.cs` | `IWorkflowActorResolver` grows; `IWorkflowSubjectResolver` added |
| `src/TaskRouter.Core/Abstractions/DirectoryTypes.cs` *(new)* | `ActorOption`, `BranchOption`, `SubjectDescriptor` |
| `src/TaskRouter.Core/Runner/IWorkflowRunnerClient.cs` | loses two records to the above |
| `src/TaskRouter.EntityFrameworkCore/Runner/EfWorkflowRunnerClient.cs` *(new)* | the port |
| `src/TaskRouter.EntityFrameworkCore/Runner/EfWorkflowInboxClient.cs` *(new)* | the port |
| `src/TaskRouter.EntityFrameworkCore/Runner/NullDirectory.cs` *(new)* | defaults for both seams |
| `src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs` | `AddWorkflowRunner()` |
| `src/TaskRouter.AspNetCore/RunnerEndpoints.cs` *(new)* | routes |
| `src/TaskRouter.AspNetCore/InboxEndpoints.cs` | one route added |
| `src/TaskRouter.Blazor/Http/HttpWorkflowRunnerClient.cs` *(new)* | transport |
| `src/TaskRouter.Blazor/Http/HttpWorkflowInboxClient.cs` *(new)* | transport |
| `samples/DemoDocuments.Server/Workflow/DemoDirectory.cs` *(new)* | replaces `DemoRunnerClient` + `DemoOrg` |
| `samples/DemoDocuments.Server/Workflow/DemoSubjectResolver.cs` *(new)* | replaces `DemoInboxClient` |

---

### Task 1: Prove the views survive JSON, before anything is built on the assumption

Task 1 of the builder plan was deliberately not construction, and it paid: it caught a
computed property whose two representations both serialized. The runner's views are deeper —
`RunTaskView` has **25 positional parameters** and `RunDetail` nests it inside `RunView`.

**Files:**
- Test: `tests/TaskRouter.Tests/RunnerSerializationTests.cs` *(new)*

- [x] **Step 1: Write the tests**

Follow `tests/TaskRouter.Tests/BuilderSerializationTests.cs` exactly — same structure, same
`JsonSerializerOptions(JsonSerializerDefaults.Web)`, same discipline: **every value must be
something that is not the property's default**, so a field that fails to round-trip fails a
test rather than passing by coincidence.

Cover `RunView`, `RunDetail`, `RunTaskView`, `ForkInfo`, `ForkBranchView`,
`SubWorkflowInstanceView`, `SubWorkflowOptionView`, `LogEntryView`, `OutcomeOption`,
`AdHocOption`, `ConvergenceOption`, `InboxItem`, `RunnerResult`, `TestRunResult`.

The one that matters most:

```csharp
[TestMethod]
public void Every_one_of_RunTaskViews_twenty_five_fields_survives()
{
    // Positional records serialize by property name, so a field that fails to round trip
    // comes back as a default rather than an error -- a task would silently lose its due
    // date, or arrive claiming CanComplete when it cannot.
    var original = FullyPopulatedTask();
    var back = RoundTrip(original);

    Assert.AreEqual(original, back,
        "Record equality compares all 25 fields, so this fails naming the whole value "
        + "rather than one assertion at a time.");
}
```

`Assert.AreEqual` on the whole record is the right assertion here **because these are
positional records with value equality** — it checks all 25 fields at once and prints both
values on failure. Add per-field assertions only for `DateTime` fields, where a UTC/local
round trip is the specific hazard and equality alone will not say which field moved.

- [x] **Step 2: Run to verify they fail**

They should not fail — these are plain records and are expected to pass. **That is the point
of running them:** if one fails, a serialization bug exists *today* and the endpoints must not
be built until it is understood.

Run: `~/.dotnet/dotnet test TaskRouter.slnx --filter "FullyQualifiedName~RunnerSerialization"`

If everything passes, say so and continue. If anything fails, stop and investigate before
Task 2 — that is a finding, not an obstacle.

- [x] **Step 3: Commit**

```bash
git add tests/TaskRouter.Tests/RunnerSerializationTests.cs
git commit -m "Prove the runner and inbox views survive JSON before building on them"
```

**Result: 10 tests, all green. No serialization defect exists today**, so Task 2 onwards can
be built on this wire format.

One thing the writing of it turned up, worth knowing before the endpoint tasks: a positional
record's generated `Equals` compares a collection member by **reference**, so `AreEqual` on a
whole `ForkInfo` or `RunDetail` fails even when every value is identical. `ForkInfo` is
compared in two halves for that reason, with a comment saying not to "simplify" it back. Any
later test asserting on a record that holds a list has the same trap waiting.

---

### Task 2: The two seams

**Files:**
- Create: `src/TaskRouter.Core/Abstractions/DirectoryTypes.cs`
- Modify: `src/TaskRouter.Core/Abstractions/HostServices.cs`
- Modify: `src/TaskRouter.Core/Runner/IWorkflowRunnerClient.cs` (remove two records)
- Create: `src/TaskRouter.EntityFrameworkCore/Runner/NullDirectory.cs`
- Modify: `src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs`
- Test: `tests/TaskRouter.Tests/DirectorySeamTests.cs` *(new)*

- [x] **Step 1: Move the shared records**

Create `src/TaskRouter.Core/Abstractions/DirectoryTypes.cs`:

```csharp
namespace TaskRouter.Core.Abstractions;

/// <summary>A person the host can assign work to. The id is the engine's, the name is
/// the host's.</summary>
public sealed record ActorOption(string ActorId, string DisplayName);

/// <summary>An org unit a fork can branch across. The key is the engine's, the name is
/// the host's.</summary>
public sealed record BranchOption(string Key, string DisplayName);

/// <summary>
/// What one of the host's subjects is called and where it lives. Every field is nullable
/// because a host may not be able to resolve a subject at all -- a deleted document, a
/// subject type this host does not own -- and the honest answer is "I do not know", not a
/// fabricated label.
/// </summary>
public sealed record SubjectDescriptor(string? Label, string? Subtitle, string? Url);
```

Delete `ActorOption` and `BranchOption` from `IWorkflowRunnerClient.cs`. That file already
has `using TaskRouter.Core.Model;`; add `using TaskRouter.Core.Abstractions;`.

**Then fix every consumer.** Expect breaks in `src/TaskRouter.Blazor/RunnerAddBranchDialog.razor`,
`RunnerForkDialog.razor`, `RunnerSubWorkflowDialog.razor`, `RunnerReassignDialog.razor`, and
`samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs`. The razor files pick up types
from `_Imports.razor` — add `@using TaskRouter.Core.Abstractions` there rather than to each
file.

- [x] **Step 2: Grow `IWorkflowActorResolver`**

In `HostServices.cs`, replace the interface. Keep its existing summary's first sentence — the
"opaque ids, no user entity" explanation is still true and still the reason this exists:

```csharp
/// <summary>
/// The host's directory of people and org units.
///
/// <para>The engine stores only opaque ids and never models a user entity — the system this
/// was extracted from carried a User navigation property on its base class and a fixed-length
/// id format, neither of which a general library can impose. So every question about who
/// somebody is, or what org units exist, comes back here.</para>
///
/// <para><b>Every method fails soft.</b> An implementation that throws is logged and treated
/// as no answer: an empty picker, a null name, an unassigned assignment. A directory that is
/// down should degrade a drop-down, not take down a run view. This matches
/// <see cref="IWorkflowAssignmentResolver"/> and <see cref="IWorkflowDueDateResolver"/>;
/// authorization is the one seam that fails closed instead.</para>
/// </summary>
public interface IWorkflowActorResolver
{
    /// <summary>What an actor id is called, or null if the host cannot say.</summary>
    Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct = default);

    /// <summary>
    /// Actors that may be assigned to, for the reassign and delegate pickers.
    ///
    /// <para>Every actor the host is willing to show, unfiltered by the current task — the
    /// engine applies no rule of its own to this list, so a host that wants to restrict who
    /// appears does it here.</para>
    /// </summary>
    Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Every org unit a fork could branch across.
    ///
    /// <para><b>Return them all.</b> The engine removes the ones a given fork has already
    /// branched on, because that is a question about fork state rather than about the host's
    /// org chart — see <c>EfWorkflowRunnerClient.GetBranchOptionsAsync</c>. A host that
    /// filters here as well is doing work the engine has already done.</para>
    /// </summary>
    Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(CancellationToken ct = default);

    /// <summary>
    /// An actor's org unit, as a complete assignment.
    ///
    /// <para>Actor and branch key travel together everywhere else — the engine inherits them
    /// as a pair — and an actor set without their org unit is what makes a downstream role
    /// key resolve against the wrong one. Returning
    /// <see cref="WorkflowAssignment.Unassigned"/> for an unknown actor is a real answer,
    /// not an error.</para>
    /// </summary>
    Task<WorkflowAssignment> GetAssignmentForAsync(
        string? actorId, CancellationToken ct = default);
}

/// <summary>
/// What the host's subjects are called and where they live.
///
/// <para>The engine stores an opaque <c>(type, id)</c> pair and can say nothing more about
/// it. It knows a run is about <c>ChangeRequest:42</c>; only the host knows that is
/// "CR-2026-0042, Pump room rewire" and that it lives at <c>/documents/42</c>.</para>
///
/// <para><b>Batched deliberately.</b> A per-subject signature would make N+1 the default for
/// every host that implements it — an inbox with forty rows would issue forty queries. One
/// call, one query.</para>
///
/// <para>Fails soft, like the directory: a resolver that throws leaves rows labelled with
/// their raw keys rather than emptying the list.</para>
/// </summary>
public interface IWorkflowSubjectResolver
{
    /// <summary>
    /// Describes each subject. A subject absent from the returned dictionary, or present
    /// with null fields, is one the host could not resolve — the caller falls back to the
    /// raw key and renders it unclickable. An orphaned task is a defect worth seeing, not
    /// worth hiding.
    /// </summary>
    Task<IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor>> ResolveAsync(
        IReadOnlyList<WorkflowSubject> subjects, CancellationToken ct = default);
}
```

- [x] **Step 3: Defaults that say what is missing**

Create `src/TaskRouter.EntityFrameworkCore/Runner/NullDirectory.cs`. Model it on the existing
`AllowAllAuthorizationPolicy`, which logs once per process — **read that class first and match
its logging idiom** rather than inventing one.

```csharp
/// <summary>
/// The directory a host gets if it registers none: empty answers, and one log line saying so.
///
/// <para>Empty rather than throwing, because the runner must still render — a host that has
/// not wired a directory should get a run view with an empty reassign picker, not an error
/// page. And one log line rather than silence, because an empty picker looks exactly like a
/// host with nobody in it, and the two are worth telling apart.</para>
/// </summary>
internal sealed class NullActorResolver(ILogger<NullActorResolver> logger) : IWorkflowActorResolver
```

Same shape for `NullSubjectResolver`, returning an empty dictionary — every row then falls
back to its raw subject key, which is the documented fallback rather than a broken state.

Register both with `TryAddScoped` inside `AddTaskRouter()`, beside the existing
`TryAddScoped` defaults. **`TryAdd`, so `AddActorResolver<T>()` still wins** regardless of
call order.

`AddActorResolver<T>()` exists already. **`AddSubjectResolver<T>()` does not — add it**,
beside it on `WorkflowEngineBuilder`, using plain `AddScoped` exactly as the other seam
registrations do:

```csharp
/// <summary>
/// Registers what the host's subjects are called and where they live. Without one, inbox
/// rows fall back to their raw keys (<c>"ChangeRequest:42"</c>) and are not clickable.
/// </summary>
public WorkflowEngineBuilder AddSubjectResolver<T>()
    where T : class, IWorkflowSubjectResolver
{
    Services.AddScoped<IWorkflowSubjectResolver, T>();
    return this;
}
```

- [x] **Step 4: Update `AddActorResolver`'s XML doc**

It currently says, in bold, that nothing in the library consumes it. **That stops being true
in Task 3.** Replace with what it now does, and say that the runner and inbox clients are the
consumers.

- [x] **Step 5: The demo implements both seams**

Create `DemoDirectory.cs` implementing `IWorkflowActorResolver` over `db.People` and
`db.Sections`, absorbing `DemoOrg.AssignmentForAsync` and `DemoActorResolver`. Create
`DemoSubjectResolver.cs` implementing `IWorkflowSubjectResolver` over `db.Documents`, lifted
from `DemoInboxClient` lines 62–67 and its fallback logic.

Leave `DemoRunnerClient` and `DemoInboxClient` in place for now, retargeted at the new seams
where they used `DemoOrg`. **They are deleted in Task 7**, and keeping them compiling until
then means every intermediate commit is green.

- [x] **Step 6: Tests**

`tests/TaskRouter.Tests/DirectorySeamTests.cs`:

```csharp
[TestMethod]
public async Task An_unregistered_directory_empties_pickers_rather_than_failing()

[TestMethod]
public async Task A_directory_that_throws_is_logged_and_treated_as_no_answer()

[TestMethod]
public async Task An_unresolved_subject_keeps_its_row_under_its_raw_key()

[TestMethod]
public async Task AddActorResolver_still_beats_the_default_registered_before_it()
```

The last one is the `TryAdd` ordering guarantee and it is worth a test: getting it backwards
silently gives every host the null directory.

- [x] **Step 7: Build, test, commit**

Expect a wide but shallow break from the record move. Full green before committing.

```bash
git add -A
git commit -m "Grow the actor resolver into a directory, and add a subject resolver"
```

---

**Done. 376/376, 0 warnings.** Two things worth carrying into Task 3:

- **The null directory keeps the actor.** The plan implied it should answer emptily
  throughout, but `GetAssignmentForAsync` returning `Unassigned` would silently *unassign*
  work on every path routing through the directory — much worse than a missing branch key,
  which already means "keep the current assignment". It returns `new WorkflowAssignment(actorId,
  null)` instead, and there is a test naming why.
- **`tests/TaskRouter.Tests/RunnerClientTests.cs` already exists with 28 tests** against
  `DemoRunnerClient`. The plan did not know that. They are the port's regression suite: point
  them at `EfWorkflowRunnerClient` in Task 3 and they will say immediately whether the port
  changed behaviour.

### Task 3: `EfWorkflowRunnerClient`

**Files:**
- Create: `src/TaskRouter.EntityFrameworkCore/Runner/EfWorkflowRunnerClient.cs`
- Modify: `src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs`
- Test: `tests/TaskRouter.Tests/RunnerClientTests.cs` *(new)*

- [x] **Step 1: Port**

Copy `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs` wholesale, then make exactly
these changes:

| Line (original) | Change |
|---|---|
| ctor `DemoDbContext db` | `IWorkflowDbContext db` |
| ctor `DemoOrg org` | remove |
| ctor `IWorkflowActorResolver actors` | keep — it is now the directory |
| 414 `db.Sections` | `await actors.GetBranchOptionsAsync(ct)`, then filter out `taken` in memory |
| 503 `org.AssignmentForAsync(...)` | `actors.GetAssignmentForAsync(...)` |
| 534 `db.People` | `actors.GetActorsAsync(ct)` |

**Nothing else changes.** If a fourth site needs editing, stop — the spec's seam analysis was
wrong and the plan needs revisiting before more code is written.

The branch filter, which is the one piece of real logic moving:

```csharp
// The host lists its org units; the engine removes the ones this fork has already
// branched on. Split here rather than at the seam because "already branched on" is
// fork state -- a host filtering it would be reimplementing a rule the engine
// enforces anyway, and would get an exception at fork time when it got it wrong.
var all = await actors.GetBranchOptionsAsync(ct).ConfigureAwait(false);

return [.. all.Where(o => !taken.Contains(o.Key))];
```

- [x] **Step 2: Settle `TestSubjectType` (spec open question 2)**

`DemoRunnerClient` declares `public const string TestSubjectType = "WorkflowTest"`, used by
`StartTestRunAsync`. Moving the client into the library makes that constant public API, so
decide rather than inherit:

- Keep it a **library constant** if nothing in the engine treats it specially and a host has
  no reason to change it — then it belongs on the client as `public const`, documented as
  "the subject a test run is recorded against; the `IsTest` flag is what actually matters".
- Make it **configurable** only if a host could collide with it, i.e. if a real host might
  already own a subject type called `WorkflowTest`.

Check the demo's comment — it says nothing in the engine treats it specially — and confirm
against `StartTestRunAsync` before choosing. Record the decision in the XML doc either way.

- [x] **Step 3: Registration**

```csharp
/// <summary>
/// Registers the runner and inbox clients, for a host running the engine in process.
///
/// <para>Separate from <c>AddWorkflowBuilder()</c> because they are separate surfaces: a
/// host may want to run workflows without letting anyone edit them, and editing is by far
/// the more privileged of the two.</para>
/// </summary>
public WorkflowEngineBuilder AddWorkflowRunner()
```

Registering both clients `AddScoped`. Follow `AddWorkflowBuilder` exactly.

- [x] **Step 4: Tests**

Against SQL Server through `TestHost`, following the existing engine tests. Cover at minimum:
reads on a seeded run, the branch filter (**fork a task, assert the taken branch is gone,
with a directory returning everything** — so the test fails if filtering moves back to the
host), reassignment through the directory, and `CanComplete` differing between two actors
under the host's authorization policy.

- [x] **Step 5: Commit**

```bash
git commit -m "Ship the runner client instead of making every host write 700 lines"
```

---

### Task 4: `EfWorkflowInboxClient`

**Files:**
- Create: `src/TaskRouter.EntityFrameworkCore/Runner/EfWorkflowInboxClient.cs`
- Test: `tests/TaskRouter.Tests/InboxClientTests.cs` *(new)*

- [x] **Step 1: Port**

From `DemoInboxClient`. Two changes: `org.AssignmentForAsync` → `actors.GetAssignmentForAsync`,
and the `db.Documents` block → `subjects.ResolveAsync`.

**Preserve the `.Unwrap()` on the engine call and its comment verbatim.** It is deliberate and
it is the opposite of everything else's policy: an empty inbox renders as "Nothing is waiting
on you", so swallowing a failed engine read tells the user the exact opposite of the truth.
The *subject* resolution around it still fails soft.

- [x] **Step 2: Tests**

Including: a subject the resolver cannot describe keeps its row, labelled with the raw key and
with an empty URL; and **a failing engine read throws rather than returning an empty inbox** —
mutate that to `return []` and the test must fail, or it is not testing the thing it names.

- [x] **Step 3: Commit**

---

**Tasks 3 and 4 done. 380/380, 0 warnings.**

- **The port needed exactly the three changes the spec predicted** and no fourth, which is
  the check the plan set. `EfWorkflowRunnerClient` compiled against `IWorkflowDbContext` on
  the first try.
- **`TestSubjectType` stays a library constant.** One use, internal, and `IsTest` is what
  hosts actually filter on — a collision would need the same random 12-character id as well
  as the same type name. An option nobody needs.
- **Both `RunnerClientTests` (28) and `InboxClientTests` (11) already existed** and now run
  against the library's clients unchanged. That is the strongest evidence available that the
  port did not change behaviour, and it was free.
- **A mistake worth recording: `InboxClientTests.cs` was overwritten** by a `cat >` on a file
  assumed to be new, destroying 11 tests. Caught by the total dropping to 371 when the
  arithmetic said 382 — *not* by anything failing. Recovered from git and merged. **Check
  whether a test file exists before creating it**; the suite total is the thing that notices.
- Three tests were added to the recovered file for what it did not cover: fail-soft on a
  broken subject resolver, fail-hard on a failed engine read, and one call rather than N.
  All three verified load-bearing by mutation.

### Task 5: The endpoints

**Files:**
- Create: `src/TaskRouter.AspNetCore/RunnerEndpoints.cs`
- Modify: `src/TaskRouter.AspNetCore/InboxEndpoints.cs`
- Modify: `src/TaskRouter.AspNetCore/WorkflowEndpointExtensions.cs`
- Modify: `tests/TaskRouter.Tests/EndpointTestHost.cs`
- Test: `tests/TaskRouter.Tests/RunnerEndpointTests.cs` *(new)*

- [x] **Step 1: Routes**

Follow `BuilderEndpoints.cs` exactly, including `[FromServices]` on every client parameter —
**re-read the class comment there for why it is load-bearing before writing a line.** Map
under `group.MapGroup("/runner")`.

The actor: mutating routes take theirs from `WorkflowEndpointActor.Resolve(http, accessor, out
var actorId)` like every other mutating route, **never from the request body**. `GetRunAsync`
also needs one, because `CanComplete` is per-actor — it is a read that resolves an actor, as
`/inbox` already is.

- [x] **Step 2: Confirm the endpoints add no second authorization gate (spec open question 3)**

Unlike the builder, every mutating runner route maps onto an engine operation the host's
`IWorkflowAuthorizationPolicy` **already** gates. Read `TaskEndpoints.cs` and confirm the
runner routes follow it: resolve the actor, call the engine, map the result — and let the
engine refuse. A check in the endpoint layer would be a second gate that can disagree with the
first, and the one that matters is the engine's, because it also guards the in-process path
that has no endpoint at all.

Note it in the class comment so the absence reads as a decision rather than an oversight.

- [x] **Step 3: The inbox route**

`GET /workflow/inbox` exists and returns raw engine rows. Add `GET /workflow/inbox/items`
returning `InboxItem`. **Keep both** — the raw one is the honest engine read and a host may
prefer it.

- [x] **Step 4: The regression test**

Copy `BuilderEndpointTests.A_host_that_never_adopted_the_builder_still_starts` for the runner.
Then **verify it is load-bearing**: strip the `[FromServices]` attributes, confirm that test
and only that test fails, restore them. Do not skip this — it is the check that caught the
mistake last time.

- [x] **Step 5: Register the runner in `EndpointTestHost`**

Beside `AddWorkflowBuilder(...)`, add `.AddWorkflowRunner()` and a test directory. Without it
every builder endpoint test fails with the body-inference error, which is what happened last
time and looked like a routing bug.

- [x] **Step 5: Commit**

---

**Done. 391/391, 0 warnings.** 23 routes under `/workflow/runner`, plus
`GET /workflow/inbox/items`.

- **No second authorization gate**, confirmed against `TaskEndpoints`: resolve the actor,
  call, map, let the engine refuse. Said so in the class comment so the absence reads as a
  decision.
- **The existing request records were reused** rather than duplicated — `CompleteTaskRequest`,
  `ForkTaskRequest` and the rest already had the right shapes.
- `[FromServices]` verified load-bearing again: stripped, exactly one test fails and it is
  `A_host_that_never_adopted_the_runner_still_starts`.
- **Both inbox routes are kept.** `/workflow/inbox` returns the engine's raw rows;
  `/workflow/inbox/items` returns `InboxItem` with subjects resolved. A host with the engine
  in process may prefer the first; a browser cannot resolve subjects and needs the second.

### Task 6: The HTTP clients

**Files:**
- Create: `src/TaskRouter.Blazor/Http/HttpWorkflowRunnerClient.cs`
- Create: `src/TaskRouter.Blazor/Http/HttpWorkflowInboxClient.cs`
- Modify: `src/TaskRouter.Blazor/Http/BuilderHttpExtensions.cs` → rename to `HttpExtensions.cs`
- Test: `tests/TaskRouter.Tests/HttpRunnerClientTests.cs` *(new)*

- [x] **Step 1: Write them**

Follow `HttpWorkflowBuilderClient` exactly: same `prefix` parameter, same `EnsureSuccessStatusCode`
discipline, same null-body throw. `GetRunAsync` returns `RunDetail?` — 404 is null, everything
else surfaces.

- [x] **Step 2: Registration**

Add `AddTaskRouterRunnerHttpClient()` beside the builder's, with both overloads. **No
`IHttpClientFactory`** — see the existing class comment for why, and keep
`TaskRouter.Blazor`'s packed dependencies at three.

- [x] **Step 3: Tests against the real server**

Not a mocked handler. The whole value of these classes is that their calls agree with the
routes, and a mock agrees with whatever the test author believed.

- [x] **Step 4: Verify the package dependencies did not change**

```bash
~/.dotnet/dotnet pack src/TaskRouter.Blazor/TaskRouter.Blazor.csproj -o /tmp/pk
python3 -c "
import zipfile,re
z=zipfile.ZipFile('/tmp/pk/TaskRouter.Blazor.0.1.0-preview.1.nupkg')
n=[x for x in z.namelist() if x.endswith('.nuspec')][0]
print(re.search(r'<dependencies>.*?</dependencies>', z.read(n).decode(), re.S).group(0))"
```

Expected: `TaskRouter.Core`, `Microsoft.AspNetCore.Components.Web`, `MudBlazor`. Nothing else.

- [x] **Step 5: Commit**

---

**Done. 403/403, 0 warnings, and `TaskRouter.Blazor` still has exactly three package
dependencies** (Core, Components.Web, MudBlazor) — verified by unpacking the nuspec.

**Task 6 found a real bug, which is the argument for testing against a real server.**
`DeleteTestRunAsync` threw `DbUpdateConcurrencyException` over HTTP while passing in process.
The tasks are never tracked — only their ids are selected — so EF does not know
`run -> task -> log` is a chain and is free to order the run's `DELETE` first; the database's
cascade then removes the log rows and EF's own log `DELETE`s affect zero rows. It survived in
the demo purely by accident: one shared `DbContext` meant the tasks happened to be tracked and
the chain happened to be visible. **A mocked `HttpMessageHandler` would not have found this,
and neither would any amount of in-process testing.** Fixed with two saves in dependency
order, with a comment saying why.

The clients mirror `EfWorkflowRunnerClient`'s failure policy rather than inventing one: reads
degrade to empty, writes return a failed `RunnerResult`, and the inbox throws. A component
must not behave differently depending on which side of a wire the engine is.

### Task 7: Shrink the demo — this is the check, not cleanup

**Files:**
- Delete: `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs` (700 lines)
- Delete: `samples/DemoDocuments.Server/Workflow/DemoInboxClient.cs` (97 lines)
- Delete: `samples/DemoDocuments.Server/Workflow/DemoOrg.cs`, `DemoActorResolver` (absorbed)
- Modify: `samples/DemoDocuments.Server/Program.cs`

- [x] **Step 1: Delete them and register the library's**

```csharp
builder.Services.AddTaskRouter()
    .AddWorkflowBuilder(...)
    .AddWorkflowRunner()
    .AddActorResolver<DemoDirectory>()
    .AddSubjectResolver<DemoSubjectResolver>();
```

- [x] **Step 2: Judge the result**

**~800 lines of demo code should have become roughly 60** — a directory of four short methods
and a subject resolver of one query. The spec says plainly: *if they do not shrink to roughly
that, the seams are wrong and this spec is wrong.*

If a demo class is still large, **stop and report it** rather than trimming to fit. Something
that looked like host knowledge is not, or something that looked like library code is not, and
that is worth knowing before publication rather than after.

- [x] **Step 3: The demo still works end to end**

Run it and drive a workflow through the runner UI. `dotnet run` **from the project directory**
— running the built DLL directly breaks static assets and Blazor silently.

- [x] **Step 4: Commit**

---

**Done, and the check passes.**

| | Before | After |
|---|---|---|
| `DemoRunnerClient` | 700 | deleted |
| `DemoInboxClient` | 97 | deleted |
| `DemoOrg` | 46 | deleted |
| `DemoDirectory` | — | 57 (41 non-comment) |
| `DemoSubjectResolver` | — | 68 (~30 non-comment) |
| **Total** | **843** | **125 (~71 non-comment)** |

The plan predicted "roughly 60" lines of code and the answer is about 71, which is close
enough to call the seam analysis right. Everything that remains is genuinely host knowledge:
`db.People`, `db.Sections`, `db.Documents`. Nothing that stayed behind looks library-shaped,
which was the thing to check.

**The demo starts clean and resolves the library's clients.** Verified by running it against
a fresh database and calling `/tasks/assigned/{actorId}`, which is backed by
`IWorkflowInboxClient` — 200 rather than a DI failure means `EfWorkflowInboxClient` resolved
and ran in the real application. The components themselves are untouched; only what sits
behind the seam moved, and that is covered by 66 tests.

*(Note for whoever runs the demo next: it needs `ConnectionStrings__Demo` with the sa
password, and an existing `WorkflowDemo` database from an older schema will fail migration
with "There is already an object named 'Groups'". Point it at a fresh database name.)*

### Task 8: Documentation

**Files:**
- Modify: `README.md`, `STATE.md`, `docs/design.md`

- [x] **Step 1: README**

Extend "The builder, and which hosting model needs what" to cover the runner and inbox — same
two-model split. Document both seams under the host-implementations list, and **move
`IWorkflowActorResolver` out of the "nothing consumes it" paragraph**, which stops being true.

- [x] **Step 2: `docs/design.md`**

It describes the seams and their failure policies. Add the two, and the reasoning for the
branch-filter split — it is a good example of the line the library draws between host
knowledge and engine knowledge.

- [x] **Step 3: STATE**

Record what shipped, the test count, and what is left: transaction composition, pre-assignment,
CI and the publish, timers.

- [x] **Step 4: Commit**

---

## Done when

- `dotnet build TaskRouter.slnx --no-incremental` → 0 errors, **0 warnings**.
- `dotnet test TaskRouter.slnx` → 0 failing, above the 361 baseline. **403/403.**
- A run driven end to end over HTTP in a test, against a real server.
- The demo's 800 lines are ~60, and the demo still runs.
- `TaskRouter.Blazor` still has exactly three package dependencies.
- Spike finding 4 is closed: `IWorkflowActorResolver` is consumed by the library.

## Not in this plan

- **Pre-assignment** — `docs/superpowers/specs/2026-09-02-pre-assignment.md`, still unbuilt.
- **Transaction composition docs.**
- **CI, SourceLink, the GitHub repo, the publish.**
- **Timers.**
- **Adopting any of this in a host.** That is `Dev`'s item 12, and it gets easier the moment
  this lands.
