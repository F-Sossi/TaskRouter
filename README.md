# TaskRouter

A task-based workflow engine for .NET. Workflows attach to **any** subject — a
document, a work order, an onboarding case — and are authored as data, not code.

Built around a human **task** model rather than an activity graph: tasks are
first-class, queryable, assignable rows, so "show me my open tasks" is a plain
query rather than something you build on top. Where Elsa, WorkflowCore and
Temporal orchestrate *code*, this routes work between *people*.

> Status: the engine core, persistence, the HTTP endpoints, the builder and runner
> UI — in process and over HTTP — the trigger runtime, deadlines and escalation all
> exist and pass 484 integration tests against SQL Server. Timers are the one roadmap
> item left — see [Roadmap](#roadmap).

## Layout

| Project | What it is |
|---|---|
| `src/TaskRouter.Core` | Abstractions, domain model, validator, `Result`. No EF, no ASP.NET, no UI. |
| `src/TaskRouter.EntityFrameworkCore` | Entity configuration and the engine implementation. |
| `src/TaskRouter.AspNetCore` | Ready-made HTTP endpoints over every engine operation. |
| `src/TaskRouter.Blazor` | The workflow builder and runner as a Razor Class Library (MudBlazor). |
| `samples/DemoDocuments.Server` | A small document-review system that exercises the engine. |
| `tests/TaskRouter.Tests` | Integration tests against real SQL Server. |

## Design decisions worth knowing

The short version is below; **[docs/design.md](docs/design.md)** is the full reasoning — the
model, the lifecycle, what the host owns, and why each decision is the way it is.
**[docs/architecture.md](docs/architecture.md)** is the same thing drawn, if diagrams land
faster.


**Routes target a task definition, not a task type.** This is the load-bearing
decision. It means one workflow may contain the same task type more than once —
a mainline `Technical Review` and another inside an ad-hoc chain — with different
outgoing routes. Engines that key routing on a task-type enum cannot express
reusable sub-workflows at all.

**Task types are data.** A `TaskTypeDefinition` row, not an enum value, so the
library ships no domain vocabulary.

**Runs pin to a published version.** Editing a workflow can never reroute work
already in flight, and you can always answer which version a given run followed.

**Every operation is one transaction**, opened through the provider's execution
strategy so it composes with `EnableRetryOnFailure`.

**The engine owns task status.** No API accepts a caller-supplied status; callers
supply an *outcome* and the engine derives the rest. This keeps routing, blocking
checks and triggers impossible to bypass.

**The host owns its domain.** Assignment, the directory of people and org units,
what a subject is called, progress destinations and route conditions are interfaces
the host implements. The engine stores an opaque `WorkflowSubject` (type + id) and
opaque actor and branch keys.

## Running the demo

Needs .NET 10 and a SQL Server instance.

```bash
export ConnectionStrings__Demo="Server=localhost,1433;Database=WorkflowDemo;User Id=sa;Password=<pw>;TrustServerCertificate=True;Encrypt=False"
dotnet run --project samples/DemoDocuments.Server
```

The database is created from migrations and a CR-style workflow is seeded on first run.

**The host owns the schema.** The engine has no database of its own —
`ConfigureTaskRouter()` configures its tables onto the host's `DbContext`, which is
what lets a task completion and a domain update commit in one transaction. Migrations
therefore belong to the consumer:

```bash
dotnet ef migrations add <Name> \
  --project samples/DemoDocuments.Server --context DemoDbContext --output-dir Data/Migrations
```

### The web UI

| Page | What |
|---|---|
| `/workflows` | Every workflow version, published and draft |
| `/workflows/{versionId}` | The builder: tasks, routes, triggers, a diagram, and a **Try it** pane that runs the version you are editing |
| `/documents` | Stand-in documents |
| `/documents/{id}` | A document, with the workflow running on it |

The builder's "Try it" pane and the document page render the **same** runner
component. That is deliberate: if the preview were a separate component it would drift
from what users actually see, and testing a workflow in the builder would be worth
less than nothing. Runs started from the builder are flagged `IsTest` on the run, so a
host can keep them out of inboxes and reports with one predicate.

### The API

The document endpoints sit under `/api`, because the UI owns `/documents`. The
`/documenttasks` surface deliberately does not: it mirrors the shape a
document-management host of the kind this was extracted from already has.

```bash
# Create a document; this starts a workflow run
curl -sX POST localhost:5000/api/documents/change-requests -H 'content-type: application/json' \
  -d '{"title":"Test change request","docNumber":"CR-0001","originator":"user-originator",
       "actorId":"user-originator","isReplyRequired":true,"sectionCodes":["C100","C200"]}'

# What is assigned to me
curl -s localhost:5000/tasks/assigned/user-originator

# Complete a task
curl -sX POST localhost:5000/documenttasks/complete/1 -H 'content-type: application/json' \
  -d '{"outcomeKey":"approved","actorId":"user-originator"}'

# Fork by section, converge at PM Review (definition ids from /workflow/definitions)
curl -sX POST localhost:5000/documenttasks/fork -H 'content-type: application/json' \
  -d '{"taskId":2,"sectionCodes":["C100","C200","C300"],
       "convergenceTaskDefinitionId":3,"actorId":"user-originator"}'

# Converge, sending two branches back for rework
curl -sX POST localhost:5000/documenttasks/complete-selective -H 'content-type: application/json' \
  -d '{"taskId":6,"outcomeKey":"rejected","rejectedSectionCodes":["C100","C300"],
       "actorId":"user-branch-head"}'
```

## Tests

Integration tests, deliberately. The convergence guard is a filtered unique index
and the transaction behaviour depends on a real execution strategy — neither is
exercised by an in-memory provider, and both are where comparable engines go wrong.

Each test class gets its own database, created and dropped around the run.

```bash
export SA_PASSWORD='<your sa password>'
dotnet test

# or point at any instance directly
export WORKFLOW_TEST_CONNECTION="Server=...;User Id=...;Password=...;TrustServerCertificate=True"
```

## Integrating into a host

**Two declarations and one call.** That is the whole of the required wiring:

```csharp
public partial class MyDbContext : DbContext, IWorkflowDbContext
{
    protected override void OnModelCreating(ModelBuilder b) => b.ConfigureTaskRouter();
}
```

```csharp
services.AddDbContext<MyDbContext>(o => o.UseSqlServer(cs));

services.AddTaskRouterFor<MyDbContext>()
        .AddAssignmentResolver<MyAssignmentResolver>()
        .AddAuthorizationPolicy<MyAuthorizationPolicy>()
        .AddRouteCondition<MyRouteCondition>()
        .ValidateWiringAtStartup();
```

**`IWorkflowDbContext` needs no members of your own.** It asks for `Set<T>()`, `Database`
and `SaveChangesAsync`, which `DbContext` already has, and defaults all nineteen of its
sets in terms of `Set<T>()`. Declare one yourself only to reach it from your own code as
`context.WorkflowTasks` — a default interface member is visible through the interface, not
the class — or to override one whose name you already use.

TaskRouter's 17 tables live in your database and your migrations, each prefixed
`TaskRouter*`, so they never collide with yours.

**`AddTaskRouterFor<T>` maps the context onto the seam for you.** That mapping is the one
registration nothing can infer, and leaving it out does not fail at startup — it fails on
the first engine call with a generic "no service for type `IWorkflowDbContext`". Naming the
context as a type argument makes it impossible to forget. `AddTaskRouter()` without the
type argument still exists for hosts that would rather map it themselves.

**`ValidateWiringAtStartup()` checks the rest**, as the application starts rather than at
the first request, and names the fix for anything missing:

```
TaskRouter is not correctly wired:
  * IWorkflowDbContext is not registered, so no engine call can reach a database.
    Either name your context when registering the engine:
        services.AddTaskRouterFor<YourDbContext>();
```

It also warns — without blocking startup — about the defaults that are legal but rarely
what you want in production: no authorization policy means every operation is permitted,
no assignment resolver means role keys resolve to nothing, no due-date resolver means
deadlines never fire.

### What is actually yours to implement

The library defines thirteen host interfaces and `AddTaskRouter()` ships a working default
for every one it can, so the list is shorter than it looks:

1. **`IWorkflowAssignmentResolver`** — who a task goes to. The default keeps whatever
   assignment it was handed, so without one nothing is ever routed to anybody.

2. **`IWorkflowAuthorizationPolicy`**, plus an `IRouteConditionEvaluator` for each
   condition key your routes name. The default policy permits every operation and says so
   in the log once per process; a route whose condition has no registered evaluator fails
   closed — the route is skipped and a warning logged.

Optional, and inert until you supply them: `IWorkflowDueDateResolver` (no resolver, no
deadlines), `IWorkflowProgressSink`, `IWorkflowNotificationSink`, `IWorkflowJobQueue`
(defaults to in-process) and any `IWorkflowTrigger` of your own.

Two more are needed only if you use the runner or inbox components, and both have defaults
that answer emptily and log once rather than failing:

- **`IWorkflowActorResolver`** — your directory of people and org units: what an actor is
  called, who may be assigned to, what org units exist, which one an actor belongs to, and
  which ones they can *see work in*. Without it the reassign and fork pickers are empty.
  The last two differ: delegation needs one unit, an inbox needs all of them. The **engine** still never consumes
  it — it stores opaque ids and routes work through `IWorkflowAssignmentResolver` — so this
  changes what the UI can show, not where work goes.
- **`IWorkflowSubjectResolver`** — what your subjects are called and where they live, so an
  inbox row reads "CR-2026-0042, Pump room rewire" and links to it rather than showing
  `ChangeRequest:42`. Batched: one call for every subject on screen, not one per row.

`AddActorResolver<T>()` and `AddSubjectResolver<T>()` register them.

### Publishing a workflow from code

A workflow is data, so a host can seed one directly against its `DbContext`. Two
constraints make the order non-obvious: `EntryTaskDefinitionId` can only be set once the
task definition it names has an id, while the publish flags are naturally set when the
version object is built — so the obvious code leaves the version briefly flagged published
while structurally incomplete, and permanently so if validation then fails.

`PublishWithEntryTaskAsync` owns that ordering:

```csharp
var errors = await db.PublishWithEntryTaskAsync(version, entryTask, ct);

if (errors.Count > 0)
{
    // Nothing was published. The version is saved as a draft; fix the graph and retry.
}
```

It saves the graph, back-fills the entry task's id, runs `WorkflowDefinitionValidator`,
demotes whichever version of the same definition was `IsLatest`, and sets the publish
flags — in that order, in two saves. A superseded version stays `IsPublished`, because
runs pin to a version and the ones already on it still have to resolve their routes.

Build the object graph with navigation properties rather than ids (`TaskType = type`, not
`TaskTypeDefinitionId = type.Id`) for anything created in the same batch: ids are minted by
the save this method makes. `samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs`
seeds a mainline workflow and a sub-workflow this way.

### Name collisions with an existing workflow feature

`TaskRouter.Core.Model` defines `ForkManifest`, `ForkManifestEntry` and
`TriggerDefinition`, and `IWorkflowDbContext` declares unprefixed member names like
`WorkflowRuns` and `WorkflowTriggerDefinitions`. A host that already has its own workflow
subsystem can own some of those names:

- **CLR type names** — a file importing both namespaces needs `using` aliases.
- **`DbSet` member names** — if your context already declares one of the seventeen, add
  TaskRouter's as an explicit interface implementation:

  ```csharp
  DbSet<TriggerDefinition> IWorkflowDbContext.WorkflowTriggerDefinitions => Set<TriggerDefinition>();
  ```

  Note the consequence: `context.WorkflowTriggerDefinitions` then resolves to *your* type,
  and TaskRouter's is reachable only through the interface or `Set<TriggerDefinition>()` —
  two things that both compile and mean different rows.

**Tables never collide**, which is what makes coexistence work while you migrate. If the
old subsystem is being retired anyway, deleting it first is cleaner than living with
either workaround.

`samples/DemoDocuments.Server` does all of this and is the reference; see
`Data/DemoDbContext.cs`, `Workflow/HostAdapters.cs` and `Program.cs`.

### Ready-made endpoints

`TaskRouter.AspNetCore` maps every engine operation over HTTP:

```csharp
builder.Services.AddTaskRouterEndpoints();
app.MapTaskRouterEndpoints().RequireAuthorization();
```

The actor comes from the authenticated principal — `ClaimTypes.NameIdentifier` by
default — never from the request body, so a caller asserts what to do and never who is
doing it. Implement `IWorkflowEndpointBranchKeyResolver` to make `GET /workflow/inbox`
return unclaimed work in the actor's org units; without one it returns only work assigned
to them by name.

The demo does **not** use this package. Its `/documenttasks` routes are document-shaped
on purpose and mirror the controller it replaced; see `samples/DemoDocuments.Server` for
a host that maps its own.

### The components, and which hosting model needs what

The components in `TaskRouter.Blazor` talk to three seams — `IWorkflowBuilderClient` for
authoring, `IWorkflowRunnerClient` for driving a run, `IWorkflowInboxClient` for "what is
waiting on me". **The library implements all three both ways.** The choice is your hosting
model, and getting it wrong is not a compile error, so it is worth a moment.

**Blazor Server**, or any host with a `DbContext` in the same process:

```csharp
services.AddTaskRouter()
        .AddWorkflowBuilder(o =>
        {
            o.AssignmentRoles = ["reviewer", "approver"];
            o.SubjectTypes = ["ChangeRequest", "Drawing"];
        })
        .AddWorkflowRunner()
        .AddActorResolver<MyDirectory>()
        .AddSubjectResolver<MySubjectResolver>();

services.AddScoped<IWorkflowEditorActorAccessor, MyEditorActorAccessor>();
```

**Blazor WebAssembly**, which has no `DbContext` because it runs in a browser — the same
two registrations on the server, plus the endpoints, plus one line in the client:

```csharp
// Server — exactly the same registrations, plus the endpoints
services.AddTaskRouter()
        .AddWorkflowBuilder(o => o.AssignmentRoles = [...])
        .AddWorkflowRunner()
        .AddActorResolver<MyDirectory>()
        .AddSubjectResolver<MySubjectResolver>();

services.AddScoped<IWorkflowEditorActorAccessor, MyEditorActorAccessor>();
services.AddTaskRouterEndpoints();
app.MapTaskRouterEndpoints().RequireAuthorization();

// Client — TaskRouter.Blazor.Http
builder.Services.AddTaskRouterBuilderHttpClient();
builder.Services.AddTaskRouterRunnerHttpClient();    // runner and inbox together
```

The components are identical either way. If you passed a custom prefix to
`MapTaskRouterEndpoints`, pass the same one to the client registrations — they are a pair.

**Both models supply the same things, and there are only four.**

| | What it answers | Without it |
|---|---|---|
| `AssignmentRoles` | which role keys your assignment resolver understands | authors get no "who does this go to" choices |
| `SubjectTypes` | what a workflow may be authored against | authors type the subject type by hand, and one typo makes a workflow nothing ever offers |
| `IWorkflowEditorActorAccessor` | who is editing a workflow | **throws** — see below |
| `IWorkflowActorResolver` | who your people are, what your org units are | empty reassign and fork pickers |
| `IWorkflowSubjectResolver` | what a subject is called and where it lives | inbox rows read `ChangeRequest:42` and do not link |

Role keys are opaque to the engine, so it cannot enumerate them and the builder has to be
told. The editor accessor is the one with no working default: a placeholder would attribute
every workflow edit in the system to a fiction, and nothing would look wrong until somebody
asked who changed a workflow. The other two default to answering emptily and logging once.

Everything else the builder offers an author is derived from what is registered or stored:
task types from the database, triggers from the trigger registry, route conditions from the
registered evaluators, sub-workflows from the definitions flagged as such.

**The builder and runner routes are mapped whether or not you register those clients**, so
upgrading cannot break a host that never adopted them — such a route fails on that host; the
rest of the application starts and serves normally. There is a test for each.

**Authorization is yours, and the same policy is probably not right for both.**
`MapTaskRouterEndpoints` returns the group without calling `RequireAuthorization`. Editing a
workflow definition changes how every future run of it behaves, which is considerably more
privileged than completing one task — so the builder subgroup can carry its own policy:

```csharp
app.MapTaskRouterEndpoints(
       configureBuilderGroup: group => group.RequireAuthorization("WorkflowAuthoring"))
   .RequireAuthorization();
```

**Two inbox routes exist and they are not the same.** `GET /workflow/inbox` returns the
engine's rows with subjects still opaque — the honest read, and enough for a host that knows
its own documents. `GET /workflow/inbox/items` returns them resolved through your
`IWorkflowSubjectResolver`, which is what the inbox component renders and the only one a
browser can use.

## Roadmap

- [x] Trigger runtime: registry, descriptors, dispatch modes, transactional outbox
- [x] Built-in triggers (webhook, notify, set variable, report progress)
- [x] `TaskRouter.AspNetCore` — endpoints and DI extension
- [x] `TaskRouter.Blazor` — the workflow builder as a Razor Class Library
- [x] Sub-workflow instances — model and lifecycle
- [x] Deadlines, reminders and SLA escalation — due dates, the overdue sweep, escalation
      triggers
- [x] EF migrations packaging guidance — see **The host owns the schema** above
- [ ] **Timers as a workflow primitive** — "wait ten days, then advance on your own".
      Durable suspend/resume, and a different feature from a deadline: a deadline nags a
      human, a timer moves the run itself. The one substantial item left.

## Licence

MIT — see [LICENSE](LICENSE).

Copyright (c) 2026 Frank Sossi.
