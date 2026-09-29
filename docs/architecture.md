# TaskRouter, drawn

The same reasoning as [design.md](design.md), in diagrams. That document is the prose and
the authority; this one is for orienting quickly or for reviewing the shape of the thing.

Throughout, a consistent distinction: **amber is owned by the engine** — it decides this and a
host cannot override it — and **teal is owned by the host** — the engine cannot know it and
will ask. Nearly every question about this library resolves to which side of that line
something falls on.

---

## The four packages

Split so a host takes only what it needs. The dependency direction is strict and one-way.

```mermaid
flowchart TD
  Core["<b>TaskRouter.Core</b><br/>abstractions · model<br/>validator · Result"]
  EF["<b>TaskRouter.EntityFrameworkCore</b><br/>the engine · 17 entity configs<br/>builder, runner + inbox clients"]
  Asp["<b>TaskRouter.AspNetCore</b><br/>HTTP endpoints over<br/>every operation"]
  Blz["<b>TaskRouter.Blazor</b><br/>builder · runner · inbox<br/>components (MudBlazor)"]
  Host["<b>Your application</b><br/>owns the DbContext,<br/>the schema, the identity"]

  Core --> EF
  EF --> Asp
  Core --> Blz
  EF --> Host
  Asp --> Host
  Blz --> Host

  classDef eng fill:#F7EBD6,stroke:#B8791F,stroke-width:1.5px,color:#3A2A08
  classDef hst fill:#D9EAE8,stroke:#1F6F72,stroke-width:1.5px,color:#0C2E2D
  class Core,EF,Asp,Blz eng
  class Host hst
```

**Note the shape.** `TaskRouter.Blazor` depends on `Core` only — never on EF. That is what
lets the same components run in a browser, where there is no DbContext at all.

---

## The data model

Seventeen tables, all prefixed `TaskRouter*`. They live in *your* database, created by *your*
migrations — the engine has no database of its own, which is what lets a task completion and a
domain update commit in one transaction.

```mermaid
flowchart LR
  subgraph DEF ["Definition side — what a workflow IS"]
    direction TB
    TT["TaskTypes<br/><i>data rows, not an enum</i>"]
    D["Definitions"]
    V["DefinitionVersions"]
    TD["TaskDefinitions"]
    TO["TaskOutcomes"]
    TR["TaskRoutes"]
    D --> V --> TD
    TD --> TO
    TD --> TR
    TT -.-> TD
  end

  subgraph RUN ["Run side — what actually happened"]
    direction TB
    R["Runs"]
    T["Tasks"]
    TL["TaskLogs"]
    VAR["Variables"]
    R --> T --> TL
    R --> VAR
  end

  subgraph EXT ["Forks, delegation, reactions"]
    direction TB
    FM["ForkManifests"]
    FME["ForkManifestEntries"]
    SWA["SubWorkflowAttachments"]
    SWI["SubWorkflowInstances"]
    TRG["TriggerDefinitions"]
    TE["TriggerExecutions"]
    OB["Outbox"]
    FM --> FME
    SWA --> SWI
    TRG --> TE
    TE -.-> OB
  end

  V ==>|"run pins to ONE version"| R
  TD ==>|"task is an instance of"| T

  classDef def fill:#E8EEF2,stroke:#7B8C9C,color:#14202B
  classDef run fill:#F7EBD6,stroke:#B8791F,color:#3A2A08
  classDef ext fill:#E4E9EC,stroke:#8B9CA8,color:#14202B
  class TT,D,V,TD,TO,TR def
  class R,T,TL,VAR run
  class FM,FME,SWA,SWI,TRG,TE,OB ext
```

**The two heavy arrows are the whole model.** A run pins to one version and never moves; a
task is an instance of a task *definition*. Everything else hangs off those two facts.

---

## Routes target definitions, not types

The single decision the rest of the design rests on. A route says "when *this step* ends with
*this outcome*, go to *that step*" — not "when any Technical Review is approved". So one
workflow may contain the same task type more than once, routing differently each time.

```mermaid
flowchart LR
  A["Draft<br/><i>type: authoring</i>"]
  B["Technical Review #1<br/><i>type: tech-review</i>"]
  C["Rework<br/><i>type: authoring</i>"]
  D["Technical Review #2<br/><i>type: tech-review</i>"]
  E["Publish<br/><i>type: closing</i>"]

  A -->|submitted| B
  B -->|rejected| C
  B -->|approved| E
  C -->|resubmitted| D
  D -->|approved| E
  D -->|rejected| C

  classDef same fill:#F7EBD6,stroke:#B8791F,stroke-width:2px,color:#3A2A08
  classDef norm fill:#E8EEF2,stroke:#7B8C9C,color:#14202B
  class B,D same
  class A,C,E norm
```

The two amber steps share a task type and route differently. An engine keying routing on a
task-type enum cannot express this — and therefore cannot express reusable sub-workflows
either, since a sub-workflow is the same shape appearing in more than one place.

---

## How a run advances

There is no scheduler and no interpreter loop. A run moves **only** when someone completes a
task, and completing a task is one transaction that does all of this or none of it.

```mermaid
sequenceDiagram
  autonumber
  participant U as Caller
  participant E as Engine
  participant H as Host seams
  participant DB as Database

  U->>E: CompleteTaskAsync(taskId, "approved", actorId)
  E->>H: IWorkflowAuthorizationPolicy — may this actor act?
  H-->>E: allow / deny
  Note over E,H: denies on throw — fails closed
  E->>DB: open transaction
  E->>E: validate outcome against the task's definition
  E->>DB: task → Completed, outcome recorded, log appended
  E->>E: pick the matching route
  E->>H: IRouteConditionEvaluator — is this route's condition met?
  H-->>E: true / false
  E->>DB: create the next task
  E->>H: IWorkflowAssignmentResolver — who gets it?
  H-->>E: an actor + org unit
  Note over E,H: keeps current assignment on throw — fails soft
  E->>H: IWorkflowDueDateResolver — when is it due?
  E->>E: fire InTransaction triggers
  E->>DB: queue AfterCommit triggers to the outbox
  E->>DB: commit
  E-->>U: Result — the new state
```

**Every host call is inside the transaction.** A seam that is slow makes the transaction slow,
which is a real constraint on what belongs in one — the outbox exists precisely so anything
talking to the outside world happens after the commit instead.

> **The engine owns task status.** No API accepts a caller-supplied status. Callers supply an
> *outcome*; the engine derives status, routing, blocking checks and triggers from it. That is
> what makes those impossible to bypass — there is no back door that sets a task to Completed
> without the routing running.

---

## Versions and pinning

```mermaid
stateDiagram-v2
  direction LR
  [*] --> Draft: CreateDraftVersion
  Draft --> Draft: edit + save
  Draft --> Published: publish (validator must pass)
  Published --> Superseded: a later version is published
  Superseded --> [*]

  note right of Superseded
    Stays IsPublished.
    Runs already on it must
    still resolve their routes.
  end note
```

**Superseding is not unpublishing.** A superseded version keeps its published flag, because
runs pinned to it still need to route; only `IsLatest` moves. So editing a workflow can never
reroute work already in flight, and "which version did this run follow?" is one column.

---

## Forks and convergence

Split a step into parallel branches across org units, then bring them back — and when they
converge, send *only some* branches back for rework.

```mermaid
flowchart TB
  O["Section Review<br/><i>fork origin — superseded</i>"]
  B1["branch: C100"]
  B2["branch: C200"]
  B3["branch: C300"]
  C{"Convergence<br/>appears only when every<br/>branch has finished"}
  M["ForkManifest<br/><i>what each branch decided</i>"]
  R1["C100 re-forked"]
  R3["C300 re-forked"]
  P["Proceed"]

  O --> B1 & B2 & B3
  B1 & B2 & B3 --> C
  C -.records.-> M
  C -->|"selective rejection:<br/>93 and 88 only"| R1 & R3
  C -->|"accept all"| P

  classDef org fill:#E8EEF2,stroke:#7B8C9C,color:#14202B
  classDef eng fill:#F7EBD6,stroke:#B8791F,stroke-width:1.5px,color:#3A2A08
  classDef rew fill:#F7E4DC,stroke:#9A3412,color:#3A1508
  class B1,B2,B3,P org
  class O,C,M eng
  class R1,R3 rew
```

**Selective rejection is a separate operation on purpose.** The engine refuses a rejection
outcome through the ordinary completion path, because it has no way to know *which* branches
were the problem — and re-forking all of them would discard work already accepted.

A **branch key is an opaque string**. In one host it is a section code, in another a region or
a team; the fork logic is identical either way.

---

## What the host owns

Eleven interfaces, but the count is misleading: `AddTaskRouter()` registers a working default
for every one it can.

```mermaid
flowchart LR
  EN["<b>WorkflowEngine</b><br/>routing · status · forks<br/>versions · validation"]

  S1["IWorkflowDbContext<br/><i>where do rows live</i>"]
  S2["IWorkflowAssignmentResolver<br/><i>who gets this task</i>"]
  S3["IWorkflowAuthorizationPolicy<br/><i>may this actor act</i>"]
  S4["IRouteConditionEvaluator<br/><i>is this route's condition met</i>"]
  S5["IWorkflowActorResolver<br/><i>your people, your org units,<br/>and which ones an actor covers</i>"]
  S6["IWorkflowSubjectResolver<br/><i>what is ChangeRequest:42 called</i>"]
  S7["IWorkflowDueDateResolver<br/><i>when is this due</i>"]
  S8["IWorkflowProgressSink<br/>IWorkflowNotificationSink<br/><i>where does output go</i>"]

  EN --> S1 & S2 & S3 & S4
  EN --> S5 & S6 & S7 & S8

  classDef eng fill:#F7EBD6,stroke:#B8791F,stroke-width:2px,color:#3A2A08
  classDef hst fill:#D9EAE8,stroke:#1F6F72,stroke-width:1.5px,color:#0C2E2D
  class EN eng
  class S1,S2,S3,S4,S5,S6,S7,S8 hst
```

Read this as a list of **questions the engine cannot answer**. Each seam exists because
something is genuinely unknowable from inside a general library, not because the design wanted
to be extensible.

### Where the line falls — a worked example

```mermaid
flowchart LR
  H["Host<br/><b>every org unit</b><br/><i>one-line projection</i>"]
  E["Engine<br/><b>minus those already branched on</b><br/><i>fork state</i>"]
  U["The picker"]
  H --> E --> U

  classDef hst fill:#D9EAE8,stroke:#1F6F72,stroke-width:2px,color:#0C2E2D
  classDef eng fill:#F7EBD6,stroke:#B8791F,stroke-width:2px,color:#3A2A08
  classDef out fill:#E8EEF2,stroke:#7B8C9C,color:#14202B
  class H hst
  class E eng
  class U out
```

Splitting it here means the host writes something it cannot get wrong. Asking the host for
"the units still available" would oblige every host to reimplement a rule the engine enforces
anyway — and one it would throw over at fork time when they got it wrong.

---

## How each seam fails

Three policies, and the asymmetry is deliberate rather than inconsistent.

```mermaid
flowchart TB
  Q{"A host seam throws.<br/>What now?"}

  A["<b>Fail soft</b><br/>log it, treat as no answer<br/><br/>assignment · due date<br/>directory · subject labels"]
  B["<b>Fail closed</b><br/>deny<br/><br/>authorization<br/>route conditions"]
  C["<b>Fail loud</b><br/>let it throw<br/><br/>the inbox read"]

  Q --> A
  Q --> B
  Q --> C

  A --- A2["Work stays visibly assigned<br/>to whoever held it.<br/><i>Somebody can see it and fix it.</i>"]
  B --- B2["The alternative is no gate at all,<br/>when nobody is watching.<br/><i>An absent gate is not diagnosable.</i>"]
  C --- C2["An empty inbox renders as<br/>'Nothing is waiting on you'.<br/><i>That is false, not degraded.</i>"]

  classDef soft fill:#F7EBD6,stroke:#B8791F,color:#3A2A08
  classDef closed fill:#E8EEF2,stroke:#7B8C9C,stroke-width:2px,color:#14202B
  classDef loud fill:#F7E4DC,stroke:#9A3412,color:#3A1508
  classDef why fill:#EDF1F3,stroke:#C6D1D8,color:#43535F
  class A soft
  class B closed
  class C loud
  class A2,B2,C2 why
```

A *lesser* answer is diagnosable — somebody sees the wrong assignee and fixes it. An *absent
gate* is not, and a *false* answer is not. Each seam is placed by asking which kind of wrong
its failure produces.

---

## Triggers and the outbox

```mermaid
flowchart TB
  Ev["A workflow event<br/><i>TaskCompleted, ForkConverged, …</i>"]
  M{"Dispatch mode"}

  IT["<b>InTransaction</b><br/>runs before the commit"]
  AC["<b>AfterCommit</b><br/>written to the outbox,<br/>drained after the commit"]
  BG["<b>Background</b><br/>handed to the job queue"]

  ITo["Its failure can roll back<br/>the whole operation<br/><i>(FailOperation policy)</i>"]
  ACo["The work is committed either way.<br/>Retried from the outbox."]

  Ev --> M
  M --> IT --> ITo
  M --> AC --> ACo
  M --> BG

  classDef ev fill:#E8EEF2,stroke:#7B8C9C,color:#14202B
  classDef eng fill:#F7EBD6,stroke:#B8791F,stroke-width:1.5px,color:#3A2A08
  classDef note fill:#EDF1F3,stroke:#C6D1D8,color:#43535F
  class Ev,M ev
  class IT,AC,BG eng
  class ITo,ACo note
```

**Rule of thumb: anything that talks to the outside world is AfterCommit.** A webhook inside
the transaction holds a database lock open for an HTTP call to somebody else's server — and
rolls back a completed task if their server is down.

---

## Two hosting models

The Blazor components talk to three seams — builder, runner, inbox. **The library implements
all three twice**, and the choice is your hosting model. Getting it wrong is not a compile
error, which is why it is worth a diagram.

```mermaid
flowchart TB
  subgraph SRV ["Blazor Server — a DbContext in the same process"]
    direction LR
    C1["Components"] --> I1["IWorkflowRunnerClient"] --> E1["EfWorkflowRunnerClient"] --> DB1[("Database")]
  end

  subgraph WASM ["Blazor WebAssembly — a browser, so no DbContext at all"]
    direction LR
    C2["Components<br/><i>identical</i>"] --> I2["IWorkflowRunnerClient"] --> H2["HttpWorkflowRunnerClient"]
    H2 -->|HTTP| EP["/workflow/runner/*"] --> E2["EfWorkflowRunnerClient"] --> DB2[("Database")]
  end

  classDef ui fill:#E8EEF2,stroke:#7B8C9C,color:#14202B
  classDef seam fill:#D9EAE8,stroke:#1F6F72,stroke-width:1.5px,color:#0C2E2D
  classDef eng fill:#F7EBD6,stroke:#B8791F,stroke-width:1.5px,color:#3A2A08
  classDef db fill:#DDE5EA,stroke:#5A6B7A,color:#14202B
  class C1,C2 ui
  class I1,I2 seam
  class E1,E2,H2,EP eng
  class DB1,DB2 db
```

The components are identical in both. Only the implementation behind the seam changes — which
is why the same runner drives both the builder's "Try it" pane and a real document page.

### What a host supplies either way

| Supply | Because | If you skip it |
|---|---|---|
| `AssignmentRoles` | Role keys are opaque, so the engine cannot enumerate them | Authors get no "who does this go to" choices |
| `IWorkflowEditorActorAccessor` | Every workflow edit is attributed to somebody | **Throws.** A placeholder would attribute every edit to a fiction |
| `IWorkflowActorResolver` | Who your people are, what your org units are | Empty pickers, one log line |
| `IWorkflowSubjectResolver` | What `ChangeRequest:42` is called | Raw keys in the inbox, no links |

> **A trap worth knowing.** Any minimal-API handler taking a library interface **must** mark it
> `[FromServices]`. Without it an unregistered type is inferred as a *body* parameter, a GET
> may not have one, and the whole application fails to start with an error naming neither the
> service nor the route. There is a regression test for each endpoint group.

---

## What is deliberately absent

| Not here | Why |
|---|---|
| A user or org model | Every host has one and none agree. Opaque ids plus a directory seam. |
| Its own database | Atomicity with the host's own writes is worth more than isolation. |
| A DSL or code-defined workflows | Customers author their own processes through the builder without a deployment. |
| A status setter | Would make routing, blocking and triggers bypassable. |
| Read authorization | Reads are not gated; the host decides what its screens show. Writes are. |
| **Timers** | The one roadmap item still unbuilt. Distinct from a deadline — a deadline nags a human, a timer moves the run itself. |
