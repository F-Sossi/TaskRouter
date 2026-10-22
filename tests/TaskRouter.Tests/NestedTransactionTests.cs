using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Builder;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// Work that runs inside a transaction already open: the library's own nesting (publish
/// saving the draft, a trigger calling back into the engine) and what happens when a
/// rollback itself fails.
///
/// <para>The MARS cases matter because SQL Server refuses savepoints on such a connection.
/// Nested work used to demand one and throw, so every builder publish failed on a host whose
/// connection string set MultipleActiveResultSets=True.</para>
/// </summary>
[TestClass]
public class NestedTransactionTests
{
    private TestHost _host = null!;

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private sealed class AssignOnCreateTrigger : IWorkflowTrigger
    {
        public const string TriggerKey = "test.assignOnCreate";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() =>
            new(Key, "Assign on create", "Test trigger.", [WorkflowEventKind.TaskCreated], []);

        public Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default) =>
            context.Actions.AssignTaskAsync(context.Task.Id, new WorkflowAssignment("user-assigned-by-trigger", null), ct);
    }

    private sealed class DoomsTransactionTrigger(IWorkflowDbContext db) : IWorkflowTrigger
    {
        public const string TriggerKey = "test.doomsTransaction";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() =>
            new(Key, "Dooms the transaction", "Test trigger.", [WorkflowEventKind.TaskCompleted], []);

        // XACT_ABORT makes the error roll the whole transaction back on the server, so the
        // engine's own rollback afterwards has nothing left to roll back and fails.
        public Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default) =>
            db.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SELECT 1/0;", ct);
    }

    private async Task<TestHost> MarsHostAsync() =>
        _host = await TestHost.CreateAsync(
            withTriggers: true,
            connectionOptions: "MultipleActiveResultSets=True",
            configure: s =>
            {
                s.AddScoped<IWorkflowTrigger, AssignOnCreateTrigger>();
                s.AddScoped<IWorkflowEditorActorAccessor, TestEditorActorAccessor>();

                // The builder refuses after-commit and overdue triggers a host cannot run, and
                // the seeded workflow has both. These say the host runs them; no worker starts.
                s.AddSingleton(new WorkflowOutboxOptions());
                s.AddSingleton(new WorkflowDeadlineOptions());
                new WorkflowEngineBuilder(s)
                    .AddWorkflowBuilder(o => o.AssignmentRoles =
                        [DemoRoles.SectionLead, DemoRoles.DivisionHead, DemoRoles.Originator])
                    .AddWorkflowRunner();
            });

    private async Task<int> SeededVersionIdAsync() =>
        await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.IsPublished && v.IsLatest && !v.WorkflowDefinition!.IsSubWorkflow)
            .Select(v => v.Id)
            .SingleAsync();

    private async Task<int> WorkflowIdAsync() =>
        await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

    [TestMethod]
    public async Task Publishing_from_the_builder_works_on_a_MARS_connection()
    {
        await MarsHostAsync();
        var builder = _host.Provider!.GetRequiredService<IWorkflowBuilderClient>();

        var model = (await builder.GetWorkflowAsync(await SeededVersionIdAsync()))!;
        model.Description = "published over MARS";

        var published = await builder.PublishAsync(model);

        Assert.IsTrue(published.Success, string.Join("; ", published.Errors.Select(e => e.Message)));
    }

    [TestMethod]
    public async Task Discarding_a_test_run_works_on_a_MARS_connection()
    {
        await MarsHostAsync();
        var runner = _host.Provider!.GetRequiredService<IWorkflowRunnerClient>();

        var started = await runner.StartTestRunAsync(await SeededVersionIdAsync(), "user-originator");
        Assert.IsTrue(started.Success, started.Error);

        var deleted = await runner.DeleteTestRunAsync(started.RunId, "user-originator");

        Assert.IsTrue(deleted.Success, deleted.Error);
        Assert.IsFalse(await _host.Db.WorkflowRuns.AnyAsync(r => r.Id == started.RunId));
    }

    [TestMethod]
    public async Task A_trigger_calling_back_into_the_engine_works_on_a_MARS_connection()
    {
        await MarsHostAsync();
        var pmReview = await _host.Db.WorkflowTaskDefinitions
            .SingleAsync(d => d.DisplayName == "PM Review"
                           && d.WorkflowDefinitionVersion!.WorkflowDefinitionId == _host.Db.WorkflowDefinitions
                               .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName).Select(w => w.Id).Single());

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = pmReview.Id,
            TriggerKey = AssignOnCreateTrigger.TriggerKey,
            Event = WorkflowEventKind.TaskCreated,
            DispatchMode = TriggerDispatchMode.InTransaction,
            FailurePolicy = TriggerFailurePolicy.FailOperation,
            CreatorId = "system",
            ModifierId = "system"
        });
        await _host.Db.SaveChangesAsync();

        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, await WorkflowIdAsync(), "user-originator")).Unwrap();
        (await _host.Engine.CompleteTaskAsync(run.Tasks.Single().Id, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks.AsNoTracking()
            .SingleAsync(t => t.WorkflowRunId == run.Id && t.Status == WorkflowTaskStatus.NotStarted);
        (await _host.Engine.CompleteTaskAsync(provideInput.Id, "approved", "user-originator")).Unwrap();

        var review = await _host.Db.WorkflowTasks.AsNoTracking()
            .SingleAsync(t => t.WorkflowRunId == run.Id && t.TaskDefinitionId == pmReview.Id);

        Assert.AreEqual("user-assigned-by-trigger", review.AssignedToActorId);
    }

    [TestMethod]
    public async Task A_rollback_that_fails_does_not_hide_the_error_that_caused_it()
    {
        _host = await TestHost.CreateAsync(
            withTriggers: true,
            configure: s => s.AddScoped<IWorkflowTrigger, DoomsTransactionTrigger>());

        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, await WorkflowIdAsync(), "user-originator")).Unwrap();
        var entry = run.Tasks.Single();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entry.TaskDefinitionId,
            TriggerKey = DoomsTransactionTrigger.TriggerKey,
            Event = WorkflowEventKind.TaskCompleted,
            DispatchMode = TriggerDispatchMode.InTransaction,
            FailurePolicy = TriggerFailurePolicy.FailOperation,
            CreatorId = "system",
            ModifierId = "system"
        });
        await _host.Db.SaveChangesAsync();

        var result = await _host.Engine.CompleteTaskAsync(entry.Id, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().ToString(), "Divide by zero");

        var stored = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(t => t.Id == entry.Id);
        Assert.AreNotEqual(WorkflowTaskStatus.Completed, stored.Status);
    }
}

[TestClass]
public class RouteDefaultsTests
{
    private static RouteEditModel Route(string outcome, bool isDefault = false, string? condition = null) =>
        new() { OutcomeKey = outcome, IsDefault = isDefault, ConditionKey = condition };

    [TestMethod]
    public void The_first_route_for_an_outcome_is_suggested_as_its_default()
    {
        var route = Route("approved");

        Assert.IsTrue(Blazor.RouteDefaults.Suggest(route, [Route("rejected", isDefault: true)]));
    }

    [TestMethod]
    public void A_second_route_for_an_outcome_that_has_a_default_is_not()
    {
        var route = Route("approved");

        Assert.IsFalse(Blazor.RouteDefaults.Suggest(route, [Route("APPROVED", isDefault: true)]));
    }

    [TestMethod]
    public void A_route_with_a_condition_is_never_suggested_as_the_default()
    {
        Assert.IsFalse(Blazor.RouteDefaults.Suggest(Route("approved", condition: "requires-review"), []));
    }

    [TestMethod]
    public void Picking_a_condition_turns_the_default_off_and_clearing_it_leaves_it_off()
    {
        var route = Route("approved", isDefault: true);

        Blazor.RouteDefaults.ConditionChanged(route, "requires-review");

        Assert.IsFalse(route.IsDefault);
        Assert.AreEqual("requires-review", route.ConditionKey);

        Blazor.RouteDefaults.ConditionChanged(route, " ");

        Assert.IsNull(route.ConditionKey);
        Assert.IsFalse(route.IsDefault, "clearing a condition should not silently make a route the default");
    }

    [TestMethod]
    public void The_route_being_edited_does_not_count_as_its_own_sibling()
    {
        var route = Route("approved", isDefault: true);

        Assert.IsTrue(Blazor.RouteDefaults.Suggest(route, [route]));
    }
}
