using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Runner;

namespace TaskRouter.Tests;

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
    /// makes it fail, which must deny rather than permit. <see cref="ThrowFactory"/>
    /// chooses *which* exception it throws, for pinning the two catch-filter exclusions
    /// in <c>WorkflowEngine.AuthorizeAsync</c>; left null, the throw is an ordinary
    /// <see cref="InvalidOperationException"/> as before.
    /// </summary>
    private sealed class ScriptedPolicy : IWorkflowAuthorizationPolicy
    {
        public bool Allow { get; set; } = true;
        public bool ShouldThrow { get; set; }
        public Func<Exception>? ThrowFactory { get; set; }
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
                    ThrowFactory?.Invoke() ?? new InvalidOperationException("policy down"));
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
        var task = await StartedTaskAsync();
        _policy.Allow = false;

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        Assert.IsTrue(result.IsError, "a denied completion must fail");
        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
    }

    [TestMethod]
    public async Task It_reports_the_reason_the_policy_gave()
    {
        var task = await StartedTaskAsync();
        _policy.Allow = false;

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
        var logsBefore = await _host.Db.WorkflowTaskLogs.CountAsync(l => l.TaskId == task.Id);
        var modifierBefore = task.ModifierId;

        _policy.Allow = false;

        await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        _host.Db.ChangeTracker.Clear();
        var after = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == task.Id);

        Assert.AreEqual(WorkflowTaskStatus.NotStarted, after.Status);
        Assert.IsNull(after.OutcomeKey);
        Assert.IsNull(after.CompletedDate);
        Assert.AreEqual(
            modifierBefore,
            after.ModifierId,
            "a refused operation must not stamp itself in as the last modifier");
        Assert.AreEqual(
            logsBefore,
            await _host.Db.WorkflowTaskLogs.CountAsync(l => l.TaskId == task.Id),
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
        var task = await StartedTaskAsync();
        _policy.ShouldThrow = true;

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        Assert.IsTrue(result.IsError, "a policy that throws must deny, never permit");
        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
    }

    /// <summary>
    /// Pins the first of the two documented catch-filter exclusions in
    /// <c>WorkflowEngine.AuthorizeAsync</c>: <c>ex is not OperationCanceledException</c>.
    /// Cancellation is not a denial, so a policy that throws one must not be reported as
    /// an authorization failure — a host mapping <see cref="WorkflowAuthorizationException"/>
    /// to HTTP 403 would otherwise tell the caller an aborted request was forbidden.
    /// </summary>
    [TestMethod]
    public async Task A_cancelling_policy_is_not_reported_as_a_denial()
    {
        var task = await StartedTaskAsync();
        _policy.ShouldThrow = true;
        _policy.ThrowFactory = () => new OperationCanceledException("the caller went away");

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        Assert.IsTrue(result.IsError, "the operation must still fail");
        Assert.IsNotInstanceOfType<WorkflowAuthorizationException>(
            result.UnwrapError(),
            "cancellation must not be reported as an authorization denial");
        Assert.IsInstanceOfType<OperationCanceledException>(result.UnwrapError());
    }

    /// <summary>
    /// Pins the second of the two documented catch-filter exclusions in
    /// <c>WorkflowEngine.AuthorizeAsync</c>: <c>ex is not WorkflowAuthorizationException</c>.
    /// A policy that raises its own exception of that type has already said precisely why;
    /// it must reach the caller with that reason, not be re-wrapped as "the authorization
    /// policy failed".
    /// </summary>
    [TestMethod]
    public async Task A_policys_own_authorization_exception_keeps_its_reason()
    {
        var task = await StartedTaskAsync();
        _policy.ShouldThrow = true;
        _policy.ThrowFactory = () => new WorkflowAuthorizationException(
            WorkflowOperation.CompleteTask,
            "user-reviewer",
            "a distinctive reason only the policy itself would give");

        var result = await _host.Engine.CompleteTaskAsync(
            task.Id, await ValidOutcomeAsync(task), "user-reviewer");

        Assert.IsInstanceOfType<WorkflowAuthorizationException>(result.UnwrapError());
        StringAssert.Contains(
            result.UnwrapError().Message,
            "a distinctive reason only the policy itself would give",
            "the policy's own exception must reach the caller intact, not re-wrapped as " +
            "the generic 'the authorization policy failed'");
    }

    /// <summary>
    /// Pins the <c>TryAddScoped</c>-vs-<c>AddScoped</c> registration ordering
    /// <see cref="TestHost"/>'s DI path depends on: a host that registers nothing gets
    /// the library's allow-all default, and a host that registers its own policy through
    /// <c>AddAuthorizationPolicy&lt;T&gt;()</c> has it win regardless of that default
    /// having already been added.
    /// </summary>
    [TestMethod]
    public void AddTaskRouter_defaults_to_allow_all_and_a_host_policy_wins()
    {
        var bare = new ServiceCollection();
        bare.AddLogging();
        bare.AddTaskRouter();

        using (var provider = bare.BuildServiceProvider())
        using (var scope = provider.CreateScope())
        {
            var policy = scope.ServiceProvider.GetRequiredService<IWorkflowAuthorizationPolicy>();

            Assert.AreEqual(
                "AllowAllAuthorizationPolicy",
                policy.GetType().Name,
                "a host that registers no policy must get the library's allow-all default");
        }

        var withHostPolicy = new ServiceCollection();
        withHostPolicy.AddLogging();
        withHostPolicy.AddTaskRouter().AddAuthorizationPolicy<StubAuthorizationPolicy>();

        using var provider2 = withHostPolicy.BuildServiceProvider();
        using var scope2 = provider2.CreateScope();

        Assert.IsInstanceOfType<StubAuthorizationPolicy>(
            scope2.ServiceProvider.GetRequiredService<IWorkflowAuthorizationPolicy>(),
            "a host's own policy, added after AddTaskRouter()'s default, must win");
    }

    private sealed class StubAuthorizationPolicy : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation,
            WorkflowAuthorizationContext context,
            CancellationToken ct = default) =>
            Task.FromResult(WorkflowAuthorizationResult.Allowed);
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

        // A run of its own for CancelRunAsync, which would otherwise cancel the tasks the
        // rows above and below it depend on.
        var cancellableRunId = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "9001"), _definitionId, "user-originator"))
            .Unwrap().Id;

        // A real sub-workflow instance, for CancelSubWorkflowAsync. "Technical Review" is
        // attached to the Provide Input task, not the entry task `task` is, so a second
        // run is advanced one step to reach a task it can actually hang off of.
        var subDefinitionId = await _host.Db.WorkflowDefinitions
            .Where(d => d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

        var attachedTaskDefinitionId = await _host.Db.WorkflowSubWorkflowAttachments
            .Where(a => a.SubWorkflowDefinitionId == subDefinitionId && a.TaskDefinitionId != null)
            .Select(a => a.TaskDefinitionId!.Value)
            .FirstAsync();

        var attachRun = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), _definitionId, "user-originator")).Unwrap();
        var attachEntryTask = await _host.Db.WorkflowTasks
            .SingleAsync(t => t.Id == attachRun.Tasks.Single().Id);
        (await _host.Engine.CompleteTaskAsync(
            attachEntryTask.Id, await ValidOutcomeAsync(attachEntryTask), "user-originator")).Unwrap();
        var attachedTask = await _host.Db.WorkflowTasks.SingleAsync(t =>
            t.WorkflowRunId == attachEntryTask.WorkflowRunId
            && t.TaskDefinitionId == attachedTaskDefinitionId);

        var instanceId = (await _host.Engine.StartSubWorkflowAsync(
            attachedTask.Id, subDefinitionId, "user-originator")).Unwrap().Id;

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
            // Its own run, because cancelling one ends every task on it -- reusing another
            // row's run would pull the rug from under whichever call ran after this.
            ("CancelRunAsync", async () =>
                ErrorOf(await engine.CancelRunAsync(cancellableRunId, "user-a", "no"))),
            // Gated as PreAssignTask rather than as one of the task operations: naming
            // somebody for work that does not exist yet is a different act from doing your
            // own, and a host will want a different rule for it.
            ("PreAssignAsync", async () =>
                ErrorOf(await engine.PreAssignAsync(
                    task.WorkflowRunId, task.TaskDefinitionId,
                    new WorkflowAssignment("user-b", null), "user-a"))),
            ("RemovePreAssignmentAsync", async () =>
                ErrorOf(await engine.RemovePreAssignmentAsync(
                    task.WorkflowRunId, task.TaskDefinitionId, "user-a"))),
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

        Assert.IsEmpty(
            ungated,
            "these mutating methods are not gated by the authorization policy: "
            + string.Join("; ", ungated));

        // The table above is itself hand-written, so a thirteenth mutating method would
        // pass the assertion above by simply not being a row in it — no hand-written
        // per-method test can catch its own omission. This reflects over the interface
        // instead: a new method with a `string actorId` parameter fails here by name
        // until someone adds it as a row above (and wires it into DemoAuthorizationPolicy).
        //
        // "Get..." methods are excluded: GetOpenTasksForActorAsync also takes an
        // `actorId`, but to query by it, not to authorize against it — the same read/write
        // split the design doc draws, applied here through the naming convention every
        // read on this interface already follows.
        var coveredNames = calls.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        var actorIdMethods = typeof(IWorkflowEngine).GetMethods()
            .Where(m => m.GetParameters()
                .Any(p => p.ParameterType == typeof(string) && p.Name == "actorId"))
            .Where(m => !m.Name.StartsWith("Get", StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsTrue(
            actorIdMethods.SetEquals(coveredNames),
            "IWorkflowEngine's actorId-taking mutating methods are "
            + $"{{{string.Join(", ", actorIdMethods.OrderBy(n => n, StringComparer.Ordinal))}}} "
            + "but the coverage table above covers "
            + $"{{{string.Join(", ", coveredNames.OrderBy(n => n, StringComparer.Ordinal))}}}. "
            + "Add the missing method as a row in the table above, in the `calls` array, "
            + "and to DemoAuthorizationPolicy's switch, so it is actually gated rather than "
            + "merely declared.");
    }

    /// <summary>The error of a failed Result, or null if it succeeded. Generic because
    /// the twelve methods return five different payload types.</summary>
    private static Exception? ErrorOf<T>(TaskRouter.Core.Results.Result<T> result) =>
        result.IsError ? result.UnwrapError() : null;
}

/// <summary>
/// The demo's own rules, which are the worked example a second host copies. Separate
/// class from <see cref="AuthorizationTests"/> because these need the real
/// <see cref="DemoAuthorizationPolicy"/> rather than a scripted one.
/// </summary>
[TestClass]
public class DemoAuthorizationTests
{
    // Taken from DemoWorkflowSeeder: "Engineering Branch" / user-division-head, section
    // C100 "Electrical" / user-lead-c100, worker user-worker-c100 (Casey Ellis, C100),
    // worker user-worker-c200 (Drew Novak, C200).
    private const string AssigneeActorId = "user-worker-c100";
    private const string SectionMateActorId = "user-worker-c100b";
    private const string OtherSectionActorId = "user-worker-c200";
    private const string SectionLeadActorId = "user-lead-c100";
    private const string DivisionHeadActorId = "user-division-head";
    private const string SectionCode = "C100";

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

        // The seed's only two C100 people are the assignee (user-worker-c100) and the
        // Section Lead (user-lead-c100). Using the Section Lead to prove "any section member may take
        // unclaimed work" would be ambiguous -- a reader could not tell whether it
        // passed because they are a section member or because they are the Section Lead. This
        // second, plain worker carries no Section Lead or branch-head role, so the
        // unclaimed-work test proves exactly what it claims.
        var sectionId = await _host.Db.Sections
            .Where(s => s.Code == SectionCode).Select(s => s.Id).FirstAsync();

        _host.Db.People.Add(new Person
        {
            ActorId = SectionMateActorId,
            FullName = "Jamie Second Worker (Electrical)",
            SectionId = sectionId
        });
        await _host.Db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    /// <summary>
    /// Starts a run and assigns its entry task to <paramref name="actorId"/> directly
    /// through the database, rather than via <c>ReassignTaskAsync</c> -- that call is
    /// itself gated and would need a manager actor just to set up the fixture, which
    /// would muddle what the test is proving.
    /// </summary>
    private async Task<WorkflowTask> TaskAssignedToAsync(string actorId)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), _definitionId, "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);
        task.AssignedToActorId = actorId;
        task.AssignedBranchKey = SectionCode;
        await _host.Db.SaveChangesAsync();

        return task;
    }

    /// <summary>
    /// Starts a run and leaves its entry task unclaimed in
    /// <paramref name="sectionCode"/> -- no assignee, only a branch key -- the same
    /// shape the inbox's unclaimed half uses.
    /// </summary>
    private async Task<WorkflowTask> UnclaimedTaskInSectionAsync(string sectionCode)
    {
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject("ChangeRequest", "1"), _definitionId, "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);
        task.AssignedToActorId = null;
        task.AssignedBranchKey = sectionCode;
        await _host.Db.SaveChangesAsync();

        return task;
    }

    /// <summary>The outcome key the seeded entry task declares, so completion is legal
    /// on every ground except authorization.</summary>
    private async Task<string> ValidOutcomeAsync(WorkflowTask task) =>
        await _host.Db.WorkflowTaskOutcomes
            .Where(o => o.TaskDefinitionId == task.TaskDefinitionId && !o.IsArchived)
            .Select(o => o.OutcomeKey)
            .FirstAsync();

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

    /// <summary>
    /// The button and the gate must agree. Before this, CanComplete was hand-written and
    /// could drift from the rule the engine enforces; a runner offering a button that
    /// then fails is worse than one that offers nothing.
    /// </summary>
    [TestMethod]
    public async Task CanComplete_matches_what_the_policy_allows()
    {
        var task = await TaskAssignedToAsync(AssigneeActorId);

        var client = new EfWorkflowRunnerClient(
            _host.Db, _host.Engine, new DemoDirectory(_host.Db),
            new DemoAuthorizationPolicy(_host.Db));

        var strangersView = (await client.GetRunAsync(task.WorkflowRunId, OtherSectionActorId))!
            .Tasks.Single(t => t.Id == task.Id);

        Assert.IsFalse(
            strangersView.CanComplete,
            "the runner must not offer a button the engine will refuse");

        var ownersView = (await client.GetRunAsync(task.WorkflowRunId, AssigneeActorId))!
            .Tasks.Single(t => t.Id == task.Id);

        Assert.IsTrue(ownersView.CanComplete);
    }
}
