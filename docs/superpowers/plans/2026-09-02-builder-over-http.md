# Builder-over-HTTP Implementation Plan

> **Revised 2026-09-02.** The first draft had every host writing its own server-side
> `IWorkflowBuilderClient`, on the strength of the demo having an 858-line one. That was wrong:
> grepping `DemoBuilderClient` for *any* demo domain type — `Document`, `Section`, `Person`,
> `WorkItem` — returns nothing. It takes `DemoDbContext` but only ever uses it as
> `IWorkflowDbContext`, and its other dependencies are `IWorkflowTriggerRegistry` and
> `IEnumerable<IRouteConditionEvaluator>`, both library types. Those 858 lines are
> **library-shaped**, and every host would write them again identically.
>
> So the library ships the implementation too. Exactly two things in it are host knowledge:
> the assignment role keys, and who is editing. Everything else is manipulation of TaskRouter's
> own tables.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the workflow builder usable from a Blazor WebAssembly host, by shipping the HTTP half the library has always assumed someone else would write.

**Architecture:** `TaskRouter.EntityFrameworkCore` gains the default `IWorkflowBuilderClient` — the graph manipulation every host would otherwise repeat — with the two genuinely host-specific pieces as seams. `TaskRouter.AspNetCore` gains endpoints exposing it. `TaskRouter.Blazor` gains an implementation of the same interface that calls those endpoints, for hosts that cannot run it in process. The components do not change.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `System.Net.Http.Json`, MudBlazor 9.7.0, MSTest, SQL Server for the suite.

---

## Why this exists

`TaskRouter.Blazor` ships the builder components. `IWorkflowBuilderClient` is the seam behind
them, and the demo implements it in-process against EF. The demo's own `Program.cs` says what
was always intended:

> *"IWorkflowBuilderClient is the seam: this host implements it in-process against the engine,
> and a WebAssembly host would implement the same interface over HTTP without the components
> changing."*

The seam is right. **The HTTP half was never written.** `TaskRouter.AspNetCore` maps every
*engine* operation — runs, tasks, reads, inbox — and **no builder operation at all**. A
WebAssembly host cannot touch a `DbContext`, so it gets the components and nothing to put
behind them.

The integration spike hit this immediately: its host is `Microsoft.NET.Sdk.BlazorWebAssembly`,
so it cannot run the in-process implementation at all. Without this plan every WASM host writes
the same twelve endpoints and the same HTTP client — and, as the revision note above says, the
same 858 lines behind them.

**This plan is the library half.** What remains for a host is hosting the components and
supplying two values; that is a separate, thin plan because it is a different subsystem, not
because it is comparable work.

## Scope

| In | Out |
|---|---|
| The default `IWorkflowBuilderClient` in `TaskRouter.EntityFrameworkCore` | Any change to the builder components |
| Builder endpoints in `TaskRouter.AspNetCore` | `IWorkflowRunnerClient`, the runner's equivalent seam — same gap, separate plan |
| An HTTP `IWorkflowBuilderClient` in `TaskRouter.Blazor` | Authentication and authorization policy — the host applies its own, as with the engine endpoints |
| Serialization proof for every edit model | Hosting the components in a particular host — separate, and now very thin |

**What a host is left with after this plan:** supply its assignment role keys, supply the acting
user, host the components, and — if it is a WebAssembly host — point the HTTP client at the
endpoints. Tens of lines, not hundreds.

**`IWorkflowRunnerClient` has the identical gap** and is deliberately not in scope. Do it next,
the same way, once this one has proven the shape.

## The twelve operations

From `src/TaskRouter.Core/Builder/IWorkflowBuilderClient.cs`. **Read it before starting** — these
signatures are the contract both new pieces must satisfy, and this table is a summary, not the
source of truth.

| # | Method | Verb and route |
|---|---|---|
| 1 | `GetWorkflowsAsync()` | `GET /workflow/builder/workflows` |
| 2 | `GetWorkflowAsync(int versionId)` | `GET /workflow/builder/workflows/{versionId}` |
| 3 | `GetTaskTypesAsync()` | `GET /workflow/builder/task-types` |
| 4 | `CreateTaskTypeAsync(string key, string displayName)` | `POST /workflow/builder/task-types` |
| 5 | `GetTriggerDescriptorsAsync()` | `GET /workflow/builder/triggers` |
| 6 | `GetAssignmentRolesAsync()` | `GET /workflow/builder/assignment-roles` |
| 7 | `GetRouteConditionsAsync()` | `GET /workflow/builder/route-conditions` |
| 8 | `GetSubWorkflowDefinitionsAsync()` | `GET /workflow/builder/sub-workflows` |
| 9 | `ValidateAsync(WorkflowEditModel)` | `POST /workflow/builder/validate` |
| 10 | `SaveAsync(WorkflowEditModel)` | `POST /workflow/builder/save` |
| 11 | `PublishAsync(WorkflowEditModel)` | `POST /workflow/builder/publish` |
| 12 | `CreateDraftVersionAsync(int fromVersionId)` | `POST /workflow/builder/workflows/{fromVersionId}/draft` |

Routes sit under the existing `/workflow` prefix so a host maps one group, and under
`builder/` so the builder surface is separable from the engine's in policy and in logs.

## Three things that will bite you

**1. The edit models were written for in-process use and have never been serialized.**
`WorkflowEditModel` and everything under it (`src/TaskRouter.Core/Builder/EditModels.cs`) has
only ever been passed between objects in one process. Round-tripping through JSON is a new
requirement on types that were not designed for it. Expect at least one of: a property with no
setter that `System.Text.Json` cannot populate, a cycle between a task and its routes, or an
interface-typed member. **Task 1 exists to find out before anything is built on top.**

**2. `TriggerDescriptor` carries behaviour-adjacent data.** It is the trigger metadata the
builder renders a form from, and it comes from the registry, which is populated from registered
`IWorkflowTrigger` implementations. That is a server-side concept. It serializes as data, but
confirm nothing in it is a delegate or a `Type`.

**3. The client is not the engine's client.** `TaskRouter.Blazor` currently has no `HttpClient`
dependency and no notion of a base address. Adding one must not make the RCL unusable to the
Blazor Server hosts that reference it today and implement the seam in-process — registration is
opt-in, and the existing demo must keep working untouched. There is a test for that.

## File Structure

| File | Responsibility |
|---|---|
| `src/TaskRouter.EntityFrameworkCore/Builder/EfWorkflowBuilderClient.cs` | **Create.** The default implementation, ported from the demo. |
| `src/TaskRouter.EntityFrameworkCore/Builder/WorkflowBuilderOptions.cs` | **Create.** The host's assignment role keys. |
| `src/TaskRouter.EntityFrameworkCore/Builder/IWorkflowEditorActorAccessor.cs` | **Create.** Who is editing. |
| `src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs` | **Modify.** `AddWorkflowBuilder()`. |
| `samples/DemoDocuments.Server/Workflow/DemoBuilderClient.cs` | **Delete.** Its 858 lines are the thing being shipped. |
| `src/TaskRouter.AspNetCore/BuilderEndpoints.cs` | **Create.** The twelve routes. |
| `src/TaskRouter.AspNetCore/WorkflowEndpointExtensions.cs` | **Modify.** Map the new group. |
| `src/TaskRouter.Blazor/Http/HttpWorkflowBuilderClient.cs` | **Create.** `IWorkflowBuilderClient` over `HttpClient`. |
| `src/TaskRouter.Blazor/Http/BuilderHttpExtensions.cs` | **Create.** `AddTaskRouterBuilderHttpClient()`. |
| `tests/TaskRouter.Tests/BuilderSerializationTests.cs` | **Create.** Task 1. |
| `tests/TaskRouter.Tests/BuilderEndpointTests.cs` | **Create.** Endpoints via the existing test host. |
| `tests/TaskRouter.Tests/HttpBuilderClientTests.cs` | **Create.** Client against a real server. |

---

### Task 1: Prove the edit models survive JSON

**Do this first and do not skip it.** Everything else assumes it. If a model does not
round-trip, that is a change to `TaskRouter.Core` and it is far cheaper to discover now than
after twelve endpoints are written against it.

**Files:**
- Test: `tests/TaskRouter.Tests/BuilderSerializationTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;

using TaskRouter.Core.Builder;

namespace TaskRouter.Tests;

/// <summary>
/// The edit models were written to be passed between objects in one process. Sending them
/// over HTTP is a new requirement on them, and a property JSON cannot populate fails
/// silently — as a default value on the far side, not as an error.
/// </summary>
[TestClass]
public class BuilderSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void A_fully_populated_workflow_survives_a_round_trip()
    {
        var original = FullyPopulated();

        var json = JsonSerializer.Serialize(original, Options);
        var back = JsonSerializer.Deserialize<WorkflowEditModel>(json, Options);

        Assert.IsNotNull(back);
        Assert.AreEqual(original.Name, back.Name);
        Assert.AreEqual(original.SubjectType, back.SubjectType);
        Assert.HasCount(original.Tasks.Count, back.Tasks);

        var task = back.Tasks[0];
        var source = original.Tasks[0];

        Assert.AreEqual(source.LocalId, task.LocalId);
        Assert.AreEqual(source.TaskTypeId, task.TaskTypeId);
        Assert.AreEqual(source.DisplayName, task.DisplayName);
        Assert.AreEqual(source.IsForkable, task.IsForkable);
        Assert.HasCount(source.Routes.Count, task.Routes);
        Assert.HasCount(source.Outcomes.Count, task.Outcomes);
        Assert.HasCount(source.Triggers.Count, task.Triggers);
    }
}
```

**`FullyPopulated()` must be written against the real models.** Open
`src/TaskRouter.Core/Builder/EditModels.cs` and populate **every** property of
`WorkflowEditModel`, one `WorkflowTaskEditModel` with every property set, and one entry in each
of its collections. A partially populated fixture proves nothing about the properties it skips,
and the properties it skips are exactly where this fails.

- [ ] **Step 2: Run it**

Run: `dotnet test tests/TaskRouter.Tests --filter BuilderSerializationTests`

**Both outcomes are informative.** If it passes, the models are already fine and the rest can
start. If it fails, **stop and read why** — a missing setter or an unsupported member shape is
a `TaskRouter.Core` change, and it must be made and recorded before any endpoint is written.

- [ ] **Step 3: Fix `TaskRouter.Core` if needed, then commit**

If a change was needed, note in the commit message that these types are now part of a wire
contract and not only an in-process one, because that changes what a future edit to them costs.

```bash
git add tests/TaskRouter.Tests/BuilderSerializationTests.cs
git commit -m "Prove the builder edit models survive a JSON round trip"
```

---

### Task 2: Ship the default builder client

**Files:**
- Create: `src/TaskRouter.EntityFrameworkCore/Builder/EfWorkflowBuilderClient.cs`
- Create: `src/TaskRouter.EntityFrameworkCore/Builder/WorkflowBuilderOptions.cs`
- Create: `src/TaskRouter.EntityFrameworkCore/Builder/IWorkflowEditorActorAccessor.cs`
- Modify: `src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs`
- Test: `tests/TaskRouter.Tests/BuilderClientTests.cs` — **exists already**, and currently exercises `DemoBuilderClient`

- [ ] **Step 1: Port the implementation**

`samples/DemoDocuments.Server/Workflow/DemoBuilderClient.cs` is the source. It is already
generic: it never touches a demo domain type, and its `DemoDbContext` dependency is used only
through `IWorkflowDbContext`. Move it, change the constructor to take `IWorkflowDbContext`, and
change nothing else about the graph logic — that logic is the value here and it is already
tested.

**The two host-specific pieces come out as seams:**

```csharp
/// <summary>
/// What the builder needs from the host that the engine does not know.
///
/// <para>Assignment role keys are opaque to the engine — a host's resolver interprets them —
/// so the builder cannot enumerate them and has to be told. Everything else the builder
/// offers as a choice (task types, trigger descriptors, route conditions, sub-workflows) is
/// derivable from what is registered or stored, and is not configured here.</para>
/// </summary>
public sealed class WorkflowBuilderOptions
{
    /// <summary>The role keys this host's assignment resolver understands.</summary>
    public IReadOnlyList<string> AssignmentRoles { get; set; } = [];
}

/// <summary>
/// Who is editing. Separate from <see cref="WorkflowBuilderOptions"/> because it is
/// per-request where the roles are configuration, and because a host that has no
/// authentication — the sample does not — still needs an answer.
/// </summary>
public interface IWorkflowEditorActorAccessor
{
    string ActorId { get; }
}
```

`GetRouteConditionsAsync` stays derived from the registered `IRouteConditionEvaluator`s and
`GetTriggerDescriptorsAsync` from `IWorkflowTriggerRegistry`, exactly as the demo does. Neither
is host configuration; both are already in the container.

- [ ] **Step 2: Registration**

On the existing builder, alongside `AddTrigger<T>()` and the rest:

```csharp
/// <summary>
/// Registers the workflow builder's server-side client — the implementation behind
/// TaskRouter.Blazor's builder components for a host that runs them in process.
/// A WebAssembly host registers the HTTP client instead and maps the endpoints in front of
/// this one.
/// </summary>
public WorkflowEngineBuilder AddWorkflowBuilder(Action<WorkflowBuilderOptions>? configure = null)
```

`TryAddScoped` the actor accessor to a default that throws with a clear message, so a host that
forgets it gets told what to register rather than an attribution of every edit to `null`.

- [ ] **Step 3: Verify it is genuinely generic by deleting the demo's copy**

`BuilderClientTests.cs` currently drives `DemoBuilderClient` and is the regression suite for all
of this logic. Point it at `EfWorkflowBuilderClient`, delete `DemoBuilderClient`, and register
the library implementation in the demo's `Program.cs` with its three role keys.

**The demo losing ~850 lines and its tests still passing is the proof** that this belonged in
the library. If something does not port, that thing is the genuine host seam and the options
class needs it — but check before adding, because "the demo did it this way" is not the same as
"a host must be able to".

- [ ] **Step 4: Run the suite**

Run: `dotnet test TaskRouter.slnx --filter BuilderClientTests`
Expected: PASS, unchanged in number. These tests moved implementation, not behaviour.

- [ ] **Step 5: Commit**

```bash
git add src/TaskRouter.EntityFrameworkCore/Builder/         src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs         samples/ tests/TaskRouter.Tests/BuilderClientTests.cs
git commit -m "Ship the builder's server-side client instead of making every host write it"
```

---

### Task 3: The endpoints

**Files:**
- Create: `src/TaskRouter.AspNetCore/BuilderEndpoints.cs`
- Modify: `src/TaskRouter.AspNetCore/WorkflowEndpointExtensions.cs`
- Test: `tests/TaskRouter.Tests/BuilderEndpointTests.cs`

- [x] **Step 1: Write the failing tests**

`tests/TaskRouter.Tests/EndpointTestHost.cs` already stands up the engine endpoints over a real
server; **read it and follow it**, rather than building a second harness. The builder endpoints
need `IWorkflowBuilderClient` registered in that host — use `DemoBuilderClient`, which is the
in-process implementation the demo already provides.

Cover, at minimum:

```csharp
[TestMethod]
public async Task Listing_workflows_returns_what_the_client_returns()
{
    var response = await _client.GetAsync("/workflow/builder/workflows");

    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

    var workflows = await response.Content.ReadFromJsonAsync<List<WorkflowSummary>>();

    Assert.IsNotNull(workflows);
    Assert.IsNotEmpty(workflows, "The demo seeds a workflow, so this is never empty.");
}

[TestMethod]
public async Task Fetching_a_workflow_that_does_not_exist_is_404_not_a_null_body()
{
    var response = await _client.GetAsync("/workflow/builder/workflows/999999");

    Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
}

[TestMethod]
public async Task Validation_errors_come_back_as_a_body_not_a_500()
{
    // A workflow with no tasks fails WF_EMPTY. It is a legitimate answer to a legitimate
    // question, not a server fault, and a builder needs to render it.
    var empty = new WorkflowEditModel { Name = "Nothing", Tasks = [] };

    var response = await _client.PostAsJsonAsync("/workflow/builder/validate", empty);

    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

    var errors = await response.Content.ReadFromJsonAsync<List<ValidationError>>();

    Assert.IsNotNull(errors);
    Assert.IsNotEmpty(errors);
}
```

**Check `WorkflowEditModel`'s real shape before writing that last fixture** — `Name` and `Tasks`
are assumed here and may not be the actual property names.

- [x] **Step 2: Run to verify they fail**

Expected: 404 from the routing layer, because nothing maps `/workflow/builder/*` yet.

- [x] **Step 3: Write the endpoints**

Follow `RunEndpoints.cs` exactly: a `static class` with an internal `MapBuilderEndpoints(this
RouteGroupBuilder)`, resolving `IWorkflowBuilderClient` per request.

**Three rules the engine endpoints already follow and these must too:**

- **Do not call `RequireAuthorization`.** The host applies its own policy; this package does not
  know the scheme. `MapTaskRouterEndpoints` returns the group for exactly that reason.
- **`GetWorkflowAsync` returns `null` for a missing version.** Map that to `404`, not `200` with
  a null body — a builder cannot tell "no such workflow" from "a workflow with nothing in it"
  otherwise.
- **`SaveResult` and validation errors are results, not faults.** They come back `200` with a
  body. A `500` here means the builder cannot show the author what is wrong with their workflow.

- [x] **Step 4: Map the group**

In `WorkflowEndpointExtensions.MapTaskRouterEndpoints`, alongside the existing four:

```csharp
group.MapBuilderEndpoints();
```

**A host that does not register `IWorkflowBuilderClient` must not break.** The engine endpoints
are useful without the builder, and a host that never adopted the builder UI should not start
failing at startup because it upgraded. Resolve the client per request rather than at map time,
so an absent registration is a failure of a builder route and not of the whole application —
and add a test that maps the endpoints in a host with no builder client and calls a task
endpoint successfully.

- [x] **Step 5: Run to verify they pass, then commit**

```bash
git add src/TaskRouter.AspNetCore/BuilderEndpoints.cs \
        src/TaskRouter.AspNetCore/WorkflowEndpointExtensions.cs \
        tests/TaskRouter.Tests/BuilderEndpointTests.cs
git commit -m "Expose the builder's operations over HTTP"
```

---


**What this task found, which the plan did not anticipate.** The handlers must mark the
client `[FromServices]`. Without it, minimal APIs infer an unregistered complex type as a
*body* parameter — a GET may not have one — so a host that mapped these endpoints without
calling `AddWorkflowBuilder()` failed to start at all, with an error naming neither the
builder nor the route. The doc comment originally claimed the opposite. Task 4's
"host with no builder client still serves its task endpoints" test was written here instead,
as `A_host_that_never_adopted_the_builder_still_starts`, because it is the direct regression
test for this; removing the attributes fails it and nothing else.

### Task 4: The HTTP client

**Files:**
- Create: `src/TaskRouter.Blazor/Http/HttpWorkflowBuilderClient.cs`
- Create: `src/TaskRouter.Blazor/Http/BuilderHttpExtensions.cs`
- Test: `tests/TaskRouter.Tests/HttpBuilderClientTests.cs`

- [x] **Step 1: Write the failing test**

**Against a real server, not a mocked `HttpMessageHandler`.** The entire value of this class is
that its twelve calls agree with twelve routes; a mocked handler tests the mock. Use the same
harness as Task 3 and point the client's `HttpClient` at its `CreateClient()`.

```csharp
[TestMethod]
public async Task Every_read_operation_round_trips_through_HTTP()
{
    var client = new HttpWorkflowBuilderClient(_host.CreateClient());

    Assert.IsNotEmpty(await client.GetWorkflowsAsync());
    Assert.IsNotEmpty(await client.GetTaskTypesAsync());
    Assert.IsNotEmpty(await client.GetTriggerDescriptorsAsync());
    Assert.IsNotNull(await client.GetAssignmentRolesAsync());
    Assert.IsNotNull(await client.GetRouteConditionsAsync());
    Assert.IsNotNull(await client.GetSubWorkflowDefinitionsAsync());
}

[TestMethod]
public async Task A_workflow_survives_a_fetch_edit_save_round_trip()
{
    var client = new HttpWorkflowBuilderClient(_host.CreateClient());

    var summaries = await client.GetWorkflowsAsync();
    var model = await client.GetWorkflowAsync(summaries[0].VersionId);

    Assert.IsNotNull(model);

    var result = await client.SaveAsync(model);

    Assert.IsEmpty(result.Errors, "A workflow that came out of the builder goes back in.");
}
```

**`WorkflowSummary.VersionId` is assumed.** Check the real property name.

That second test is the one that matters: it proves serialization, routing, and the endpoint
contract together, in the direction a user actually drives them.

- [x] **Step 2: Run to verify it fails**

Expected: `HttpWorkflowBuilderClient` does not exist.

- [x] **Step 3: Write the client**

Twelve methods, each one `GetFromJsonAsync` or `PostAsJsonAsync` against the route table above.
Constructor takes `HttpClient`; the host configures the base address, as it does for any typed
client.

**Two things to get right:**

- **A `404` from `GetWorkflowAsync` is `null`, not an exception.** It is the documented answer
  for a version that is not there, and the interface returns `WorkflowEditModel?` for that
  reason.
- **Do not swallow other failures.** A `500` should surface. A builder that silently shows an
  empty workflow because the server errored is worse than one that shows an error.

- [x] **Step 4: Registration**

```csharp
/// <summary>
/// Registers the builder client against an HTTP endpoint, for a host that cannot implement
/// <see cref="IWorkflowBuilderClient"/> in process — a WebAssembly host, which has no
/// DbContext because it runs in a browser.
///
/// <para>A Blazor Server host does not need this: it implements the seam directly against the
/// engine, and registering this instead would add a network hop to a call that does not need
/// one.</para>
/// </summary>
public static IServiceCollection AddTaskRouterBuilderHttpClient(
    this IServiceCollection services,
    Uri baseAddress)
```

- [x] **Step 5: Prove the RCL still works without it**

The demo is a Blazor Server host that implements the seam in process and must be entirely
unaffected. Its existing tests passing is that proof — **confirm they do**, and note it, because
"we added an HttpClient dependency to the RCL" is exactly the kind of change that breaks a
consumer quietly.

- [x] **Step 6: Commit**

```bash
git add src/TaskRouter.Blazor/Http/ tests/TaskRouter.Tests/HttpBuilderClientTests.cs
git commit -m "Implement the builder client over HTTP for WebAssembly hosts"
```

---

**Deviation, deliberate.** The plan's signature took a `Uri baseAddress` and implied
`AddHttpClient`. That would put a `Microsoft.Extensions.Http` dependency on a package whose
reason to exist is running in a browser, where the factory's pooling buys nothing — no socket
to exhaust, no DNS entry to go stale, the handler is the browser's fetch. So the primary
overload registers over the `HttpClient` the WebAssembly template already provides, and the
`Uri` overload is a convenience that `TryAddSingleton`s one. `TaskRouter.Blazor`'s packed
dependencies are unchanged: Core, Components.Web, MudBlazor.

`HttpWorkflowBuilderClient` also takes the endpoint `prefix`, which the plan did not mention.
`MapTaskRouterEndpoints` takes one, so a client that hardcoded `/workflow` would silently
break any host that changed it. The two are a pair and the parameter is named to match.

Step 5 confirmed: 361/361, 0 warnings, and the demo — a Blazor Server host implementing the
seam in process — is untouched.

### Task 5: Document it, and say which host needs which

**Files:**
- Modify: `README.md`
- Modify: `STATE.md`

- [x] **Step 1: README**

The integration section lists the seams a host implements. `IWorkflowBuilderClient` needs a
paragraph saying the choice depends on the hosting model, because getting it wrong is not a
compile error:

- **Blazor Server** — `AddWorkflowBuilder()` and you are done. `samples/DemoDocuments.Server` is
  the reference, and after this plan it is a handful of lines.
- **Blazor WebAssembly** — `AddWorkflowBuilder()` and `MapTaskRouterEndpoints()` on the server,
  `AddTaskRouterBuilderHttpClient()` in the client. The components are identical.

Both supply the same two things: their assignment role keys, and who is editing.

- [x] **Step 2: STATE**

Record that the builder is now usable from both hosting models, and that
`IWorkflowRunnerClient` still has the same gap.

- [x] **Step 3: Commit**

---

## Done when

- `dotnet build TaskRouter.slnx --no-incremental` → 0 errors, 0 warnings.
- `dotnet test TaskRouter.slnx` → 0 failing, with this plan's tests added to the 333 baseline. **361/361.**
- A `WorkflowEditModel` fetched over HTTP, sent back, and saved, in a test that goes through a
  real server.
- The demo, a Blazor Server host, is untouched and still passing.

## Not in this plan

- **`IWorkflowRunnerClient`** — the identical gap for the runner components. Next, same shape.
- **Hosting the components** in a particular application, and any workflow-selection UI that
  goes with it. Separate plan, and after this one it is thin: register, route, supply two things.
- **Authorization.** The host applies its own policy to the returned group, exactly as it does
  for the engine endpoints. Worth noting for whoever writes the host plan: the builder edits
  workflow *definitions*, which is a far more privileged operation than completing a task, and
  the same policy is unlikely to be right for both.
