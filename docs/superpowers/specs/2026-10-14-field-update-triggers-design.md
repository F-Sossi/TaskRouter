# Field-update triggers — design

**Date:** 2026-10-14 · **Branch:** `field-triggers` · **Status:** design, not built

## Problem

A workflow author cannot change anything on the document a workflow runs against. "When the
review task completes, put this Misc Document On Hold" today means a host developer writing a
C# `IWorkflowTrigger` for that one case. Triggers are code; authors can only configure the
ones that exist.

The goal is a trigger an author **builds** in the builder — pick a target, pick fields, give
values — over a set of fields the host has deliberately exposed, with the host's rules
(TRDIS's document and work-item state machines) still in force.

## Decisions taken

| Question | Decision |
|---|---|
| What can be modified | The run's subject, plus related entities the host exposes. For TRDIS: the document, the work item for the task's section, and all work items on the document |
| What a value can be | A literal, or a string containing tokens (`{actor}`, `{today+5d}`, `{var:name}`, …) |
| How the host declares fields | A fluent code catalog registered at startup (approach A). Not attributes, not an admin table |
| When a write is refused | The trigger's existing `TriggerFailurePolicy`. This trigger defaults to `FailOperation` |
| State-machine fields | **Respected.** Setting Status to *On Hold* fires whichever transition leads there; if none is permitted from the current state, the write fails |
| Different document types having different fields | Layered catalog: base fields declared once on `DocumentBase`, each subject type adds its own (see below) |

## The mechanism

### 1. The catalog (host, startup)

The catalog answers, per **subject type**: which **targets** exist, and which **fields** each
target has. A target is a named way to get from a trigger context to zero or more entities.

```csharp
services.AddTaskRouterFor<TRDISContext>()
    .AddWorkflowFields(fields =>
    {
        // ---- every document type -------------------------------------------------------
        fields.ForSubjectTypes(DocumentSubjectTypes)
            .Target<DocumentBase>("document", "This document", Cardinality.One,
                resolve: ctx => ctx.Db<TRDISContext>().Documents
                                   .Where(d => d.Id == DocumentId(ctx.Subject)))
                .Field(d => d.Status, "Status",
                    write: (doc, value, w) => w.Service<IDocumentService>()
                                               .TransitionToAsync(doc, value, w.ModifierEdipi))
                .Field(d => d.InternalDueDate, "Internal due date")
                .Field(d => d.DueDate, "Due date")
                .Field(d => d.Comments, "Comments")
                .Field(d => d.GroupId, "Group", FieldKind.Lookup, options: GroupOptions)
                .OnWritten((doc, w) => Stamp(doc, w))

            .Target<WorkItem>("branchWorkItem", "Work item for this task's section", Cardinality.One,
                resolve: ctx => WorkItemsOf(ctx)
                                   .Where(w => w.AssignedSectionId == SectionId(ctx.Task.AssignedBranchKey)))
                .Field(w => w.WorkItemState, "State", write: WorkItemTransition)
                .Field(w => w.CurrentlyAssignedEdipi, "Assigned to", FieldKind.Actor)
                .Field(w => w.InternalDueDate, "Internal due date")
                .OnWritten((wi, w) => Stamp(wi, w))

            .Target<WorkItem>("allWorkItems", "All work items on this document", Cardinality.Many,
                resolve: ctx => WorkItemsOf(ctx))
                .Field(w => w.WorkItemState, "State", write: WorkItemTransition)
                .Field(w => w.InternalDueDate, "Internal due date")
                .OnWritten((wi, w) => Stamp(wi, w));

        // ---- LARs add their own fields to the same "document" target ------------------
        fields.ForSubjectType("LiaisonActionRecord")
            .Extend<LiaisonActionRecord>("document")
                .Field(l => l.Urgency, "Urgency")
                .Field(l => l.IsReplyRequired, "Reply required")
                .Field(l => l.DateReplyRequiredBy, "Reply required by")
                .Field(l => l.DateReplyReceived, "Reply received")
                .Field(l => l.DateSent, "Date sent")
                .Field(l => l.CogCoordinatorId, "Cog coordinator", FieldKind.Actor)
                .Field(l => l.RecipientId, "Recipient", FieldKind.Lookup, options: IndustrialActivityOptions);
    });
```

**Field kind** is inferred from the property type unless given:

| Property type | Kind | Builder control | Accepts tokens |
|---|---|---|---|
| `string` | Text | text box | yes |
| `int`, `double`, `decimal` | Number | number box | `{var:x}` |
| `bool` | Boolean | checkbox | no |
| `DateTime` | Date | date picker or token | `{today}`, `{today+Nd}`, `{now}`, `{var:x}` |
| enum | Choice | dropdown of values, labelled by `[Display(Name)]` | no |
| explicit `FieldKind.Actor` | Actor | the existing person autocomplete | `{actor}`, `{task.assignee}` |
| explicit `FieldKind.Lookup` | Lookup | autocomplete over the host's `options` delegate | no |

Nullable properties get a *Clear* option. A field may declare `write:`; without one the library
assigns the property directly. **Any field guarded by host rules must declare `write:`** — the
catalog is where the host decides that, which is why it is code.

### 2. Different document types, different fields

This is the part that has to scale: LARs already carry nineteen fields of their own, Ship
Alterations twenty-one, and the other types will grow.

**The catalog for a subject type is the union of every declaration that applies to it.**

- `ForSubjectTypes(DocumentSubjectTypes).Target<DocumentBase>("document", …)` applies to
  all eight document types. Declared once.
- `ForSubjectType("LiaisonActionRecord").Extend<LiaisonActionRecord>("document")` adds fields
  to the **same** `document` target, for LARs only.
- So a Misc Document workflow sees *Status, Internal due date, Due date, Comments, Group*; a LAR
  workflow sees those **plus** *Urgency, Reply required, …, Recipient*. The author sees one
  "This document" target either way.

Why this works without extra plumbing: `DocumentBase` is TPH in one `Documents` table, so the
`document` target's resolver already materialises a `LiaisonActionRecord` for a LAR. The
extension's accessors run against that instance. At runtime each field checks the resolved
entity is assignable to its declaring type and fails with a wiring error if not (a resolver
for a LAR subject that returned a non-LAR is a host bug, not an authoring error).

`Extend` can also add whole new targets for one type — e.g. a future
`ForSubjectType("ShipAlteration").Target<SAItem>("saItems", …)` — the same way.

**Convention for TRDIS:** one partial per document type —
`TrdisWorkflowFields.cs` (base + work items), `TrdisWorkflowFields.Lar.cs`,
`TrdisWorkflowFields.ShipAlteration.cs`, … — so whoever extends a document type extends its
workflow fields in a file named after it, next to `TrdisWorkflowVocabulary`.

**Startup validation** (joins `ValidateWiringAtStartup`) rejects:

- a field key declared twice for the same subject type and target (base and extension collide);
- `Extend<T>(key)` where no base target `key` applies to that subject type, or `T` does not
  derive from the base target's entity type;
- a subject type in the catalog that is not in `WorkflowBuilderOptions.SubjectTypes`.

### 3. Authoring (builder)

The library ships one built-in trigger, **`workflow.updateFields`** ("Update fields"). Its
descriptor sets `CustomEditorComponent` to `UpdateFieldsEditor`, which fetches the catalog for
the workflow version's `SubjectType` from a new builder endpoint and renders:

```
Update fields                                 on: [ Task completed ▾ ]
Target:  [ This document                 ▾ ]
  [ Status            ▾ ]  =  [ On Hold ▾ ]                      ✕
  [ Comments          ▾ ]  =  [ Paused by {actor} on {today} ]   ✕
  + Add field
If a change is refused:  (•) Undo the step   ( ) Log it and carry on
```

One trigger = one target + an ordered list of assignments. A second target is a second
trigger. A version with no `SubjectType` cannot use this trigger; the editor says so.

**Stored config** stays in the existing flat `ITriggerConfig`:

| Parameter | Value |
|---|---|
| `target` | `"document"` |
| `assignments` | `[{"field":"Status","value":"Hold"},{"field":"Comments","value":"Paused by {actor} on {today}"}]` |

Choice values are stored by **enum member name** (`"Hold"`), not display name or number, so a
relabel does not break a workflow and a renumber does not silently change its meaning.

### 4. Execution (runtime)

Default dispatch `InTransaction`, default failure policy `FailOperation`. Runs on the host's
context, inside the engine's transaction, as the acting user.

1. Look up `(run.SubjectType, target)` in the catalog. Missing → fail (*"This workflow updates
   'Urgency', which Misc Documents do not have"*).
2. `resolve` the entities. `Cardinality.One` with zero results → fail (*"No work item for
   section 12 on this document"*); with more than one → fail. `Many` with zero → succeed, nothing changed.
3. For each assignment: render tokens, convert to the field's type. Unparseable → fail.
4. Authorize: `WorkflowOperation.UpdateSubjectFields` via `IWorkflowAuthorizationPolicy`, once
   per trigger execution, with the subject, target and field keys on the context. Deadline sweep
   runs as `System` and bypasses it, as today.
5. For each entity, for each assignment in order: if the value already equals the target value,
   record *unchanged* and skip; otherwise call `write` (or assign). A throw is a refusal.
6. `OnWritten` once per changed entity.
7. Record a change summary on the `TriggerExecution` —
   `document#42 Status: Drafting → On Hold; Comments: (empty) → "Paused by …"` — so the run
   history shows what automation changed and why.

All assignments on all entities succeed or the trigger fails as a whole; under
`FailOperation` the engine's transaction rolls back, so nothing is half-applied. Under
`LogAndContinue`, a refusal from a host `write` that already called `SaveChangesAsync` inside
the ambient transaction is **not** undone — see *Risks*.

### 5. State-machine fields (TRDIS)

`Status` and `WorkItemState` declare `write:` delegates that call new methods on the existing
services:

```csharp
// IDocumentService
Task<Result<Unit, Exception>> TransitionToAsync(DocumentBase document, DocumentStatus target, string? modifierEdipi);
```

which builds the machine with `DocumentStateMachineFactory.Create(document)`, finds the
permitted trigger whose destination is `target` (via Stateless's `GetPermittedTriggers` +
`GetInfo()`), and fires it. No permitted trigger → `InvalidOperationException("Cannot move
from Issued to On Hold")`. More than one → error (none today: every state has at most one
transition per destination). Already in `target` → no-op. `WorkItemService` gets the same
method over `WorkItemStateMachineFactory`. The existing `FireTriggerAsync` methods are
refactored to share the core, so the transition rules live in one place.

**Modifier stamping** follows `TrdisProgressSink`: `ModifierId` takes the actor's EDIPI via
`TrdisWorkflowVocabulary.ToEdipi`; for `WorkflowActors.System` the row keeps its existing
modifier. `Modified = UtcNow` either way.

### 6. Tokens

`TriggerTemplate` gains `{actor}` (acting actor id), `{now}`, `{today}`, `{today+Nd}` /
`{today-Nd}`, and `{var:name}` (run variable). Existing tokens (`{task.*}`, `{event}`,
`{subject.type}`) are unchanged. Unknown tokens pass through literally, as today. Dates render
as ISO-8601 invariant, then convert to the field's type.

### 7. Publish-time validation

`WorkflowDefinitionValidator` gets an optional catalog. For each `workflow.updateFields`
trigger on a version with a `SubjectType`:

- target and every field key exist for that subject type;
- a Choice value is a member name; a literal Number/Boolean/Date parses;
- the trigger's event is one the catalog can resolve on (e.g. `branchWorkItem` needs a task with
  a branch — warn, not error, since branching is decided at run time).

So removing *Urgency* from LARs fails validation on every LAR workflow that sets it, at publish,
rather than failing a user's task completion later. Versions already published are not
re-validated; one that names a removed field fails at run time with step 1's message.

## What gets built

| Package | Adds |
|---|---|
| **Core** | `WorkflowFieldCatalog`, `WorkflowFieldsBuilder` (`ForSubjectTypes`, `ForSubjectType`, `Target`, `Extend`, `Field`, `OnWritten`), `FieldKind`, `Cardinality`, serialisable `FieldCatalogDto` for the builder, `WorkflowOperation.UpdateSubjectFields`, validator rule |
| **EntityFrameworkCore** | `UpdateFieldsTrigger` (`workflow.updateFields`), value conversion, `TriggerTemplate` tokens, `TriggerExecution.Detail` (nullable, one migration), startup wiring checks |
| **AspNetCore** | `GET {builder}/fields/{subjectType}` and `GET {builder}/fields/{subjectType}/{target}/{field}/options?q=` (lookups) |
| **Blazor** | `UpdateFieldsEditor.razor`; builder client methods for both endpoints |
| **Demo** | Catalog over `DocumentBase` / `ChangeRequest` (as the LAR stand-in) / `WorkItem`; a seeded "put on hold" workflow; demo statuses gain a small state machine so refusal is demonstrable |
| **TRDIS (`Dev`)** | `TrdisWorkflowFields*.cs`; `TransitionToAsync` on both services; registration in `PersistenceStartupConfiguration`; claim for the new operation in `TrdisAuthorizationPolicy` |

## Testing

- Catalog: union per subject type, `Extend` merging, each startup rejection.
- Conversion: every kind, nullable clear, enum by name, each token.
- Trigger: One/Many resolution, zero/many-for-One failures, unchanged short-circuit, ordered
  assignments, refusal under each failure policy, change summary text.
- Authorization: denied operation fails the execution; `System` bypass.
- Validator: unknown target/field, LAR-only field on a Misc workflow, bad literal.
- EF integration (demo context): completing a task puts the document On Hold in the same
  transaction; refused transition rolls back the completion.
- TRDIS (`Dev` test fixture): `TransitionToAsync` against both state machines; a Misc Document
  workflow puts the document On Hold; an Issued document refuses and the completion rolls back;
  a LAR workflow sets Urgency.
- Browser: author the trigger in the demo builder and run it from *Try it*.

## Risks and open points

- **`LogAndContinue` + a host write that saved.** TRDIS's service methods call
  `SaveChangesAsync` inside the ambient transaction. If assignment 2 of 3 fails under
  `LogAndContinue`, assignment 1 is already saved and the transaction still commits. Mitigation
  in this design: the trigger wraps its writes in a savepoint and rolls back to it on failure,
  so `LogAndContinue` means *nothing changed, workflow advanced*. The engine always runs this
  inside `WorkflowTransaction`, so `Database.CurrentTransaction` exists; on a provider without
  savepoint support the trigger refuses `LogAndContinue` at publish and requires `FailOperation`.
- **`LiaisonActionRecord.IsCancelled` duplicates `Status == Cancelled`.** Not exposed in the
  catalog; cancelling a LAR from a workflow goes through Status. TRDIS should decide which is
  authoritative before anyone asks for the flag.
- **`TrdisDocumentType` includes types with no workflow yet.** `ForSubjectTypes` takes the same
  derived list `TrdisSubjectTypes()` already uses, so a new type gets the base fields with no
  extra wiring.
- **Out of scope:** expressions (`DueDate + 5d` relative to another field), conditional
  assignments, writes to entities the host has not exposed, raw (state-machine-bypassing)
  writes.
