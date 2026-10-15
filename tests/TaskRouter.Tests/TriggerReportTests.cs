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
        var execution = await CompleteWithTriggerAsync(
            ReportingTrigger.TriggerKey, TriggerDispatchMode.AfterCommit, drain: true);

        Assert.AreEqual(TriggerExecutionStatus.Succeeded, execution.Status, execution.Error);
        Assert.AreEqual("did the thing", execution.Detail);
    }

    [TestMethod]
    public async Task A_failing_after_commit_trigger_leaves_detail_empty()
    {
        var execution = await CompleteWithTriggerAsync(
            FailingReportingTrigger.TriggerKey, TriggerDispatchMode.AfterCommit, drain: true);

        Assert.AreEqual(TriggerExecutionStatus.Failed, execution.Status);
        Assert.IsNull(execution.Detail);
    }

    [TestMethod]
    public async Task Now_renders_as_a_round_trip_utc_timestamp()
    {
        var probe = await RenderAsync("{now}") ?? string.Empty;

        var parsed = DateTime.ParseExact(
            probe, "O", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

        Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
    }

    [TestMethod]
    public async Task An_oversized_day_offset_passes_through_literally()
    {
        Assert.AreEqual("{today+999999d}|{today+99999999999d}", await RenderAsync("{today+999999d}|{today+99999999999d}"));
    }

    [TestMethod]
    public async Task Substituted_text_is_not_rendered_again()
    {
        await SetRunVariableAsync("echo", "{actor}");

        Assert.AreEqual("{actor}", await RenderAsync("{var:echo}"));
    }

    [TestMethod]
    public async Task A_variable_name_is_trimmed()
    {
        await SetRunVariableAsync("colour", "blue");

        Assert.AreEqual("blue", await RenderAsync("{var: colour}"));
    }

    [TestMethod]
    public async Task A_later_trigger_sees_a_variable_an_earlier_one_set_on_the_same_event()
    {
        var (runId, entryTaskId, entryDefinitionId) = await StartAsync();

        AddSetVariable(entryDefinitionId, 0, "first", "one");
        AddSetVariable(entryDefinitionId, 1, "second", "{var:first}");

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        Assert.AreEqual("one", await ReadVariableAsync(runId, "second"));
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

        // Read the date on both sides of the call so a run across midnight cannot fail the test.
        var before = DateTime.UtcNow.Date;
        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();
        var after = DateTime.UtcNow.Date;

        var probe = await ReadVariableAsync(runId, "probe");

        string Day(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string Expected(DateTime t) =>
            $"user-originator|{Day(t)}|{Day(t.AddDays(2))}|{Day(t.AddDays(-1))}|blue||{{nope}}";

        Assert.IsTrue(probe == Expected(before) || probe == Expected(after), probe);
    }

    private void AddSetVariable(int definitionId, int order, string name, string value) =>
        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = definitionId,
            TriggerKey = BuiltInTriggerKeys.SetVariable,
            Event = WorkflowEventKind.TaskCompleted,
            Order = order,
            Configuration = System.Text.Json.JsonSerializer.Serialize(new { name, value }),
            CreatorId = "system",
            ModifierId = "system"
        });

    private async Task<string?> ReadVariableAsync(int runId, string name) =>
        await _host.Db.WorkflowVariables.AsNoTracking()
            .Where(v => v.WorkflowRunId == runId && v.Name == name)
            .Select(v => v.Value)
            .SingleAsync();

    private async Task SetRunVariableAsync(string name, string value)
    {
        _pendingVariables.Add((name, value));
        await Task.CompletedTask;
    }

    private readonly List<(string Name, string Value)> _pendingVariables = [];

    /// <summary>Renders a SetVariable value through the real engine and returns what was stored.</summary>
    private async Task<string?> RenderAsync(string template)
    {
        var (runId, entryTaskId, entryDefinitionId) = await StartAsync();

        foreach (var (name, value) in _pendingVariables)
        {
            _host.Db.WorkflowVariables.Add(new WorkflowVariable
            {
                WorkflowRunId = runId,
                Name = name,
                Value = value,
                CreatorId = "system",
                ModifierId = "system"
            });
        }

        AddSetVariable(entryDefinitionId, 0, "probe", template);

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        return await ReadVariableAsync(runId, "probe");
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
