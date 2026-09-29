# `TaskRouter` Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the four packages, their namespaces, the four entry points and the 17 database tables from `Workflow*` to `TaskRouter*`, so the library can be published on nuget.org under a name that is actually available.

**Architecture:** A mechanical rename in nine commits, one per unit, each ending with a green build and a green suite. No behaviour changes anywhere in this plan — if a test needs editing to pass, something has gone wrong. Two long-standing defects close as a side effect: the MudBlazor namespace trap and the `WorkflowTriggerDefinitions` table collision with the original system.

**Tech Stack:** .NET 10, EF Core 10.0.10, MSTest, SQL Server integration tests.

**Spec:** `docs/superpowers/specs/2026-08-25-taskrouter-rename-design.md`

---

## Before you start

Every `dotnet` command in this plan needs these two lines first — `dotnet` is not on the
PATH and the integration tests need the SA password:

```bash
export DOTNET_ROOT=~/.dotnet && export PATH=$DOTNET_ROOT:$PATH
source "$WORKFLOW_DEV_ENV"
```

Verify the baseline before changing anything:

```bash
cd <repo root> && dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, and `Passed! - Failed: 0, Passed: 307`.

**All 307 tests must pass, unmodified, at every commit in this plan.** This is a rename;
no test's *behaviour* changes. Test files change only where they name a namespace, an
entry point or a table. If a test starts failing on its assertions, revert and find out
why — do not edit the assertion.

Note on incremental builds: a plain `dotnet build` can report 0 warnings when a project
was not rebuilt. Use `--no-incremental` whenever you are checking the warning count.

### Why `git ls-files` and not `grep -r`

Every bulk edit below pipes `git ls-files` into `sed`. This is deliberate: it touches only
tracked files, so `obj/` and `bin/` — which contain generated `AssemblyInfo.cs` files full
of the old names — are never rewritten. Editing those does nothing and creates confusing
diffs. They are regenerated on the next build.

### What is NOT renamed

Do not rename these. They are domain vocabulary, and the spec explains why:

- Types: `IWorkflowEngine`, `WorkflowTask`, `WorkflowRun`, `WorkflowDefinition`,
  `WorkflowSubject`, `WorkflowTaskStatus`, `WorkflowAssignment`, `WorkflowEngineBuilder`,
  `WorkflowNotFoundException`, `WorkflowAuthorizationException`, and every other type whose
  name starts with `Workflow`.
- File names: `WorkflowModelBuilder.cs`, `WorkflowEngine.cs`, `WorkflowEndpointActor.cs`
  and friends keep their names — they are named after their types.
- Methods on `WorkflowEngineBuilder`: `AddOutboxProcessing()`, `AddDeadlineProcessing()`,
  `AddAssignmentResolver()`, `AddActorResolver()`, `AddTrigger()`, `AddRouteCondition()`.
- The demo's own `DemoDocuments.Server.Workflow` namespace and its `Workflow/` folder.
- Database *column* names, and the two SQL Server index filters, which are written over
  columns rather than tables.

## File structure

| Unit | Today | Becomes |
|---|---|---|
| Core library | `src/Workflow.Core/` | `src/TaskRouter/` |
| EF persistence | `src/Workflow.Persistence.EF/` | `src/TaskRouter.EntityFrameworkCore/` |
| HTTP endpoints | `src/Workflow.AspNetCore/` | `src/TaskRouter.AspNetCore/` |
| Blazor UI | `src/Workflow.MudBlazor/` | `src/TaskRouter.Blazor/` |
| Tests | `tests/Workflow.Tests/` | `tests/TaskRouter.Tests/` |
| Solution | `WorkflowEngine.slnx` | `TaskRouter.slnx` |

There are no `RootNamespace` or `AssemblyName` properties in any `.csproj`, so both derive
from the project file name. Renaming the directory and the `.csproj` is enough to move the
assembly name and default namespace together.

---

## Task 1: `Workflow.Core` → `TaskRouter`

**Files:**
- Rename: `src/Workflow.Core/` → `src/TaskRouter/` (and its `.csproj`)
- Modify: every tracked `.cs`, `.razor`, `.csproj`, `.slnx` naming `Workflow.Core`

This one is first because it is the most referenced: `Workflow.Core.Model`,
`.Abstractions`, `.Results`, `.Builder`, `.Inbox`, `.Runner` and `.Validation` are used by
all four other projects.

- [ ] **Step 1: Move the directory and the project file**

```bash
cd <repo root>
git mv src/Workflow.Core src/TaskRouter
git mv src/TaskRouter/Workflow.Core.csproj src/TaskRouter/TaskRouter.csproj
```

- [ ] **Step 2: Rewrite every reference**

`Workflow.Core` becomes `TaskRouter`, which also turns `Workflow.Core.Model` into
`TaskRouter.Model` and the `ProjectReference` paths (`..\Workflow.Core\Workflow.Core.csproj`)
into `..\TaskRouter\TaskRouter.csproj` in the same pass:

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' \
  | xargs -0 sed -i 's/Workflow\.Core/TaskRouter/g'
```

- [ ] **Step 3: Verify nothing is left**

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' | xargs -0 grep -l 'Workflow\.Core' || echo "clean"
```

Expected: `clean`.

- [ ] **Step 4: Build and test**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed: 307`.

If you get CS0246 errors mentioning types that plainly exist, read the namespace-trap
section in `STATE.md` before changing anything — a bare `using` inside a namespace whose
root shares a segment with the target resolves relatively and silently misses.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Rename Workflow.Core to TaskRouter

The base package of the family, so it goes first: everything else
references it. Namespace, assembly name and package id move together
because the csproj sets neither explicitly."
```

---

## Task 2: `Workflow.Persistence.EF` → `TaskRouter.EntityFrameworkCore`

**Files:**
- Rename: `src/Workflow.Persistence.EF/` → `src/TaskRouter.EntityFrameworkCore/`
- Modify: every tracked file naming `Workflow.Persistence.EF`

`EntityFrameworkCore` rather than `Persistence.EF` because the package id has to say what a
consumer is choosing, and `X.EntityFrameworkCore` is the convention the whole .NET
ecosystem already uses for exactly this.

- [ ] **Step 1: Move the directory and the project file**

```bash
git mv src/Workflow.Persistence.EF src/TaskRouter.EntityFrameworkCore
git mv src/TaskRouter.EntityFrameworkCore/Workflow.Persistence.EF.csproj \
       src/TaskRouter.EntityFrameworkCore/TaskRouter.EntityFrameworkCore.csproj
```

- [ ] **Step 2: Rewrite every reference**

This also converts the sub-namespace `Workflow.Persistence.EF.Triggers` into
`TaskRouter.EntityFrameworkCore.Triggers`:

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' \
  | xargs -0 sed -i 's/Workflow\.Persistence\.EF/TaskRouter.EntityFrameworkCore/g'
```

- [ ] **Step 3: Verify nothing is left**

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' | xargs -0 grep -l 'Workflow\.Persistence' || echo "clean"
```

Expected: `clean`.

- [ ] **Step 4: Build and test**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed: 307`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Rename Workflow.Persistence.EF to TaskRouter.EntityFrameworkCore

X.EntityFrameworkCore is the convention the ecosystem already uses for
'this is the EF Core implementation of X', and it says what a consumer
is choosing better than Persistence.EF did."
```

---

## Task 3: `Workflow.AspNetCore` → `TaskRouter.AspNetCore`

**Files:**
- Rename: `src/Workflow.AspNetCore/` → `src/TaskRouter.AspNetCore/`
- Modify: every tracked file naming `Workflow.AspNetCore`

- [ ] **Step 1: Move the directory and the project file**

```bash
git mv src/Workflow.AspNetCore src/TaskRouter.AspNetCore
git mv src/TaskRouter.AspNetCore/Workflow.AspNetCore.csproj \
       src/TaskRouter.AspNetCore/TaskRouter.AspNetCore.csproj
```

- [ ] **Step 2: Rewrite every reference**

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' \
  | xargs -0 sed -i 's/Workflow\.AspNetCore/TaskRouter.AspNetCore/g'
```

- [ ] **Step 3: Verify nothing is left**

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' | xargs -0 grep -l 'Workflow\.AspNetCore' || echo "clean"
```

Expected: `clean`.

- [ ] **Step 4: Build and test**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed: 307`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Rename Workflow.AspNetCore to TaskRouter.AspNetCore"
```

---

## Task 4: `Workflow.MudBlazor` → `TaskRouter.Blazor`, closing the namespace trap

**Files:**
- Rename: `src/Workflow.MudBlazor/` → `src/TaskRouter.Blazor/`
- Modify: `src/TaskRouter.Blazor/_Imports.razor` (drop two `global::` qualifiers and the
  comment explaining them)
- Modify: every tracked file naming `Workflow.MudBlazor`

`Blazor` rather than `MudBlazor` is the whole point of this task. A project named
`TaskRouter.MudBlazor` contains a namespace segment `MudBlazor`, so a bare
`using MudBlazor;` inside it resolves *relative to the enclosing namespace*, binds to the
project itself, and never reaches the real MudBlazor. Markup keeps compiling — the Razor
compiler emits components fully qualified — while `@inject` and `@code` fail CS0246 with
an error that points nowhere near the cause. `STATE.md` records this as a standing hazard
that has bitten twice.

- [ ] **Step 1: Move the directory and the project file**

```bash
git mv src/Workflow.MudBlazor src/TaskRouter.Blazor
git mv src/TaskRouter.Blazor/Workflow.MudBlazor.csproj \
       src/TaskRouter.Blazor/TaskRouter.Blazor.csproj
```

- [ ] **Step 2: Rewrite every reference**

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' \
  | xargs -0 sed -i 's/Workflow\.MudBlazor/TaskRouter.Blazor/g'
```

- [ ] **Step 3: Build and test before removing the workarounds**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed: 307`. The `global::` qualifiers are
harmless but redundant at this point; this run proves the rename itself is sound before
the next step changes anything else.

- [ ] **Step 4: Remove the two now-unnecessary qualifiers**

In `src/TaskRouter.Blazor/_Imports.razor`, delete **lines 7–15** — the seven-line comment
block explaining the trap, plus the two qualified usings beneath it — and put this in their
place:

```razor
@using MudBlazor
@using TaskRouter.Inbox
```

The comment goes with them. It describes a hazard that no longer exists in this project,
and leaving it would send the next reader hunting for a problem that has been designed
out.

- [ ] **Step 5: Build and test again**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed: 307`.

**If this fails with CS0246 on `IDialogService` or `IMudDialogInstance`, stop.** That would
mean the trap is still live and the qualifiers are load-bearing — restore them, and report
it, because it contradicts the spec's central claim about this rename.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Rename Workflow.MudBlazor to TaskRouter.Blazor and close the trap

A project whose namespace ends in MudBlazor makes a bare 'using
MudBlazor' resolve to itself, which fails only in hand-written @code
and @inject and points nowhere near its cause. Naming the project
Blazor removes the collision, so the global:: qualifiers come out.

The package no longer advertises MudBlazor in its id; the description
and tags carry that instead."
```

---

## Task 5: `Workflow.Tests` → `TaskRouter.Tests`, the solution, and the demo's qualifiers

**Files:**
- Rename: `tests/Workflow.Tests/` → `tests/TaskRouter.Tests/`, `WorkflowEngine.slnx` → `TaskRouter.slnx`
- Modify: `samples/DemoDocuments.Server/Components/_Imports.razor` (drop five `global::` qualifiers)

The demo carries the second instance of the same trap, for a different reason: it has its
own `DemoDocuments.Server.Workflow` namespace, so `using Workflow.Core.Model` inside it
bound to `DemoDocuments.Server.Workflow.Core.Model` and found nothing. Tasks 1–4 renamed
the library out of the way — there is no `DemoDocuments.Server.TaskRouter` — so those
qualifiers are now redundant too. The demo's own `Workflow` namespace is untouched.

- [ ] **Step 1: Move the test project and the solution**

```bash
git mv tests/Workflow.Tests tests/TaskRouter.Tests
git mv tests/TaskRouter.Tests/Workflow.Tests.csproj tests/TaskRouter.Tests/TaskRouter.Tests.csproj
git mv WorkflowEngine.slnx TaskRouter.slnx
```

- [ ] **Step 2: Rewrite the test namespace and project references**

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' \
  | xargs -0 sed -i 's/Workflow\.Tests/TaskRouter.Tests/g'
```

- [ ] **Step 3: Drop the demo's five qualifiers**

In `samples/DemoDocuments.Server/Components/_Imports.razor`, replace **lines 16–27** — a
seven-line comment whose first line is still worth keeping, followed by five qualified
usings — with this:

```razor
@* The builder components, and the models they bind to. *@
@using TaskRouter.Blazor
@using TaskRouter.Builder
@using TaskRouter.Runner
@using TaskRouter.Model
@using TaskRouter.Inbox
```

Keep that first line of the comment; drop the paragraph beneath it explaining the
`global::` requirement, which no longer applies.

- [ ] **Step 4: Verify no library namespace survives**

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' \
  | xargs -0 grep -n 'Workflow\.\(Core\|Persistence\|AspNetCore\|MudBlazor\|Tests\)' || echo "clean"
```

Expected: `clean`.

- [ ] **Step 5: Build and test**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed: 307`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Rename the test project and solution, and clear the demo's qualifiers

The demo's five global:: usings existed because its own Workflow
namespace shadowed the library's. The library is no longer called
Workflow, so they are redundant -- and the demo's namespace is
untouched."
```

---

## Task 6: The entry points

**Files:**
- Modify: `src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs` (`AddWorkflowEngine`)
- Modify: `src/TaskRouter.EntityFrameworkCore/WorkflowModelBuilder.cs` (`ConfigureWorkflowEngine`)
- Modify: `src/TaskRouter.AspNetCore/WorkflowEndpointExtensions.cs` (`AddWorkflowEndpoints`, `MapWorkflowEndpoints`)
- Modify: every call site — 32 across the demo, the tests and the docs comments

A consumer who installs `TaskRouter` and then calls `AddWorkflowEngine()` is looking at two
product names in one line. These four methods are the package's front door, so they take
its name.

- [ ] **Step 1: Rename all four, definitions and call sites together**

Order matters: `AddWorkflowEndpoints` must be rewritten before `AddWorkflowEngine`, or the
shorter pattern would not match it anyway — but running the endpoint ones first keeps the
two independent:

```bash
git ls-files -z '*.cs' '*.razor' | xargs -0 sed -i \
  -e 's/AddWorkflowEndpoints/AddTaskRouterEndpoints/g' \
  -e 's/MapWorkflowEndpoints/MapTaskRouterEndpoints/g' \
  -e 's/AddWorkflowEngine/AddTaskRouter/g' \
  -e 's/ConfigureWorkflowEngine/ConfigureTaskRouter/g'
```

- [ ] **Step 2: Confirm the old names are gone**

```bash
git ls-files -z '*.cs' '*.razor' | xargs -0 grep -n \
  'AddWorkflowEngine\|AddWorkflowEndpoints\|MapWorkflowEndpoints\|ConfigureWorkflowEngine' || echo "clean"
```

Expected: `clean`.

- [ ] **Step 3: Confirm what must NOT have changed**

`WorkflowEngineBuilder` is the return type of `AddTaskRouter()` and keeps its name, as do
the builder's own methods:

```bash
grep -c 'WorkflowEngineBuilder' src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs
grep -n 'AddOutboxProcessing\|AddDeadlineProcessing' src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs
```

Expected: a non-zero count, and both methods still present under their original names.

- [ ] **Step 4: Build and test**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed: 307`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Rename the four entry points to TaskRouter

AddTaskRouter, AddTaskRouterEndpoints, MapTaskRouterEndpoints and
ConfigureTaskRouter. Installing TaskRouter and then calling
AddWorkflowEngine read like two products stitched together.

AddOutboxProcessing and AddDeadlineProcessing keep their names: they
name the feature being switched on, not the product."
```

---

## Task 7: The 17 database tables

**Files:**
- Modify: `src/TaskRouter.EntityFrameworkCore/WorkflowModelBuilder.cs` (17 `ToTable` calls)
- Create: one migration under `samples/DemoDocuments.Server/Data/Migrations/`

This is the task that clears the collision with the original system. The original system already has a table called
`WorkflowTriggerDefinitions`, and the engine maps its own `TriggerDefinition` to a table of
exactly that name — two CLR types on one table name in one model. Renaming the engine's
tables removes it permanently, for the original system and for any future host with a `Workflow`-anything
table.

There is no workflow data anywhere, so nothing migrates.

- [ ] **Step 1: Rename the tables**

Every one of the 17 begins with `Workflow`, so a single anchored substitution does it
without touching column names:

```bash
sed -i 's/ToTable("Workflow/ToTable("TaskRouter/g' \
  src/TaskRouter.EntityFrameworkCore/WorkflowModelBuilder.cs
```

- [ ] **Step 2: Verify all 17 moved and none were missed**

```bash
grep -c 'ToTable("TaskRouter' src/TaskRouter.EntityFrameworkCore/WorkflowModelBuilder.cs
grep -n 'ToTable("Workflow' src/TaskRouter.EntityFrameworkCore/WorkflowModelBuilder.cs || echo "none left"
```

Expected: `17`, then `none left`. The resulting names are `TaskRouterTaskTypes`,
`TaskRouterDefinitions`, `TaskRouterDefinitionVersions`, `TaskRouterTaskDefinitions`,
`TaskRouterTaskOutcomes`, `TaskRouterTaskRoutes`, `TaskRouterTriggerDefinitions`,
`TaskRouterSubWorkflowAttachments`, `TaskRouterSubWorkflowInstances`, `TaskRouterRuns`,
`TaskRouterTasks`, `TaskRouterForkManifests`, `TaskRouterForkManifestEntries`,
`TaskRouterVariables`, `TaskRouterTaskLogs`, `TaskRouterTriggerExecutions`,
`TaskRouterOutbox`.

- [ ] **Step 3: Confirm the index filters were not touched**

They are written over columns, not tables, and must be byte-for-byte unchanged:

```bash
grep -n 'HasFilter' src/TaskRouter.EntityFrameworkCore/WorkflowModelBuilder.cs
```

Expected: two lines, `[ForkManifestId] IS NOT NULL AND [Status] <> 3` and
`[ReminderSentAt] IS NULL`.

- [ ] **Step 4: Generate the demo's migration**

The host owns the schema, so the demo generates its own migration. This needs the `dotnet-ef`
tool; if it is missing, install it with `dotnet tool install --global dotnet-ef`.

```bash
dotnet ef migrations add TaskRouterRename \
  --project samples/DemoDocuments.Server --context DemoDbContext --output-dir Data/Migrations
```

- [ ] **Step 5: Check the migration is a rename, not a drop**

Open the generated `*_TaskRouterRename.cs` and confirm it contains `RenameTable`
operations. If it contains `DropTable` followed by `CreateTable`, EF did not recognise the
rename — stop and report it, because on a host with real data that migration would destroy
it. (It is safe for the demo, which seeds from scratch, but the same migration shape is
what a consumer would generate.)

- [ ] **Step 6: Build and test**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed: 307`.

`MigrationTests` is the one that matters here — it fails if the model has drifted from the
migrations, which is exactly what a forgotten migration looks like. The other tests build
their databases with `EnsureCreated`, which reads the model rather than the migrations, so
they would pass either way.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Rename the 17 tables to TaskRouter*

This is what clears the collision with the original system, which already has a
WorkflowTriggerDefinitions table of its own -- two CLR types on one
table name in one model.

No workflow data exists anywhere, in the original system or the demo, so nothing
migrates. The tables will never be this cheap to rename again."
```

---

## Task 8: Documentation

**Files:**
- Modify: `README.md`, `STATE.md`
- Modify: `docs/superpowers/specs/2026-08-25-taskrouter-rename-design.md` (status line)
- Modify: the six earlier specs and six earlier plans (banner only)

Living documents get rewritten. Historical design documents get a banner instead: they are
dated records of what was decided at the time, and silently rewriting them would make them
claim a name that did not exist when they were written.

- [ ] **Step 1: Rewrite the living documents**

```bash
sed -i \
  -e 's/Workflow\.Core/TaskRouter/g' \
  -e 's/Workflow\.Persistence\.EF/TaskRouter.EntityFrameworkCore/g' \
  -e 's/Workflow\.AspNetCore/TaskRouter.AspNetCore/g' \
  -e 's/Workflow\.MudBlazor/TaskRouter.Blazor/g' \
  -e 's/Workflow\.Tests/TaskRouter.Tests/g' \
  -e 's/WorkflowEngine\.slnx/TaskRouter.slnx/g' \
  -e 's/AddWorkflowEndpoints/AddTaskRouterEndpoints/g' \
  -e 's/MapWorkflowEndpoints/MapTaskRouterEndpoints/g' \
  -e 's/AddWorkflowEngine/AddTaskRouter/g' \
  -e 's/ConfigureWorkflowEngine/ConfigureTaskRouter/g' \
  README.md STATE.md
```

- [ ] **Step 2: Read back the passages that need prose, not substitution**

`sed` cannot fix a sentence whose meaning changed. Read and hand-edit these:

- `README.md` — the title and opening description, which should now introduce the library
  as `TaskRouter`, and the **Layout** section, whose project list is now wrong.
- `STATE.md` — the **Where things live** section, and the two entries in **Things to be
  careful about** that describe the namespace trap and the SQL-Server-specific index
  filters. The namespace-trap entry must now record that the hazard was *designed out* by
  the rename rather than worked around, and that the `global::` qualifiers are gone.
- `STATE.md` — the table-name references in the convergence-index entry.

- [ ] **Step 3: Banner the historical documents**

For each of the six specs and six plans dated before 2026-08-25, insert this line directly
beneath the `**Status:**` line:

```markdown
> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.
```

- [ ] **Step 4: Mark this spec implemented**

In `docs/superpowers/specs/2026-08-25-taskrouter-rename-design.md`, change the status line
to:

```markdown
**Status:** Implemented 2026-08-25 — see `docs/superpowers/plans/2026-08-25-taskrouter-rename.md`
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Rename TaskRouter through the documentation

Living documents are rewritten; the dated specs and plans get a banner
instead, because rewriting them would make them claim a name that did
not exist when they were written."
```

---

## Task 9: Final verification

**Files:** none — this task only checks.

- [ ] **Step 1: Full clean build and suite**

```bash
dotnet build --no-incremental && dotnet test
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed! - Failed: 0, Passed: 307`.

- [ ] **Step 2: No old name survives in code**

```bash
git ls-files -z '*.cs' '*.razor' '*.csproj' '*.slnx' \
  | xargs -0 grep -n 'Workflow\.\(Core\|Persistence\|AspNetCore\|MudBlazor\|Tests\)' || echo "clean"
```

Expected: `clean`.

- [ ] **Step 3: No `global::` workaround survives**

```bash
git ls-files -z '*.razor' '*.cs' | xargs -0 grep -n 'global::' || echo "clean"
```

Expected: `clean`. Any survivor means a namespace collision was carried across rather than
closed, which is the one outcome this rename was supposed to prevent.

- [ ] **Step 4: The demo actually runs**

Razor and DI failures can survive compilation, so the suite alone does not prove this. The
demo must be started **from its project directory** — running the built DLL directly breaks
static assets and Blazor silently:

```bash
cd samples/DemoDocuments.Server && dotnet run
```

Load the workflow builder and the runner pages, confirm they render with styling and are
interactive, then stop the host and `cd` back to the repository root.

- [ ] **Step 5: The package ids are what we think they are**

```bash
for id in TaskRouter TaskRouter.EntityFrameworkCore TaskRouter.AspNetCore TaskRouter.Blazor; do
  echo "$id -> $(ls -d src/${id} 2>/dev/null || echo MISSING)"
done
```

Expected: all four directories present.

- [ ] **Step 6: Update STATE.md's status block and commit**

Record the rename as done, with the suite at 307/307, and note that the next steps are the
LICENSE and package metadata, then the the original system integration.

```bash
git add -A
git commit -m "Record the TaskRouter rename in STATE"
```

---

## Done when

- `dotnet build --no-incremental` reports 0 warnings and 0 errors.
- `dotnet test` passes 307/307, with no test's assertions modified.
- `git ls-files | xargs grep 'Workflow\.\(Core\|Persistence\|AspNetCore\|MudBlazor\|Tests\)'` returns nothing.
- No `global::` qualifier remains anywhere in the repository.
- `src/TaskRouter`, `src/TaskRouter.EntityFrameworkCore`, `src/TaskRouter.AspNetCore` and
  `src/TaskRouter.Blazor` exist, and `TaskRouter.slnx` is the solution.
- The 17 tables are `TaskRouter*`, and the demo has a migration containing `RenameTable`.
- The demo runs from its project directory and renders the builder and runner.
- `IWorkflowEngine`, `WorkflowTask`, `WorkflowRun` and `WorkflowEngineBuilder` still have
  their names.
