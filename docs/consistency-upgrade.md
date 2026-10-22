# Upgrading to `0.1.0-preview.16`: consistency fixes

These changes address the second pre-1.0 review, of `preview.15`, and section A of
[`1.0-readiness.md`](1.0-readiness.md). Apply the schema changes before
deploying the updated engine. Do not run old and new workers together during the upgrade.

## Database upgrade

The demo includes the `ReviewConsistencyGuards` EF migration. Applications that own their
workflow tables must generate and review a migration from their own DbContext after
updating all TaskRouter packages together. The model adds:

- Nullable `TaskRouterTasks.ReminderLastCheckedAt`, with a filtered reminder scan index.
- Nullable `TaskRouterOutbox.EventPayload` containing a versioned event snapshot.
- A unique `(WorkflowRunId, Name)` index for run-scoped variables (`TaskId IS NULL`).
- A unique `WorkflowDefinitionId` index for active, latest, published versions.
- Concurrency metadata on task status and outbox lease ownership (no new token columns).

The existing task-variable unique index remains filtered to `TaskId IS NOT NULL`.
These filtered indexes target SQL Server, as does the rest of the supported persistence
model. Generate provider-appropriate migrations before using another provider.

Before migrating, check for historical duplicates:

```sql
SELECT WorkflowRunId, Name, COUNT(*) AS Copies
FROM TaskRouterVariables
WHERE TaskId IS NULL
GROUP BY WorkflowRunId, Name
HAVING COUNT(*) > 1;

SELECT WorkflowDefinitionId, COUNT(*) AS LatestVersions
FROM TaskRouterDefinitionVersions
WHERE IsPublished = 1 AND IsLatest = 1 AND IsArchived = 0
GROUP BY WorkflowDefinitionId
HAVING COUNT(*) > 1;
```

Back up the database and reconcile duplicates according to the application's history:
choose the authoritative variable value and the version intended for **new** runs. Do
not change the version IDs pinned by existing runs. The demo migration refuses duplicate
data with a diagnostic rather than deleting history or choosing an arbitrary winner.
Variable-name index comparisons follow the database collation; the variable API uses
case-insensitive name lookup when writing.

Pending legacy outbox messages whose payload is null use the previous live task/branch
lookup. New messages preserve the task and explicit branch context **at event time**.
Run state, variables, trigger configuration, and host data are still read at delivery
time; this is not a snapshot of the entire database. Unsupported payload versions fail
the attempt rather than being silently reinterpreted.

## Transaction and worker contract

Workflow mutations serialize through the owning run row. Task status is also an
optimistic concurrency token, so a stale completion cannot create another successor.
Conflicts return a readable failure and HTTP 409; reload before retrying. Use a separate
DbContext/DI scope for each concurrent operation, not simultaneous calls on one context.

Database rollback restores tracked values, original values, pending host changes,
generated keys, and navigation membership to the operation checkpoint. Retry attempts
discard their automatic-completion work and post-commit callbacks. Inside a host-owned
transaction, operations use savepoints, so a failure undoes only that operation and a
caller that catches it can carry on. On a connection without savepoints — SQL Server with
`MultipleActiveResultSets=True` — they join the transaction as before: a failure cannot be
undone on its own, so let it reach whoever owns the transaction and roll back there. The
library's own nested work (publishing, discarding a test run, a trigger calling back into
the engine) works on either kind of connection. The host still owns the outer commit and
must run the scoped post-commit actions only after committing. If the rollback itself
fails, the original error is the one reported; discard that context and transaction.

Taking the tracker checkpoint is cheap: only entries with pending changes are copied up
front, and an unchanged entity is copied the first time it changes. Its cost is close to
one change-detection pass over the tracked entities, which `SaveChanges` already does.

Custom `IWorkflowPostCommitActions` implementations must implement `Checkpoint()` and
`Rewind(int)` in addition to `Add` and `RunAsync`. Rewind must remove only callbacks
added after the checkpoint, retaining callbacks belonging to the caller.

`WorkflowVariables.SetAsync` joins an existing transaction. Standalone calls now own a
transaction and save changes, including pending changes on the shared context, just as
engine operations do. Wrap related host/variable writes in `WorkflowTransaction` when
they must commit together. Within a workflow transaction, inline triggers continue to
see earlier unsaved variable writes, and task-scoped values still shadow run values.

Outbox workers claim messages with conditional updates, one message immediately before
execution. The processing transaction retains a row lock on the owned message through
bookkeeping, so an expired lease cannot be stolen from a still-running handler. Lease
ownership is also checked when saving. A successful recorded execution is not executed
again. The message's row stays locked while its trigger runs, including any HTTP call the
trigger makes, so keep handlers bounded and avoid touching their own outbox bookkeeping rows from
the handler's separate database scope. A crash after an external side effect but before
bookkeeping commits can still cause delivery again: consumers must deduplicate by the
idempotency key. This remains **at-least-once**, not exactly-once delivery.

Discarding a test run is transactional and deletes its queued messages. Do not discard a
test run while one of its triggers is calling back into the engine: the discard waits on the
trigger's locked outbox row, the trigger waits on the run, and the two block until the
command times out. An active lease
refuses deletion; stop or finish processing first. Already-delivered external side
effects cannot be undone by deleting a run.

## Routing and lifecycle changes

- Default routes are fallbacks, not additional fan-out edges. Use multiple matching
  non-default routes for intentional fan-out. A default route cannot have a condition.
  **This changes published workflows**: an outcome with a default route and a matching
  non-default route used to follow both, and now follows only the non-default one. The
  builder used to mark every new route as the default, so this is common. The builder now
  suggests a default only for the first unconditional route of an outcome, and turns the
  default off when a condition is picked. Before deploying, list what will change:

  ```sql
  -- Outcomes that used to follow both routes and will now follow only the non-default one
  SELECT r.TaskDefinitionId, r.TaskOutcomeDefinitionId, COUNT(*) AS Routes
  FROM TaskRouterTaskRoutes r
  WHERE r.IsArchived = 0
  GROUP BY r.TaskDefinitionId, r.TaskOutcomeDefinitionId
  HAVING SUM(CASE WHEN r.IsDefault = 1 THEN 1 ELSE 0 END) > 0
     AND SUM(CASE WHEN r.IsDefault = 0 THEN 1 ELSE 0 END) > 0;

  -- Default routes with a condition: the condition was always ignored, and the version
  -- can no longer be republished until the route is fixed
  SELECT * FROM TaskRouterTaskRoutes
  WHERE IsDefault = 1 AND ConditionKey IS NOT NULL AND IsArchived = 0;
  ```

  For each one, decide what the author meant: untick Default to keep both routes, or keep
  the fallback behaviour, and republish.
- A nonterminal task with no eligible route refuses completion instead of silently
  ending the run. Terminal tasks and route-free ad-hoc leaves remain valid endings.
- Selective rejection requires one eligible **rework** route, evaluates its condition
  against the projected completed task, and refuses duplicate rejected branch keys.
  Previously any route for the outcome was used. A convergence step whose send-back route
  is not marked "Sends work backwards" now refuses selective rejection; tick it and
  republish. To find the candidates — convergence steps that route somewhere but have no
  route marked as rework (check whether one of their routes actually sends work back):

  ```sql
  SELECT d.Id, d.DisplayName
  FROM TaskRouterTaskDefinitions d
  WHERE d.IsConvergencePoint = 1 AND d.IsArchived = 0
    AND EXISTS (SELECT 1 FROM TaskRouterTaskRoutes r
                WHERE r.TaskDefinitionId = d.Id AND r.IsArchived = 0)
    AND NOT EXISTS (SELECT 1 FROM TaskRouterTaskRoutes r
                    WHERE r.TaskDefinitionId = d.Id AND r.IsReworkRoute = 1 AND r.IsArchived = 0);
  ```
- Ad-hoc tasks must belong to their parent's definition version and require an open
  parent in a running run. This also applies inside delegated workflows, where the
  parent's version can differ from the run's pinned version.
- Every creation path raises `TaskCreated` before spawning automatic attachments.
  Sub-workflow entry tasks and added fork branches now participate in that lifecycle.
  Cyclic automatic attachment chains fail atomically.
- Convergence and rework retain their parent task relationship. Whether a task blocks
  its parent still depends on its definition's `IsBlocking` setting.
- Rework assignment uses the last completed branch's actual worker as its explicit
  starting assignment. A configured assignment role still has the existing precedence.
- Origin-style subject views retain live rework branches when there is no live fork
  origin to represent them. Progress counts branch and sub-workflow scopes separately,
  while superseded attempts within the same scope count only once.
- Reminder scans rotate through all candidates, including null, future, and resolver-
  failure cases; they still re-resolve live deadlines before dispatching.
- The HTTP runner sends run-cancellation reasons as the endpoint's `note` property.

`RaiseEventAsync` now dispatches matching custom-event subscriptions and records the
payload variable in the same transaction. Dispatcher-created actions carry their source
task automatically. Manually constructed `WorkflowActions` must also receive the source
task ID and dispatcher to raise events; the four-argument constructor remains usable
for assignment and cancellation. Custom-event recursion is limited to 32 levels.

## Completion previews

Previews project known engine cascades: task creation, automatic attachments, automatic
terminal completion, sub-workflow completion, and run completion. They carry projected
run state and simulate built-in inline variable writes in memory, without saving or
executing host triggers. Conditions and action descriptions see that projected state.

This is an advisory plan, not a guarantee against concurrent edits or arbitrary host
code. Custom triggers that create/cancel work, perform external calls, or change host
data cannot be simulated safely; they describe themselves but are not executed during
preview. Future task IDs and creation-time host side effects are not predictable.

## Verification

`ReviewConsistencyTests` exercises stale and concurrent completion, coordinated competing
outbox workers, rollback/retry recovery, callback isolation, route selection, lifecycle
cascades, branch/rework state, reminders, variable upserts, publishing, and test teardown
against SQL Server. The existing HTTP cancellation test also asserts the stored reason.
Migration tests check model drift and run a workflow on a database built from migrations.
`NestedTransactionTests` runs builder publishing, test-run discarding and a trigger calling
back into the engine on a `MultipleActiveResultSets=True` connection, and checks that a
rollback which itself fails still reports the original error. `RouteDefaultsTests` covers
the builder's default-route suggestion.

Validation: all **793 tests pass**, including 41 new regression tests, using
`dotnet test TaskRouter.slnx --no-restore`. Whitespace verification and `git diff --check`
also pass. Existing `System.Security.Cryptography.Pkcs` dependency advisory warnings
remain unchanged; this patch does not upgrade unrelated dependencies.
