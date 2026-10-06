# Current State — read this first

Snapshot of where the engine stands, so work can resume without re-deriving
context. Last updated **2026-10-05**: the integration ergonomics pass — `IWorkflowDbContext`
needs no members of its own, `AddTaskRouterFor<T>()`, and startup wiring diagnostics —
shipped as `0.1.0-preview.5`, with a defect in it fixed by `0.1.0-preview.6`. Before that, workflow duplication, assignment business rules
and a filtered inbox index, as `0.1.0-preview.4`. Before that, `preview.3` and the host
consuming it with no version override. Before
that, pre-assignment and a second day of driving the host
application. Before that, the type-registry work, and a round of feedback
from somebody using it. Before that: shipping the runner and inbox, and acting on what the
integration spike found, twice — once on plan 1's seven findings and again on the two plan 2
produced. See the git log for the order things happened in.

**Where the session ended (2026-10-05):** `main` is clean and pushed, 0 warnings,
485/485 green. **Published: `0.1.0-preview.6`** of all four packages, verified against the
registry rather than against a green CI run. The host consumes it with no
`-p:TaskRouterVersion=` override: 0 warnings, 2,179 tests green across its five suites.

`0.1.0-preview.2` does not exist on nuget.org and never will. Its release run died on a
nuget.org read-only 503, and by the time that could be retried the tag pointed at a commit
older than the fixes worth shipping — so the work went out as `preview.3` rather than
force-pushing a public tag onto different content. The stale tag was deleted from GitHub on
2026-10-01, so the tags now match the registry: `preview.1`, `preview.3`, `preview.4`.

### `0.1.0-preview.6` — the defect preview.5 shipped, 2026-10-05

**`ValidateWiringAtStartup()` refused to start a correctly wired host.** The provider-level
check asked `IWorkflowEditorActorAccessor` for an actor id to prove it could answer. The
usual implementation reads the current user from the HTTP context and throws at startup
because there is no request in flight — correct behaviour, not a wiring fault — so the
check reported a problem and stopped the application.

Found by running the real host against `preview.5`, not by the suite: the demo's accessor
answers outside a request, so the test written alongside the feature passed. The regression
test now uses an accessor that behaves like a real one.

**Any host on `preview.5` that calls `ValidateWiringAtStartup()` will fail to start.**
Everything else in `preview.5` is unaffected — the interface change and
`AddTaskRouterFor<T>` are sound.

### What `0.1.0-preview.5` added — 2026-10-05

An ergonomics pass, prompted by the question "why does adopting this need so much knowledge
of how it works?" The measurement that framed it: a host hand-wrote **19 `DbSet`
properties**, and **35 wiring mistakes were documented in the library's own comments as
failing only at runtime**, with no startup validation anywhere. Nearly every friction point
was the same shape — the library knew the host had got it wrong, said nothing until the
first request, then said something unhelpful.

1. **`IWorkflowDbContext` needs no members of its own.** It asks for `Set<T>()`, `Database`
   and `SaveChangesAsync` — which `DbContext` already declares with exactly those
   signatures — and defaults all nineteen sets in terms of `Set<T>()`. A host context
   satisfies it with an empty body.

   This also dissolves the name-collision finding from the first integration spike. A host
   may still declare any set and its own wins; it is no longer forced to declare the other
   eighteen to do so. **Source-compatible**: a host that declares all nineteen is unaffected,
   which was verified against the real host before publishing.

   The trade-off, stated plainly: a default interface member is reachable through the
   interface, not the class. Host code wanting `context.WorkflowTasks` declares that one set,
   or uses `Set<T>()`. Measured on the real host — production code needed **1 of 19**.

2. **`AddTaskRouterFor<TContext>()`** registers the context mapping from the type argument.
   That mapping is the one registration nothing can infer, and omitting it failed on the
   first engine call rather than at startup.

3. **`ValidateWiringAtStartup()`** reports everything missing at once and names the fix for
   each. **It runs twice, deliberately.** The descriptor pass runs at registration time
   because it has to beat ASP.NET Core's own validate-on-build, which in Development trips
   first on a missing `IWorkflowDbContext` and reports it as eight services that could not
   be constructed — the actual cause repeated inside each and named nowhere. The provider
   pass runs as a hosted service for what descriptors cannot answer. Permissive defaults
   warn rather than fail: a check that refuses to start over a deliberate choice gets
   switched off, and then catches nothing.

   Verified by sabotage as well as by tests — removing the context mapping from the demo
   produces one sentence and the line to add.

### What `0.1.0-preview.4` added — 2026-10-01

1. **Duplicate a workflow.** Copies a version's whole graph onto a *new* workflow under a
   new name. The mechanism already existed — `CreateDraftVersionAsync` deep-copies a graph —
   so this is that pointed at a different target: clearing the definition id as well as the
   version id makes `PersistCoreAsync` mint a new `WorkflowDefinition` numbered from 1. The
   distinction worth keeping straight is which thing is new: a draft is the next *version*
   and supersedes on publish, a duplicate is a separate *workflow*. Pre-assignments do not
   come across and cannot — they hang off a run, not a version.

2. **`CarryAssignmentForward`**, the first entry in a new **Business rules** section of the
   builder. Decides who gets a step that names nobody and carries no role: the previous
   step's assignee, or nobody. **Defaults to off**, which changed behaviour for workflows
   that already existed — deliberately, and the migration says so.

   The section is kept either way. The inbox offers unclaimed work only to members of the
   task's own org unit, so dropping the unit too would hide the task from everybody.

   **The distinction that matters**, found by breaking sub-workflow delegation on the first
   attempt: an assignment somebody *chose* (delegation, an ad-hoc task against a person, a
   run started on a named actor, rework returned to whoever worked the branch) is always
   honoured; only one *inherited* from the previous step is governed. `AssignmentOrigin`
   carries it and is a required parameter, so a new creation path cannot default silently.

3. **The inbox index is filtered** to `[Status] IN (0, 1)`. It sat between two sweeper
   indexes filtered for exactly this reason and was not, so its size tracked total history
   rather than outstanding work.

**Archiving completed runs as JSON was considered and rejected** (2026-10-01). The numbers
say it solves the wrong problem: the workflow map is ~14 rows per version and fixed at
authoring time, so duplication does not threaten it, and runs are ~18 rows each — 1.8M rows
after a decade at 10k documents a year, which SQL Server does not notice. The cost would be
the thing this library exists for: queryable state. The levers, in order, are the filtered
index (done), then pruning `TaskRouterTaskLogs` (two thirds of row growth, and nothing in
the engine reads it back), then a relational archive table or partitioning on
`Runs.CompletedDate`. A denormalised run snapshot is only worth it as a display cache
*alongside* the rows, never instead of them.

This repository was started fresh at publication — the development history before that is
kept privately, because it was written while working inside the first host and names it
throughout.

**Where 2026-09-03 ended: `docs/superpowers/plans/2026-09-02-builder-over-http.md` is
complete, all five tasks. 361/361, 0 warnings.** The builder now works from both hosting
models, which was the critical path for a first host — without it nobody can author a
workflow at all.

- **The library ships the builder client.** `EfWorkflowBuilderClient`, 858 lines lifted
  wholesale from the demo after checking that not one of them touches a demo domain type.
  Exactly two things in it were host knowledge and both became seams:
  `WorkflowBuilderOptions.AssignmentRoles` and `IWorkflowEditorActorAccessor`. The latter has
  no working default on purpose — it throws and names what to register, because a placeholder
  would attribute every workflow edit in the system to a fiction.
- **Twelve routes** under `/workflow/builder`, mapped by `MapTaskRouterEndpoints` alongside
  the engine's, and **an HTTP client** in `TaskRouter.Blazor.Http` for a WebAssembly host.
  `TaskRouter.Blazor`'s packed dependencies are unchanged — no `IHttpClientFactory`, which
  would have meant a `Microsoft.Extensions.Http` dependency on a package whose reason to
  exist is running in a browser.
- **What the plan did not anticipate, and it is the useful part.** The endpoint handlers must
  mark the client `[FromServices]`. Without it minimal APIs infer the unregistered interface
  as a *body* parameter, a GET may not have one, and a host that mapped these endpoints
  without calling `AddWorkflowBuilder()` fails to start at all — with an error naming neither
  the builder nor the route. The doc comment claimed the opposite until a test said
  otherwise. `A_host_that_never_adopted_the_builder_still_starts` is the regression test:
  strip the attributes and it is the only test that fails.
- Task 1 was deliberately not construction. Nine tests proving the edit models survive JSON,
  before twelve endpoints were built on the assumption that they would.

README now documents which hosting model needs what, under "The builder, and which hosting
model needs what".

---

## 2026-09-05: the runner and inbox ship too, and the UI story is complete

`docs/superpowers/plans/2026-09-05-runner-and-inbox.md`, all eight tasks. **403/403, 0
warnings.** Every component in `TaskRouter.Blazor` — builder, runner, inbox — now works from
both hosting models with no host writing a client.

**The demo's 843 lines became 125** (about 71 of actual code): a `DemoDirectory` of four short
methods over `People` and `Sections`, and a `DemoSubjectResolver` of one query over
`Documents`. That was the plan's stated check on whether the seams were drawn in the right
place, with instructions to stop and report rather than trim to fit. It passed, and nothing
left in the demo looks library-shaped.

**Two seams, both breaking changes taken deliberately before publication:**

- **`IWorkflowActorResolver` grew from one method to four** — display name, who may be
  assigned to, what org units exist, which one an actor is in. This closes spike finding 4:
  it had been registered by `AddActorResolver<T>()` and consumed by *nothing* since the
  beginning, and the note said "give it a purpose or remove it before publication rather than
  ship it as decoration". This is the purpose.
- **`IWorkflowSubjectResolver` is new**, batched, turning `ChangeRequest:42` into a label and
  a link. The inbox cannot exist in the library without it — that is the whole job the seam
  was hiding.

**Where the line falls, as a worked example worth keeping.** The host is asked for *every* org
unit; the engine removes the ones a fork has already branched on. The host writes a one-line
projection it cannot get wrong, and the fork-state rule stays where the engine already
enforces it. The test hands over a directory returning everything, so it fails if that
filtering is ever pushed back across the seam.

**Two bugs found, both by testing against a real server rather than a mocked handler:**

1. **`DeleteTestRunAsync` threw `DbUpdateConcurrencyException` over HTTP** while passing in
   process. The tasks are never tracked — only their ids are selected — so EF does not know
   `run → task → log` is a chain and is free to order the run's `DELETE` first; the database
   cascade then removes the log rows and EF's own `DELETE`s affect zero. It survived in the
   demo purely by accident: one shared `DbContext` meant the tasks happened to be tracked.
   Fixed with two saves in dependency order.
2. **`[FromServices]` again**, exactly as with the builder. Verified load-bearing by
   stripping the attributes and confirming precisely one test fails.

**And one mistake worth remembering:** `InboxClientTests.cs` was overwritten by a `cat >` on a
file assumed to be new, destroying 11 tests. **Nothing failed** — the suite total dropped when
the arithmetic said it should rise, and that is what caught it. Recovered from git and merged.
Check whether a test file exists before creating it.

---

---

## 2026-09-09/10: what a real host asked for

Four days of a host actually using the library. Everything below came from that rather than
from planning, which is the argument for having done it before publishing.

**Added:**

- **`WorkflowBuilderOptions.SubjectTypes`** — the "applies to" field was free text, so an
  author typing an abbreviation instead of the subject type's exact spelling produced a
  workflow nothing ever offered, with nothing on screen to say why. Same shape and reason as `AssignmentRoles`.
- **`CancelRunAsync`** — `WorkflowRunStatus.Cancelled` was in the model and unreachable. The
  only way to stop a workflow was cancelling each task by hand, which marks the run
  **Completed** — a lie no report can see through. Its own `WorkflowOperation.CancelRun`,
  because ending everyone's work is not a repeated version of cancelling one task, plus
  `WorkflowEventKind.RunCancelled`.
- **`DialogOptions` at every `ShowAsync`** — the library's dialogs specified no size and
  inherited the host's `MudDialogProvider`. One real host runs `FullWidth` with
  `MaxWidth.False`, which stretched a two-field reassign form across the window.
- **A note on the builder's version panel** saying that saving a published version forks the
  next one. `PersistAsync` always did this; nothing said so, so a host had to either forbid
  opening a published version or let the new version arrive as a surprise.

**Fixed:** `TaskRouter.Blazor` floored `Components.Web` at 10.0.11 while every other package
sat at 10.0.10, which failed the host's restore outright with NU1605. A library's
`PackageReference` is a floor, and this one was accidental drift.

**A distinction worth keeping.** The host asked for "one version for this unit and another
for that one" of a workflow. Those are **variants**, not versions — different processes for one subject type,
which are separate definitions. Versions are revisions of one process, and a run pins to the
one it started on. Conflating them is the natural mistake and it changes what the UI should
offer: a picker of workflows must list definitions, never versions, because the engine always
starts `IsPublished && IsLatest`.

## 2026-09-16: six pieces of feedback from somebody using it

All six came from the owner driving the host application, not from planning. Four are
library changes; the host-side two are recorded in the host's own integration notes.

**Three deliberate rules were reversed, each at the owner's instruction.** They are listed
first because a reader who remembers the old reasoning should find out here rather than by
being surprised by the code.

1. **`/inbox/items` takes `?orgUnits=`.** The rule was that no route accepts branch keys,
   because guessing them would enumerate another unit's unclaimed work. What changed is the
   use case: work handed to a section by a sub-workflow or a fork waits there unclaimed, and
   somebody covering for that section could not see it had arrived. The replacement guard is
   that requested units are checked against the host's own directory, so a guessed key
   matches nothing; a host wanting per-person scoping implements `IWorkflowInboxClient`
   itself. **The raw `/inbox` route still takes no keys** — it has no client in front of it
   to do the checking.
2. **Starting a sub-workflow takes an org unit as well as a person.** The rule was that only
   an actor id crosses, and the client derives the unit, because a caller naming a unit could
   put work where the person is not. That is the normal case rather than the abuse: every
   role inside a sub-workflow resolves against the unit the chain belongs to, so withholding
   it left whole chains resolving against whichever section the named person happened to sit
   in — and there was no way to hand a chain to a section at all.
3. *(host-side)* The host's facade refused to start a workflow in a section the document has
   no work item in. Reversed for the same reason the owner gives for both of the above: the
   manager picks. See the host's notes.

**Changed in the library:**

- **`IWorkflowRunnerClient.GetOrgUnitsAsync`** — every org unit, unfiltered, for the reassign
  picker. Deliberately not `GetBranchOptionsAsync`, which is scoped to a task and removes the
  units a fork has already branched on: reassignment is not forking, so filtering by fork
  state hides valid choices. Without it the reassign dialog asked a person to *type* a branch
  key, which is an opaque host id — often a row id, so the dialog was asking for "1" from
  somebody who thinks in the unit's name.
- **`StartSubWorkflowAsync` takes `assignToBranchKey`.** Unit and person, either or both.
  Neither still inherits the parent; a person with no unit still falls back to theirs.
- **`RunTaskView.SubWorkflowInstanceId`**, and the runner nests delegated tasks under the
  task that spawned them. A sub-workflow's tasks live in the *same run* as their parent, so
  the flat list mixed delegated work in among the workflow's own steps with nothing to say
  which was which. Grouping is done in the component rather than the seam — a host rendering
  its own runner may want the flat list.
- **`IWorkflowInboxClient.GetOrgUnitsAsync`**, and a section picker in `WorkflowInbox`. One
  call answers both halves a picker needs: the list to offer and which are the viewer's own.
- **The builder can add a task type.** `CreateTaskTypeAsync` had been on the seam since it was
  written with nothing calling it, so a new kind of step needed a developer. One field — the
  key is derived from the name (letters and digits only) and shown while typing — and saved
  immediately, because a task type is a row other workflows share.

**The consequence worth remembering:** a host mapping the engine's task-type keys onto a
closed enum of its own now gets keys it has never heard of, because authors can add them.
The first host printed "None" for one. Its fix was to carry the engine's own `DisplayName`
through to the screen, which is the better label in every case — it is the name the author
gave that step.

**The same gap for outcome keys was closed on 2026-09-16** by
`WorkflowBuilderOptions.Outcomes`: configured, the outcome key field becomes a picker;
empty, it stays free text, which is right for a host whose outcomes are genuinely open. An
outcome key is the sharper of the two — it is what a route matches on, so a key nobody routes
on is a step that completes and stops the workflow dead, and a key the host cannot translate
took out a whole screen with a 500. A task already carrying a key the host does not list keeps
it and says so, rather than having it stripped: runs completed on that key and its routes
still match.

## 2026-09-23: types became data, and driving it found four bugs

The host owner settled the open question — **stop translating** — and the type-registry
design was built on top of it. Then an afternoon in the browser found four defects, three of
them real and one that only looked like one. That ratio is the argument for driving the
thing.

### Routes point at an outcome, not at its name

`TaskRoute.OutcomeKey` became `TaskOutcomeDefinitionId`, a foreign key to a declared outcome
of the same task. Three things were wrong with the string:

- A route could name an outcome its task never declared. Completion validates the outcome
  first, so such a route could never fire — a dead edge nobody could see.
- Renaming an outcome silently stopped every route on it firing.
- The comparison happened **in SQL**, so whether `Approved` matched `approved` was the
  database collation's business, while the same comparison in a host's route condition, in
  C#, was ordinal. The same key could match in one place and not the other. The first host's
  database has both spellings in it.

Safe to key on an id because **a published version is an immutable deep copy** — drafting
re-materialises every task, outcome and route, so these ids never move under a run pinned to
the version. Ids do not travel *between* versions, which is why the key survives on
`WorkflowTask.OutcomeKey` and in trigger conditions: across versions the key is an outcome's
only identity.

**The migration is hand-edited and must stay that way.** The scaffold drops the old column
before adding the new one, which would leave every route pointing at nothing. Add, backfill on
the pair the engine used to match, prune routes that could never fire, then drop.

### Outcomes have a catalogue

`OutcomeTypeDefinition`, mirroring `TaskTypeDefinition`. Keys are trimmed and lower-cased on
write — the fix for `Approved`/`approved` at the source rather than at the comparison.

**Deliberately not a foreign key from `TaskOutcomeDefinition`.** A task copies the key into
its version; the catalogue governs what can be *authored*, never what a running workflow
means. A test retires an outcome the seeded workflow routes on and checks the workflow is
untouched.

`WorkflowBuilderOptions.Outcomes` changed meaning rather than being deleted: it used to be
*the* list, and is now "of the catalogue, the ones this host can read back" — useful only to
a host that translates outcome keys.

**Not built, deliberately:** `Source` (standard vs host) and `SeedStandardTypesAsync`. Both
existed to tell seeded rows from added ones; the owner skipped the starter set, so every row
is author-created and the column would have no reader.

**Archiving turned out to be half-built already** — `IsArchived` has been on every entity from
the start and the pickers already filtered it; nothing could ever *set* it. Now it can, from
`WorkflowTypeManager`, a component a host puts on a page of its own.

### The four bugs

1. **Forking inside a sub-workflow could never succeed.** `GetConvergenceOptionsAsync` and
   `GetAdHocOptionsAsync` took the version from the **run**, and a run is pinned to the
   mainline version — so a delegated task was offered the mainline's task definitions and the
   engine then refused the fork, naming the version rather than the picker. Both read the
   version from the task's own definition now.
2. **A blocking child superseded by a fork blocked its parent for ever.** The guard listed
   finished statuses as "not Completed and not Cancelled"; `Forked` is a third. Every sibling
   predicate in the engine had all three — this one predated `Forked`. It is a **positive**
   list now, which is what the inbox's own comment has recommended since it was written:
   *"the negative form silently admits any status added to the enum later, and Forked is
   exactly the value somebody writing this by hand leaves out."* That comment predicted this
   bug. Second time the distinction has cost something.
3. **Progress read "8,000%".** `RunView.PercentComplete` is 0–100; the runner formatted it
   with `P0` and multiplied it again for the bar. The value was never wrong — the unit was
   never stated. It is now, on the record, with a test pinning the contract.
4. **Not a bug:** "the fork converged and the sub-workflow still blocks". A fork spans every
   step between the forked task and the convergence point, so each branch finishes its own
   chain first. The panel said "3 done" beside "3 outstanding", which reads as a
   contradiction; the chip now says "3 step(s) before convergence".

### Open, found while driving

**A document whose workflows have all finished cannot show them.** `DocumentWorkflowPanel`
puts the only way into the runner — the *Manage/View Workflow* button — inside its
"something is live" branch, so once every run is completed or cancelled there is no route to
the history. The read-only branch also says "No workflow has been started on this document",
which is false for a document that has run three. The runner component itself is fine: it
lists every run for a subject with tabs and renders finished ones. The fix is host-side and
small — see the host's notes.

## 2026-09-30: published

### Plan 4, then the publish track

The four `ProjectReference`s into a sibling checkout are gone — the host consumes
`0.1.0-preview.1` as packages. Verified rather than assumed: the locally-packed `.nupkg` files
were removed from the folder feed **and** `~/.nuget/packages/taskrouter.*` deleted, so nothing
local could satisfy the restore. All four then came from nuget.org, and the host built, passed
its full suite, and ran against them.

**Two things caught before they became permanent:**

- **The package URLs pointed at the wrong account.** `ProjectUrl` and `RepositoryUrl` said
  `github.com/fsossi`, which exists and belongs to somebody else. The publishing account is
  **`F-Sossi`** on GitHub and **`fsossi`** on nuget.org — two names that look like typos of
  each other, and this is the one place the distinction bites. Those URLs are baked into every
  published package.
- **Everything in the repository goes public, not just the packages.** The working tree was
  scrubbed, but 231 commits of history still named the host in 36 of them and its role keys in
  35. Scrubbing a working tree does nothing about history, so the public repository was started
  from a **fresh `git init`** with one commit.

### How it publishes

**Trusted Publishing, not an API key.** The workflow asks GitHub for a short-lived OIDC token;
nuget.org validates it against a policy naming the repository and `release.yml` and returns a
key good for an hour. Nothing long-lived is stored. The one secret is `NUGET_USER`, the
nuget.org profile name, which is not itself sensitive.

Two workflows, both standing up a real SQL Server service container because this suite is
integration-first on purpose. `release` fires on a `v*` tag and re-runs the whole suite before
pushing, since a tag can be moved and publishing cannot be undone. Both went green first time.

### Three defects the published packages then surfaced

Found by driving the host against the real packages, and **all three are in
`0.1.0-preview.1` as published**:

1. **Discarding a test run 500'd** once it had a delegated chain — `SubWorkflowInstance` points
   at its parent task with `Restrict`, so the run's cascade could not clear it.
2. **The vendored Mermaid was unreachable** — the module asked for
   `_content/Workflow.MudBlazor/…`, left behind by the rename. Masked in the first host because
   it loads its own copy of Mermaid; the sample, which does not, is where it shows.
3. **The diagram had no colour**, only shape.

All three were fixed, and shipped in **`0.1.0-preview.3`** along with the defects below.

### What `0.1.0-preview.3` fixed — 2026-09-30

1. **Testing a draft made it permanently unpublishable.** The one that matters. Saving a
   draft rewrites its graph from scratch, and `ClearGraphAsync` assumed nothing could
   reference the rows it deletes — the comment said so: *"published versions never reach
   here"*. Test runs break that assumption. They pin to the draft, and their tasks point at
   the very task definitions the rewrite destroys, so the save failed on a foreign key and
   surfaced as a 500 with no way back for that draft.

   A draft's test runs are now discarded before the rewrite, which is the only coherent
   answer available: a re-save mints new task definition ids, so the run has nothing left to
   point at and no sense in which it still runs that workflow. The teardown is the one
   `DeleteTestRunAsync` already had, extracted to `TestRunTeardown` rather than duplicated —
   its ordering is load-bearing and there should be one copy. A non-test run reaching that
   path throws instead of deleting anything.

   Found from the host, reproduced against the real database, and pinned by
   `A_draft_that_has_been_tried_out_can_still_be_saved_and_published`.

2. **The diagram fought Mermaid's stock themes.** `dark` and `default` ship opposite
   palettes, so the diagram changed character with the page. It now uses `theme: 'base'`
   with an explicit palette, as the first host's own `mermaidInterop.js` did.

   The division that matters, and the one that caused the two follow-up bugs: **anything
   drawn on a node follows the node; anything drawn on the page follows the theme.** Node
   fills are saturated with white labels — the one combination that survives being generated
   in `WorkflowMermaid.cs`, where the theme is unknown — and ordinary steps take the
   application's own primary so the diagram belongs to the page it is on. Lines and edge
   labels follow the theme and so live in `workflowDiagram.js`, where `dark` is known.

   Pale fills with dark text were tried first and are a trap: they look clean on a dark page
   and vanish on a light one. Then edge labels came out white-on-white in light mode, because
   Mermaid styles node and edge labels with a *single* rule —
   `.label text, span { fill: nodeTextColor }` — and `nodeTextColor` falls back to
   `primaryTextColor`, which is white here for the node fills. Classed nodes override it;
   edge labels have no class. `nodeTextColor` is now set explicitly and tracks the page.

3. **The pre-assignment row rule broke mid-table.** The cell carried `d-flex` on a `<td>`,
   which takes it out of table layout; on a row whose step cannot be pre-assigned that cell
   is empty, so it collapsed and took the row's rule with it. The flex box moved to a `div`
   inside the cell.

**A caution for any future UI review: Dark Reader is installed in the browser.** It rewrites
colours *and* reports its own substitutions as the computed style, so a page can look wrong
and measure wrong while the code is right. It cost a full misdiagnosis of the diagram here —
the conclusion that Mermaid's `classDef color:` is inert for SVG labels was wrong. Judge
colour in a clean profile.

## Which of the builder's types could be data — 2026-09-16

The owner wants authors to be able to create the types the builder offers — outcome types,
task types, and so on — and asked what is library and what is host before going further.
This is the answer, recorded because it is the map the next few changes follow.

**Where each dropdown's list comes from:**

| Type | List comes from | Stored | User-creatable? |
|---|---|---|---|
| Task type | `TaskRouterTaskTypes`, a real table | Library | **Yes**, since 2026-09-16 |
| Outcome | *No list exists.* Rows hang off each task definition; the new picker reads a host config list | Per task definition | Partly |
| Subject type | `WorkflowBuilderOptions.SubjectTypes` | Nowhere | Only with host code |
| Assignment role | `WorkflowBuilderOptions.AssignmentRoles` | Nowhere | Only with host code |
| Route condition | The DI registry — every registered `IRouteConditionEvaluator` | Code | Never |
| Trigger | The DI registry — every registered `IWorkflowTrigger`'s descriptor | Code | Never |

**The line runs between the first two rows and the rest: whether anything has to *interpret*
the string.** A task type is a label and nothing branches on it, which is why it was safe to
open up. An outcome key is matched by routes — string equality, inside the engine — so the
engine accepts any key; it is only constrained when a *host* translates it. A subject type a
user invents matches no document ever, because the host is what starts runs with that string.
A role key a user invents resolves to nothing and the engine keeps the previous assignment,
logging and continuing — it fails soft and silently. Conditions and triggers are code: a key
with no implementation is not inert, the route is skipped or the trigger never fires.

**Triggers already offer only what is implemented**, and need no permission tier to keep it
that way — both lists are derived from DI rather than configuration, so there is no way to
name one that does not exist, and `TriggerDescriptor` carries the parameters so the builder
renders a form rather than a JSON box. What a higher tier *would* buy is hiding the sharp
edges from ordinary authors: dispatch mode, failure policy, raw config. That is a host
authorization decision — the endpoints already take an `AuthoringPolicy` — not a library one.

**The library work this implies is one thing: an outcome-type table**, mirroring
`TaskTypeDefinition`, with the same Add affordance in the task dialog. Today outcomes are
per-task rows with no registry at all, so "the outcomes this organisation uses" does not exist
as an object — an author retypes `approved` on every task and a typo is a silent dead end.
`WorkflowBuilderOptions.Outcomes` (added earlier the same day) is a stopgap that lets a host
with a closed vocabulary constrain the field; a table would make outcomes a first-class type
the way task types now are, and the option would become the way a host *narrows* that table
rather than the only list there is.

**Subject types and assignment roles should stay host config.** They are the two where a
value a user invents is meaningless without code behind it.

**Blocked on a host decision**, recorded in the host's own integration notes: the first host
translates engine keys into two closed enums of its own, and those enums turn out to have
almost no consumers. If it stops translating, task types and outcomes are freely creatable
end to end and the host's list shrinks to what genuinely needs translating. The owner is
considering it; the removal list is written at
the host's own integration notes, and the answer is that it is **code-only**
— task definitions reference their task type by id and routes match outcomes by string, so
no data moves either way.

**The design is written: `docs/superpowers/specs/2026-09-22-type-registries.md`**, and it
works under either answer, which is why it was written first. Its two decisions:

- **A `Source` flag, not a namespace in the key.** `TaskTypeDefinition.Key` already carries a
  globally unique index, so a seeded `review` and a host's `review` are the *same row* — a
  namespace would turn that collision into a silent duplicate, which is worse, while making
  the string that routes match and hosts translate longer everywhere. What was actually wanted
  is idempotent seeding that never overwrites, a way to tell shipped from added, and
  retirement; a `Source` column plus `IsArchived` gives all three. A namespace would be right
  only if one database served several tenants, and then it belongs in a `Tenant` column, not
  inside the key.
- **The outcome registry is a picker source, not a foreign key.** Per-task outcome rows keep
  their `OutcomeKey` string. If they referenced the registry by id, renaming a registry row
  would change what every route matches — silently, for workflows already running — because
  `TaskRoute.OutcomeKey` is compared as a string.

## Next session

Nothing is blocking a host any more; what is left is publication and two features.

1. ~~**CI, SourceLink, symbol packages**, the GitHub repository and the first publish.~~
   ~~**Cut the next preview.**~~ **Both done 2026-09-30.** `0.1.0-preview.3` is on nuget.org
   and the host consumes it unpinned. This track is finished; releasing is now just
   bump `VersionSuffix`, tag `v<version>`, and let `release.yml` do the rest.
2. **Document transaction composition.** `WorkflowTransaction` already behaves correctly under
   a host-owned transaction and nothing says so; the joined path leaves
   `IWorkflowPostCommitActions` to the caller, which a host will not guess.
3. ~~**Pre-assignment**~~ — **built 2026-09-23**, as specced. See the section above. The
   spec's open question 2 was answered by the host: the view belongs in the runner.
4. **Timers** — the one roadmap item still unbuilt.
5. The 708 undocumented public members, deferred from the packaging pass.

**In the host application's own repository:** plans 3b and 3c are
both done, and the facade settled at six methods rather than the old interface's eighteen —
the runner component covers fourteen of them. What is open there is the feedback round of
2026-09-16 and the items listed at the end of
the host's own integration notes.

**The owner is reviewing the six changes above before the next round**, so expect the next
session to start with a reaction to them rather than with this list.

**Also open:** the outcome-type table described in the section above, once the host decides
whether it is still translating outcome keys into an enum of its own.

### The rule that has now cost two sessions

Every minimal-API handler taking a library interface must mark it `[FromServices]`. Without
it, an unregistered type is inferred as a **body** parameter; a GET may not have one; the host
fails to start with an error naming neither the service nor the route. It applies to any
endpoint file added from here.

---

**2026-09-02: the bulk subject read is built.** `GetCurrentStateForSubjectsAsync` — the read a
document list view needs, and the one whose absence made a host's first move be to bypass the
read API for the DbContext. Grouped by run and named, so concurrent workflows on one subject can
be shown separately, with a `ForkView` choosing whether a forked step reports its branches (what
an inbox wants) or its origin with an open-branch count (what a list wants). Spec, including
what changed while building it, is in `docs/superpowers/specs/2026-08-31-bulk-subject-reads.md`.

**Where 2026-08-31 ended.** Plan 3a is complete: a host document now starts a run, advances a
step, and reads back through the host's own vocabulary — the first time any host seam has run
against a real workflow. It immediately found that nothing could put an org unit on a run, so
role-based assignment never resolved; fixed here as an optional `WorkflowAssignment` on
`StartRunAsync`, finding 10 below.

**Pick up at one of three**, in no forced order:

1. **Build the bulk subject read** — `docs/superpowers/specs/2026-08-31-bulk-subject-reads.md`.
   Specified, decided, not built. Additive. It is the read a document list view needs, and its
   absence is why a host's first move is to bypass the read API for the DbContext.
2. **The host's remaining facade methods** — thirteen of eighteen, plus composing several
   operations under a host-owned transaction.
3. **The workflow builder.** On the host side this became critical path rather than later work:
   definitions are authored by customers in the UI, so without `TaskRouter.Blazor` and an
   `IWorkflowBuilderClient` implementation there is no way to create a workflow at all. Likely
   the largest of the three and the least explored.

The one library task still outstanding from the spike is small: **document that engine
operations compose under a host-owned transaction**, and that the joined path leaves
`IWorkflowPostCommitActions` to the caller. `WorkflowTransaction` already behaves correctly and
nothing says so.

---

**Earlier note — plan 3a on the host side** — seed a host workflow definition,
start a run on a real document, complete a task, read it back. That is the first time any
host seam runs against an actual workflow; everything the spike has proved so far is unit
and registration tests. The one library-side task outstanding from it is small and worth
doing first: **document that engine operations compose under a host-owned transaction**, and
that the joined path leaves `IWorkflowPostCommitActions` to the caller. `WorkflowTransaction`
already behaves correctly — nothing on `IWorkflowEngine` or in the README says so, and a host
that wraps a sequence without running post-commit actions loses every after-commit trigger
silently.

**The integration spike is underway and has already earned its keep.** The engine
now runs inside a real host application's own `DbContext` — referenced, schema
created by that host's EF migration, engine resolved from that host's DI container,
a run started and its entry task created, all against SQL Server. It found seven
things about this library's public API that a second pair of eyes on this side would
not have.

**Nine findings so far, all settled** — see
[What the integration spike found](#what-the-integration-spike-found). Seven came from the
foundation and six of those were documentation; the two-phase publish became
`PublishWithEntryTaskAsync`. Two more came from implementing the host seams: neither sink was
told who acted (fixed, breaking, done pre-publish) and progress could not report several
branches at once (fixed additively with an `all-branches` scope).

**No public API has been renamed**, and the two breaking changes are behind us. The remaining
pre-publish work is CI and the GitHub repository — plus whatever plan 3's facade turns up,
which is the last exercise likely to move the API.

The spike's own notes, plans and findings live with the host application rather than
here, since they name that system throughout. This repo keeps only what is true of
the library.

**The library is now called TaskRouter.** `Workflow.Core` and the bare `Workflow`
were both taken on nuget.org, so the family was renamed before publishing rather
than after, while it still had no consumers and no data. Packages:
`TaskRouter.Core`, `TaskRouter.EntityFrameworkCore`, `TaskRouter.AspNetCore`,
`TaskRouter.Blazor`. Entry points are `AddTaskRouter()`, `AddTaskRouterEndpoints()`,
`MapTaskRouterEndpoints()` and `ConfigureTaskRouter()`. Domain types — `IWorkflowEngine`,
`WorkflowTask`, `WorkflowRun` — deliberately kept their names; a workflow is what they
model. All 17 tables are `TaskRouter*`.

The seeded "Technical Review" sub-workflow is now three steps: a role-less **Get Info**
entry, then the Section Lead review, then the branch review. The entry declares no role on
purpose — it is what a delegated person lands on, and the section they bring is what the
two reviews above it resolve against.

## Where things live

| Path | What |
|---|---|
| `src/` | The four shipping packages: Core, EntityFrameworkCore, AspNetCore, Blazor |
| `samples/DemoDocuments.Server` | The reference host. Every seam is implemented here |
| `tests/` | One project, integration-first against SQL Server |
| `docs/` | Architecture, specs and the build plans behind each feature |

This engine was extracted from an existing internal application after a review of its
workflow subsystem — nineteen findings about what was wrong with it, and an analysis of what
coupled the engine to that application. Those documents are not published; what they
concluded is in `docs/` and in the comments, which is where the reasoning belongs anyway.

## Status

**On `main`: builds clean with 0 warnings, 333/333 integration tests pass** against SQL
Server. Every project, `TaskRouter.Blazor` and `TaskRouter.AspNetCore` included, is in the
solution.

`workflow-aspnetcore` was merged into `main` on 2026-08-25 and the branch is gone; the
section below is kept as the record of what it did and why. The `authorization` branch
was found to be fully merged already and was deleted the same day, so `main` is the only
branch.

The demo was run after the rename and verified by hand, not just by the suite: it serves
its pages, MudBlazor's CSS resolves, and the vendored Mermaid and the diagram module both
load from the renamed RCL's new `_content/TaskRouter.Blazor/…` paths.

> **The original system has no legacy workflow data.** The workflow feature is new and that system is in
> early development, so there is no in-flight state to migrate. The cutover is a
> wiring exercise, not a data project. Earlier notes about migration risk no longer apply.

```bash
source "$WORKFLOW_DEV_ENV"    # provides SA_PASSWORD
cd <repo root>
dotnet build && dotnet test
```

The tests need the SQL Server dev container running (`$WORKFLOW_DEV_DB_START`).
Each test class creates and drops its own database.

Running the demo, which is the fastest way to see any of this:

```bash
source "$WORKFLOW_DEV_ENV"
export ConnectionStrings__Demo="Server=localhost,1433;Database=WorkflowDemo;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;Encrypt=False"
cd samples/DemoDocuments.Server && dotnet run --urls http://localhost:5199
```

| Page | What |
|---|---|
| `/workflows` | Every workflow version, mainline and sub-workflow, published and draft |
| `/workflows/{versionId}` | The builder: tasks, routes, triggers, diagram, and a **Try it** pane |
| `/documents` | Stand-in documents |
| `/documents/{id}` | A document, with the runner driving the workflow on it |

`dotnet-ef` is installed globally (10.0.10); migrations live in
`samples/DemoDocuments.Server/Data/Migrations`.

### Packaging, as of 2026-08-26

`LICENSE` is MIT. Shared package metadata lives in the root `Directory.Build.props`
(authors, copyright, licence expression, project and repository URL, and the
`0.1.0-preview.1` version); `src/Directory.Build.props` imports it and turns on the
things only the four shipping projects want — `IsPackable`, `GenerateDocumentationFile`,
the packed `README.md`, and the shared tags. Per-project `PackageId`, `Description` and
any extra tags sit in each `.csproj`. Samples and tests inherit `IsPackable=false`.

`dotnet pack -c Release` produces all four `.nupkg`s with `lib/net10.0/*.dll` and the
matching `.xml` docs. **net10.0 only, deliberately** — widening to net8/net9 later is a
non-breaking change, so nothing is lost by not doing it before there is a consumer
asking. **Pre-1.0 deliberately** too: the public API stays free to move until the
integration spike has stressed every seam.

Two things to know about the doc build:

- **`CS1591`/`CS1573` are suppressed in `src/Directory.Build.props`.** Turning on
  `GenerateDocumentationFile` surfaced **708 undocumented public members** — heaviest in
  `Model/Entities.cs` (145), `Runner/IWorkflowRunnerClient.cs` (72), `Builder/EditModels.cs`
  (72), `Model/Enums.cs` (53), `WorkflowEndpointRequests.cs` (52) and
  `WorkflowEngine.Reads.cs` (49). Suppressed so the 0-warning build survives; the gap is
  real debt and the `NoWarn` should come off once the surface is documented.
- **Eight XML doc comments were being silently dropped** (`CS1587`) in `Snapshots.cs`,
  `IWorkflowRunnerClient.cs` and `WorkflowEngine.Inbox.cs`. They sat above positional
  record parameters, which is not a valid position — C# wants `<param name="X">` on the
  record declaration. Moved there, and they now ship in the `.xml`. **Anything documenting
  a positional record parameter must be written as a `<param>` tag on the type**, or the
  prose is written and never shipped.

Note that `dotnet pack` on the solution warns that `DemoDocuments.Server` cannot be
packaged. That is `IsPackable=false` doing its job; CI should pack `src/**` rather than
the solution to keep it quiet.

### Done

- `TaskRouter.Core` — model, abstractions, validator, own `Result` (no internal package names)
- `TaskRouter.EntityFrameworkCore` — entity configuration, engine, transaction helper
- Engine operations: start run (on the latest published version, or on a named one
  for a test run), complete + route, cancel, reassign, add ad-hoc, fork, convergence,
  selective rejection with re-fork, add branch to fork, start a sub-workflow, run
  queries, and the read side (valid outcomes, task logs, child tasks, fork context,
  fork manifests, sub-workflow options and instances, notes editing)
- **`IWorkflowEngine` now covers everything the original system's `IDocumentTaskService` does**,
  minus the deliberately-removed status setter
- **`PublishWithEntryTaskAsync`** — publishes a hand-built version with its entry task in
  the order EF requires, validating before it sets the publish flags. From finding 5 of the
  integration spike; the demo seeder uses it for both the mainline and the sub-workflow
- `DemoDocuments.Server` — API host mirroring the original system's document system:
  `DocumentBase` TPH root with `ChangeRequest` and `Drawing`, `WorkItem` with
  clamped completion, `Group`, `Section`/`Branch`/`Person`, and an API surface that
  mirrors `DocumentTasksController` route for route
- **`DocumentTaskService`** — the integration guide. The original system's service shape
  implemented on `IWorkflowEngine`; every method is subject mapping, vocabulary
  translation, or a straight delegate
- **Trigger runtime** — registry with startup duplicate detection, descriptor-driven
  typed configuration, ordering, conditions, failure policy, and all three dispatch
  modes. After-commit runs through a transactional outbox with leases, exponential
  backoff and at-least-once delivery keyed on an idempotency key. Every attempt is
  recorded on `TriggerExecution` with its real outcome.
- **Built-in triggers**: `workflow.setVariable`, `workflow.reportProgress`,
  `workflow.notify`, `workflow.webhook`
- **Progress calculation** — `WorkflowProgress.Calculate`, scoped per run or per
  branch so numerator and denominator always describe the same thing
- **Workflow variables** — read and write, used by triggers and route conditions
- **`AddTaskRouter()`** DI extension with a fluent builder for host seams
- **`TaskRouter.Blazor`** — the builder UI, ~900 lines, hosted and rendering. See
  [Where the builder UI stands](#where-the-builder-ui-stands) below.
- **`DemoBuilderClient`** — the in-process `IWorkflowBuilderClient`. Loads a version
  into the edit model and back, and enforces the two rules the builder depends on:
  a published version is never edited in place, and a draft may be saved while still
  incomplete.
- **Blazor Server in `DemoDocuments.Server`** — `/workflows` lists versions,
  `/workflows/{id}` opens the builder, `/workflows/new` starts an empty one. The API
  surface is unchanged and still there.
- **Diagram and task ordering** — `WorkflowLayout` orders tasks by longest path from
  the entry task so the list reads in execution order, and `WorkflowMermaid` renders
  the same graph as a Mermaid flowchart. Both live in `TaskRouter.Core`, so they are
  testable without a browser and a host can draw a workflow anywhere it likes.
- **Subject type is versioned.** It sits on `WorkflowDefinitionVersion`, not on
  `WorkflowDefinition`, because it is behavioural: hosts resolve which workflow to
  start by matching it. Name and description stay on the definition and are shared by
  every version on purpose — they identify the logical workflow. The builder shows the
  two groups in separate panels, labelled.
- **Cancel cancels.** The edit dialogs work on a clone and commit with `CopyFrom` on
  save. They previously bound straight to the caller's object, so cancelling an edit to
  an existing task, route or trigger left every change applied.
- **The workflow runner.** `WorkflowRunner` renders a run and the actions on it —
  complete with a declared outcome, notes, cancel, reassign, add an ad-hoc task, task
  history, **fork across org units, add a branch to a live fork, and selective
  rejection at convergence**. It is used in **two places on purpose**: the builder's
  *Try it* pane and the demo's document page render the same component.
- **`IWorkflowRunnerClient`** and `DemoRunnerClient`, the second worked example of the
  host seam. Two things it decides that the engine will not: whether a task may be
  acted on (`CanComplete`), and what an actor is called. Since authorization landed,
  `CanComplete` is *derived* rather than invented — the host still owns the rule, but
  it is the same `IWorkflowAuthorizationPolicy` the engine enforces, so the button and
  the gate cannot disagree.
- **Test runs.** `WorkflowRun.IsTest` marks a run started to try a workflow out, so a
  host excludes them with one predicate. `StartRunOnVersionAsync` starts a run on a
  named version, which is what makes a draft testable before publishing; it refuses a
  non-test run on an unpublished version.
- **Outcome keys are validated on completion** — see the note below.
- **The outbox actually runs.** `AddOutboxProcessing()` registers a hosted service that
  drains on a timer, plus an in-process signal so `Background` triggers are picked up as
  soon as their transaction commits rather than at the next poll. **Each message runs in
  its own scope and its own transaction**, so an after-commit trigger's writes to the
  host's tables commit together and one failing message cannot affect its neighbours.
- **`IWorkflowPostCommitActions`** — work queued during an engine operation and run once,
  after the transaction commits and only if it did.
- **EF migrations.** The demo builds its database with `Database.MigrateAsync()`, and
  two tests guard the story: one fails if the model has drifted from the migrations, the
  other builds a database from the migration scripts and runs a workflow on it.
- **Sub-workflow lifecycle.** `SubWorkflowAttachment` says which sub-workflows may hang
  off which mainline task; `SubWorkflowInstance` is one run of one, pinned to a published
  version. Spawn on request or automatically, completion when the instance runs out of
  open work, blocking parents, and cancellation that takes instances with it.
- **The task inbox.** `GetOpenTasksForActorAsync` on the engine owns the predicate —
  open statuses as a positive list, `!IsTest`, and mine-or-unclaimed-in-my-units — and
  `IWorkflowInboxClient` is the third host seam, turning an opaque subject into a
  labelled, linkable row. `WorkflowInbox` renders it and drops into a `MudTabPanel`.
- **Deadlines and reminders.** `IWorkflowDueDateResolver` is the fourth host seam — the
  host answers when a task is due and the engine never computes it. The answer is resolved
  inside the single `NewTaskAsync` funnel, so all eight creation paths inherit deadlines
  and a ninth added later inherits one for free. `WorkflowTask.DueDate` and
  `ReminderSentAt` cache the answer and the fire-once stamp;
  `WorkflowTaskDefinition.ReminderLeadTimeMinutes` says how far ahead to nudge. The inbox
  and the runner show the date with no background machinery at all. A scoped
  `IWorkflowDeadlineProcessor`, driven by `WorkflowDeadlineHostedService` and opted into
  with `AddDeadlineProcessing()`, re-resolves each open candidate every pass, repairs the
  cached date, claims the row conditionally and dispatches `WorkflowEventKind.TaskDueSoon`
  — which the built-in `workflow.notify` trigger already supported, because it declares
  every event kind.
- 258 integration tests over fork/convergence/selective rejection/ad-hoc chains/
  version pinning/the adapter layer/the trigger runtime/the builder client/
  layout and diagram generation/edit-model copy semantics/the runner client/
  outbox isolation/migrations/sub-workflows/the inbox predicate/the inbox client/
  the deadline sweep, escalation and authorization
- **Escalation.** `TaskOverdue` fires once per task when its deadline passes, swept by
  `DeadlineProcessor` alongside reminders — one pass, two candidate queries, two claim
  columns. **No opt-in:** overdue is a fact about a task, not a feature you enable.
  Escalation itself is host code, because who is above whom is host knowledge: the demo's
  `DemoEscalationTrigger` walks section → Section Lead → branch head, and refuses to notify
  anyone below the person who is late. The inbox and runner distinguish "late" from
  "late, and somebody has been told".
- **Authorization.** `IWorkflowAuthorizationPolicy` is consulted before every one of the
  engine's twelve mutating methods, after the task loads and **before any validation** —
  so a denial writes nothing and does not reveal what became of the task. (It does not
  hide whether the task *exists*: `LoadTaskAsync` throws "not found" before the policy
  runs, so a denial and a missing task already read differently. See "Still open" below.)
  The rule stays host knowledge; only the asking is the engine's.
  A host registering no policy gets `AllowAllAuthorizationPolicy` and the
  pre-authorization behaviour, warned about once on first use.

  **A policy that throws denies.** That is deliberately the opposite of
  `IWorkflowAssignmentResolver` and `IWorkflowDueDateResolver`, whose failures are logged
  and treated as null: their fallback is a lesser answer, this one's would be no gate at
  all. Cancellation and a `WorkflowAuthorizationException` raised by the policy itself are
  both excluded from that — the first is not a denial, and the second already carries a
  better reason than any wrapper could.

  `WorkflowActors.System` bypasses the gate, checked before the policy is consulted, so a
  host policy cannot break the deadline sweep. **A host must never let that id arrive from
  an untrusted caller.** The demo splits doing from managing: the assignee completes, the
  section's Section Lead or the branch head cancels and reassigns. `DemoRunnerClient` derives
  `RunTaskView.CanComplete` from the same policy, so the button and the gate cannot drift.

### Not done

- **Timers as a workflow primitive.** No "wait 10 days, then advance on your own". That
  is durable suspend/resume and a different feature from a deadline.

## Agreed plan

1. Get the library working.
2. Grow `DemoDocuments.Server` into a fuller app in the shape of the original system, with a real web UI, and
   use it to test the builder and task-inbox UX properly.

Both are substantially done. The refinement that made the difference — **do not defer
all UI to the end** — is worth keeping for whatever comes next: the builder is what
validated the trigger-descriptor design and task-type-as-data, and the runner is what
turned up the missing outcome validation. Building the library first and the UI later
would have found both much later, with more depending on them.

The demo host is now a small application in the shape of the original system rather than an API sample:
documents, an org model, a workflow builder, and a runner driving real work.

## Where the builder UI stands

`src/TaskRouter.Blazor/` contains a complete first cut, ~900 lines:

| File | What |
|---|---|
| `WorkflowBuilder.razor` | The editor: tasks, routes, triggers, validate/save/publish |
| `TaskEditDialog.razor` | Task definition, flags, outcomes |
| `RouteEditDialog.razor` | Routing, targeting a task rather than a task type |
| `TriggerEditDialog.razor` | Trigger picker, events filtered to the trigger, advanced options |
| `TriggerConfigForm.razor` | **Descriptor-driven config form** — renders any trigger's parameters |

Supporting edit models and the `IWorkflowBuilderClient` abstraction are in
`TaskRouter.Core/Builder/`.

**It compiles, is in `TaskRouter.slnx`, and renders.** The CS0246 failure on
`IMudDialogInstance` and `IDialogService` was a namespace collision, fixed
2026-08-18 — see [the namespace trap](#the-namespace-trap) below.

`DemoDocuments.Server` hosts it on Blazor Server with global interactivity. Loading
`/workflows/1` renders the whole seeded ChangeRequest chain — all seven task definitions, the
entry/terminal/forkable/convergence chips, the rework route, and triggers by display
name — which means the definition graph survives the round trip through
`WorkflowEditModel` and back.

**What is verified, and what is not.** The server half is covered by
`BuilderClientTests` (11 tests): round-tripping, the published-version fork rule, a
run staying pinned while a new version publishes, draft tolerance versus publish
rigour, and building a workflow from nothing and starting a run on it. The
*components* have only been rendered, not clicked — no test drives the dialogs, so
the interactive behaviour of `TaskEditDialog`, `RouteEditDialog` and
`TriggerEditDialog` is still unproven.

Run it with:

```bash
source "$WORKFLOW_DEV_ENV"
export ConnectionStrings__Demo="Server=localhost,1433;Database=WorkflowDemo;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;Encrypt=False"
cd samples/DemoDocuments.Server && dotnet run --urls http://localhost:5199
```

## The workflow runner

**Built 2026-08-18.** Core actions only; fork and selective rejection are the next
pass. The reasoning behind building it as one component, which still holds:

**Originally agreed 2026-08-18.** The library should ship a component that *runs* a workflow, not
only one that builds it. Two reasons it is one component and not two:

1. **Testing what you just built.** A workflow is only really validated by walking a
   run through it — completing tasks, taking each outcome, watching a fork converge and
   a rejection route backwards. Today that is API-only, so the builder can produce a
   workflow nobody has executed.
2. **It is the same component the host needs anyway.** Whatever the original system puts on a
   document page to interact with a running workflow — the task list for this document,
   valid outcomes, complete/reassign/cancel, the log — is the same surface. Building it
   once, in the library, means the builder's "test this" pane and the document page are
   the same code, and the builder stops being able to drift from what users see.

So: **one runner component, used in two places.** In the builder it runs against a
throwaway subject; on a document page it runs against the real one. The engine already
exposes everything it needs — `GetRunsForSubjectAsync`, `GetValidOutcomesAsync`,
`GetTaskLogsAsync`, `GetChildTasksAsync`, `GetForkContextAsync`, plus the write side —
so this is a UI exercise, not an engine one.

It has a client abstraction of its own alongside `IWorkflowBuilderClient`, for the
same reason: Blazor Server talks to the engine in-process, WebAssembly over HTTP, and
the component cares about neither.

### Fork and selective rejection in the UI

Added 2026-08-18. Three things about it are load-bearing:

- **A rework outcome on a convergence task opens the rejection dialog, not a plain
  complete.** The engine refuses `CompleteTaskAsync` with a backwards-routing outcome on
  a convergence task, because it has no way to know which branches were the problem.
  `OutcomeOption.IsRework` is what lets the UI tell them apart before offering a button
  that would be rejected; the button shows an ellipsis to say it will ask.
- **The branch list comes from the manifest at convergence, and from live tasks before
  it.** By the time a convergence task is actionable the branch tasks are closed, and
  the manifest is the record of what each one decided — which is what rejection is
  chosen from.
- **The dialogs enforce what the engine enforces.** A fork needs at least two branches,
  and a unit already branched on is not offered again. Both are engine rules; surfacing
  them as errors after the fact would be an error the user could not have predicted.

### Not yet in the runner

- **Nobody may be stopped from doing anything.** `CanComplete` is host-decided and the
  demo only asks whether the task is open. The original system will need real rules there — the
  engine enforces none.
- **One fork shown at a time.** `WorkflowRunner` displays a single active fork, which is
  all the current model produces. Nested or concurrent forks would need more.

## Sub-workflows

**Built 2026-08-18.** One departure from `Dev/WORKFLOW_SUBWORKFLOW_DESIGN.md` worth
knowing, because that document still says otherwise.

**A sub-workflow is an ordinary `WorkflowDefinition` with `IsSubWorkflow` set**, not the
separate `SubWorkflowDefinition` entity the design proposed. That design was written
against the original system's unversioned model; here a parallel definition side would have duplicated
tasks, outcomes, routes, triggers, the validator and the builder — and would have left
sub-workflows unversioned while mainline workflows were pinned. Reusing the definition
means a sub-workflow is built in the same editor, validated by the same validator, and
its instances pin to a published version exactly as runs do.

Everything else follows the design:

- `SubWorkflowAttachment` is the join row deciding where a sub-workflow may hang.
  Attaching one to five mainline tasks is five rows, not five copies of the graph.
- `SubWorkflowInstance` is one run of one, hanging off a parent task.
- Its tasks are ordinary `WorkflowTask` rows **in the parent's run**, carrying
  `SubWorkflowInstanceId`. There is no second engine: routing, forking and convergence
  already propagated the tag, so only the beginning and the end needed writing.
- `IsBlocking` is copied onto the instance at spawn, so changing the attachment later
  cannot unblock work that started under the old rule.

`AddAdHocTaskAsync` remains as the degenerate case — a sub-workflow of one task and no
routes — because the cheap case should stay cheap.

This is what definition-id routing was for: the seeded "Technical Review" sub-workflow
uses `section-review`, the same task type the mainline uses, and both can be in flight at
once. The original system cannot express that.

## Where `workflow-aspnetcore` stands

**Complete and merged into `main`** (2026-08-25). All ten tasks of the plan are
implemented; the suite is green at 312/312 with 0 warnings.

- Design: `docs/superpowers/specs/2026-08-24-workflow-aspnetcore-design.md`
- Plan: `docs/superpowers/plans/2026-08-24-workflow-aspnetcore.md` — ten tasks, each with
  full code and its own commit. **Both documents have been corrected as review found
  defects in them**, so trust them over any memory of what they first said.

| Task | State |
|---|---|
| 1. `WorkflowNotFoundException` | done — 13 sites converted |
| 2. Project skeleton | done |
| 3. `EndpointTestHost` | done |
| 4. Actor seam + reserved-id guard | done |
| 5. Error mapping | done |
| 6. Task mutation routes | done — turned the deliberately-red suite green |
| 7. Run, fork and sub-workflow routes | done |
| 8. The read side | done |
| 9. Inbox + branch-key seam | done |
| 10. Documentation | done |

The branch was deliberately red between Tasks 4 and 6, because Tasks 4 and 5 build seams
whose first consumer is Task 6. That is history now: Task 6 mapped the routes and all 13
`Actual:<NotFound>` failures went green at once, as the plan predicted.

### Decisions taken during implementation, not in the original design

Each was a review finding, and each is now written into the spec:

- **The reserved-actor refusal answers 403, not 401.** The caller is authenticated; they
  simply may not be that actor. Missing-actor stays 401.
- **The reserved-prefix check is case-insensitive.** The engine's own gate compares
  ordinally so this was never a root bypass, but inbox matching and the
  `CreatorId`/`ModifierId` columns compare under a case-insensitive collation — so
  `WORKFLOW:SYSTEM` would have passed an ordinal guard and then been treated as the system
  actor by the audit trail the prefix exists to keep legible.
- **`ClaimsActorAccessor` reads claims only from authenticated identities.** It first gated
  on `context.User.Identity` (the primary identity) while reading with
  `context.User.FindFirst` (which searches all of them), so a claim on an unauthenticated
  secondary identity came back as though authenticated.
- **`ArgumentException` maps to 400.** Well-formed JSON the engine refuses on its merits —
  duplicate branch keys, a fork of one branch — was answering 500 with the message
  withheld.
- **`WorkflowNotFoundException` covers 13 sites, not 11.** The two extra are the fork-group
  lookups, which reported absence as `"No fork manifest for group {id}."` and so escaped a
  grep for the `"… not found."` shape.
- **`WorkflowAssignment` lives in `TaskRouter.Core.Abstractions`, not `.Model`.** The plan's
  request-record and inbox-test usings named the wrong namespace; corrected on the way in.
- **`Assert.IsTrue(x.Length > 2)` trips MSTEST0037.** The plan's outcome-read assertion
  is now `Assert.IsGreaterThan`, because this repo holds itself to 0 warnings.

### The open question, now answered

Five collection reads — `GetTaskLogsAsync`, `GetChildTasksAsync`, `GetForkManifestsForRunAsync`,
`GetSubWorkflowInstancesAsync`, `GetRunsForSubjectAsync` — return an empty list for a parent
id that does not exist, so those routes answer **200 `[]`** rather than 404.
**Ratified by Frank on 2026-08-25:** keep the engine's contract, so the HTTP path and the
in-process path cannot disagree, and no read pays for an existence query. Recorded in the
XML doc on `ReadEndpoints`.

## Suggested next step

**The repo is clear of terms from the internal system** as of 2026-08-25. The org
vocabulary in the demo is generic — a Division contains Sections, a Section Lead runs
one, and the document type is a ChangeRequest — and the demo's migrations were
regenerated as a single `InitialSchema` so no internal table name survives in schema
history either. Re-check with the sweep in the standing rule below before publishing,
excluding `wwwroot/lib`, whose minified vendor code matches almost any short string.

**Next, in this order:**

1. ~~**LICENSE and package metadata.**~~ **Done 2026-08-26** — see
   [Packaging](#packaging-as-of-2026-08-26) above. The one thing it deferred rather than
   solved is the 708 undocumented public members.
2. ~~**Act on the integration spike's findings.**~~ **Done 2026-08-29** — all seven,
   under [What the integration spike found](#what-the-integration-spike-found). Six were
   documentation; one added `PublishWithEntryTaskAsync`. Nothing was renamed, so the
   public API is no longer holding anything up.
3. **The integration spike — plans 1 and 2 of 4 done, 2026-08-29.** The host references the
   packages, its `DbContext` implements `IWorkflowDbContext`, an EF migration creates the 17
   tables, its legacy workflow subsystem is deleted, and **every host seam is implemented and
   registered**: vocabulary, assignment resolver, authorization policy, five route conditions,
   progress sink, notification sink. `AddTaskRouter()` is wired into the host's real container.

   **Nothing calls the engine yet**, so no seam has run against a real workflow — every test
   over there is a unit or registration test. That changes with plan 3, the facade: the host's
   existing task service re-implemented on `IWorkflowEngine`, roughly 18 methods. It is where
   any remaining API gaps will surface, because it must express every operation the host
   already has. Plan 4 then flips `ProjectReference` to `PackageReference`.

   **The plans and progress live in the host's own integration notes, not here.**
4. **CI, SourceLink, symbol packages**, then the GitHub repo and the publish. This is now
   the next thing blocking a first publish.
5. **Timers as a workflow primitive** — the one roadmap item still unbuilt.

The remaining smaller candidates are:

- **Duplicate a workflow.** Requested 2026-09-30 from the host. Two real uses, and they
  want different things: copying a workflow *for another section*, where the graph is the
  same and the assignments differ, and reusing one *on a different document type*, where
  `SubjectType` changes and the assignments may not. Today the only way to get a second
  workflow shaped like an existing one is to rebuild it task by task.

  `CreateDraftVersionAsync` already deep-copies a version's whole graph, so the copying
  is solved; what is missing is copying into a **new definition** rather than a new
  version of the same one — a new `WorkflowDefinition` row, name supplied by the caller,
  version numbering restarted, and the copy landing as an unpublished draft. Worth
  settling at the same time: whether a duplicate carries its triggers and sub-workflow
  attachments (it should) and its pre-assignments (it should not — those name people).

- **Timers as a workflow primitive** — "wait 10 days, then advance on your own". Durable
  suspend/resume, and a different feature from a deadline: a deadline nags a human, a
  timer moves the run. The largest thing left on the roadmap.
- **bUnit coverage for the dialogs**, once the builder's shape settles. See the note on
  it below for why it is still deferred rather than forgotten.

Still open:

- **Reads are ungated over HTTP too.** `TaskRouter.AspNetCore` maps the read side with no
  actor resolution, matching the engine. `EndpointRouteTests.It_does_not_require_an_actor_to_read`
  pins it, so a change of mind has to be deliberate rather than accidental.

- **Reads are not gated.** `GetRunAsync`, `GetTaskLogsAsync`, `GetForkManifestAsync` and
  the rest are open to any caller; authorization covers mutations only. Deliberate: reads
  are how a host builds the page it then decides whether to show, the inbox already
  answers a similar question through its own predicate, and gating reads would put a
  policy call on every per-page load. Revisit if a host needs run-level visibility rules.

- **A denied actor learns that the task exists.** `LoadTaskAsync` throws "Task {id} not
  found" before `AuthorizeAsync` ever runs, in every method taking a task id, so a denial
  and a missing task are already distinguishable — "not authorized" versus "not found" —
  which is a working existence oracle. Inherent to the design: the policy needs the task
  loaded to decide anything about it, and refusing to distinguish the two errors would
  mean refusing to say *why* a call failed. Accepted rather than fixed.

- **Trigger actions are authorized as the person who tripped them.** `IWorkflowActions`
  re-enters `CancelTaskAsync`/`ReassignTaskAsync` with the dispatcher's actor id — the
  human who caused the event — so a trigger that cancels a sibling task runs as whoever
  completed the first one. If the host's policy denies them, the action fails and the
  dispatcher records the execution as failed: the workflow advances, the automation
  quietly does not. Nothing in the repo calls `Actions.*` today, so nothing is broken.
  Documented on the seam rather than changed, because the obvious alternative — running
  trigger actions as `WorkflowActors.System` — would let anyone who can author a workflow
  perform any operation on any task. The deadline sweep already dispatches as `System`, so
  reminders and escalations are unaffected either way.

- **The dialogs have never been driven by a test.** `TaskEditDialog`,
  `RouteEditDialog` and `TriggerEditDialog` render, and their copy-on-edit semantics
  are covered by `EditModelCloneTests`, but nothing exercises the components
  themselves. **bUnit is deliberately deferred** — the UI is still changing shape, and
  rewriting component assertions on every tweak costs more than it catches right now.
  Revisit once the builder and the runner have settled.

## What the integration spike found

From putting the library into a substantial application written by someone else, with
no knowledge of this codebase. Ordered by how much they should change before the first
publish — **all seven are now closed**, each with what was decided and why. The original
finding is kept under each resolution, because the reasoning is the part worth rereading.

**1. ~~`AddTaskRouter()` does not register `IWorkflowDbContext`, and nothing says so.~~
Fixed 2026-08-29** — the XML doc on `AddTaskRouter` now leads with it and names the
failure mode, and it is step 2 of the README's integration section.

The host must add the mapping from its own context to the seam itself:

```csharp
services.AddDbContext<MyContext>(o => o.UseSqlServer(cs));
services.AddScoped<IWorkflowDbContext>(sp => sp.GetRequiredService<MyContext>());
services.AddTaskRouter();
```

Omit the middle line and the failure arrives later as a generic "no service for type
`IWorkflowDbContext`". This is the first thing every host must do and the only one the DI
extension cannot do for it — it has no way to know which of the host's contexts implements
the interface.

**2. ~~`IWorkflowDbContext`'s member names are generic and unprefixed — and they collide.~~
Settled 2026-08-29: the interface keeps its names.** The host's colliding types were its
own legacy workflow subsystem, which TaskRouter replaces — so they were deleted rather
than worked around, which is cleaner than either living with the explicit interface
implementation or prefixing seventeen interface members that read correctly as they are.
The removal is recorded in the host's own integration notes
(nine tables, with a migration and a standalone drop script) and closed out under "Known
landmines" in `migration-notes.md`. The README documents both collision classes and the
workaround for a host that has to coexist rather than delete.

The interface requires `WorkflowRuns`, `WorkflowTasks`, `WorkflowTriggerDefinitions` and
so on. The host in the spike already had a `DbSet` called `WorkflowTriggerDefinitions`,
for a type of its own, so the class could not declare both implicitly. The resolution is
an explicit interface implementation:

```csharp
DbSet<TriggerDefinition> IWorkflowDbContext.WorkflowTriggerDefinitions => Set<TriggerDefinition>();
```

It works, needs no change to either side, and is discoverable only by hitting the
compiler error. Note the consequence: `context.WorkflowTriggerDefinitions` then returns
the *host's* type, and this library's is reachable only through the interface or
`Set<TriggerDefinition>()` — two things that both compile and mean different rows.

Documented rather than renamed. Any host with a workflow feature of its own is a
candidate to hit it, so the README says so under "Name collisions with an existing
workflow feature", including the advice that deleting a subsystem being retired anyway
beats either workaround.

**3. ~~CLR type names collide too.~~ Documented 2026-08-29.**

`TaskRouter.Core.Model` defines `ForkManifest`, `ForkManifestEntry` and
`TriggerDefinition`; the host independently had `ForkManifest`, `ForkManifestEntry` and
`WorkflowTriggerDefinition`. Harmless in any file importing one namespace, and it needs
`using` aliases in files importing both. Table names never
collide — every table is prefixed `TaskRouter*`, which is what makes coexistence work at
the database level. In the README, alongside finding 2.

**4. ~~`IWorkflowActorResolver` is registered but never consumed by the EF engine.~~
Documented as host-only, 2026-08-29.**

`WorkflowEngineBuilder.AddActorResolver<T>()` exists, and nothing in
`TaskRouter.EntityFrameworkCore` resolves it. Checked again on 2026-08-29 and it is
narrower than the original finding assumed: **nothing in the library consumes it at all**,
Blazor included. The only caller in the repo is the demo's own runner client. Kept where
it is — a registered place for a host's "actor id → display name" lookup is worth having —
but the XML doc on `AddActorResolver` now says in bold that the engine will not start
returning names, and the README lists it apart from the optional seams for the same
reason. Moving it to the Blazor package would be wrong: Blazor does not use it either.

**5. ~~Publishing a workflow requires a two-phase seed, and the ordering is
undiscoverable.~~ Fixed 2026-08-29 — `PublishWithEntryTaskAsync`.**

`WorkflowDefinitionVersion.EntryTaskDefinitionId` can only be set after
the task definition it points at exists, while `IsPublished`/`IsLatest` are set before —
so a version passes through a state that is flagged published but structurally
incomplete. Seeding the smallest possible workflow took four `SaveChangesAsync`
round-trips. Three are ordinary EF key-generation ordering; the fourth is this back-fill.

`IWorkflowDbContext.PublishWithEntryTaskAsync(version, entryTask, ct)` in
`src/TaskRouter.EntityFrameworkCore/WorkflowPublishing.cs` owns the ordering: save the
graph, back-fill the entry id, validate, demote the previous `IsLatest`, set the publish
flags — two saves, and the flags are set only in the last one, so **a version that fails
validation stays a draft** instead of remaining a published fragment. Returns the
validation errors; empty means published. Five tests in `PublishingTests.cs`, and the demo
seeder was moved onto it for both the mainline workflow and the sub-workflow, so the
reference host shows the shape the docs describe.

One thing it exposes, now in the README: build the graph with navigation properties rather
than ids for anything created in the same batch, because the ids are minted by the save
this method makes. Moving the seeder onto it turned one `SubWorkflowDefinitionId =
definition.Id` into a foreign key violation for exactly that reason.

**6. ~~A host needs four implementations, not nine — say so.~~ Fixed 2026-08-29.**

`AddTaskRouter` already registers `NullAssignmentResolver`, `NullDueDateResolver` and
`AllowAllAuthorizationPolicy` through `TryAddScoped`, defaults `IWorkflowJobQueue` to `SignallingJobQueue`, and resolves
`IWorkflowProgressSink` and `IWorkflowNotificationSink` with `GetService<>`. Required in
practice: `IWorkflowDbContext`, an assignment resolver, an authorization policy, and route
conditions. The README's integration section now opens with "four things are yours to
provide", says what each default does when you skip it, and lists the optional seams
separately.

**8. Neither sink was told who acted. Fixed 2026-08-29 — breaking, done pre-publish.**

`IWorkflowProgressSink.ReportAsync` and `WorkflowNotification` carried the payload but no
actor, while the engine knew one all along: `WorkflowTriggerContext.ActorId` is required and
both built-in triggers had it in hand. It only bites a host whose storage is audited, which
is why nothing here noticed — the demo has no modifier column. The spike's host does:
`CreatorId`/`ModifierId` are `[StringLength(10)]` foreign keys to its user table, so a
workflow-driven write had to name somebody, and with no actor the host either invented one
or left the write unattributed.

`actorId` is now a parameter on `ReportAsync` and a member of `WorkflowNotification`. Note
what a host must expect: the deadline sweep reports as `WorkflowActors.System`
(`"workflow:system"`), which is deliberately not shaped like a user id — a host storing it
in a column with a foreign key to its own users has to map it rather than write it through.
`SinkActorTests` pins all three cases, including the sweep.

**9. `IWorkflowProgressSink` could not report several branches at once. Fixed 2026-08-29 —
additive.**

`ReportAsync` takes one percentage and one optional branch key, so it said either "this
branch" or "the whole run". A convergence needs neither: every branch has its own progress
at that moment, and one figure for all of them is the run's average, which is nobody's
actual progress. The system the spike's host replaced updated each participating section's
row with that section's own number, and no combination of the existing scopes could express
it.

`reportProgress` gains a fourth scope, **`all-branches`**, which calls the sink once per
branch key with that branch's own figure, falling back to the whole run when the run never
forked. A new choice on an existing parameter rather than a signature change, so a sink
written against the old scopes needs no edit. `ProgressScopeTests` covers it.

**10. Nothing could put an org unit on a run, so role-based assignment never resolved.
Fixed 2026-08-31 — `StartRunAsync` takes an optional `WorkflowAssignment`.**

Found the first time the spike ran a real workflow, which is the only way it could have been:
every seam was individually correct and the composition was not. A host whose documents belong
to an org unit — a section, a team — could not get a role key to resolve at all. The resolver
was handed `current` with a null branch key, correctly kept the assignment as it found it, and
the role silently never applied.

The cause was narrow. Three paths create tasks with an org unit and only one lacked a way to
set it: `ForkTaskAsync` takes branch keys per branch, `StartSubWorkflowAsync` takes an
assignment to delegate a chain to another org unit, and `StartRunAsync` took nothing. So a
workflow that never forked never acquired an org unit, and there was no host-side workaround
for the entry task either — that call passes a placeholder snapshot carrying run id 0, so a
resolver cannot even look the run up.

`StartRunAsync` and `StartRunOnVersionAsync` now take `WorkflowAssignment? initialAssignment`,
the same shape `StartSubWorkflowAsync` already used for delegation. The entry task carries it
and every routed task inherits it as its starting assignment. Omitting it preserves the old
behaviour exactly, which `InitialAssignmentTests` pins alongside the new.

**Not quite purely additive:** the parameter sits before `ct`, so callers passing the token
positionally have to name it. Four call sites in this repo needed it. Free now; it would be a
source break after publication, which is the argument for doing it now rather than later.

**7. Two things that went right, worth not regressing.** `IWorkflowDbContext` declaring
all 17 `DbSet` names and types means a host cannot expose a wrongly-named or wrongly-typed
set — divergence is a compile error, not a runtime surprise. And `Result<T>` carrying
`Exception` as its error type made the adapter to the host's own `Result<T, Exception>` a
single `Match` — no error-type impedance at all.

## Things to be careful about

- **The inbox shows nothing that is wholly unassigned.** Both halves of the predicate
  need something to match on, so a task with neither an actor nor a branch key is in
  nobody's inbox. That state is reachable — `IWorkflowAssignmentResolver` may return
  `WorkflowAssignment.Unassigned` — and finding those is an admin query, not an inbox one.
- **Unclaimed means unowned, not "owned by a colleague".** The inbox shows tasks in your
  units only when `AssignedToActorId` is null. Widening it to everything in your section
  turns an inbox into a supervisor's report, and the count on the tab stops meaning
  "work I must do".
- **The inbox's `Created` is UTC, and any sort on it must tiebreak on `TaskId`.** Every
  branch of a fork is stamped with one `DateTime.UtcNow`, so `Created` alone is not a
  total order — `WorkflowEngine.Inbox.cs` adds `.ThenBy(t => t.Id)` and
  `WorkflowInbox.razor`'s sort label re-applies the same tiebreak. A client-side re-sort
  on `Created` alone reshuffles rows between refreshes.
- **`DemoInboxClient` resolves a subject on the id's shape, not its `SubjectType`.**
  Correct while every subject this host creates is a document; the first non-document
  subject type with an integer key will link to the wrong page. The call site says so.
- **An assignment's actor and branch key must move together.** The engine inherits them
  as a pair, so they only desync where a host sets an actor directly — delegating a
  sub-workflow, and ad-hoc tasks. `DemoOrg.AssignmentForAsync` is the single place that
  maps a person to both; setting one without the other makes a downstream role key
  resolve against the wrong org unit, silently and plausibly.
- **The runner offers a start icon for a sub-workflow that cannot start.**
  `HasSubWorkflowOptions` accounts for scope but not `AllowMultiple`, so a task with a
  running non-multiple attachment still shows the icon; the dialog then says
  "Everything attachable here is already running." A dead-end click that explains
  itself, rather than the exception the blocking case used to produce — but the same
  class of problem, and the runner otherwise holds to surfacing engine rules before the
  action rather than after.
- **Attachments are version-scoped and must round-trip through `WorkflowEditModel`.**
  They are rewritten wholesale on every save, exactly like routes and triggers. Before
  2026-08-19 they were neither loaded nor rewritten, and the FK cascade from
  `ClearGraphAsync` deleted them on every draft save.
- **The seeder short-circuits on an existing database, so an old `WorkflowDemo` shows no
  reminders.** `DemoWorkflowSeeder.SeedAsync` returns early if the ChangeRequest workflow is already
  there (`DemoWorkflowSeeder.cs:46`), so a database created before the deadline work keeps
  its old seed: "Provide Input" has no `ReminderLeadTimeMinutes`, nothing is ever a sweep
  candidate, and the feature looks broken rather than unseeded. Drop the database, or run
  the demo against a new one, after any change to what is seeded.
- **The reminder claim happens before the dispatch, not after.** If dispatch fails the
  reminder is lost rather than repeated, and that is the deliberate choice: a person nudged
  twice for the same task stops trusting the nudges. The outbox message's `IdempotencyKey`
  is a second line of defence behind the claim, not the primary one.
- **The two sweeps narrow on `DueDate` in opposite ways, and that is deliberate.** The
  reminder candidate query must not filter on it: narrowing to tasks already inside their
  window looks like an obvious optimisation and silently breaks the case the re-resolution
  exists for — a deadline moved *earlier* would never enter the set and would never nudge
  at all. The overdue candidate query must do the opposite and filter on it, because
  overdue has no opt-in: there is no lead-time predicate left to bound the set with, and
  without the date filter the candidates never shrink, so `OrderBy(Id).Take(batchSize)`
  would re-examine the same low-id tasks every pass and starve its own tail. Read the two
  comments on `ReminderCandidatesAsync` and `OverdueCandidatesAsync` in
  `DeadlineProcessor.cs` before changing either query — they carry the full argument,
  including what each approach costs.
- **A task with no due date at creation never escalates.** The overdue sweep narrows on
  the cached `DueDate`, and the only things that write that column are task creation and
  the sweep's own repair — which only runs for tasks already in a candidate set. A task
  whose resolver returned null at creation, on a definition with no reminder lead time,
  is in neither set and nothing will ever re-resolve it. Not late: never. Pinned by
  `EscalationTests.A_task_with_no_cached_due_date_never_escalates`. Closing it needs a
  repair pass over null-dated open tasks.
- **`DemoEscalationTrigger` renders only two template tokens.** `TriggerTemplate` is
  `internal` to `TaskRouter.EntityFrameworkCore`, so a host trigger cannot reuse it and this one
  reimplements `{task.id}` and `{task.assignee}` only. Any other token in a host trigger's
  body passes through literally into a sent message. The descriptor's description says so,
  because the builder UI is the only documentation whoever configures it will read.
- **A trigger that declines to act still records `Succeeded`.** `TriggerDispatcher` marks
  the execution succeeded whenever `ExecuteAsync` returns without throwing, so a trigger
  that deliberately does nothing — `DemoEscalationTrigger` finding nobody to escalate to,
  for instance — is indistinguishable in the audit trail from one that delivered. That is
  why it logs at each decline point; a host trigger that stays silent in both the
  notification and the log leaves a missed escalation with no trace anywhere.
- **A `FailOperation` trigger leaves its audit row in the change tracker.**
  `TriggerDispatcher` adds the `TriggerExecution` before invoking the trigger
  (`TriggerDispatcher.cs:175`), so when a `FailOperation` trigger throws, the transaction
  rolls back but the entity stays `Added` on the scoped context — and the *next*
  `SaveChangesAsync` inserts it, under a different task's transaction. The row's data is
  correct; its attribution is not, and if nothing else saves in that pass it is lost
  entirely. Surfaced by `EscalationTests.A_trigger_that_throws_does_not_stop_the_rest_of_the_pass`,
  which the containment in `SweepAsync` is what makes reachable: before it, the exception
  left the whole operation and the scope went with it. Fixing it means detaching the entity
  in the dispatcher's catch, which is an engine-wide change and was left out of the
  escalation branch deliberately.
- **`ReminderLeadTimeMinutes` is minutes, and the builder's day field is `double?`.** EF
  Core maps `TimeSpan` to SQL Server `time(7)`, which caps at 24 hours, so a lead time of a
  day or more would silently truncate — that is why the column is an int. And an `int?`
  day-facing property does integer division on those minutes, so a 12-hour lead time reads
  back as `0` and the next save writes that zero. `TaskEditModel.ReminderLeadTimeDays` is
  `double?` for exactly this reason, and the `MudNumericField` bound to it must be
  `T="double?"` too.
- **`DemoDueDateResolver` keys on the id's shape, not `SubjectType`** — the same
  limitation `DemoInboxClient` has, with the same consequence: the first non-document
  subject type with an integer key inherits an unrelated document's deadline.
- **A test that asserts a sweep fired nothing must scope the assertion to its own task.**
  "Provide Input" is seeded with a two-day lead time so the demo has something to show, so
  any run that reaches it produces a legitimate candidate. `It_skips_a_completed_task`
  asserted on the whole pass's total and broke the moment that seed landed; it now asserts
  on the task it names.
- **Two SQL Server-specific index filters now, not one.** The sweeper's
  `(Status, ReminderSentAt)` index uses `HasFilter("[ReminderSentAt] IS NULL")`, joining
  the convergence unique index in `WorkflowModelBuilder.cs`. Supporting another provider
  means conditioning both.
- **`WorkflowEventKind.TaskDueSoon` is last in the enum and must stay there**, like
  `SubWorkflowCancelled` before it. `SchemaGuardTests.Event_kind_ordinals_are_stable` pins
  it at 15; if that test fails, fix the enum, not the test.
- **The subject cache on the engine is per-scope, and depends on a run's subject never
  changing.** `_subjectsByRun` memoises so the funnel does not query per task, and
  `StartOnAsync` seeds it with the subject it already holds. Nothing on `IWorkflowEngine`
  can change a run's subject today; anything that ever could must invalidate this.
- **`AddDeadlineProcessing()` needs `AddOutboxProcessing()` to actually deliver
  anything.** The sweeper raises the event, but `workflow.notify` defaults to after-commit
  dispatch, so the outbox is what carries it to the sink. A host that registers only the
  first gets `TriggerExecution` rows and no notifications.
- **Keep this repo free of anything specific to the internal system it came from** — its
  task types, document types, id formats, org-model vocabulary, internal package names and
  internal paths. It is going public, and the demo host deliberately uses look-alike but
  generic names. The sweep, which must return nothing:

  ```bash
  git ls-files -z 'src/*' 'samples/*' 'tests/*' '*.md' | grep -zv 'wwwroot/lib' \
    | xargs -0 grep -in '<the internal system name>\|<its org-role terms>\|<its id format>'
  ```

  Two traps it has already caught: XML doc comments ship to consumers in the NuGet
  packages and show up in their IntelliSense, and one leak was `HelperText` — UI text a
  consumer's own users would have read.
- **The convergence unique index is SQL Server-specific** — `HasFilter` in
  `WorkflowModelBuilder.cs` uses `[ForkManifestId] IS NOT NULL AND [Status] <> 3`.
  Supporting another provider means conditioning that.
- **`Status <> 3`** in that filter is `WorkflowTaskStatus.Cancelled`. If the enum is
  ever reordered the index silently changes meaning, so `SchemaGuardTests` pins the
  ordinals. If those tests fail, fix the index filter — don't just update the test.
- **The host owns the schema, and therefore the migrations.** The engine has no
  database of its own: `ConfigureTaskRouter()` puts its tables on the host's context,
  which is what lets a task completion and a domain update share one transaction. So a
  change to an engine entity is a change to every consumer's schema, and each consumer
  generates its own migration:

  ```bash
  dotnet ef migrations add <Name> \
    --project samples/DemoDocuments.Server --context DemoDbContext --output-dir Data/Migrations
  ```

  `MigrationTests` fails if the model has drifted from the migrations, so forgetting this
  is caught rather than discovered on deployment. The other tests build their databases
  with `EnsureCreated` for speed, which reads the model rather than the migrations — that
  is exactly the gap those tests exist to close.
- **A DbContext permits one operation at a time, and Blazor Server components render
  concurrently.** The demo registers `AddDbContextFactory` plus a scoped context
  resolved from it: pages take a short-lived context, while the engine keeps the scoped
  one because it must share a change tracker and a transaction with the host's writes.
  `WorkflowRunner` additionally serialises its own loads behind a semaphore — a parent
  that loads its data asynchronously re-renders when it finishes, which re-fires
  `OnParametersSetAsync` while the first load is still in flight.
- **Dialogs must edit a clone.** Blazor passes reference types by reference, so a
  dialog that binds to its `Model` parameter writes into the caller's object as the user
  types, and Cancel cannot undo it. Any new dialog needs the same
  `Clone()` / `CopyFrom()` pattern the three existing ones use.
- **Never call `BeginTransaction` directly. Use `WorkflowTransaction.ExecuteAsync`.**
  A provider configured with `EnableRetryOnFailure` — which any host that expects to
  survive a transient network blip has on — refuses a user-initiated transaction taken
  outside its execution strategy. This has now been got wrong twice, in
  `DemoBuilderClient` and in `OutboxProcessor`. The test host enables retries for exactly
  this reason: without it the tests cannot catch the mistake, and both bugs shipped past
  a green suite.
- **A fork needs at least two branches**, and duplicate branch keys are refused. Both
  are engine rules (`WorkflowEngine.Fork.cs`); `RunnerForkDialog` enforces the first and
  `GetBranchOptionsAsync` filters for the second, so neither reaches a user as an error.
- **An outcome must be one the task definition declares.** Added 2026-08-18 after the
  runner work turned it up: nothing checked the key, so any caller could complete a task
  with any string — the task went to Completed, no route matched, nothing was created,
  and the run stalled silently. A task that declares *no* outcomes still accepts any
  key, because terminal tasks are usually written that way.
- **The demo's document API lives under `/api`**, because the Blazor UI owns
  `/documents`. `/documenttasks` deliberately keeps its own paths — mirroring a
  of the kind the original system has, controller route for controller route, is the point of it.
- **Mermaid is vendored, not loaded from a CDN** —
  `src/TaskRouter.Blazor/wwwroot/lib/mermaid/mermaid.min.js`, 3.5 MB, MIT, with
  provenance in the README beside it. Consumers may be air-gapped, and a diagram that
  silently fails to draw on a disconnected network is worse than a large file in the
  repository. Mermaid's render API has changed between major versions, so re-check
  `WorkflowDiagram.razor` when updating it.
- **Static assets are served by `MapStaticAssets()`, not `UseStaticFiles()`.**
  The latter only serves referenced component libraries' assets in Development, so a
  Production run renders unstyled markup with no interactivity at all — a symptom that
  does not obviously point at middleware.

### The namespace trap — closed 2026-08-25, but understand it before adding a namespace

A `using X;` inside `namespace A.B` resolves **relative to the enclosing namespace**.
If `A` contains a member called `X`, the using binds to `A.B.X` or `A.X` and never
reaches the global `X`. In Razor it fails in a way that misleads: markup keeps
compiling, because the Razor compiler emits components fully qualified
(`global::MudBlazor.MudStack`), while anything written by hand in `@inject` or
`@code` — `IDialogService`, `IMudDialogInstance` — fails CS0246. The reference and
the package are fine; do not go looking for a missing `FrameworkReference`.

It bit four times. The TaskRouter rename closed all four, and **there is no
`global::` qualifier anywhere in the repository any more**:

| Where | The name | Was binding to | Closed by |
|---|---|---|---|
| `src/Workflow.MudBlazor/_Imports.razor` | `MudBlazor` | `Workflow.MudBlazor` (itself) | renaming the project to `TaskRouter.Blazor` |
| `samples/DemoDocuments.Server/Components/_Imports.razor` | `Workflow.MudBlazor` | `DemoDocuments.Server.Workflow.…` (nonexistent) | the library no longer being called `Workflow` |
| `WorkflowBuilder.razor:255` | `Core.Model.TriggerDispatchMode` | resolved *only* because `Workflow.MudBlazor` and `Workflow.Core` shared a root | fully qualifying it |
| `TaskRouter.AspNetCore` | `Results` | would have bound to the `TaskRouter.Results` **namespace** instead of ASP.NET's `Results` class | keeping `Core` as a segment: `TaskRouter.Core.Results` |

The third and fourth are the instructive ones. The third was invisible to a
search for the old name, because the text never contained it — a relative
reference that worked by accident of a shared root. The fourth was *created* by
the rename rather than found by it: flattening `Workflow.Core.Results` to
`TaskRouter.Results` put a namespace called `Results` directly under `TaskRouter`,
which shadows `Microsoft.AspNetCore.Http.Results` for every file in the family.

**The rule this leaves:** before adding a namespace segment under `TaskRouter`,
ask whether its last segment is also the name of a type or root namespace that
`TaskRouter.*` code uses unqualified. `Results` was; `Model`, `Abstractions`,
`Builder`, `Inbox`, `Runner` and `Validation` were not.
