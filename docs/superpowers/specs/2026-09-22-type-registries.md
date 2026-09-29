# Task types and outcomes as user-definable registries

**Status:** design, not built. Written 2026-09-22 after the host's owner asked for the
builder's types to be user-definable, with a seeded starter set.

**Depends on nothing.** It is, however, *shaped* by a host decision recorded in
the host's own integration notes — see [What the first host
does](#what-the-first-host-does) at the end.

---

## The ask

> "Task types should live in the library as user definable table entries so that they get a
> loaded subset of common task types and outcomes and they can add more."

Plus an open question: **namespaces, to avoid collisions with what a host already has?**

## What exists today

Three facts decide most of this, and they are easy to get wrong from memory.

1. **`TaskTypeDefinition.Key` is globally unique** — `HasIndex(x => x.Key).IsUnique()` in
   `WorkflowModelBuilder`, `nvarchar(100)`. There is one row per key for the whole database.
2. **Outcomes have no registry at all.** `TaskOutcomeDefinition` hangs off a *task definition*
   (`TaskDefinitionId`, `OutcomeKey`, `DisplayName`, `Order`), unique on
   `(TaskDefinitionId, OutcomeKey)`. "The outcomes this organisation uses" is not an object
   that exists — an author retypes `approved` on every task, and a typo is a silent dead end.
3. **Routes match outcomes by string.** `TaskRoute.OutcomeKey` is `nvarchar(100)`, compared
   for equality. Task definitions, by contrast, reference their task type **by id**.

That last asymmetry drives two different answers below, so it is worth holding on to:
**renaming a task type's key is safe for routing and breaks host vocabularies; renaming an
outcome key breaks routing.**

## Decision: a flag, not a namespace

**Keys stay bare.** `review`, not `taskrouter:review`.

Namespacing looks like it prevents collisions and mostly relocates them. With
`taskrouter:review` and `acme:review` both in the picker, an author chooses one and nothing
says which is correct — a collision has become a silent duplicate, which is worse, because a
collision at least announces itself. The cost is paid everywhere: the key is the string that
routes match, that hosts translate, and that appears in logs and support conversations.

Instead, `TaskTypeDefinition` gains:

| Column | Why |
|---|---|
| `Source` (`Standard` \| `Host`) | Tells shipped from added. Groups the picker, and lets the UI protect a standard row from casual renaming. |
| `IsArchived` | Retirement. A type in use cannot be deleted — task definitions reference it by id — but it can stop being offered. |

That covers what namespacing was reaching for:

- **Collisions** — seeding is idempotent and never overwrites (below).
- **Telling them apart** — that is what `Source` is.
- **Not wanting the starter set** — archive the rows, or never seed them.

**When a namespace *would* be right:** one database serving several tenants who each define
their own vocabulary. TaskRouter is single-tenant — one database, one organisation — so
namespacing would be paying for a problem it does not have. If that ever changes, the
namespace belongs in a `Tenant` column and a composite unique index, not inside the key
string.

## Shape

### Task types

`TaskTypeDefinition` gains `Source` and `IsArchived`. Nothing else changes; it is already a
global table with a unique key, and `CreateTaskTypeAsync` is already idempotent — it returns
the existing row for a key that is present, rather than throwing.

### Outcomes

A new global table, mirroring task types:

```
TaskRouterOutcomeTypes
  Id, Key (unique, 100), DisplayName (200), Description?, Source, IsArchived, + audit
```

**It is a picker source, not a foreign key.** `TaskOutcomeDefinition` keeps its own
`OutcomeKey` string exactly as it is today.

This is the important decision in the whole document. If per-task outcomes referenced the
registry by id, then renaming a registry row would change what every route in the system
matches — silently, for workflows already running — and deleting one would break them. Routes
match strings; the registry exists to stop people *typing* those strings, not to own them.

The practical consequence: an outcome key can exist on a task without being in the registry.
That is not a defect, it is how existing data stays valid, and the builder already surfaces
it — the task dialog warns when a task carries a key the host's list does not contain. That
warning generalises to the registry with no change.

### Seeding

```csharp
await db.SeedStandardTypesAsync(ct);   // opt-in, idempotent
```

- **Opt-in.** Never run from a migration. A host arriving with a settled vocabulary should not
  find a dozen rows it did not ask for in its pickers.
- **Never overwrites.** A key that already exists is skipped, and the skip is **logged at
  information level with the key**. That log line is the only signal that the host's `review`
  and the standard `review` may mean different things, so it must not be silent.
- **Re-runnable.** Calling it twice is a no-op; calling it after a library upgrade adds only
  what is new.

### The starter set

Deliberately small and boring. Every one of these is a label — nothing in the engine branches
on a task type — so the set is a convenience, not a contract.

**Task types:** `review`, `approval`, `signature`, `data-entry`, `notification`, `rework`.

**Outcomes:** `approved`, `rejected`, `completed`, `returned`, `acknowledged`,
`no-action-required`.

Twelve rows. A host that wants none of them does not call the seeder.

## What this does not change

- **No existing data is touched.** Task definitions reference task types by id; routes carry
  outcome strings. Both keep working untouched whether or not a registry exists.
- **`WorkflowBuilderOptions.Outcomes` keeps its meaning**, but changes role: today it is the
  only list there is; afterwards it is how a host *narrows* the registry to what it can
  translate. A host that stops translating (see below) simply stops setting it.
- **Nothing becomes required.** A host that never seeds and never registers a type gets
  exactly today's behaviour.

## What the first host does

The first host to integrate this translates engine keys into two closed enums of its own —
eighteen task types and eight outcomes. Those enums are the only reason its types cannot
already be created freely, and they turn out to have almost no consumers; the count and the
removal list live with that host's own notes rather than here.

**This design works either way**, which is why it is written down before that decision:

- If the host **stops translating**, it seeds nothing, keeps the task-type rows it already
  has, and drops `WorkflowBuilderOptions.Outcomes`. Authors then add types freely.
- If it **keeps translating**, it keeps setting `Outcomes` to the ones it can read, and the
  registry is constrained to that list in its builder while remaining open in the library.

The one thing to settle before building: whether the starter keys collide with the host's
own. On that host's spelling they do not — its keys are all compound and specific to its
domain, none of them in the starter set — so it would take the starter set cleanly if it
wanted it.

## Build order, when it happens

1. `Source` and `IsArchived` on `TaskTypeDefinition`, plus the migration.
2. The outcome registry table, its migration, and `GetOutcomeTypesAsync` /
   `CreateOutcomeTypeAsync` on `IWorkflowBuilderClient` — mirroring the task-type pair that
   already exists.
3. The builder's outcome editor reads the registry, with the same Add affordance the task
   type picker gained on 2026-09-16.
4. `SeedStandardTypesAsync`, with the starter set and the skip log.
5. Archive/retire in the builder UI, which is the part that stops the picker filling up over
   years.
