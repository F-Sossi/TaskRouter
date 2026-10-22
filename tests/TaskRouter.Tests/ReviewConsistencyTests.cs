using System.Collections.Concurrent;
using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.Core.Runner;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Runner;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

[TestClass]
public class ReviewConsistencyTests
{
    private TestHost _host = null!;
    private WorkflowDefinitionVersion _version = null!;
    private WorkflowTaskDefinition _entry = null!;
    private WorkflowTaskDefinition _end = null!;
    private WorkflowTaskDefinition _adHoc = null!;
    private readonly Probe _probe = new();

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync(withTriggers: true, configure: services =>
        {
            services.AddSingleton(_probe);
            services.AddScoped<IWorkflowTrigger, ProbeTrigger>();
            services.AddScoped<IRouteConditionEvaluator, MatchingCondition>();
            services.AddScoped<IWorkflowDueDateResolver, DueDates>();
            services.AddDbContext<DemoDbContext>(options => options.AddInterceptors(_probe.Candidates));
            services.AddDbContext<DemoDbContext>(options => options.AddInterceptors(_probe.DeleteFailure));
        });
        var definition = new WorkflowDefinition { Name = "Consistency" };
        _version = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = 0,
            WorkflowDefinition = definition,
            Version = 1,
            SubjectType = "Consistency",
            IsPublished = true,
            IsLatest = true
        };
        var typeId = await _host.Db.WorkflowTaskTypes.Select(type => type.Id).FirstAsync();
        _entry = Definition("Entry", typeId);
        _end = Definition("End", typeId);
        _end.IsTerminal = true;
        _adHoc = Definition("Ad hoc", typeId);
        _adHoc.IsAdHoc = true;
        _adHoc.IsBlocking = true;
        _host.Db.AddRange(_entry, _end, _adHoc);
        await _host.Db.SaveChangesAsync();
        _version.EntryTaskDefinitionId = _entry.Id;
        _host.Db.WorkflowTaskRoutes.Add(new TaskRoute
        {
            TaskDefinitionId = _entry.Id,
            TaskOutcomeDefinitionId = _entry.ValidOutcomes.Single().Id,
            NextTaskDefinitionId = _end.Id
        });
        await _host.Db.SaveChangesAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private WorkflowTaskDefinition Definition(string name, int typeId)
    {
        var definition = new WorkflowTaskDefinition
        {
            WorkflowDefinitionVersionId = 0,
            WorkflowDefinitionVersion = _version,
            TaskTypeDefinitionId = typeId,
            DisplayName = name
        };
        definition.ValidOutcomes.Add(new TaskOutcomeDefinition
        {
            TaskDefinitionId = 0,
            TaskDefinition = definition,
            OutcomeKey = "done",
            DisplayName = "Done"
        });
        return definition;
    }

    private async Task<WorkflowRunSnapshot> StartAsync(string subject = "ordinary", bool isTest = false) =>
        (await _host.Engine.StartRunOnVersionAsync(new("Consistency", subject), _version.Id, "alice", isTest)).Unwrap();

    private async Task<TriggerDefinition> TriggerAsync(WorkflowTaskDefinition task, WorkflowEventKind eventKind,
        string mode = "capture", TriggerDispatchMode dispatch = TriggerDispatchMode.InTransaction)
    {
        var trigger = new TriggerDefinition
        {
            TaskDefinitionId = task.Id,
            TriggerKey = "test.consistency",
            Event = eventKind,
            Configuration = System.Text.Json.JsonSerializer.Serialize(new { mode }),
            DispatchMode = dispatch,
            FailurePolicy = TriggerFailurePolicy.FailOperation
        };
        _host.Db.Add(trigger);
        await _host.Db.SaveChangesAsync();
        return trigger;
    }

    private EfWorkflowRunnerClient Runner() => new(_host.Db, _host.Engine,
        _host.Provider!.GetRequiredService<IWorkflowActorResolver>(),
        _host.Provider!.GetRequiredService<IWorkflowAuthorizationPolicy>());

    private async Task<WorkflowTaskDefinition> ConvergenceAsync(WorkflowTaskDefinition? from = null)
    {
        from ??= _entry;
        from.IsForkable = true;
        var convergence = Definition("Convergence", _entry.TaskTypeDefinitionId);
        convergence.IsConvergencePoint = true;
        var rejected = new TaskOutcomeDefinition
        {
            TaskDefinitionId = 0,
            TaskDefinition = convergence,
            OutcomeKey = "rejected",
            DisplayName = "Rejected"
        };
        convergence.ValidOutcomes.Add(rejected);
        _host.Db.Add(convergence);
        await _host.Db.SaveChangesAsync();
        var existing = await _host.Db.WorkflowTaskRoutes.SingleOrDefaultAsync(route => route.TaskDefinitionId == from.Id);
        if (existing is not null) existing.NextTaskDefinitionId = convergence.Id;
        else _host.Db.Add(new TaskRoute
        {
            TaskDefinitionId = from.Id,
            TaskOutcomeDefinitionId = from.ValidOutcomes.Single().Id,
            NextTaskDefinitionId = convergence.Id
        });
        _host.Db.AddRange(new TaskRoute
        {
            TaskDefinitionId = convergence.Id,
            TaskOutcomeDefinitionId = convergence.ValidOutcomes.First().Id,
            NextTaskDefinitionId = _end.Id
        }, new TaskRoute
        {
            TaskDefinitionId = convergence.Id,
            TaskOutcomeDefinitionId = rejected.Id,
            NextTaskDefinitionId = from.Id,
            IsReworkRoute = true
        });
        await _host.Db.SaveChangesAsync();
        return convergence;
    }

    private async Task<(WorkflowDefinitionVersion Version, WorkflowTaskDefinition Entry, WorkflowTaskDefinition End)> SubWorkflowAsync(string name)
    {
        var definition = new WorkflowDefinition { Name = name, IsSubWorkflow = true };
        var version = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = 0,
            WorkflowDefinition = definition,
            SubjectType = "Consistency",
            Version = 1,
            IsPublished = true,
            IsLatest = true
        };
        var entry = new WorkflowTaskDefinition
        {
            WorkflowDefinitionVersionId = 0,
            WorkflowDefinitionVersion = version,
            TaskTypeDefinitionId = _entry.TaskTypeDefinitionId,
            DisplayName = name + " entry"
        };
        var end = new WorkflowTaskDefinition
        {
            WorkflowDefinitionVersionId = 0,
            WorkflowDefinitionVersion = version,
            TaskTypeDefinitionId = _entry.TaskTypeDefinitionId,
            DisplayName = name + " end",
            IsTerminal = true,
            CompletesAutomatically = true
        };
        var outcome = new TaskOutcomeDefinition
        {
            TaskDefinitionId = 0,
            TaskDefinition = entry,
            OutcomeKey = "done",
            DisplayName = "Done"
        };
        _host.Db.AddRange(entry, end, outcome);
        await _host.Db.SaveChangesAsync();
        version.EntryTaskDefinitionId = entry.Id;
        _host.Db.Add(new TaskRoute { TaskDefinitionId = entry.Id, TaskOutcomeDefinitionId = outcome.Id, NextTaskDefinitionId = end.Id });
        await _host.Db.SaveChangesAsync();
        return (version, entry, end);
    }

    private async Task AttachAsync(WorkflowTaskDefinition parent, WorkflowDefinitionVersion subWorkflow)
    {
        _host.Db.Add(new SubWorkflowAttachment
        {
            WorkflowDefinitionVersionId = parent.WorkflowDefinitionVersionId,
            TaskDefinitionId = parent.Id,
            SubWorkflowDefinitionId = subWorkflow.WorkflowDefinitionId,
            IsAutomatic = true,
            IsBlocking = true
        });
        await _host.Db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task A_stale_completion_cannot_create_a_second_successor()
    {
        var run = await StartAsync();
        using var scope = _host.Provider!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DemoDbContext>();
        await db.WorkflowTasks.SingleAsync(task => task.Id == run.Tasks.Single().Id);
        (await _host.Engine.CompleteTaskAsync(run.Tasks.Single().Id, "done", "alice")).Unwrap();

        var second = await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .CompleteTaskAsync(run.Tasks.Single().Id, "done", "bob");

        Assert.IsTrue(second.IsError);
        StringAssert.Contains(second.UnwrapError().Message, "changed");
        Assert.AreEqual(1, await db.WorkflowTasks.CountAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == _end.Id));
        Assert.AreEqual(1, await db.WorkflowTaskLogs.CountAsync(log => log.TaskId == run.Tasks.Single().Id && log.Action == "Completed"));
    }

    [TestMethod]
    public async Task Completion_and_cancellation_serialize_across_contexts()
    {
        var run = await StartAsync();
        using var first = _host.Provider!.CreateScope();
        using var second = _host.Provider!.CreateScope();
        var taskId = run.Tasks.Single().Id;
        var results = await Task.WhenAll(
            first.ServiceProvider.GetRequiredService<IWorkflowEngine>().CompleteTaskAsync(taskId, "done", "alice"),
            second.ServiceProvider.GetRequiredService<IWorkflowEngine>().CancelTaskAsync(taskId, "bob"));
        Assert.AreEqual(1, results.Count(result => result.IsOk));
        var task = await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(task => task.Id == taskId);
        Assert.AreEqual(task.Status == WorkflowTaskStatus.Completed ? 1 : 0,
            await _host.Db.WorkflowTasks.CountAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == _end.Id));
    }

    [TestMethod]
    public async Task A_blocking_child_and_parent_completion_cannot_both_succeed_concurrently()
    {
        var run = await StartAsync();
        using var first = _host.Provider!.CreateScope();
        using var second = _host.Provider!.CreateScope();
        var completion = first.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .CompleteTaskAsync(run.Tasks.Single().Id, "done", "alice");
        var addition = second.ServiceProvider.GetRequiredService<IWorkflowEngine>()
            .AddAdHocTaskAsync(run.Tasks.Single().Id, _adHoc.Id, "alice");
        await Task.WhenAll(completion, (Task)addition);
        Assert.AreNotEqual(completion.Result.IsOk, addition.Result.IsOk);
    }

    [TestMethod]
    public async Task Concurrent_last_branches_converge_once_under_read_committed_snapshot()
    {
        await using var connection = new SqlConnection(TestHost.BaseConnectionString);
        await connection.OpenAsync();
        var database = _host.Db.Database.GetDbConnection().Database.Replace("]", "]]", StringComparison.Ordinal);
        await using var command = new SqlCommand($"ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE", connection);
        await command.ExecuteNonQueryAsync();

        var convergence = await ConvergenceAsync();
        var run = await StartAsync();
        var fork = (await _host.Engine.ForkTaskAsync(run.Tasks.Single().Id, ["A", "B"], convergence.Id, "alice")).Unwrap();
        using var first = _host.Provider!.CreateScope();
        using var second = _host.Provider!.CreateScope();
        var results = await Task.WhenAll(
            first.ServiceProvider.GetRequiredService<IWorkflowEngine>().CompleteTaskAsync(fork.Branches[0].Id, "done", "alice"),
            second.ServiceProvider.GetRequiredService<IWorkflowEngine>().CompleteTaskAsync(fork.Branches[1].Id, "done", "alice"));
        foreach (var result in results) Assert.IsTrue(result.IsOk, result.IsError ? result.UnwrapError().Message : null);
        Assert.AreEqual(1, await _host.Db.WorkflowTasks.CountAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == convergence.Id));
    }

    [TestMethod]
    public async Task A_failed_operation_restores_the_tracker_and_discards_callbacks()
    {
        var run = await StartAsync();
        var trigger = await TriggerAsync(_entry, WorkflowEventKind.TaskCompleted, "fail");
        var task = await _host.Db.WorkflowTasks.SingleAsync(task => task.Id == run.Tasks.Single().Id);
        var document = new ChangeRequest { Title = "Pending host change", DocNumber = "PENDING", Originator = "alice", CreatorId = "alice" };
        _host.Db.Documents.Add(document);
        var callbacks = _host.Provider!.GetRequiredService<IWorkflowPostCommitActions>();
        var calls = 0;
        callbacks.Add(_ => { calls++; return Task.CompletedTask; });

        Assert.IsTrue((await _host.Engine.CompleteTaskAsync(task.Id, "done", "alice")).IsError);
        Assert.AreEqual(WorkflowTaskStatus.NotStarted, task.Status);
        Assert.AreEqual(EntityState.Added, _host.Db.Entry(document).State);
        Assert.AreEqual(0, document.Id);
        Assert.IsFalse(await _host.Db.Documents.AsNoTracking().AnyAsync(doc => doc.DocNumber == "PENDING"));
        Assert.AreEqual(0, calls);
        Assert.IsFalse(await _host.Db.WorkflowTriggerExecutions.AnyAsync());
        trigger.IsActive = false;
        _probe.Fail = false;

        (await _host.Engine.CompleteTaskAsync(task.Id, "done", "alice")).Unwrap();
        Assert.AreEqual(1, calls);
        Assert.IsTrue(await _host.Db.Documents.AnyAsync(doc => doc.DocNumber == "PENDING"));
        Assert.AreEqual(1, await _host.Db.WorkflowTasks.CountAsync(candidate => candidate.WorkflowRunId == run.Id && candidate.TaskDefinitionId == _end.Id));
    }

    [TestMethod]
    public async Task A_caught_nested_failure_rolls_back_to_its_savepoint_only()
    {
        await _host.Db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await _host.Db.Database.BeginTransactionAsync();
            var definition = new WorkflowDefinition { Name = "Host pending" };
            _host.Db.Add(definition);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => WorkflowTransaction.ExecuteAsync(_host.Db, async token =>
            {
                definition.Name = "Rolled back";
                await _host.Db.SaveChangesAsync(token);
                throw new InvalidOperationException("fail");
            }));
            Assert.AreEqual("Host pending", definition.Name);
            Assert.AreEqual(EntityState.Added, _host.Db.Entry(definition).State);
            await _host.Db.SaveChangesAsync();
            await transaction.CommitAsync();
        });
        Assert.IsTrue(await _host.Db.WorkflowDefinitions.AnyAsync(definition => definition.Name == "Host pending"));
    }

    [TestMethod]
    public async Task Nested_trigger_actions_do_not_drop_automatic_completion()
    {
        _end.CompletesAutomatically = true;
        await TriggerAsync(_end, WorkflowEventKind.TaskCreated, "assign");
        var run = await StartAsync();
        (await _host.Engine.CompleteTaskAsync(run.Tasks.Single().Id, "done", "alice")).Unwrap();
        Assert.AreEqual(WorkflowRunStatus.Completed, (await _host.Engine.GetRunAsync(run.Id)).Unwrap().Status);
    }

    [TestMethod]
    public async Task Ad_hoc_definitions_are_scoped_to_the_parent_version()
    {
        var foreign = await _host.Db.WorkflowTaskDefinitions.FirstAsync(definition => definition.IsAdHoc && definition.Id != _adHoc.Id);
        var run = await StartAsync();
        Assert.IsTrue((await _host.Engine.AddAdHocTaskAsync(run.Tasks.Single().Id, foreign.Id, "alice")).IsError);
        Assert.AreEqual(1, await _host.Db.WorkflowTasks.CountAsync(task => task.WorkflowRunId == run.Id));
    }

    [TestMethod]
    public async Task A_closed_parent_cannot_acquire_new_ad_hoc_work()
    {
        var run = await StartAsync();
        var parentId = run.Tasks.Single().Id;
        (await _host.Engine.CompleteTaskAsync(parentId, "done", "alice")).Unwrap();
        Assert.IsTrue((await _host.Engine.AddAdHocTaskAsync(parentId, _adHoc.Id, "alice")).IsError);
        var end = await _host.Db.WorkflowTasks.SingleAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == _end.Id);
        (await _host.Engine.CompleteTaskAsync(end.Id, "done", "alice")).Unwrap();
        Assert.IsTrue((await _host.Engine.AddAdHocTaskAsync(parentId, _adHoc.Id, "alice")).IsError);
    }

    [TestMethod]
    public async Task A_default_route_is_used_only_when_no_ordinary_route_matches()
    {
        var route = await _host.Db.WorkflowTaskRoutes.SingleAsync(route => route.TaskDefinitionId == _entry.Id);
        route.IsDefault = true;
        _host.Db.Add(new TaskRoute
        {
            TaskDefinitionId = _entry.Id,
            TaskOutcomeDefinitionId = route.TaskOutcomeDefinitionId,
            NextTaskDefinitionId = _end.Id,
            ConditionKey = "consistency.matches"
        });
        await _host.Db.SaveChangesAsync();
        var run = await StartAsync();
        (await _host.Engine.CompleteTaskAsync(run.Tasks.Single().Id, "done", "alice")).Unwrap();
        Assert.AreEqual(1, await _host.Db.WorkflowTasks.CountAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == _end.Id));
    }

    [TestMethod]
    public async Task A_missing_route_refuses_completion_without_poisoning_the_next_operation()
    {
        var route = await _host.Db.WorkflowTaskRoutes.SingleAsync(route => route.TaskDefinitionId == _entry.Id);
        route.ConditionKey = "unregistered";
        await _host.Db.SaveChangesAsync();
        var run = await StartAsync();
        var result = await _host.Engine.CompleteTaskAsync(run.Tasks.Single().Id, "done", "alice");
        Assert.IsTrue(result.IsError);
        Assert.AreEqual(WorkflowTaskStatus.NotStarted, (await _host.Engine.GetRunAsync(run.Id)).Unwrap().Tasks.Single().Status);
        route.ConditionKey = null;
        await _host.Db.SaveChangesAsync();
        (await _host.Engine.CompleteTaskAsync(run.Tasks.Single().Id, "done", "alice")).Unwrap();
    }

    [TestMethod]
    public async Task Custom_events_dispatch_the_matching_subscription()
    {
        await TriggerAsync(_entry, WorkflowEventKind.TaskCreated, "raise");
        var matching = await TriggerAsync(_entry, WorkflowEventKind.Custom);
        matching.CustomEventName = "ready";
        var other = await TriggerAsync(_entry, WorkflowEventKind.Custom);
        other.CustomEventName = "other";
        await _host.Db.SaveChangesAsync();
        var run = await StartAsync();
        Assert.AreEqual(1, _probe.Contexts.Count(context => context.Event == WorkflowEventKind.Custom));
        Assert.AreEqual("{}", await new WorkflowVariables(_host.Db, run.Id).GetAsync("event:ready"));
    }

    [TestMethod]
    public async Task Outbox_delivery_preserves_each_assignment_event()
    {
        await TriggerAsync(_entry, WorkflowEventKind.TaskAssigned, dispatch: TriggerDispatchMode.AfterCommit);
        var run = await StartAsync();
        var taskId = run.Tasks.Single().Id;
        (await _host.Engine.ReassignTaskAsync(taskId, new("bob", "A"), "alice")).Unwrap();
        (await _host.Engine.ReassignTaskAsync(taskId, new("carol", "A"), "alice")).Unwrap();
        Assert.AreEqual(2, await _host.Outbox.ProcessPendingAsync());
        CollectionAssert.AreEqual(new[] { "bob", "carol" }, _probe.Contexts.Select(context => context.Task.AssignedToActorId).ToArray());
        Assert.AreEqual(0, await _host.Outbox.ProcessPendingAsync());
    }

    [TestMethod]
    public async Task Outbox_claims_are_exclusive_even_when_both_workers_poll_together()
    {
        await TriggerAsync(_entry, WorkflowEventKind.TaskCreated, dispatch: TriggerDispatchMode.AfterCommit);
        await StartAsync();
        using var first = _host.Provider!.CreateScope();
        using var second = _host.Provider!.CreateScope();
        _probe.Candidates.Enabled = true;
        var results = await Task.WhenAll(
            first.ServiceProvider.GetRequiredService<IWorkflowOutboxProcessor>().ProcessPendingAsync(),
            second.ServiceProvider.GetRequiredService<IWorkflowOutboxProcessor>().ProcessPendingAsync());
        Assert.AreEqual(1, results.Sum());
        Assert.HasCount(1, _probe.Contexts);
        Assert.AreEqual(1, await _host.Db.WorkflowTriggerExecutions.CountAsync());
    }

    [TestMethod]
    public async Task A_previously_successful_outbox_execution_is_not_repeated()
    {
        await TriggerAsync(_entry, WorkflowEventKind.TaskCreated, dispatch: TriggerDispatchMode.AfterCommit);
        await StartAsync();
        Assert.AreEqual(1, await _host.Outbox.ProcessPendingAsync());
        await _host.Db.WorkflowOutbox.ExecuteUpdateAsync(update => update.SetProperty(message => message.Status, OutboxStatus.Pending));
        Assert.AreEqual(1, await _host.Outbox.ProcessPendingAsync());
        Assert.HasCount(1, _probe.Contexts);
    }

    [TestMethod]
    public async Task Run_variables_are_upserted_across_contexts_and_keep_task_shadowing()
    {
        var run = await StartAsync();
        using var first = _host.Provider!.CreateScope();
        using var second = _host.Provider!.CreateScope();
        var firstDb = first.ServiceProvider.GetRequiredService<DemoDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<DemoDbContext>();
        await Task.WhenAll(new WorkflowVariables(firstDb, run.Id).SetAsync("reviewer", "alice"),
            new WorkflowVariables(secondDb, run.Id).SetAsync("reviewer", "bob"));
        Assert.AreEqual(1, await _host.Db.WorkflowVariables.CountAsync(variable => variable.WorkflowRunId == run.Id && variable.TaskId == null));
        var taskVariables = new WorkflowVariables(_host.Db, run.Id, run.Tasks.Single().Id);
        await taskVariables.SetAsync("reviewer", "local");
        Assert.AreEqual("local", await taskVariables.GetAsync("reviewer"));
        Assert.AreNotEqual("local", await new WorkflowVariables(_host.Db, run.Id).GetAsync("reviewer"));
    }

    [TestMethod]
    public async Task Automatic_terminal_completion_and_run_closure_are_previewed_without_execution()
    {
        _end.CompletesAutomatically = true;
        await TriggerAsync(_end, WorkflowEventKind.TaskCompleted);
        await TriggerAsync(_end, WorkflowEventKind.RunCompleted);
        var run = await StartAsync();
        var actions = (await _host.Engine.GetCompletionPreviewAsync(run.Tasks.Single().Id, "done", "alice")).Unwrap();
        Assert.HasCount(2, actions);
        Assert.IsEmpty(_probe.Contexts);
        Assert.AreEqual(1, await _host.Db.WorkflowTasks.CountAsync(task => task.WorkflowRunId == run.Id));
        (await _host.Engine.CompleteTaskAsync(run.Tasks.Single().Id, "done", "alice")).Unwrap();
        Assert.HasCount(2, _probe.Contexts);
    }

    [TestMethod]
    public async Task Independent_branches_and_sub_workflows_have_independent_progress()
    {
        var run = await StartAsync();
        var task = run.Tasks.Single();
        var first = task with { Id = 1, AssignedBranchKey = "A", Status = WorkflowTaskStatus.Completed };
        var second = task with { Id = 2, AssignedBranchKey = "B" };
        Assert.AreEqual(50d, WorkflowProgress.Calculate(run with { Tasks = [first, second] }, null));
        Assert.AreEqual(50d, WorkflowProgress.Calculate(run with { Tasks = [first with { Status = WorkflowTaskStatus.NotStarted }, second with { Status = WorkflowTaskStatus.Completed }] }, null));
        Assert.AreEqual(50d, WorkflowProgress.Calculate(run with { Tasks = [first with { AssignedBranchKey = null, SubWorkflowInstanceId = 1 }, second with { AssignedBranchKey = null, SubWorkflowInstanceId = 2 }] }, null));
    }

    [TestMethod]
    public async Task Discarding_a_test_run_removes_its_outbox_too()
    {
        await TriggerAsync(_entry, WorkflowEventKind.TaskCreated, dispatch: TriggerDispatchMode.AfterCommit);
        var run = await StartAsync(isTest: true);
        var result = await Runner().DeleteTestRunAsync(run.Id, "alice");
        Assert.IsTrue(result.Success, result.Error);
        Assert.IsFalse(await _host.Db.WorkflowOutbox.AnyAsync());
        Assert.AreEqual(0, await _host.Outbox.ProcessPendingAsync());
    }

    [TestMethod]
    public async Task Reminder_batches_rotate_past_future_null_and_failing_resolvers()
    {
        _entry.ReminderLeadTimeMinutes = 60;
        await _host.Db.SaveChangesAsync();
        await StartAsync("null");
        await StartAsync("failure");
        await StartAsync("future");
        var soon = await StartAsync("soon");
        Assert.AreEqual(0, await _host.Deadlines!.ProcessDueAsync(2));
        Assert.AreEqual(1, await _host.Deadlines.ProcessDueAsync(2));
        Assert.IsNotNull(await _host.Db.WorkflowTasks.AsNoTracking().Where(task => task.Id == soon.Tasks.Single().Id)
            .Select(task => task.ReminderSentAt).SingleAsync());
    }

    [TestMethod]
    public async Task Sub_workflow_entry_tasks_spawn_their_own_automatic_attachments()
    {
        var first = await SubWorkflowAsync("First");
        var second = await SubWorkflowAsync("Second");
        await AttachAsync(_entry, first.Version);
        await AttachAsync(first.Entry, second.Version);
        var run = await StartAsync();
        Assert.AreEqual(2, await _host.Db.WorkflowSubWorkflowInstances.CountAsync(instance => instance.WorkflowRunId == run.Id));
        Assert.AreEqual(3, await _host.Db.WorkflowTasks.CountAsync(task => task.WorkflowRunId == run.Id));
    }

    [TestMethod]
    public async Task Automatic_attachment_cycles_fail_atomically()
    {
        var sub = await SubWorkflowAsync("Cycle");
        await AttachAsync(_entry, sub.Version);
        await AttachAsync(sub.Entry, sub.Version);
        var result = await _host.Engine.StartRunOnVersionAsync(new("Consistency", "cycle"), _version.Id, "alice");
        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "cycle");
        Assert.IsFalse(await _host.Db.WorkflowRuns.AnyAsync(run => run.WorkflowDefinitionVersionId == _version.Id));
        Assert.IsFalse(await _host.Db.WorkflowSubWorkflowInstances.AnyAsync());
    }

    [TestMethod]
    public async Task Added_branches_get_creation_events_and_automatic_attachments()
    {
        var convergence = await ConvergenceAsync();
        var run = await StartAsync();
        var fork = (await _host.Engine.ForkTaskAsync(run.Tasks.Single().Id, ["A", "B"], convergence.Id, "alice")).Unwrap();
        var sub = await SubWorkflowAsync("Added branch delegation");
        await AttachAsync(_entry, sub.Version);
        await TriggerAsync(_entry, WorkflowEventKind.TaskCreated);
        var added = (await _host.Engine.AddBranchToForkAsync(fork.ForkGroupId, ["C"], "alice")).Unwrap();
        var branchId = _probe.Contexts.Single().Task.Id;
        Assert.AreEqual("C", _probe.Contexts.Single().Task.AssignedBranchKey);
        Assert.IsTrue(await _host.Db.WorkflowSubWorkflowInstances.AnyAsync(instance => instance.ParentTaskId == branchId));
    }

    [TestMethod]
    public async Task Selective_rework_stays_visible_and_returns_to_the_actual_branch_worker()
    {
        var convergence = await ConvergenceAsync();
        var run = await StartAsync();
        var fork = (await _host.Engine.ForkTaskAsync(run.Tasks.Single().Id, ["A", "B"], convergence.Id, "alice")).Unwrap();
        foreach (var branch in fork.Branches)
            (await _host.Engine.CompleteTaskAsync(branch.Id, "done", "stand-in")).Unwrap();
        var review = await _host.Db.WorkflowTasks.SingleAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == convergence.Id);
        (await _host.Engine.CompleteWithSelectiveRejectionAsync(review.Id, "rejected", ["A"], "alice")).Unwrap();
        var rework = await _host.Db.WorkflowTasks.SingleAsync(task => task.WorkflowRunId == run.Id && task.Status == WorkflowTaskStatus.NotStarted);
        Assert.AreEqual("stand-in", rework.AssignedToActorId);
        var current = (await _host.Engine.GetCurrentStateForSubjectsAsync([run.Subject], ForkView.Origin)).Unwrap();
        Assert.IsTrue(current.ContainsKey(run.Subject));
        Assert.AreEqual(rework.Id, current[run.Subject].Single().CurrentTasks.Single().Task.Id);
    }

    [TestMethod]
    public async Task Selective_rejection_refuses_ordinary_false_and_duplicate_branch_routes()
    {
        var convergence = await ConvergenceAsync();
        var run = await StartAsync();
        var fork = (await _host.Engine.ForkTaskAsync(run.Tasks.Single().Id, ["A", "B"], convergence.Id, "alice")).Unwrap();
        foreach (var branch in fork.Branches) (await _host.Engine.CompleteTaskAsync(branch.Id, "done", "alice")).Unwrap();
        var review = await _host.Db.WorkflowTasks.SingleAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == convergence.Id);
        Assert.IsTrue((await _host.Engine.CompleteWithSelectiveRejectionAsync(review.Id, "done", ["A"], "alice")).IsError);
        Assert.IsTrue((await _host.Engine.CompleteWithSelectiveRejectionAsync(review.Id, "rejected", ["A", "A"], "alice")).IsError);
        var route = await _host.Db.WorkflowTaskRoutes.SingleAsync(route => route.TaskDefinitionId == convergence.Id && route.IsReworkRoute);
        route.ConditionKey = "unregistered";
        await _host.Db.SaveChangesAsync();
        Assert.IsTrue((await _host.Engine.CompleteWithSelectiveRejectionAsync(review.Id, "rejected", ["A"], "alice")).IsError);
        Assert.AreEqual(WorkflowTaskStatus.NotStarted, (await _host.Db.WorkflowTasks.AsNoTracking().SingleAsync(task => task.Id == review.Id)).Status);
    }

    [TestMethod]
    public async Task A_blocking_ad_hoc_fork_keeps_its_parent_through_convergence_and_rework()
    {
        var convergence = await ConvergenceAsync(_adHoc);
        var run = await StartAsync();
        var parentId = run.Tasks.Single().Id;
        var child = (await _host.Engine.AddAdHocTaskAsync(parentId, _adHoc.Id, "alice")).Unwrap();
        var fork = (await _host.Engine.ForkTaskAsync(child.Id, ["A", "B"], convergence.Id, "alice")).Unwrap();
        foreach (var branch in fork.Branches) (await _host.Engine.CompleteTaskAsync(branch.Id, "done", "alice")).Unwrap();
        var review = await _host.Db.WorkflowTasks.SingleAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == convergence.Id);
        Assert.AreEqual(parentId, review.ParentTaskId);
        convergence.IsBlocking = true;
        await _host.Db.SaveChangesAsync();
        Assert.IsTrue((await _host.Engine.CompleteTaskAsync(parentId, "done", "alice")).IsError);
        (await _host.Engine.CompleteWithSelectiveRejectionAsync(review.Id, "rejected", ["A"], "alice")).Unwrap();
        var rework = await _host.Db.WorkflowTasks.SingleAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == _adHoc.Id && task.Status == WorkflowTaskStatus.NotStarted);
        Assert.AreEqual(parentId, rework.ParentTaskId);
        Assert.IsTrue((await _host.Engine.CompleteTaskAsync(parentId, "done", "alice")).IsError);
    }

    [TestMethod]
    public async Task Outbox_convergence_events_keep_the_explicit_branch_context()
    {
        var convergence = await ConvergenceAsync();
        await TriggerAsync(convergence, WorkflowEventKind.ForkConverged, dispatch: TriggerDispatchMode.AfterCommit);
        var run = await StartAsync();
        var fork = (await _host.Engine.ForkTaskAsync(run.Tasks.Single().Id, ["A", "B"], convergence.Id, "alice")).Unwrap();
        foreach (var branch in fork.Branches) (await _host.Engine.CompleteTaskAsync(branch.Id, "done", "alice")).Unwrap();
        await _host.Outbox.ProcessPendingAsync();
        var context = _probe.Contexts.Single();
        Assert.IsNull(context.Task.ForkGroupId);
        Assert.IsNotNull(context.Branch);
        Assert.AreEqual(fork.ForkGroupId, context.Branch.ForkGroupId);
        CollectionAssert.AreEquivalent(new[] { "A", "B" }, context.Branch.RejectedBranchKeys.ToArray());
    }

    [TestMethod]
    public async Task Automatic_sub_workflow_completion_and_the_parent_event_are_previewed()
    {
        var sub = await SubWorkflowAsync("Preview delegation");
        await AttachAsync(_entry, sub.Version);
        await TriggerAsync(sub.End, WorkflowEventKind.TaskCompleted);
        await TriggerAsync(_entry, WorkflowEventKind.SubWorkflowCompleted);
        var run = await StartAsync();
        var entry = await _host.Db.WorkflowTasks.SingleAsync(task => task.WorkflowRunId == run.Id && task.TaskDefinitionId == sub.Entry.Id);
        var preview = (await _host.Engine.GetCompletionPreviewAsync(entry.Id, "done", "alice")).Unwrap();
        Assert.HasCount(2, preview);
        Assert.IsEmpty(_probe.Contexts);
        (await _host.Engine.CompleteTaskAsync(entry.Id, "done", "alice")).Unwrap();
        Assert.HasCount(2, _probe.Contexts);
    }

    [TestMethod]
    public async Task Concurrent_publishing_leaves_exactly_one_latest_version()
    {
        foreach (var number in new[] { 2, 3 })
        {
            var version = new WorkflowDefinitionVersion
            {
                WorkflowDefinitionId = _version.WorkflowDefinitionId,
                Version = number,
                SubjectType = "Consistency"
            };
            var entry = new WorkflowTaskDefinition
            {
                WorkflowDefinitionVersionId = 0,
                WorkflowDefinitionVersion = version,
                TaskTypeDefinitionId = _entry.TaskTypeDefinitionId,
                DisplayName = "Publish terminal",
                IsTerminal = true
            };
            entry.ValidOutcomes.Add(new TaskOutcomeDefinition
            {
                TaskDefinitionId = 0,
                TaskDefinition = entry,
                OutcomeKey = "done",
                DisplayName = "Done"
            });
            _host.Db.Add(entry);
            await _host.Db.SaveChangesAsync();
        }

        using var first = _host.Provider!.CreateScope();
        using var second = _host.Provider!.CreateScope();
        async Task PublishAsync(IServiceProvider provider, int number)
        {
            var db = provider.GetRequiredService<DemoDbContext>();
            var version = await db.WorkflowDefinitionVersions.Include(version => version.Tasks).ThenInclude(task => task.ValidOutcomes)
                .SingleAsync(version => version.WorkflowDefinitionId == _version.WorkflowDefinitionId && version.Version == number);
            Assert.IsEmpty(await db.PublishWithEntryTaskAsync(version, version.Tasks.Single()));
        }

        await Task.WhenAll(PublishAsync(first.ServiceProvider, 2), PublishAsync(second.ServiceProvider, 3));
        Assert.AreEqual(1, await _host.Db.WorkflowDefinitionVersions.CountAsync(version => version.WorkflowDefinitionId == _version.WorkflowDefinitionId && version.IsPublished && version.IsLatest && !version.IsArchived));
    }

    [TestMethod]
    public async Task A_test_run_with_an_active_outbox_lease_cannot_be_discarded()
    {
        await TriggerAsync(_entry, WorkflowEventKind.TaskCreated, dispatch: TriggerDispatchMode.AfterCommit);
        var run = await StartAsync(isTest: true);
        await _host.Db.WorkflowOutbox.ExecuteUpdateAsync(update => update.SetProperty(message => message.LeaseId, Guid.NewGuid())
            .SetProperty(message => message.LeasedUntil, DateTime.UtcNow.AddMinutes(1)));
        Assert.IsFalse((await Runner().DeleteTestRunAsync(run.Id, "alice")).Success);
        Assert.IsTrue(await _host.Db.WorkflowRuns.AnyAsync(candidate => candidate.Id == run.Id));
        Assert.IsTrue(await _host.Db.WorkflowOutbox.AnyAsync());
    }

    [TestMethod]
    public async Task A_mid_teardown_failure_restores_every_deleted_record()
    {
        await TriggerAsync(_entry, WorkflowEventKind.TaskCreated, dispatch: TriggerDispatchMode.AfterCommit);
        var run = await StartAsync(isTest: true);
        await new WorkflowVariables(_host.Db, run.Id).SetAsync("keep", "value");
        _probe.DeleteFailure.Enabled = true;
        var result = await Runner().DeleteTestRunAsync(run.Id, "alice");
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "teardown failure");
        Assert.IsTrue(await _host.Db.WorkflowRuns.AnyAsync(candidate => candidate.Id == run.Id));
        Assert.IsTrue(await _host.Db.WorkflowOutbox.AnyAsync(message => message.TaskId == run.Tasks.Single().Id));
        Assert.IsTrue(await _host.Db.WorkflowVariables.AnyAsync(variable => variable.WorkflowRunId == run.Id));
        Assert.IsTrue(await _host.Db.WorkflowTaskLogs.AnyAsync(log => log.TaskId == run.Tasks.Single().Id));
        _probe.DeleteFailure.Enabled = false;
        Assert.IsTrue((await Runner().DeleteTestRunAsync(run.Id, "alice")).Success);
    }

    [TestMethod]
    public async Task A_transient_retry_rebuilds_saved_host_state_and_callbacks()
    {
        var options = new DbContextOptionsBuilder<DemoDbContext>()
            .UseSqlServer(_host.Db.Database.GetConnectionString())
            .ReplaceService<IExecutionStrategyFactory, RetryFactory>().Options;
        await using var db = new DemoDbContext(options);
        var pending = new WorkflowDefinition { Name = "Pending across retry" };
        db.Add(pending);
        var existing = await db.WorkflowDefinitions.SingleAsync(definition => definition.Id == _version.WorkflowDefinitionId);
        var originalName = existing.Name;
        var callbacks = new WorkflowPostCommitActions();
        var attempts = 0;
        var callbacksRun = 0;

        await WorkflowTransaction.ExecuteAsync(db, async token =>
        {
            Assert.AreEqual(EntityState.Added, db.Entry(pending).State);
            Assert.AreEqual(originalName, existing.Name);
            existing.Name = "Committed on retry";
            callbacks.Add(_ => { callbacksRun++; return Task.CompletedTask; });
            await db.SaveChangesAsync(token);
            if (++attempts == 1) throw new RetryException();
        }, postCommit: callbacks);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, callbacksRun);
        Assert.AreEqual(1, await db.WorkflowDefinitions.CountAsync(definition => definition.Name == "Pending across retry"));
        Assert.AreEqual("Committed on retry", await db.WorkflowDefinitions.AsNoTracking().Where(definition => definition.Id == existing.Id).Select(definition => definition.Name).SingleAsync());
    }

    private sealed class Probe
    {
        public ConcurrentQueue<WorkflowTriggerContext> Contexts { get; } = new();
        public bool Fail { get; set; } = true;
        public CandidateBarrier Candidates { get; } = new();
        public DeleteFailureInterceptor DeleteFailure { get; } = new();
    }

    private sealed class DeleteFailureInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<WorkflowRun>().Any(entry => entry.State == EntityState.Deleted))
                throw new InvalidOperationException("Injected teardown failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RetryException : Exception;

    private sealed class RetryFactory(ExecutionStrategyDependencies dependencies) : IExecutionStrategyFactory
    {
        public IExecutionStrategy Create() => new RetryStrategy(dependencies);
    }

    private sealed class RetryStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is RetryException;
    }

    private sealed class CandidateBarrier : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        private int _readers;
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("FROM [TaskRouterOutbox]", StringComparison.Ordinal)
                && command.CommandText.Contains("TOP(", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _readers) == 2) _bothRead.TrySetResult();
                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }

            return result;
        }
    }

    private sealed class ProbeTrigger(Probe probe) : IWorkflowTrigger
    {
        public string Key => "test.consistency";
        public TriggerDescriptor Describe() => new(Key, "Consistency probe", "Test", Enum.GetValues<WorkflowEventKind>(), []);
        public Task<string?> DescribeActionAsync(WorkflowTriggerContext context, CancellationToken ct = default) => Task.FromResult<string?>("Probe " + context.Event);
        public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            switch (context.Config.GetString("mode"))
            {
                case "fail" when probe.Fail: throw new InvalidOperationException("Deliberate trigger failure");
                case "assign": await context.Actions.AssignTaskAsync(context.Task.Id, new("bob", null), ct); break;
                case "raise": await context.Actions.RaiseEventAsync("ready", "{}", ct); break;
                default: probe.Contexts.Enqueue(context); break;
            }
        }
    }

    private sealed class MatchingCondition : IRouteConditionEvaluator
    {
        public string ConditionKey => "consistency.matches";
        public bool Evaluate(WorkflowTaskSnapshot task, IReadOnlyDictionary<string, string?> variables) => true;
    }

    private sealed class DueDates : IWorkflowDueDateResolver
    {
        public Task<DateTime?> ResolveAsync(WorkflowSubject subject, WorkflowTaskSnapshot task, CancellationToken ct = default) =>
            subject.SubjectId switch
            {
                "soon" => Task.FromResult<DateTime?>(DateTime.UtcNow.AddMinutes(30)),
                "future" => Task.FromResult<DateTime?>(DateTime.UtcNow.AddDays(1)),
                "failure" => throw new InvalidOperationException("Resolver unavailable"),
                _ => Task.FromResult<DateTime?>(null)
            };
    }
}
