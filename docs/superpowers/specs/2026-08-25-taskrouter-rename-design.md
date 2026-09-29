# `TaskRouter` — the rename that establishes package identity

**Date:** 2026-08-25
**Status:** Implemented 2026-08-25 — see `docs/superpowers/plans/2026-08-25-taskrouter-rename.md`

## What this is for

The library is finished enough to publish and was always meant to be: extracted from the
workflows in the original system, kept general so several projects can use it, MIT, bound for a public
GitHub repository and then NuGet.org.

Publishing needs a name, and the current one cannot have it. `Workflow.Core` is **taken**
on nuget.org, as is the bare `Workflow`. The other three IDs happen to be free, but a
family whose base package is unavailable is not a family — and `Workflow.AspNetCore`
unprefixed is too generic to defend or to reserve.

So the packages need a name of their own. This is the last moment it is cheap: no
consumer exists, no data exists, and the original system has not yet adopted the library. After the
first NuGet publish every one of these names is a contract, and changing one is a
breaking change for everybody who took a dependency.

## The name

**`TaskRouter`.** It says what the engine does — it routes tasks to people according to
outcomes — and it matches vocabulary already in the code: `TaskRoute` is a real entity,
and the original system's own original has `DocumentTaskOutcomeRoute`. Nobody needs it explained.

It also stays away from the crowded neighbourhood. `WorkflowCore`, `Elsa`, Temporal and
Conductor all orchestrate *code*; this orchestrates *people*. A name built on "workflow"
would compete directly with them for a search term that describes the other thing.

Availability was confirmed against the nuget.org registration API on 2026-08-25: all four
IDs return 404 (unregistered).

Rejected along the way, and why, so nobody relitigates it:

| Candidate | Why not |
|---|---|
| `Cadence` | Uber's workflow engine — the predecessor to Temporal. |
| `Dispatch` | Available, but the library already has a `TriggerDispatcher`, and the word is overloaded across .NET. |
| `Bosun`, `Docket`, `Baton`, `Tessera` | Brandable, but any name needing an explanation is the wrong name here. |
| `DocumentTasker` | Narrows the library to documents. The subject is generic (`WorkflowSubject` is a type plus an id — "ChangeRequest", "WorkOrder", anything), and reuse across projects is the whole point. |

## The map

| Today | Becomes |
|---|---|
| `Workflow.Core` | `TaskRouter` |
| `Workflow.Persistence.EF` | `TaskRouter.EntityFrameworkCore` |
| `Workflow.AspNetCore` | `TaskRouter.AspNetCore` |
| `Workflow.MudBlazor` | `TaskRouter.Blazor` |
| `Workflow.Tests` | `TaskRouter.Tests` (not published) |

Sub-namespaces follow the base: `Workflow.Core.Model` → `TaskRouter.Model`,
`Workflow.Core.Abstractions` → `TaskRouter.Abstractions`, and likewise for `.Results`,
`.Builder`, `.Inbox`, `.Runner` and `.Validation`.

Package ID, assembly name and root namespace are kept identical for each project. A
consumer who installs `TaskRouter.AspNetCore` and types `using TaskRouter.AspNetCore;`
should not have to learn that the two differ.

### Entry points

| Today | Becomes |
|---|---|
| `AddWorkflowEngine()` | `AddTaskRouter()` |
| `AddWorkflowEndpoints()` | `AddTaskRouterEndpoints()` |
| `MapWorkflowEndpoints()` | `MapTaskRouterEndpoints()` |
| `ConfigureWorkflowEngine()` | `ConfigureTaskRouter()` |

`AddOutboxProcessing()` and `AddDeadlineProcessing()` keep their names: they describe the
feature being switched on, not the product, and both read correctly beside
`AddTaskRouter()`.

### What deliberately does not change

Domain types keep their names — `IWorkflowEngine`, `WorkflowTask`, `WorkflowRun`,
`WorkflowDefinition`, `WorkflowSubject`, `WorkflowTaskStatus` and the rest. A workflow is
genuinely what they model, and renaming a concept because the package around it changed
would make the vocabulary worse, not better.

This does mean `services.AddTaskRouter()` registers an `IWorkflowEngine`. That is
accepted: the package is the product, the interface is the domain. `ITaskRouter` was
considered and rejected — it would rename the single most-referenced type in the codebase
to say less than the current name does.

## The namespace trap, closed permanently

`Workflow.MudBlazor` collides with MudBlazor's own root namespace. Inside
`namespace Workflow.MudBlazor`, a bare `using MudBlazor;` resolves *relative to the
enclosing namespace* and binds to the project itself, so anything hand-written in
`@inject` or `@code` fails CS0246 while markup keeps compiling — a failure that points
nowhere near its cause. STATE.md records it as a standing hazard that has bitten twice,
worked around today by **7 `global::` qualifiers** across two `_Imports.razor` files, and
notes that renaming either side would remove it permanently.

`TaskRouter.MudBlazor` would reproduce the bug exactly. **`TaskRouter.Blazor` ends it**,
and the qualifiers come out with it.

The demo carries the second instance of the same trap for a different reason:
`DemoDocuments.Server` has its own `Workflow` namespace, so `using Workflow.Core.Model`
inside it binds to `DemoDocuments.Server.Workflow.Core.Model` and finds nothing. That one
is fixed by the same rename — there is no `DemoDocuments.Server.TaskRouter` — so its five
qualifiers come out too, without touching the demo's own namespaces.

The cost is that the package name no longer advertises its MudBlazor dependency. The
package description and tags carry that instead, which is where a consumer looks anyway.

`TaskRouter.EntityFrameworkCore` and `TaskRouter.AspNetCore` are not exposed to the same
trap: the usings they need are rooted at `Microsoft`, which is not a member of
`TaskRouter`.

## Database tables

All 17 tables go `Workflow*` → `TaskRouter*`: `TaskRouterTasks`, `TaskRouterRuns`,
`TaskRouterDefinitions`, `TaskRouterTaskLogs`, `TaskRouterOutbox`, and so on.

This is not cosmetic. The original system **already has a `WorkflowTriggerDefinitions` table**, and the
engine maps its own `TriggerDefinition` to a table of exactly that name. Two CLR types on
one table name in one model is a collision that has to be resolved by hand at integration
time. Renaming the engine's tables clears it permanently and for every future host that
happens to have a table called `Workflow`-anything.

There is no workflow data anywhere — not in the original system, which is early enough to have none,
and not in the demo, which seeds from scratch. So nothing migrates. The tables will never
be this cheap to rename again.

The two SQL-Server-specific filtered indexes are defined over *column* names
(`[ForkManifestId] IS NOT NULL AND [Status] <> 3`, `[ReminderSentAt] IS NULL`) and are
unaffected by table renaming. `SchemaGuardTests` pins enum ordinals, not table names, and
is likewise unaffected.

`DemoDocuments.Server` needs one new migration. `MigrationTests` fails if the model has
drifted from the migrations, so forgetting it is caught rather than discovered later.

## Sequencing

**The rename lands before the the original system integration, not after.** Every namespace, entry point
and table name the original system would bind to is changing in this pass; integrating first means doing
that work twice and reviewing it twice.

It also lands before any packaging work, because package metadata is written in terms of
these names.

## Scope

In scope: package IDs, assembly names, root namespaces, sub-namespaces, the four entry
points, the 17 table names, the demo migration, the `global::` cleanup, the solution file,
and the documentation that names any of them (README, STATE.md, the specs and plans that
reference the old names).

Not in scope, and each is its own piece of work afterwards:

- LICENSE file and `PackageLicenseExpression` (README says "MIT." in one word today; there
  is no licence text and no copyright line).
- Package metadata proper — `PackageId`, `Description`, `Authors`, `PackageTags`,
  `RepositoryUrl`, `PackageReadmeFile`, `GenerateDocumentationFile`.
- CI, SourceLink, symbol packages.
- The GitHub repository and the publish itself.
- The the original system integration.
- Renaming the local working directory. The GitHub repository can be named `TaskRouter`
  while the repository root stays where it is; moving it mid-flight breaks
  tooling for no benefit. `WorkflowEngine.slnx` **does** become `TaskRouter.slnx`.

## Testing

The rename is mechanical and changes no behaviour, so the existing suite is the test: it
must be **307/307 green** at the end, with the same test names, and `dotnet build
--no-incremental` must report **0 warnings and 0 errors**.

Three things the suite alone will not catch, so they are checked explicitly:

- **The demo still runs.** Razor and DI failures from a bad namespace rename can survive
  compilation. The demo must be launched from its project directory — running the built
  DLL directly breaks static assets and Blazor silently.
- **No stale name survives.** `grep -rn 'Workflow\.\(Core\|Persistence\.EF\|AspNetCore\|MudBlazor\)'`
  over tracked files returns nothing when the rename is complete.
- **The `global::` workarounds are gone**, not merely still working — their continued
  presence would mean the trap was carried across rather than closed.

## What this leaves for later

The name is the commitment; everything else about publishing is reversible. Once this
lands, the order is: LICENSE and package metadata, then the the original system integration to prove the
seams against a host nobody on this side wrote, then CI, then the publish.
