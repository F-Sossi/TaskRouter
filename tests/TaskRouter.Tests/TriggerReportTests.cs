using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// A trigger's own account of what it did reaches the execution row — inline and from the
/// outbox, on success and on failure — and the tokens a field value may use render.
/// </summary>
[TestClass]
public class TriggerReportTests
{
    private TestHost _host = null!;
    private int _workflowId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync(withTriggers: true, configure: s =>
        {
            s.AddScoped<IWorkflowTrigger, ReportingTrigger>();
            s.AddScoped<IWorkflowTrigger, FailingReportingTrigger>();
        });

        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task An_inline_trigger_report_lands_on_its_execution()
    {
        var execution = await CompleteWithTriggerAsync(ReportingTrigger.TriggerKey, TriggerDispatchMode.InTransaction);

        Assert.AreEqual(TriggerExecutionStatus.Succeeded, execution.Status, execution.Error);
        Assert.AreEqual("did the thing", execution.Detail);
    }

    [TestMethod]
    public async Task A_failing_trigger_still_reports_how_far_it_got()
    {
        var execution = await CompleteWithTriggerAsync(FailingReportingTrigger.TriggerKey, TriggerDispatchMode.InTransaction);

        Assert.AreEqual(TriggerExecutionStatus.Failed, execution.Status);
        Assert.AreEqual("got halfway", execution.Detail);
    }

    [TestMethod]
    public async Task An_after_commit_trigger_report_lands_on_its_execution()
    {
        await CompleteWithTriggerAsync(ReportingTrigger.TriggerKey, TriggerDispatchMode.AfterCommit, drain: true);

        var execution = await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Where(e => e.Detail != null)
            .SingleAsync();

        Assert.AreEqual("did the thing", execution.Detail);
    }

    [TestMethod]
    public async Task Value_tokens_render_actor_dates_and_variables()
    {
        var (runId, entryTaskId, entryDefinitionId) = await StartAsync();

        _host.Db.WorkflowVariables.Add(new WorkflowVariable
        {
            WorkflowRunId = runId,
            Name = "colour",
            Value = "blue",
            CreatorId = "system",
            ModifierId = "system"
        });

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefinitionId,
            TriggerKey = BuiltInTriggerKeys.SetVariable,
            Event = WorkflowEventKind.TaskCompleted,
            Configuration = """{"name":"probe","value":"{actor}|{today}|{today+2d}|{today-1d}|{var:colour}|{var:missing}|{nope}"}""",
            CreatorId = "system",
            ModifierId = "system"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var probe = await _host.Db.WorkflowVariables.AsNoTracking()
            .Where(v => v.WorkflowRunId == runId && v.Name == "probe")
            .Select(v => v.Value)
            .SingleAsync();

        var today = DateTime.UtcNow.Date;
        string Day(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        Assert.AreEqual(
            $"user-originator|{Day(today)}|{Day(today.AddDays(2))}|{Day(today.AddDays(-1))}|blue||{{nope}}",
            probe);
    }

    private async Task<(int RunId, int EntryTaskId, int EntryDefinitionId)> StartAsync()
    {
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        var entryTaskId = run.Tasks.Single().Id;

        var definitionId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == entryTaskId).Select(t => t.TaskDefinitionId).SingleAsync();

        return (run.Id, entryTaskId, definitionId);
    }

    private async Task<TriggerExecution> CompleteWithTriggerAsync(
        string key, TriggerDispatchMode mode, bool drain = false)
    {
        var (_, entryTaskId, entryDefinitionId) = await StartAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefinitionId,
            TriggerKey = key,
            Event = WorkflowEventKind.TaskCompleted,
            DispatchMode = mode,
            FailurePolicy = TriggerFailurePolicy.LogAndContinue,
            CreatorId = "system",
            ModifierId = "system"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        if (drain)
        {
            await _host.Outbox.ProcessPendingAsync();
        }

        var keyDefinitionId = await _host.Db.WorkflowTriggerDefinitions
            .Where(d => d.TriggerKey == key).Select(d => d.Id).SingleAsync();

        return await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Where(e => e.TriggerDefinitionId == keyDefinitionId)
            .SingleAsync();
    }

    private sealed class ReportingTrigger : IWorkflowTrigger
    {
        public const string TriggerKey = "test.report";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() =>
            new(Key, "Report", "Reports.", Enum.GetValues<WorkflowEventKind>(), []);

        public Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            context.Report.Detail = "did the thing";
            return Task.CompletedTask;
        }
    }

    private sealed class FailingReportingTrigger : IWorkflowTrigger
    {
        public const string TriggerKey = "test.report-fail";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() =>
            new(Key, "Report and fail", "Reports, then fails.", Enum.GetValues<WorkflowEventKind>(), []);

        public Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            context.Report.Detail = "got halfway";
            throw new InvalidOperationException("stopped");
        }
    }
}
