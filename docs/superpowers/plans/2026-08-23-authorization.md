# Authorization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the engine ask a host-supplied policy whether an actor may perform an operation, before every one of its twelve mutating methods does anything.

**Architecture:** A new `IWorkflowAuthorizationPolicy` seam in `Workflow.Core.Abstractions` takes a `WorkflowOperation` enum and a context record, and returns allow/deny with a reason. `WorkflowEngine` gains a private `AuthorizeAsync` helper called inside each mutating method's existing transaction body, after the task is loaded and before any validation; denial throws `WorkflowAuthorizationException` (deriving from `InvalidOperationException`, so the existing `Try.RunAsync` → `Result` machinery is untouched). A host registering no policy gets `AllowAllAuthorizationPolicy` and today's behaviour exactly. The demo implements a real policy over its org chart and derives `CanComplete` from it.

**Tech Stack:** C# / .NET 10, EF Core against SQL Server, MSTest integration tests, MudBlazor for the demo UI.

**Spec:** `docs/superpowers/specs/2026-08-23-authorization-design.md`

---

## Before you start

Every `dotnet` command in this plan needs these two things in the shell. Without the
first, `dotnet` is not on `PATH`; without the second, every integration test throws
"Set SA_PASSWORD (or WORKFLOW_TEST_CONNECTION) to run integration tests."

```bash
export DOTNET_ROOT=~/.dotnet && export PATH=$DOTNET_ROOT:$PATH
source "$WORKFLOW_DEV_ENV"
cd <repo root>
```

Work happens on the `authorization` branch, which already exists and holds the spec commit.

Baseline before touching anything: `dotnet build && dotnet test` must report
**239/239 passing**. If it does not, stop — something unrelated is broken and this plan's
"no test regressions" checks will be meaningless.

To run one test class: `dotnet test --filter FullyQualifiedName~AuthorizationTests`
To run one test: `dotnet test --filter FullyQualifiedName~AuthorizationTests.It_denies_a_stranger`

**Domain vocabulary you will need.** An *actor* is an opaque string id; the engine never
models a user. A *branch key* is an opaque string the engine inherits down a run — in the
demo it is a `Section.Code`. A *Section Lead* is the person leading a section
(`Section.SectionLeadActorId`); a *branch head* is the person above that
(`Section.Division.DivisionHeadActorId`). "Unclaimed" means a task has a branch key but no
`AssignedToActorId` — real work, nobody has picked it up.

---

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `src/Workflow.Core/Abstractions/Authorization.cs` | **create** | The whole seam: enum, context, result, interface, exception. New file rather than appending to `HostServices.cs`, which is already seven interfaces long, and this is one cohesive feature. |
| `src/Workflow.Persistence.EF/WorkflowEngine.cs` | modify | The `AuthorizeAsync` helper, the ctor parameter, and gates on the methods defined here. |
| `src/Workflow.Persistence.EF/WorkflowEngine.Fork.cs` | modify | Gates on `ForkTaskAsync` and `CompleteWithSelectiveRejectionAsync`. |
| `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs` | modify | Gates on `StartSubWorkflowAsync` (line 39) and `CancelSubWorkflowAsync` (line 100). |
| `src/Workflow.Persistence.EF/WorkflowEngine.Queries.cs` | modify | Gate on `AddAdHocTaskAsync` (line 93) — it lives here, not with the sub-workflow code. |
| `src/Workflow.Persistence.EF/WorkflowEngine.Reads.cs` | modify | Gate on `AddBranchToForkAsync` (line 209) — a mutating method in the reads file. |
| `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs` | modify | `AddAuthorizationPolicy<T>()` and the `AllowAllAuthorizationPolicy` default. |
| `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs` | modify | Correct the `CanComplete` doc comment that says the engine has no opinion. |
| `samples/DemoDocuments.Server/Workflow/DemoAuthorizationPolicy.cs` | **create** | The worked example: the demo's org rules. |
| `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs` | modify | Derive `CanComplete` from the policy; correct its class doc comment. |
| `samples/DemoDocuments.Server/Program.cs` | modify | Register the demo policy. |
| `tests/Workflow.Tests/TestHost.cs` | modify | Let a test supply a policy on both construction paths. |
| `tests/Workflow.Tests/AuthorizationTests.cs` | **create** | The seam, the coverage table, and the demo rules. |
| `STATE.md` | modify | Move authorization into Done; record read gating as a deliberate gap. |

Task order is deliberate: the seam exists before anything consumes it (Task 1), the
engine is wired but ungated (Task 2), then gates land in three batches each with tests
(Tasks 3–5), then the coverage table proves no method was missed (Task 6), then the demo
(Tasks 7–9), then docs (Task 10).

---

### Task 1: The authorization seam

Pure types, no behaviour. Nothing consumes them yet, so there is nothing to test beyond
compilation — the first real test arrives in Task 3.

**Files:**
- Create: `src/Workflow.Core/Abstractions/Authorization.cs`

- [ ] **Step 1: Create the file**

```csharp
using Workflow.Core.Model;

namespace Workflow.Core.Abstractions;

/// <summary>
/// The engine operations that can be authorized — every method on <c>IWorkflowEngine</c>
/// that changes state.
///
/// An enum rather than a method per operation on <see cref="IWorkflowAuthorizationPolicy"/>:
/// adding a gated operation is then a new member rather than a breaking change to every
/// host's implementation. Same reasoning as <c>WorkflowEventKind</c> on the trigger seam.
///
/// <para><b>Append only.</b> A host may persist or log these names, and reordering would
/// silently repoint stored values.</para>
/// </summary>
public enum WorkflowOperation
{
    /// <summary>Covers both <c>StartRunAsync</c> and <c>StartRunOnVersionAsync</c> —
    /// the same act on a different version.</summary>
    StartRun,
    CompleteTask,
    CompleteWithSelectiveRejection,
    CancelTask,
    ReassignTask,
    ForkTask,
    AddBranchToFork,
    AddAdHocTask,
    StartSubWorkflow,
    CancelSubWorkflow,
    UpdateTaskNotes,
}

/// <summary>
/// What the policy gets to decide with.
///
/// <see cref="Task"/> is null exactly when the operation is
/// <see cref="WorkflowOperation.StartRun"/>, which has no task yet;
/// <see cref="Subject"/> and <see cref="WorkflowDefinitionId"/> are set exactly then and
/// null otherwise. One record rather than two context types, because a host switching on
/// the operation already knows which shape it is holding.
/// </summary>
public sealed record WorkflowAuthorizationContext(
    string ActorId,
    WorkflowTaskSnapshot? Task,
    WorkflowSubject? Subject,
    int? WorkflowDefinitionId);

/// <summary>
/// Allowed, or denied with a reason.
///
/// The reason reaches the caller in the exception message and therefore in the failed
/// <c>Result</c>. Telling somebody they may not act without saying why generates support
/// tickets; this is an internal line-of-business tool, and the org chart a reason might
/// disclose is on the wall.
/// </summary>
public sealed record WorkflowAuthorizationResult(bool IsAllowed, string? Reason)
{
    public static readonly WorkflowAuthorizationResult Allowed = new(true, null);

    public static WorkflowAuthorizationResult Denied(string reason) => new(false, reason);
}

/// <summary>
/// Whether an actor may perform an operation. Implemented by the host, consulted by the
/// engine before every mutating method does anything.
///
/// The rule is host knowledge — who may cancel a task depends on an org model the library
/// does not have — but asking has to be the engine's job, because the engine owns the only
/// chokepoint every caller passes through.
///
/// <para><b>This seam fails closed, unlike the two resolvers in <c>HostServices.cs</c>.</b>
/// <see cref="IWorkflowAssignmentResolver"/> and <see cref="IWorkflowDueDateResolver"/>
/// both document that an implementation which throws is logged and treated as null, never
/// fatal — correct for them, because the fallback is a lesser answer and work continues
/// visibly diminished. Here the fallback would be *no gate*: a policy throwing under load
/// would open every operation to everybody at the moment nobody is watching. So a policy
/// that throws <b>denies</b>, and the failure is logged.</para>
///
/// <para>Registering none is a legitimate configuration and gives the library's
/// pre-authorization behaviour: every operation permitted. It is never silent — see
/// <c>AllowAllAuthorizationPolicy</c>.</para>
/// </summary>
public interface IWorkflowAuthorizationPolicy
{
    Task<WorkflowAuthorizationResult> EvaluateAsync(
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a policy denies an operation, and surfaced to the caller as a failed
/// <c>Result</c> like every other engine refusal.
///
/// Derives from <see cref="InvalidOperationException"/> deliberately: the engine's idiom
/// is to throw one inside a <c>Try.RunAsync</c> body, so deriving leaves every existing
/// call site working untouched, while a host that wants to map a denial to HTTP 403
/// rather than 400 can catch this type specifically. The operation, actor and reason are
/// properties so a host need not parse the message.
/// </summary>
public sealed class WorkflowAuthorizationException(
    WorkflowOperation operation,
    string actorId,
    string reason)
    : InvalidOperationException(
        $"Actor '{actorId}' is not authorized to perform {operation}: {reason}")
{
    public WorkflowOperation Operation { get; } = operation;

    public string ActorId { get; } = actorId;

    public string Reason { get; } = reason;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add src/Workflow.Core/Abstractions/Authorization.cs
git commit -m "Add the authorization seam

An operation enum, a context, an allow/deny result, the host interface and
the exception a denial becomes. Nothing consults it yet.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Wire the policy into the engine, ungated

The helper and the plumbing, with no call sites. Splitting this from the first gate keeps
the diff that changes behaviour small enough to read.

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.cs` (constructor at lines 10-17, helpers section near line 452)
- Modify: `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`
- Modify: `tests/Workflow.Tests/TestHost.cs`

- [ ] **Step 1: Add the constructor parameter**

In `src/Workflow.Persistence.EF/WorkflowEngine.cs`, append to the primary constructor.
It must be **last** — the parameters before it are positional at several call sites.

```csharp
public sealed partial class WorkflowEngine(
    IWorkflowDbContext db,
    IWorkflowAssignmentResolver assignmentResolver,
    IEnumerable<IRouteConditionEvaluator> conditionEvaluators,
    ILogger<WorkflowEngine> logger,
    Triggers.IWorkflowTriggerDispatcher? dispatcher = null,
    IWorkflowPostCommitActions? postCommit = null,
    IWorkflowDueDateResolver? dueDateResolver = null,
    IWorkflowAuthorizationPolicy? authorizationPolicy = null) : IWorkflowEngine
```

- [ ] **Step 2: Add the helper**

In the same file, in the `Helpers` region (immediately after `LoadTaskAsync`, which ends
around line 460 with `?? throw new InvalidOperationException($"Task {taskId} not found.");`):

```csharp
    /// <summary>
    /// Asks the host whether this actor may do this, and throws
    /// <see cref="WorkflowAuthorizationException"/> if not.
    ///
    /// Called inside each mutating method's transaction, after the task is loaded and
    /// <b>before any validation</b>. The ordering is the point: told "task 42 is already
    /// completed", a denied actor has learned that task 42 exists and what became of it.
    ///
    /// <paramref name="task"/> is null only for <see cref="WorkflowOperation.StartRun"/>.
    /// </summary>
    private async Task AuthorizeAsync(
        WorkflowOperation operation,
        WorkflowTask? task,
        string actorId,
        WorkflowSubject? subject,
        int? workflowDefinitionId,
        CancellationToken ct)
    {
        // No policy registered: the library's pre-authorization behaviour. DI supplies
        // AllowAllAuthorizationPolicy, but the engine can also be constructed directly.
        if (authorizationPolicy is null)
        {
            return;
        }

        // The sweeper and the trigger dispatcher act under this id. The bypass lives here
        // rather than in every host's policy because a host that forgot it would break its
        // own deadline sweep, and the symptom would be tasks quietly not escalating.
        if (actorId == WorkflowActors.System)
        {
            return;
        }

        var context = new WorkflowAuthorizationContext(
            actorId,
            task is null ? null : ToSnapshot(task),
            subject,
            workflowDefinitionId);

        WorkflowAuthorizationResult result;

        try
        {
            result = await authorizationPolicy
                .EvaluateAsync(operation, context, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Fail closed. See IWorkflowAuthorizationPolicy: this is the opposite of the
            // rule the resolver seams document, and deliberately so.
            logger.LogError(
                ex,
                "Authorization policy threw evaluating {Operation} for actor {ActorId}; denying.",
                operation,
                actorId);

            throw new WorkflowAuthorizationException(
                operation, actorId, "the authorization policy failed");
        }

        if (!result.IsAllowed)
        {
            throw new WorkflowAuthorizationException(
                operation, actorId, result.Reason ?? "not permitted");
        }
    }
```

**Note on `ToSnapshot`:** it is an extension method on `WorkflowTask`, defined at
`src/Workflow.Persistence.EF/Snapshots.cs:9`, already used elsewhere in the engine (e.g.
`AddAdHocTaskAsync` calls `parent.ToSnapshot()`). Call it as `task.ToSnapshot()`.

- [ ] **Step 3: Add the builder method and default policy**

In `src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs`, add to
`WorkflowEngineBuilder` next to `AddDueDateResolver`:

```csharp
    /// <summary>
    /// Supplies the authorization rules. Without this every operation is permitted, which
    /// is the library's pre-authorization behaviour and a legitimate configuration for a
    /// host that gates access somewhere else.
    /// </summary>
    public WorkflowEngineBuilder AddAuthorizationPolicy<T>()
        where T : class, IWorkflowAuthorizationPolicy
    {
        Services.AddScoped<IWorkflowAuthorizationPolicy, T>();
        return this;
    }
```

In `AddWorkflowEngine`, beside the two existing `TryAddScoped` defaults:

```csharp
        // A host that supplies none of these still gets a working engine.
        services.TryAddScoped<IWorkflowAssignmentResolver, NullAssignmentResolver>();
        services.TryAddScoped<IWorkflowDueDateResolver, NullDueDateResolver>();
        services.TryAddScoped<IWorkflowAuthorizationPolicy, AllowAllAuthorizationPolicy>();
```

And at the bottom of the file, beside `NullDueDateResolver` and `NullAssignmentResolver`:

```csharp
/// <summary>
/// Permits everything. Used when the host registers no policy.
///
/// A named class rather than a null check at the call site: "this system has no
/// authorization" should be a greppable object, not an absence. It warns once per process
/// on first use — an unauthorized system is a legitimate configuration, but it should
/// never be a silent one.
/// </summary>
internal sealed class AllowAllAuthorizationPolicy(
    ILogger<AllowAllAuthorizationPolicy> logger) : IWorkflowAuthorizationPolicy
{
    private static int _warned;

    public Task<WorkflowAuthorizationResult> EvaluateAsync(
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            logger.LogWarning(
                "No IWorkflowAuthorizationPolicy is registered: every workflow operation "
                + "is permitted for every actor. Register one with "
                + "AddWorkflowEngine().AddAuthorizationPolicy<T>() to change this.");
        }

        return Task.FromResult(WorkflowAuthorizationResult.Allowed);
    }
}
```

This needs `using Microsoft.Extensions.Logging;` at the top of the file — check whether it
is already there and add it if not.

- [ ] **Step 4: Let tests supply a policy**

In `tests/Workflow.Tests/TestHost.cs`, add a parameter to `CreateAsync` (after
`dueDates`, keeping it optional so no existing caller changes):

```csharp
    public static async Task<TestHost> CreateAsync(
        bool withTriggers = false,
        Action<IServiceCollection>? configure = null,
        IWorkflowDueDateResolver? dueDates = null,
        IWorkflowAuthorizationPolicy? policy = null)
```

In the `if (!withTriggers)` branch, pass it to the directly-constructed engine:

```csharp
            var plainEngine = new WorkflowEngine(
                plainDb,
                new DemoAssignmentResolver(plainDb, NullLogger<DemoAssignmentResolver>.Instance),
                [new RequiresReviewCondition()],
                NullLogger<WorkflowEngine>.Instance,
                dueDateResolver: dueDates,
                authorizationPolicy: policy);
```

In the `withTriggers` branch, register it into the container so DI injects it — add this
immediately before the `services.AddWorkflowEngine()` call, so `TryAddScoped` finds it
already present and the allow-all default stands down:

```csharp
        if (policy is not null)
        {
            services.AddScoped(_ => policy);
        }
```

Add `using Workflow.Core.Abstractions;` to `TestHost.cs` if it is not already imported.

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build && dotnet test`
Expected: `Build succeeded` and `Passed! - Failed: 0, Passed: 239`

Nothing is gated yet, so the count must be unchanged. A drop here means the constructor
change broke a call site.

- [ ] **Step 6: Commit**

```bash
git add src/Workflow.Persistence.EF/WorkflowEngine.cs \
        src/Workflow.Persistence.EF/ServiceCollectionExtensions.cs \
        tests/Workflow.Tests/TestHost.cs
git commit -m "Give the engine a policy to ask, with nothing yet asking it

The AuthorizeAsync helper, the optional constructor parameter, the
AddAuthorizationPolicy builder method and the allow-all default that keeps
an unconfigured host behaving as it does today. No call sites yet.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Gate CompleteTaskAsync, and pin the three properties of the placement

The first real gate. Its tests establish the behaviour every later gate inherits, so they
are written here in full and not repeated.

**Files:**
- Create: `tests/Workflow.Tests/AuthorizationTests.cs`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.cs` (`CompleteTaskAsync`, line 187)

- [ ] **Step 1: Write the failing tests**

Create `tests/Workflow.Tests/AuthorizationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using Workflow.Core.Abstractions;
using Workflow.Core.Model;
using Workflow.Persistence.EF;

namespace Workflow.Tests;

/// <summary>
/// The authorization gate.
///
/// The tests that matter most are not the ones proving a denial is refused — that is one
/// line of engine code — but the three proving *where* the gate sits:
/// <see cref="It_writes_nothing_when_it_denies"/>,
/// <see cref="It_authorizes_before_it_validates"/>, and the coverage table in
/// <see cref="It_gates_every_mutating_method"/>. Each pins a property that would rot
/// silently otherwise.
/// </summary>
[TestClass]
public class AuthorizationTests
{
    /// <summary>
    /// A policy a test drives directly. <see cref="Allow"/> false denies everything;
    /// <see cref="DenyOperations"/> denies a named subset; <see cref="ShouldThrow"/>
    /// makes it fail, which must deny rather than permit.
    /// </summary>
    private sealed class ScriptedPolicy : IWorkflowAuthorizationPolicy
    {
        public bool Allow { get; set; } = true;
        public bool ShouldThrow { get; set; }
        public HashSet<WorkflowOperation> DenyOperations { get; } = [];
        public List<(WorkflowOperation Operation, string ActorId)> Seen { get; } = [];

        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation,
            WorkflowAuthorizationContext context,
            CancellationToken ct = default)
        {
            Seen.Add((operation, context.ActorId));

            if (ShouldThrow)
            {
                return Task.FromException<WorkflowAuthorizationResult>(
                    new InvalidOperationException("policy down"));
            }

            var denied = !Allow || DenyOperations.Contains(operation);

            return Task.FromResult(denied
                ? WorkflowAuthorizationResult.Denied("the test says no")
                : WorkflowAuthorizationResult.Allowed);
        }
    }

    private ScriptedPolicy _policy = null!;
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        _policy = new ScriptedPolicy();
        _host = await TestHost.CreateAsync(policy: _policy);

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>Starts a run and returns its single entry task.</summary>
    private async Task<WorkflowTask> StartedTaskAsync()
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), _definitionId, "user-originator")).Unwrap();

        return await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);
    }

    /// <summary>The outcome key the seeded entry task declares, so completion is legal
    /// on every ground except authorization.</summary>
    private async Task<string> ValidOutcomeAsync(WorkflowTask task) =>
        await _host.Db.WorkflowTaskOutcomes
            .Where(o => o.TaskDefinitionId == task.TaskDefinitionId && !o.IsArchived)
            .Select(o => o.OutcomeKey)
            .FirstAsync();

    [TestMethod]
    public async Task It_allows_when_the_policy_allows()
    {
        var task = await StartedTaskAsync();

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        Assert.IsTrue(result.IsOk, "an allowed completion must succeed");
    }

    [TestMethod]
    public async Task It_denies_when_the_policy_denies()
    {
        _policy.Allow = false;
        var task = await StartedTaskAsync();

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        Assert.IsTrue(result.IsError, "a denied completion must fail");
        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_the_reason_the_policy_gave()
    {
        _policy.Allow = false;
        var task = await StartedTaskAsync();

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        StringAssert.Contains(
            result.UnwrapError().Message,
            "the test says no",
            "the policy's reason must reach the caller, or a UI cannot explain the refusal");
    }

    /// <summary>
    /// A denial must leave the task exactly as it was — no status, no outcome, no
    /// modifier, and no log row. This is what "before mutation" means, and it would fail
    /// if the gate were ever moved below the writes.
    /// </summary>
    [TestMethod]
    public async Task It_writes_nothing_when_it_denies()
    {
        var task = await StartedTaskAsync();
        var logsBefore = await _host.Db.WorkflowTaskLogs.CountAsync(l => l.WorkflowTaskId == task.Id);

        _policy.Allow = false;

        await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        _host.Db.ChangeTracker.Clear();
        var after = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);

        Assert.AreEqual(WorkflowTaskStatus.NotStarted, after.Status);
        Assert.IsNull(after.OutcomeKey);
        Assert.IsNull(after.CompletedDate);
        Assert.AreEqual(
            logsBefore,
            await _host.Db.WorkflowTaskLogs.CountAsync(l => l.WorkflowTaskId == task.Id),
            "a refused operation must not be logged as though it happened");
    }

    /// <summary>
    /// Authorization runs before validation, so a denied actor cannot learn task state by
    /// reading the error. Completing an already-completed task is normally refused with
    /// "already completed"; denied, it must be refused for the other reason.
    /// </summary>
    [TestMethod]
    public async Task It_authorizes_before_it_validates()
    {
        var task = await StartedTaskAsync();
        var outcome = await ValidOutcomeAsync(task);

        (await _host.Engine.CompleteTaskAsync(task.Id, outcome, "user-reviewer")).Unwrap();

        _policy.Allow = false;

        var result = await _host.Engine.CompleteTaskAsync(task.Id, outcome, "user-reviewer");

        Assert.IsInstanceOfType<WorkflowAuthorizationException>(
            result.UnwrapError(),
            "the authorization error must win, or the message leaks that the task is done");
        StringAssert.Contains(result.UnwrapError().Message, "not authorized");
    }

    /// <summary>Fail closed — the one place this library's convention inverts.</summary>
    [TestMethod]
    public async Task It_denies_when_the_policy_throws()
    {
        _policy.ShouldThrow = true;
        var task = await StartedTaskAsync();

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        Assert.IsTrue(result.IsError, "a policy that throws must deny, never permit");
        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
    }

    /// <summary>
    /// The sweeper and the dispatcher act as WorkflowActors.System. If the bypass were a
    /// host responsibility, a host that forgot it would break its own deadline sweep.
    /// </summary>
    [TestMethod]
    public async Task It_lets_the_system_actor_through_a_denying_policy()
    {
        var task = await StartedTaskAsync();
        _policy.Allow = false;

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), WorkflowActors.System);

        Assert.IsTrue(result.IsOk, "the engine's own actor must not be gated by a host policy");
    }

    /// <summary>
    /// A host that registers no policy keeps the library's pre-authorization behaviour.
    ///
    /// The other 239 tests rely on this, but they rely on it silently — this asserts it,
    /// so the day it stops being true one test says so instead of two hundred.
    /// </summary>
    [TestMethod]
    public async Task It_permits_everything_when_no_policy_is_registered()
    {
        await using var open = await TestHost.CreateAsync(policy: null);

        var definitionId = await open.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var run = (await open.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), definitionId, "anybody-at-all")).Unwrap();

        var task = await open.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        var outcome = await open.Db.WorkflowTaskOutcomes
            .Where(o => o.TaskDefinitionId == task.TaskDefinitionId && !o.IsArchived)
            .Select(o => o.OutcomeKey)
            .FirstAsync();

        var result = await open.Engine.CompleteTaskAsync(task.Id, outcome, "a-total-stranger");

        Assert.IsTrue(result.IsOk, "with no policy registered, nothing is gated");
    }

    /// <summary>A host mapping denials to HTTP 403 catches the specific type; every
    /// existing engine call site catches the base one.</summary>
    [TestMethod]
    public async Task It_throws_an_exception_catchable_as_either_type()
    {
        var ex = new WorkflowAuthorizationException(
            WorkflowOperation.CompleteTask, "user-a", "no");

        Assert.IsInstanceOfType<InvalidOperationException>(ex);
        Assert.AreEqual(WorkflowOperation.CompleteTask, ex.Operation);
        Assert.AreEqual("user-a", ex.ActorId);
        Assert.AreEqual("no", ex.Reason);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test --filter FullyQualifiedName~AuthorizationTests`

Expected: three **pass** — `It_allows_when_the_policy_allows`,
`It_permits_everything_when_no_policy_is_registered` (nothing is gated yet, so it is
trivially true and will stay true afterwards), and
`It_throws_an_exception_catchable_as_either_type` (it only constructs an exception). The
other six **fail** — the completion succeeds where a denial was expected.

If `It_denies_when_the_policy_denies` passes at this point, the policy is being consulted
somewhere unintended; stop and find out where.

- [ ] **Step 3: Add the gate**

In `src/Workflow.Persistence.EF/WorkflowEngine.cs`, in `CompleteTaskAsync`, immediately
after `LoadTaskAsync` and **before** the convergence check:

```csharp
        Try.RunAsync(() => InTransactionAsync(async token =>
        {
            var task = await LoadTaskAsync(taskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.CompleteTask, task, actorId, null, null, token)
                .ConfigureAwait(false);

            if (task.ForkManifestId.HasValue && await IsRejectionOutcomeAsync(task, outcomeKey, token).ConfigureAwait(false))
            {
```

- [ ] **Step 4: Run them again**

Run: `dotnet test --filter FullyQualifiedName~AuthorizationTests`
Expected: `Passed! - Failed: 0, Passed: 9`

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: `Failed: 0, Passed: 248` (239 + 9)

- [ ] **Step 6: Commit**

```bash
git add tests/Workflow.Tests/AuthorizationTests.cs src/Workflow.Persistence.EF/WorkflowEngine.cs
git commit -m "Gate task completion, and pin where the gate sits

The gate runs after the task loads and before any validation, so a denial
writes nothing and tells a denied actor nothing about task state. Both are
tested directly -- they are the properties that would rot silently.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Gate the remaining methods in WorkflowEngine.cs

`StartRunAsync`, `StartRunOnVersionAsync`, `CancelTaskAsync`, `ReassignTaskAsync`,
`UpdateTaskNotesAsync`. No new tests — Task 6's table covers them, and writing per-method
denial tests now would duplicate it.

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.cs`

- [ ] **Step 1: Gate the two start methods**

These have no task. They authorize on subject and definition id as the first act inside
the transaction, before the definition is resolved or a version pinned.

In `StartRunAsync`, as the first statement of the transaction body:

```csharp
            await AuthorizeAsync(
                WorkflowOperation.StartRun, null, actorId, subject, workflowDefinitionId, token)
                .ConfigureAwait(false);
```

In `StartRunOnVersionAsync`, likewise — same operation, null task, and **null definition
id**, because the parameter there is a *version* id and the context field is a definition
id. Passing the version id would be a quiet lie to every host policy:

```csharp
            await AuthorizeAsync(
                WorkflowOperation.StartRun, null, actorId, subject, null, token)
                .ConfigureAwait(false);
```

- [ ] **Step 2: Gate the three task methods**

In each of `CancelTaskAsync`, `ReassignTaskAsync` and `UpdateTaskNotesAsync`, immediately
after that method's `LoadTaskAsync` call and before anything else:

```csharp
            await AuthorizeAsync(
                WorkflowOperation.CancelTask, task, actorId, null, null, token)
                .ConfigureAwait(false);
```

```csharp
            await AuthorizeAsync(
                WorkflowOperation.ReassignTask, task, actorId, null, null, token)
                .ConfigureAwait(false);
```

```csharp
            await AuthorizeAsync(
                WorkflowOperation.UpdateTaskNotes, task, actorId, null, null, token)
                .ConfigureAwait(false);
```

If a method loads its task by some other means than `LoadTaskAsync`, place the call
immediately after whatever produces the `WorkflowTask`, still before any validation or
write.

- [ ] **Step 3: Build and run the whole suite**

Run: `dotnet build && dotnet test`
Expected: `Failed: 0, Passed: 248`

Unchanged, because the existing tests use no policy and `AuthorizationTests` uses an
allowing one by default. A failure here means a gate landed after a write.

- [ ] **Step 4: Commit**

```bash
git add src/Workflow.Persistence.EF/WorkflowEngine.cs
git commit -m "Gate run start, cancel, reassign and notes

StartRunOnVersionAsync passes a null definition id rather than its version
id -- the context field means definition, and filling it with a version
would be a quiet lie to every host policy.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Gate the fork, ad-hoc and sub-workflow methods

These four methods live in four different files — `AddAdHocTaskAsync` is in
`WorkflowEngine.Queries.cs` and `AddBranchToForkAsync` is in `WorkflowEngine.Reads.cs`,
not where their names suggest. Confirm before editing:

```bash
grep -rn "public Task<Result<" src/Workflow.Persistence.EF/WorkflowEngine*.cs \
  | grep -E "ForkTaskAsync|CompleteWithSelectiveRejectionAsync|AddBranchToForkAsync|AddAdHocTaskAsync|StartSubWorkflowAsync|CancelSubWorkflowAsync"
```

**Files:**
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Fork.cs`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Queries.cs`
- Modify: `src/Workflow.Persistence.EF/WorkflowEngine.Reads.cs`

- [ ] **Step 1: ForkTaskAsync**

In `WorkflowEngine.Fork.cs`. The method validates its `branchKeys` argument first, then
loads the origin task into a local called `origin`. Put the gate immediately after that
load and before the status check:

```csharp
            var origin = await LoadTaskAsync(taskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.ForkTask, origin, actorId, null, null, token)
                .ConfigureAwait(false);

            if (origin.Status is WorkflowTaskStatus.Completed or WorkflowTaskStatus.Cancelled
                or WorkflowTaskStatus.Forked)
```

The `ArgumentNullException`/`ArgumentException` guards above it stay where they are. They
are argument validation, not information about the task — a caller who passed one branch
key learns nothing about the run by being told so.

- [ ] **Step 2: CompleteWithSelectiveRejectionAsync**

In `WorkflowEngine.Fork.cs`. It loads its task with an inline query into `task`. Gate
immediately after, before the `ForkManifest is null` check — so a denied actor is not told
what kind of task it is:

```csharp
                ?? throw new InvalidOperationException($"Task {convergenceTaskId} not found.");

            await AuthorizeAsync(
                WorkflowOperation.CompleteWithSelectiveRejection, task, actorId, null, null, token)
                .ConfigureAwait(false);

            if (task.ForkManifest is null)
```

- [ ] **Step 3: AddAdHocTaskAsync**

In `WorkflowEngine.Queries.cs` at line 93. It loads the parent into `parent` as its first
act. Gate immediately after, before the definition lookup:

```csharp
            var parent = await LoadTaskAsync(parentTaskId, token).ConfigureAwait(false);

            await AuthorizeAsync(
                WorkflowOperation.AddAdHocTask, parent, actorId, null, null, token)
                .ConfigureAwait(false);

            var definition = await db.WorkflowTaskDefinitions
```

The parent is the right subject: the question is whether this actor may hang work off it.

- [ ] **Step 4: AddBranchToForkAsync**

In `WorkflowEngine.Reads.cs` at line 209. This one is keyed on a `forkGroupId` rather than
a task id, so it needs a task loaded before it can ask the question at all. Add the lookup
and gate immediately after the argument guards, **before** the manifest load:

```csharp
            if (branchKeys.Distinct(StringComparer.Ordinal).Count() != branchKeys.Count)
            {
                throw new ArgumentException("Duplicate branch keys are not allowed.", nameof(branchKeys));
            }

            // Authorized against one task of the group. Every task in a fork group shares a
            // run and a convergence point, so any of them answers the same question — and
            // the group has to be resolved to a task before there is anything to ask about.
            var groupTask = await db.WorkflowTasks
                .FirstOrDefaultAsync(t => t.ForkGroupId == forkGroupId, token)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"No tasks in fork group {forkGroupId}.");

            await AuthorizeAsync(
                WorkflowOperation.AddBranchToFork, groupTask, actorId, null, null, token)
                .ConfigureAwait(false);

            var manifest = await db.WorkflowForkManifests
```

A group that does not exist is refused as "not found" rather than as unauthorized. That is
honest — there is no task, so there is no question to put to the host — and it is why
Task 6's coverage table uses a **real** fork group.

- [ ] **Step 5: StartSubWorkflowAsync and CancelSubWorkflowAsync**

In `WorkflowEngine.SubWorkflows.cs`, at lines 39 and 100. Gate each immediately after the
task it loads, using that method's existing local name:

```csharp
            await AuthorizeAsync(
                WorkflowOperation.StartSubWorkflow, parent, actorId, null, null, token)
                .ConfigureAwait(false);
```

```csharp
            await AuthorizeAsync(
                WorkflowOperation.CancelSubWorkflow, parent, actorId, null, null, token)
                .ConfigureAwait(false);
```

`CancelSubWorkflowAsync` takes an instance id, so its parent task is reached through
`SubWorkflowInstance.ParentTaskId`. If the method does not already load that task, add:

```csharp
            var parent = await LoadTaskAsync(instance.ParentTaskId, token).ConfigureAwait(false);
```

immediately after the instance is loaded, and gate on it. Use whatever the method already
calls its instance local.

- [ ] **Step 6: Build and run the whole suite**

Run: `dotnet build && dotnet test`
Expected: `Failed: 0, Passed: 248`

Unchanged, because the existing tests use no policy and `AuthorizationTests` uses an
allowing one by default. A failure here means a gate landed after a write.

- [ ] **Step 7: Commit**

```bash
git add src/Workflow.Persistence.EF/WorkflowEngine.Fork.cs \
        src/Workflow.Persistence.EF/WorkflowEngine.SubWorkflows.cs \
        src/Workflow.Persistence.EF/WorkflowEngine.Queries.cs \
        src/Workflow.Persistence.EF/WorkflowEngine.Reads.cs
git commit -m "Gate the fork, ad-hoc and sub-workflow operations

Sub-workflow and ad-hoc operations authorize against the parent task: the
question is whether the actor may hang work off it. AddBranchToFork has to
resolve its group to a task first -- a group with no tasks is a not-found,
not a denial, because there is nothing to ask the host about.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Prove no mutating method was missed

The test that makes the previous three tasks trustworthy, and the one that will fail years
from now when somebody adds a mutating method and forgets the gate.

**Files:**
- Modify: `tests/Workflow.Tests/AuthorizationTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `AuthorizationTests`:

```csharp
    /// <summary>
    /// Every mutating method refuses under a deny-everything policy.
    ///
    /// Driven from the twelve <b>methods</b>, not the eleven operations: an
    /// operation-driven table would pass while <c>StartRunOnVersionAsync</c> sat ungated,
    /// because <c>StartRunAsync</c> already covers <see cref="WorkflowOperation.StartRun"/>.
    ///
    /// Each call is given arguments that would otherwise be legal, so a failure means the
    /// gate is missing rather than the call being rejected on other grounds — which is why
    /// each is asserted on the exception type and not merely on IsError.
    /// </summary>
    [TestMethod]
    public async Task It_gates_every_mutating_method()
    {
        // Prerequisites are built while the policy still allows, so the table below is
        // testing the gate rather than tripping over missing rows. They do NOT all need to
        // be *valid* arguments: authorization runs before validation, so a call that would
        // be refused on other grounds must still be refused on this one first. The fork
        // group is the exception — AddBranchToForkAsync has to resolve a group to a task
        // before it has anything to authorize against, so that one must be real.
        var task = await StartedTaskAsync();
        var outcome = await ValidOutcomeAsync(task);
        var subject = new WorkflowSubject("ChangeRequest", "1");

        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == _definitionId)
            .OrderByDescending(v => v.Id)
            .Select(v => v.Id)
            .FirstAsync();

        var adHocDefinitionId = await _host.Db.WorkflowTaskDefinitions
            .Select(d => d.Id).FirstAsync();

        // A real fork, and a second run to fork it in, so forking does not disturb `task`.
        var forkRun = (await _host.Engine.StartRunAsync(
            subject, _definitionId, "user-originator")).Unwrap();
        var forkOrigin = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.Id == forkRun.Tasks.Single().Id);
        var fork = (await _host.Engine.ForkTaskAsync(
            forkOrigin.Id, ["A", "B"], forkOrigin.TaskDefinitionId, "user-originator")).Unwrap();
        var forkGroupId = fork.ForkGroupId;

        // A real sub-workflow instance, for CancelSubWorkflowAsync.
        var subDefinitionId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
        var instanceId = (await _host.Engine.StartSubWorkflowAsync(
            task.Id, subDefinitionId, "user-originator")).Unwrap().Id;

        _policy.Allow = false;

        var engine = _host.Engine;

        var calls = new (string Name, Func<Task<Exception?>> Invoke)[]
        {
            ("StartRunAsync", async () =>
                ErrorOf(await engine.StartRunAsync(subject, _definitionId, "user-a"))),
            ("StartRunOnVersionAsync", async () =>
                ErrorOf(await engine.StartRunOnVersionAsync(subject, versionId, "user-a", isTest: true))),
            ("CompleteTaskAsync", async () =>
                ErrorOf(await engine.CompleteTaskAsync(task.Id, outcome, "user-a"))),
            ("CompleteWithSelectiveRejectionAsync", async () =>
                ErrorOf(await engine.CompleteWithSelectiveRejectionAsync(task.Id, outcome, [], "user-a"))),
            ("CancelTaskAsync", async () =>
                ErrorOf(await engine.CancelTaskAsync(task.Id, "user-a"))),
            ("ReassignTaskAsync", async () =>
                ErrorOf(await engine.ReassignTaskAsync(
                    task.Id, new WorkflowAssignment("user-b", null), "user-a"))),
            ("ForkTaskAsync", async () =>
                ErrorOf(await engine.ForkTaskAsync(task.Id, ["A", "B"], task.TaskDefinitionId, "user-a"))),
            ("AddBranchToForkAsync", async () =>
                ErrorOf(await engine.AddBranchToForkAsync(forkGroupId, ["C"], "user-a"))),
            ("AddAdHocTaskAsync", async () =>
                ErrorOf(await engine.AddAdHocTaskAsync(task.Id, adHocDefinitionId, "user-a"))),
            ("StartSubWorkflowAsync", async () =>
                ErrorOf(await engine.StartSubWorkflowAsync(task.Id, subDefinitionId, "user-a"))),
            ("CancelSubWorkflowAsync", async () =>
                ErrorOf(await engine.CancelSubWorkflowAsync(instanceId, "user-a"))),
            ("UpdateTaskNotesAsync", async () =>
                ErrorOf(await engine.UpdateTaskNotesAsync(task.Id, "note", "user-a"))),
        };

        var ungated = new List<string>();

        foreach (var (name, invoke) in calls)
        {
            var error = await invoke();

            if (error is not WorkflowAuthorizationException)
            {
                ungated.Add($"{name} -> {error?.GetType().Name ?? "no error at all"}");
            }
        }

        Assert.AreEqual(
            0,
            ungated.Count,
            "these mutating methods are not gated by the authorization policy: "
            + string.Join("; ", ungated));
    }

    /// <summary>The error of a failed Result, or null if it succeeded. Generic because
    /// the twelve methods return five different payload types.</summary>
    private static Exception? ErrorOf<T>(Workflow.Core.Results.Result<T> result) =>
        result.IsError ? result.UnwrapError() : null;
```

**If a row reports the wrong exception type**, the assertion message names it. Two causes
are worth telling apart:

- *"no error at all"* — the method is ungated. Go back to Task 4 or 5 and add the gate.
- *a different exception* — the gate is there but sits **below** some validation in that
  method, so the other check fired first. Move the gate up; do not weaken the assertion.
  This is the same property `It_authorizes_before_it_validates` pins for `CompleteTaskAsync`,
  applied to the other eleven.

The seeded sub-workflow is the "Technical Review" definition; if
`_host.Db.WorkflowDefinitions.Where(d => d.IsSubWorkflow)` comes back empty, the seed
changed — find the current sub-workflow definition rather than deleting the row.

- [ ] **Step 2: Run it**

Run: `dotnet test --filter FullyQualifiedName~It_gates_every_mutating_method`

Expected: PASS if Tasks 4 and 5 were complete. If it fails, the assertion message names
exactly which methods are ungated — go back and gate them, then rerun.

- [ ] **Step 3: Deliberately verify the test bites**

Comment out the `AuthorizeAsync` call in `UpdateTaskNotesAsync`, rerun the test, and
confirm it fails naming `UpdateTaskNotesAsync`. Then restore the call and confirm it
passes again. A coverage test that cannot fail is worse than no test.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test`
Expected: `Failed: 0, Passed: 249`

- [ ] **Step 5: Commit**

```bash
git add tests/Workflow.Tests/AuthorizationTests.cs
git commit -m "Assert every mutating method is gated

Driven from the twelve methods rather than the eleven operations: an
operation-driven table would pass while StartRunOnVersionAsync sat ungated.
Verified to fail by removing a gate.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: The demo policy

**Files:**
- Create: `samples/DemoDocuments.Server/Workflow/DemoAuthorizationPolicy.cs`
- Modify: `samples/DemoDocuments.Server/Program.cs`

- [ ] **Step 1: Check the org shape you are querying**

```bash
sed -n '110,140p' samples/DemoDocuments.Server/Domain/DemoDomain.cs
sed -n '1,50p' samples/DemoDocuments.Server/Workflow/DemoEscalationTrigger.cs
```

`Person` has `ActorId` and `SectionId` but no `Section` navigation, so the lookups below
are correlated subqueries — the same shape `DemoOrg.AssignmentForAsync` uses, for the same
reason.

- [ ] **Step 2: Write the policy**

```csharp
using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using Workflow.Core.Abstractions;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// The demo's authorization rules, and the worked example of the
/// <see cref="IWorkflowAuthorizationPolicy"/> seam.
///
/// It splits <b>doing</b> the work from <b>managing</b> it. Completing a task, editing its
/// notes and hanging work off it are for the person who has it; cancelling, reassigning,
/// forking and unwinding it are for the person above them. That split is why the seam
/// takes an operation rather than answering one question — a policy that ignored its first
/// argument would suggest the enum was unnecessary.
///
/// The org walk is the one <see cref="DemoEscalationTrigger"/> already uses, in the same
/// direction: <c>Person.SectionId</c> → <c>Section.Code</c> (which is the engine's opaque
/// branch key) → <c>Section.SectionLeadActorId</c> → <c>Division.DivisionHeadActorId</c>. The original system would
/// answer the same questions against its own model; nothing here is engine knowledge.
/// </summary>
public sealed class DemoAuthorizationPolicy(DemoDbContext db) : IWorkflowAuthorizationPolicy
{
    public async Task<WorkflowAuthorizationResult> EvaluateAsync(
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        CancellationToken ct = default)
    {
        var person = await db.People
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ActorId == context.ActorId, ct)
            .ConfigureAwait(false);

        if (person is null)
        {
            return WorkflowAuthorizationResult.Denied("you are not a known person");
        }

        // Starting a run is open to anybody on the books. The demo has no notion of who
        // may originate work, and inventing one would teach a rule the original system does not have.
        if (operation == WorkflowOperation.StartRun)
        {
            return WorkflowAuthorizationResult.Allowed;
        }

        var task = context.Task;

        if (task is null)
        {
            return WorkflowAuthorizationResult.Denied("no task to authorize against");
        }

        return operation switch
        {
            WorkflowOperation.CompleteTask
            or WorkflowOperation.CompleteWithSelectiveRejection
            or WorkflowOperation.UpdateTaskNotes
            or WorkflowOperation.AddAdHocTask
            or WorkflowOperation.StartSubWorkflow
                => await DoingAsync(person, task, ct).ConfigureAwait(false),

            WorkflowOperation.CancelTask
            or WorkflowOperation.ReassignTask
            or WorkflowOperation.ForkTask
            or WorkflowOperation.AddBranchToFork
            or WorkflowOperation.CancelSubWorkflow
                => await ManagingAsync(person, task, ct).ConfigureAwait(false),

            // A new operation the engine gained and this policy has not considered.
            // Denied on purpose: an unreviewed operation should fail visibly here rather
            // than be permitted by a default arm nobody remembers writing.
            _ => WorkflowAuthorizationResult.Denied(
                $"this host has no rule for {operation}"),
        };
    }

    /// <summary>
    /// The assignee, or anyone in the section when nobody has claimed it.
    ///
    /// "Unclaimed" is the same shape as the inbox's unclaimed half — a branch key that
    /// matches, and no actor — deliberately, so the demo cannot show somebody work in
    /// their inbox that it then refuses to let them do.
    /// </summary>
    private async Task<WorkflowAuthorizationResult> DoingAsync(
        Domain.Person person, Workflow.Core.Abstractions.WorkflowTaskSnapshot task, CancellationToken ct)
    {
        if (task.AssignedToActorId == person.ActorId)
        {
            return WorkflowAuthorizationResult.Allowed;
        }

        if (task.AssignedToActorId is null
            && task.AssignedBranchKey is not null
            && task.AssignedBranchKey == await SectionCodeAsync(person, ct).ConfigureAwait(false))
        {
            return WorkflowAuthorizationResult.Allowed;
        }

        return WorkflowAuthorizationResult.Denied(
            "this task belongs to somebody else, or to another section");
    }

    /// <summary>The section's Section Lead, or the branch head above it.</summary>
    private async Task<WorkflowAuthorizationResult> ManagingAsync(
        Domain.Person person, Workflow.Core.Abstractions.WorkflowTaskSnapshot task, CancellationToken ct)
    {
        if (task.AssignedBranchKey is null)
        {
            return WorkflowAuthorizationResult.Denied(
                "this task is in no section, so it has no supervisor");
        }

        var section = await db.Sections
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == task.AssignedBranchKey, ct)
            .ConfigureAwait(false);

        if (section is null)
        {
            return WorkflowAuthorizationResult.Denied(
                $"section '{task.AssignedBranchKey}' is not one of ours");
        }

        if (section.SectionLeadActorId == person.ActorId)
        {
            return WorkflowAuthorizationResult.Allowed;
        }

        var branchHead = await db.Branches
            .AsNoTracking()
            .Where(b => b.Id == section.BranchId)
            .Select(b => b.DivisionHeadActorId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return branchHead == person.ActorId
            ? WorkflowAuthorizationResult.Allowed
            : WorkflowAuthorizationResult.Denied(
                "only the section's Section Lead or the branch head above it may do this");
    }

    private async Task<string?> SectionCodeAsync(Domain.Person person, CancellationToken ct) =>
        await db.Sections
            .AsNoTracking()
            .Where(s => s.Id == person.SectionId)
            .Select(s => s.Code)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
```

Fix the `Domain.Person` and `WorkflowTaskSnapshot` type references to whatever the actual
namespaces are — check the `using` block of `DemoOrg.cs` and `DemoEscalationTrigger.cs`
and match them, then simplify these to bare `Person` and `WorkflowTaskSnapshot`.

Confirm the snapshot's property names before relying on them:

```bash
grep -n "AssignedToActorId\|AssignedBranchKey" src/Workflow.Core/Abstractions/Snapshots.cs
```

- [ ] **Step 3: Register it**

In `samples/DemoDocuments.Server/Program.cs`, in the `AddWorkflowEngine()` chain, after
`.AddDueDateResolver<DemoDueDateResolver>()`:

```csharp
    // Who may act. Without this every operation is permitted for every actor -- which is
    // what this demo did before, and what the engine still does for a host that registers
    // no policy.
    .AddAuthorizationPolicy<DemoAuthorizationPolicy>()
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: `Failed: 0, Passed: 249`

The tests build their own container and do not read `Program.cs`, so this registration
must not change the count. If it drops, `TestHost` is picking up the demo policy — find
out why before continuing.

- [ ] **Step 6: Commit**

```bash
git add samples/DemoDocuments.Server/Workflow/DemoAuthorizationPolicy.cs \
        samples/DemoDocuments.Server/Program.cs
git commit -m "Add the demo's authorization rules

Doing the work is for the person who has it; managing it is for the person
above them. An unknown operation is denied rather than defaulted open.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: Test the demo rules

**Files:**
- Modify: `tests/Workflow.Tests/AuthorizationTests.cs`

- [ ] **Step 1: Find out who the seed creates**

```bash
grep -n "ActorId\|SectionLeadActorId\|DivisionHeadActorId\|Section\b" \
    samples/DemoDocuments.Server/Workflow/DemoWorkflowSeeder.cs | head -40
```

Note the actual seeded actor ids, section codes, Section Lead and branch head. The tests below use
placeholder-free names taken from the seed — **substitute the real ones** rather than
inventing them, and if the seed lacks a branch head, add one in the test's setup.

- [ ] **Step 2: Let TestHost build a policy over its own context**

`DemoAuthorizationPolicy` needs the same `DemoDbContext` the engine is using, but
`TestHost.CreateAsync` creates that context internally — so a caller cannot construct the
policy beforehand. Add a factory parameter alongside the `policy` one from Task 2, in
`tests/Workflow.Tests/TestHost.cs`:

```csharp
    public static async Task<TestHost> CreateAsync(
        bool withTriggers = false,
        Action<IServiceCollection>? configure = null,
        IWorkflowDueDateResolver? dueDates = null,
        IWorkflowAuthorizationPolicy? policy = null,
        Func<DemoDbContext, IWorkflowAuthorizationPolicy>? policyFactory = null)
```

In the `if (!withTriggers)` branch, after `plainDb` is created and seeded and before the
engine is constructed:

```csharp
            var effectivePolicy = policy ?? policyFactory?.Invoke(plainDb);
```

then pass `authorizationPolicy: effectivePolicy` to the `new WorkflowEngine(...)` call
instead of `policy`.

- [ ] **Step 3: Write the failing tests**

Append a second test class to `tests/Workflow.Tests/AuthorizationTests.cs`:

```csharp
/// <summary>
/// The demo's own rules, which are the worked example a second host copies. Separate
/// class from <see cref="AuthorizationTests"/> because these need the real
/// <see cref="DemoAuthorizationPolicy"/> rather than a scripted one.
/// </summary>
[TestClass]
public class DemoAuthorizationTests
{
    private TestHost _host = null!;
    private int _definitionId;

    [TestInitialize]
    public async Task Setup()
    {
        // policyFactory, not policy: the rules query the same database the engine writes.
        _host = await TestHost.CreateAsync(
            policyFactory: db => new DemoAuthorizationPolicy(db));

        _definitionId = await _host.Db.WorkflowDefinitions
            .Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();
}
```

Then write the rules tests. Substitute the seeded ids found in Step 1 for
`AssigneeActorId`, `SectionMateActorId`, `OtherSectionActorId`, `SectionLeadActorId` and
`DivisionHeadActorId`:

```csharp
    [TestMethod]
    public async Task The_assignee_may_complete()
    {
        var task = await TaskAssignedToAsync(AssigneeActorId);

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), AssigneeActorId);

        Assert.IsTrue(result.IsOk);
    }

    [TestMethod]
    public async Task A_stranger_may_not_complete()
    {
        var task = await TaskAssignedToAsync(AssigneeActorId);

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), OtherSectionActorId);

        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task An_unknown_actor_may_not_start_a_run()
    {
        var result = await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), _definitionId, "nobody-at-all");

        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task A_section_member_may_complete_unclaimed_work_in_their_section()
    {
        var task = await UnclaimedTaskInSectionAsync(SectionCode);

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), SectionMateActorId);

        Assert.IsTrue(result.IsOk, "unclaimed work is claimable by the section it sits in");
    }

    [TestMethod]
    public async Task Somebody_from_another_section_may_not_take_unclaimed_work()
    {
        var task = await UnclaimedTaskInSectionAsync(SectionCode);

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), OtherSectionActorId);

        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task The_assignee_may_not_cancel_their_own_task()
    {
        var task = await TaskAssignedToAsync(AssigneeActorId);

        var result = await _host.Engine.CancelTaskAsync(task.Id, AssigneeActorId);

        Assert.IsInstanceOfType<WorkflowAuthorizationException>(
            result.UnwrapError(),
            "cancelling is managing, not doing -- this is the split the enum exists for");
    }

    [TestMethod]
    public async Task The_section_lead_may_cancel()
    {
        var task = await TaskAssignedToAsync(AssigneeActorId);

        var result = await _host.Engine.CancelTaskAsync(task.Id, SectionLeadActorId);

        Assert.IsTrue(result.IsOk);
    }

    [TestMethod]
    public async Task The_branch_head_may_cancel()
    {
        var task = await TaskAssignedToAsync(AssigneeActorId);

        var result = await _host.Engine.CancelTaskAsync(task.Id, DivisionHeadActorId);

        Assert.IsTrue(result.IsOk, "the branch head sits above the Section Lead and inherits the reach");
    }
```

Write `TaskAssignedToAsync`, `UnclaimedTaskInSectionAsync` and `ValidOutcomeAsync` as
private helpers on this class: start a run, then update the entry task's
`AssignedToActorId` / `AssignedBranchKey` directly through `_host.Db` and save, because
`ReassignTaskAsync` is itself gated and would need a manager to call it.

- [ ] **Step 4: Run them**

Run: `dotnet test --filter FullyQualifiedName~DemoAuthorizationTests`
Expected: `Failed: 0, Passed: 8`

Fix the policy, not the tests, if a rule disagrees with the spec's table.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: `Failed: 0, Passed: 257`

- [ ] **Step 6: Commit**

```bash
git add tests/Workflow.Tests/AuthorizationTests.cs tests/Workflow.Tests/TestHost.cs
git commit -m "Test the demo's rules, including the split they turn on

The assignee may complete their task and may not cancel it; the Section Lead and
the branch head may cancel it and are not the assignee. That pair is the
whole reason the seam takes an operation.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 9: Derive CanComplete from the policy

**Files:**
- Modify: `samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs` (class doc at line 22, `CanComplete` at line 197, call site at line 179)
- Modify: `src/Workflow.Core/Runner/IWorkflowRunnerClient.cs` (doc comment at line 152)
- Modify: `tests/Workflow.Tests/AuthorizationTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `DemoAuthorizationTests`:

```csharp
    /// <summary>
    /// The button and the gate must agree. Before this, CanComplete was hand-written and
    /// could drift from the rule the engine enforces; a runner offering a button that
    /// then fails is worse than one that offers nothing.
    /// </summary>
    [TestMethod]
    public async Task CanComplete_matches_what_the_policy_allows()
    {
        var task = await TaskAssignedToAsync(AssigneeActorId);
        var client = RunnerClientFor(OtherSectionActorId);

        var detail = (await client.GetRunDetailAsync(task.WorkflowRunId)).Tasks
            .Single(t => t.Id == task.Id);

        Assert.IsFalse(
            detail.CanComplete,
            "the runner must not offer a button the engine will refuse");

        var owner = RunnerClientFor(AssigneeActorId);

        var ownersView = (await owner.GetRunDetailAsync(task.WorkflowRunId)).Tasks
            .Single(t => t.Id == task.Id);

        Assert.IsTrue(ownersView.CanComplete);
    }
```

`RunnerClientFor` builds a `DemoRunnerClient` for a given actor. Check the existing
constructions in `tests/Workflow.Tests/RunnerClientTests.cs` and match them, adding the
policy argument:

```bash
grep -n "new DemoRunnerClient" tests/Workflow.Tests/RunnerClientTests.cs
```

Check `GetRunDetailAsync`'s exact name and signature the same way — the method returning
`RunDetail` may be named differently.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test --filter FullyQualifiedName~CanComplete_matches`
Expected: FAIL — either it does not compile (`DemoRunnerClient` takes no actor or policy
yet), or `CanComplete` is true for both actors because it still only checks status.

- [ ] **Step 3: Make DemoRunnerClient consult the policy**

The client currently has no notion of *who is looking*. Add the acting actor and the
policy to its constructor:

```csharp
public sealed class DemoRunnerClient(
    DemoDbContext db,
    IWorkflowEngine engine,
    IWorkflowActorResolver actors,
    DemoOrg org,
    IWorkflowAuthorizationPolicy authorization) : IWorkflowRunnerClient
```

Replace the static `CanComplete` with an async one. The status check does not disappear,
it moves — a completed task is not *authorized* differently, it is simply not actionable:

```csharp
    /// <summary>
    /// Whether the runner offers to complete a task.
    ///
    /// Two conditions, and they answer different questions. The status check asks whether
    /// the task is actionable at all; the policy asks whether this actor may act — the
    /// same policy the engine consults, so the button and the gate cannot disagree. This
    /// used to be a hand-written rule, and a hand-written rule is one that drifts.
    ///
    /// One policy call per task in the list. In-process against the small lists a single
    /// run produces that is fine; a host with a remote directory behind its policy would
    /// want to memoize per request, which the seam does not prevent.
    /// </summary>
    private async Task<bool> CanCompleteAsync(
        WorkflowTaskSnapshot task, string actorId, CancellationToken ct)
    {
        if (task.Status is not (WorkflowTaskStatus.NotStarted or WorkflowTaskStatus.InProgress))
        {
            return false;
        }

        var result = await authorization
            .EvaluateAsync(
                WorkflowOperation.CompleteTask,
                new WorkflowAuthorizationContext(actorId, task, null, null),
                ct)
            .ConfigureAwait(false);

        return result.IsAllowed;
    }
```

The call site at line 179 sits inside a synchronous `.Select(...)` projection, which
cannot await. Convert that projection to a `foreach` building a `List<RunTaskView>`, so
each task can be awaited:

```csharp
        var tasks = new List<RunTaskView>();

        foreach (var t in snapshots)
        {
            // ... the existing per-task locals (d, subWorkflowCounts, created) unchanged ...

            tasks.Add(new RunTaskView(
                // ... every existing argument unchanged ...
                CanComplete: await CanCompleteAsync(t, actorId, ct).ConfigureAwait(false),
                OverdueFiredAt: t.OverdueFiredAt));
        }

        tasks = [.. tasks.OrderBy(t => t.Created).ThenBy(t => t.Id)];
```

If `GetRunDetailAsync` has no `actorId` parameter, add one — the interface method in
`IWorkflowRunnerClient.cs` needs it too, and `WorkflowRunner.razor` must pass its
`ActorId` parameter down. Check what the component already holds:

```bash
grep -n "ActorId" src/Workflow.MudBlazor/WorkflowRunner.razor | head
```

- [ ] **Step 4: Correct the two doc comments that are now untrue**

`src/Workflow.Core/Runner/IWorkflowRunnerClient.cs`, replacing the `CanComplete` summary
at line 152:

```csharp
/// <summary>
/// A task as the runner shows it: what it is, who has it, what can be done to it.
///
/// <see cref="CanComplete"/> is decided by the host rather than the component, because
/// whether someone may act on a task is an authorization question and the rule belongs to
/// the host's org model. The engine does not decide it, but it does <b>enforce</b> it: a
/// host registering an <c>IWorkflowAuthorizationPolicy</c> has the same rule applied to
/// every mutating engine method, so a host that derives this from that policy cannot show
/// a button the engine will then refuse.
/// </summary>
```

Leave the comment at line 181 alone — the distinction it draws between `CanComplete` and
`BlockingSubWorkflowCount` is still exactly right.

`samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs`, the bullet at line 22:

```csharp
///  - **Who may act on a task.** The rule is the host's, and lives in
///    <see cref="DemoAuthorizationPolicy"/>. <see cref="RunTaskView.CanComplete"/> is
///    derived from that same policy rather than hand-written here, so the button and the
///    engine's gate cannot drift apart.
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test`
Expected: `Failed: 0, Passed: 258`

`RunnerClientTests` will need its `DemoRunnerClient` constructions updated for the new
parameters. Give those an allow-everything policy so their existing assertions hold — they
are testing the runner, not authorization:

```csharp
private sealed class AllowAll : IWorkflowAuthorizationPolicy
{
    public Task<WorkflowAuthorizationResult> EvaluateAsync(
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        CancellationToken ct = default) =>
        Task.FromResult(WorkflowAuthorizationResult.Allowed);
}
```

- [ ] **Step 6: Commit**

```bash
git add samples/DemoDocuments.Server/Workflow/DemoRunnerClient.cs \
        src/Workflow.Core/Runner/IWorkflowRunnerClient.cs \
        src/Workflow.MudBlazor/WorkflowRunner.razor \
        tests/Workflow.Tests/
git commit -m "Derive CanComplete from the policy the engine enforces

It was hand-written, and a hand-written rule drifts. Two doc comments
saying the engine has no opinion on authorization are now untrue and
corrected; the one distinguishing CanComplete from blocking sub-workflows
is still right and left alone.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 10: Run the demo, then record the state

- [ ] **Step 1: Verify the whole suite**

Run: `dotnet build && dotnet test`
Expected: `Build succeeded. 0 Warning(s)` and `Failed: 0, Passed: 258`

Record the real number — later steps quote it and it must match.

- [ ] **Step 2: Drive the demo by hand**

Run the demo and confirm the feature is real in the UI, not just in tests:

```bash
dotnet run --project samples/DemoDocuments.Server
```

Open a document with an active run, use the **Acting as** picker to switch between the
assignee and somebody from another section, and confirm the Complete button appears for
one and not the other. Then check the app log shows no `AllowAllAuthorizationPolicy`
warning — the demo registers a real policy, so seeing that warning means the registration
in Task 7 did not take.

- [ ] **Step 3: Update STATE.md**

In the `### Done` list, add after the escalation bullet:

```markdown
- **Authorization.** `IWorkflowAuthorizationPolicy` is consulted before every one of the
  engine's twelve mutating methods, after the task loads and before any validation — so a
  denial writes nothing and tells a denied actor nothing about task state. The rule stays
  host knowledge; only the asking is the engine's. A host registering no policy gets
  `AllowAllAuthorizationPolicy` and the pre-authorization behaviour, warned about once at
  first use. **A policy that throws denies** — deliberately the opposite of the assignment
  and due-date resolvers, because their fallback is a lesser answer and this one's would be
  no gate at all. `WorkflowActors.System` bypasses, so the deadline sweep cannot be broken
  by a host policy. The demo splits doing from managing: the assignee completes, the Section Lead
  or branch head cancels and reassigns.
```

Update the test count in the `## Status` section from **239/239** to the number from Step
1, and add `authorization` to the list of what the integration tests cover in the
`### Done` bullet that enumerates them.

Replace the `## Suggested next step` section — authorization is no longer it:

```markdown
## Suggested next step

**`Workflow.AspNetCore`** — ready-made endpoints for hosts that want them. The demo maps
its own routes by hand, and now that authorization is enforced in the engine, an endpoint
layer can be thin without being unsafe.

Still open:

- **Reads are not gated.** `GetRunAsync`, `GetTaskLogsAsync` and the rest are open to any
  caller; authorization covers mutations only. Deliberate: reads are how a host builds the
  page it then decides whether to show, the inbox already answers a similar question
  through its own predicate, and gating reads would put a policy call on every per-page
  load. Revisit if a host needs run-level visibility rules.
```

Keep the existing bullet about the builder dialogs and bUnit under "Still open" — it is
unrelated to this work and still true.

- [ ] **Step 4: Commit**

```bash
git add STATE.md
git commit -m "Record authorization in STATE.md

Also replaces the suggested next step, which was this, and writes down why
reads are deliberately not gated so it is not re-derived later.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

- [ ] **Step 5: Mark the spec implemented**

Change the header of `docs/superpowers/specs/2026-08-23-authorization-design.md`:

```markdown
**Status:** Implemented 2026-08-23 — see `docs/superpowers/plans/2026-08-23-authorization.md`

> **Naming note:** written before the TaskRouter rename of 2026-08-25. `Workflow.Core` is
> now `TaskRouter.Core`, `Workflow.Persistence.EF` is `TaskRouter.EntityFrameworkCore`,
> `Workflow.AspNetCore` is `TaskRouter.AspNetCore`, and `Workflow.MudBlazor` is
> `TaskRouter.Blazor`. The names below are left as written.
```

```bash
git add docs/superpowers/specs/2026-08-23-authorization-design.md
git commit -m "Mark the authorization spec implemented

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Done when

- `dotnet build` reports 0 warnings, 0 errors.
- `dotnet test` reports 0 failures, with 19 more tests than the 239 baseline.
- `It_gates_every_mutating_method` passes, and has been seen to fail with a gate removed.
- The demo shows the Complete button to the assignee and hides it from a stranger.
- STATE.md names authorization as done and read gating as a deliberate gap.
