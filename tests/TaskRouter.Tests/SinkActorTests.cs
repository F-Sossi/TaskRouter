using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// Both sinks are told who acted.
///
/// The engine has always known — every operation that fires a trigger took an actor id,
/// and <c>WorkflowTriggerContext.ActorId</c> is required — but the two sinks were handed
/// only the payload. That is fine until a host's storage is audited: a row written on the
/// engine's behalf has to name somebody, and a host with no actor either invents one or
/// leaves the write unattributed. The integration spike hit exactly that.
/// </summary>
[TestClass]
public class SinkActorTests
{
    private sealed class RecordingProgressSink : IWorkflowProgressSink
    {
        public List<(string ActorId, string? BranchKey, double Percent)> Reports { get; } = [];

        public Task ReportAsync(
            WorkflowSubject subject,
            string? branchKey,
            double percentComplete,
            string actorId,
            CancellationToken ct = default)
        {
            lock (Reports) { Reports.Add((actorId, branchKey, percentComplete)); }
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers a date inside the reminder lead time, so the sweep has something
    /// to find. The engine stamps it when the task is created; setting DueDate afterwards
    /// is not the same thing and the sweep will not see it.</summary>
    private sealed class SoonDueDateResolver : IWorkflowDueDateResolver
    {
        public Task<DateTime?> ResolveAsync(
            WorkflowSubject subject,
            WorkflowTaskSnapshot task,
            CancellationToken ct = default) => Task.FromResult<DateTime?>(DateTime.UtcNow.AddHours(1));
    }

    private TestHost _host = null!;
    private RecordingProgressSink _progress = null!;
    private int _workflowId;

    [TestInitialize]
    public async Task Setup()
    {
        _progress = new RecordingProgressSink();

        _host = await TestHost.CreateAsync(
            withTriggers: true,
            configure: services => services.AddSingleton<IWorkflowProgressSink>(_progress),
            dueDates: new SoonDueDateResolver());

        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    private async Task<int> StartAsync()
    {
        var subject = new WorkflowSubject(
            nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());

        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        return run.Tasks.Single().Id;
    }

    [TestMethod]
    public async Task The_progress_sink_is_told_who_completed_the_task()
    {
        var entryTaskId = await StartAsync();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        Assert.IsNotEmpty(_progress.Reports, "The seeded workflow reports progress on completion.");
        Assert.IsTrue(
            _progress.Reports.TrueForAll(r => r.ActorId == "user-originator"),
            "Progress is attributed to whoever caused it, not to the assignee or to nobody.");
    }

    [TestMethod]
    public async Task The_notification_sink_is_told_who_caused_the_notification()
    {
        var entryTaskId = await StartAsync();

        // Completing Enter Record creates Provide Input; completing that creates PM Review,
        // which is the task carrying the notify trigger.
        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var provideInput = await _host.Db.WorkflowTasks
            .Include(t => t.TaskDefinition)
            .FirstAsync(t => t.TaskDefinition!.DisplayName == "Provide Input");

        (await _host.Engine.CompleteTaskAsync(provideInput.Id, "approved", "user-lead-c100")).Unwrap();
        _ = await _host.Outbox.ProcessPendingAsync();

        var delivered = _host.Notifications.Delivered;

        Assert.IsNotEmpty(delivered);
        Assert.IsTrue(
            delivered.All(n => n.ActorId == "user-lead-c100"),
            "A notification names the person whose action produced it.");
    }

    [TestMethod]
    public async Task The_deadline_sweep_reports_as_the_system_actor()
    {
        // The sweep has no human behind it, so a sink sees WorkflowActors.System. This is
        // the case a host has to plan for: the id is deliberately not shaped like one of
        // its own users, and a host storing it in a column with a foreign key to its user
        // table has to map it rather than write it through.
        var run = (await _host.Engine.StartRunAsync(
            new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString()),
            _workflowId,
            "user-originator")).Unwrap();

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == run.Tasks.Single().Id);

        var definition = await _host.Db.WorkflowTaskDefinitions
            .SingleAsync(d => d.Id == task.TaskDefinitionId);

        definition.ReminderLeadTimeMinutes = 120;

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = definition.Id,
            TriggerKey = BuiltInTriggerKeys.Notify,
            Event = WorkflowEventKind.TaskDueSoon,
            // InTransaction so the notification has landed by the time the sweep returns.
            DispatchMode = TriggerDispatchMode.InTransaction,
            IsActive = true,
            Configuration = """{"subject":"Due soon","body":"This is due soon."}""",
            Order = 1,
            CreatorId = "seed",
            ModifierId = "seed",
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow
        });

        await _host.Db.SaveChangesAsync();

        Assert.IsGreaterThan(0, await _host.Deadlines!.ProcessDueAsync());

        var delivered = _host.Notifications.Delivered;

        Assert.IsNotEmpty(delivered);
        Assert.IsTrue(
            delivered.All(n => n.ActorId == WorkflowActors.System),
            "Nobody acted, so the sink must be told that rather than given a plausible-looking user.");
    }
}
