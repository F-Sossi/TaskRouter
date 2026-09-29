# TaskRouter — design overview

What the library is, how it is meant to be used, and why it is shaped the way it is.

For the API surface, read the XML docs on `IWorkflowEngine`. For the current state of the
work, read `STATE.md`. This document is the reasoning.

---

> **Prefer diagrams?** [architecture.md](architecture.md) is the same reasoning drawn —
> the model, the lifecycle, the seam, and how each seam fails.

## What it is

A task-based workflow engine for .NET. Workflows attach to **any** subject — a document, a work
order, an onboarding case — and are authored as **data**, not code.

The distinction that matters: where Elsa, WorkflowCore and Temporal orchestrate *code*, this
routes work between *people*. A task is a first-class, queryable, assignable row, so "show me my
open tasks" is a plain query rather than something built on top of an execution log. If your
workflow is a series of service calls, use one of the others. If it is a series of humans, this
is shaped for that.

## The model

Six concepts, and everything else is built from them.

| | |
|---|---|
| **Task type** | A row, not an enum value. "Technical Review" is data the host defines. |
| **Definition** | The logical workflow. Behaviour lives in its versions. |
| **Version** | An immutable published snapshot: task definitions, their valid outcomes, and the routes between them. |
| **Run** | One execution of a version against one subject. |
| **Task** | One step of a run: assigned, completed with an outcome, routed onward. |
| **Subject** | What a run is *about*, as an opaque `(type, id)` pair. The engine never learns what it means. |

## The lifecycle

**Author.** A workflow is a graph of task definitions. Each carries its valid outcomes, its
outgoing routes, an optional assignment role key, and flags — is it terminal, forkable, a
convergence point, blocking. The graph is validated before it can be published: unreachable
tasks, dead ends, uncovered outcomes and cycles are all refused. A workflow that publishes is a
workflow that can run.

**Publish.** Publishing produces a version and demotes the previous one. Runs pin to the version
they started on, so editing a workflow can never reroute work already in flight, and you can
always answer which version a given run followed.

**Start.** A run begins against a subject, optionally carrying an initial assignment — the org
unit the work belongs to, and optionally the person its first task goes to. The entry task is
created and the host is asked who gets it.

**Advance.** Someone completes a task with an **outcome** — never a status. The engine matches
the outcome to a route, optionally gated by a named condition the host evaluates, creates the
next task, resolves its assignment, and fires triggers. Repeat until a terminal task.

**Branch.** A forkable step can fan out into one task per **branch key** — an opaque org unit —
and converge at a named task once every branch finishes. Selective rejection sends some branches
back for rework while the rest hold.

**Delegate.** A **sub-workflow** is an ordinary workflow definition attached to a version, which
can be started from a task and optionally blocks it until finished. Attach it to the version
rather than to one task and it is available from any task in that workflow. Supply an assignment
when starting one and the chain resolves its role keys against *that* org unit rather than the
parent's — which is how one team asks another for input.

**Chase.** Task definitions can carry reminder lead times. A sweep finds work approaching or past
its deadline and fires trigger events for it, so a host can nag, escalate, or both.

## What the host owns

The engine has no user model, no org chart, and no idea what a document is. Those are seams.

**Required in practice**

- **`IWorkflowDbContext`** — the engine's 17 tables live in *your* database, created by *your*
  migrations. Every table is prefixed `TaskRouter*`, so they coexist with yours. This is the one
  registration no DI extension can do for you, because nothing else knows which of your contexts
  implements it.
- **`IWorkflowAssignmentResolver`** — who a task goes to. Given a role key and the current
  assignment, return the new one. The default keeps whatever it was handed, so without one
  nothing is ever routed to anybody.
- **`IWorkflowAuthorizationPolicy`** — who may act. The default permits everything and says so in
  the log, once.
- **`IRouteConditionEvaluator`** — one per named condition your routes use.

**Optional, and inert until supplied**

`IWorkflowDueDateResolver` (no resolver, no deadlines), `IWorkflowProgressSink`,
`IWorkflowNotificationSink`, `IWorkflowJobQueue` (defaults to in-process), and any
`IWorkflowTrigger` of your own.

**Needed only for the UI components**

- **`IWorkflowActorResolver`** — your directory of people and org units: what an actor is called,
  who may be assigned to, what org units exist, which one an actor belongs to, and which ones
  they can *see work in*. The runner's reassign and fork pickers come from here.

  The last two are separate on purpose. Delegating a task needs exactly one unit — work goes to
  one place. An inbox needs all of them, because "unclaimed work waiting for my section" is a
  different question for somebody covering three sections than for somebody covering one. The
  first host to integrate had a many-to-many person/section table, and a single-unit seam hid
  half their inbox with nothing on screen to indicate it had.
- **`IWorkflowSubjectResolver`** — what a subject is called and where it lives, so an inbox row
  reads "CR-2026-0042, Pump room rewire" and links to it rather than showing `ChangeRequest:42`.
  Batched by signature: one call for every subject on screen, because a per-subject method would
  make N+1 the default for everyone who implemented it.

Both default to answering emptily and logging once. **Neither is consumed by the engine** — it
stores opaque ids and routes work through `IWorkflowAssignmentResolver`; these decide what the UI
can *show*, not where work goes.

## Design decisions

Most of these are legible only against the engine this was extracted from, which had already
failed in specific ways. Where that is the reason, it is stated.

### Routes target a task definition, not a task type

The load-bearing decision. It means one workflow may contain the same task type more than once —
a mainline review and another inside a delegated chain — with different outgoing routes.

The original engine keyed routing on a task-type enum, so a type could appear only once per
workflow. That is *why* it could not express reusable sub-workflows at all: a shared chain had
nowhere to live. Every capability built on sub-workflows traces back to this one change.

### Task types are data

A row, not an enum member. The original used a 55-value C# enum — which a general library cannot
ship, and which forced one node per type per workflow.

### Runs pin to a published version

The original had no versioning: its create-or-update merged task definitions **in place**, on
rows that in-flight tasks referenced. Editing a live workflow silently rerouted work already
running through it.

### The engine owns task status

No API accepts a caller-supplied status. Callers supply an *outcome*; the engine derives status,
routing, blocking checks and triggers from it. In the original, a client-supplied `Status`
bypassed all four.

### Everything is opaque at the boundary

Subjects are `(type, id)`. Actors and branch keys are strings. The engine's base entity has no
user navigation, no fixed-length id, no serialization or grid attributes.

This is what made extraction possible at all. The original's base entity transitively imported
every one of those, which is precisely what made that engine unpackageable.

The cost is real and worth stating: the engine cannot answer "who is this person" or "which team
owns this document". A host that wants role-based assignment must tell it, which is why a run
can be started with an org unit.

### Every operation is one transaction

Opened through the provider's execution strategy, so it composes with `EnableRetryOnFailure`
rather than throwing on contact with it.

Operations also **compose**: if a host opens its own transaction, the engine joins it rather than
opening a second, so several operations can be made atomic together. One caveat — the joined
path leaves post-commit actions to whoever owns the outer transaction, so a host that wraps a
sequence must run `IWorkflowPostCommitActions` itself, or after-commit triggers never fire.

### Seams fail differently, deliberately

**Resolvers fail soft.** An assignment or due-date resolver that throws is logged and treated as
no-change. Work stays visibly assigned to whoever held it, which somebody can see and correct.

**A run can be abandoned, and that is not the same as finishing it.** `CancelRunAsync` cancels
every open task and marks the run `Cancelled`. Cancelling each task by hand instead leaves the
run `Completed` — no report can tell that from work that was actually done. Completed tasks
keep their outcomes either way: cancelling ends what is outstanding, it does not rewrite what
was decided. It is its own `WorkflowOperation`, because ending everyone's work on a subject is
a different act from cancelling one task and a host will want a different rule for it.

**Authorization fails closed.** A policy that throws denies. The fallback would otherwise be *no
gate* — every operation open to everybody at the moment nobody is watching.

The asymmetry is the point: a lesser assignment is diagnosable, an absent gate is not.

**The inbox fails loudly, and it is the exception that proves the rule.** Every read in the runner
degrades to an empty list — a run view missing one panel beats an error page. The inbox does the
opposite: a failed engine read throws rather than returning no rows, because an empty inbox
renders as *"Nothing is waiting on you"*, which is not a degraded answer but a false one. Subject
resolution around it still fails soft, since a row under a raw key is visibly wrong. The test for
each direction is in `InboxClientTests`, and both were verified by mutation — the pair is easy to
state and easy to get backwards.

### Where the line between host and engine falls

The clearest worked example is the fork picker. A host is asked for **every** org unit it has;
the engine removes the ones that fork has already branched on. Splitting it there means the host
writes a one-line projection it cannot get wrong, while the rule about fork state — which the
engine enforces anyway, and would throw over at fork time — stays in one place. Asking the host
for "the units still available" would have obliged every host to reimplement that rule.

The test for it hands the engine a directory returning everything, so it fails if the filtering is
ever pushed back across the seam.

### Reads are not authorized

The policy is consulted for mutations only. A host reads in order to decide what to show, and
gating reads would put a policy call on every page load.

Two consequences, both accepted rather than overlooked. A host re-exposing workflow reads must
gate them itself. And a denied actor can distinguish "not authorized" from "not found", because
the policy needs the task loaded before it can decide anything about it — refusing to
distinguish them would mean refusing to say *why* a call failed.

### Triggers have dispatch modes and an outbox

A trigger runs either **in transaction** with the state change, or **after commit**. The
distinction is whether the effect can be undone: progress should be atomic with the task that
produced it; an email cannot be un-sent if the transaction rolls back. After-commit work goes
through a transactional outbox, so it survives a crash between commit and dispatch, and each
message gets its own scope and transaction — one failing trigger does not fail the batch.

### The validator refuses to publish a broken graph

Because definitions are data, and data can be wrong. Unreachable tasks, dead ends, uncovered
outcomes, cycles and entry-point problems are all refused at publish time. Drafts are exempt, so
a half-finished edit cannot affect anything running.

## What is deliberately absent

- **No user, org or document model.** See the opacity decision above.
- **No job framework.** `IWorkflowJobQueue` is a contract; the default runs in-process.
- **No opinion about your schema beyond 17 prefixed tables**, created by your migrations.
- **No authorization scheme.** The endpoints package deliberately never calls
  `RequireAuthorization`, because it does not know your scheme.
- **Timers.** The one substantial roadmap item still unbuilt: "wait ten days, then advance on
  your own". Durable suspend and resume, and a different feature from a deadline — a deadline
  nags a human, a timer moves the run itself.

## What a first integration taught

The library was validated by putting it into a substantial application written by someone with
no knowledge of this codebase. That found a dozen things, and the pattern is more useful than
the list: **almost every one was about composition or the boundary, and none was about core
mechanics.**

- Neither sink was told **who acted**, though the engine always knew.
- Progress could not report **several branches at once**, so a convergence lost per-branch
  figures.
- **Nothing could put an org unit on a run**, so role-based assignment silently never resolved —
  every role default fell through to "keep the current assignment".
- Publishing a workflow by hand took **four saves in an undiscoverable order**, and left the
  version briefly flagged published while structurally incomplete.
- The read side answered "tell me about this thing" and never "tell me about **these** things",
  so the first screen a host built bypassed the read API entirely.

Each was individually correct code that composed wrongly, which is why running one real workflow
end to end found more in an afternoon than review had in weeks. All are fixed. The remaining
known gap is that the builder UI ships components and an in-process seam but no HTTP layer, so
it is currently usable only from a Blazor **Server** host.
