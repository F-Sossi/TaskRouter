using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Triggers;

/// <summary>
/// Triggers the library ships. Every one is domain-agnostic: anything needing
/// knowledge of the host's entities is a host-registered trigger instead.
/// </summary>
public static class BuiltInTriggerKeys
{
    public const string SetVariable = "workflow.setVariable";
    public const string ReportProgress = "workflow.reportProgress";
    public const string Notify = "workflow.notify";
    public const string Webhook = "workflow.webhook";
}

/// <summary>Writes a workflow variable. The simplest useful trigger.</summary>
public sealed class SetVariableTrigger : IWorkflowTrigger
{
    public string Key => BuiltInTriggerKeys.SetVariable;

    public TriggerDescriptor Describe() => new(
        Key,
        "Set Workflow Variable",
        "Stores a value against the run, readable by later triggers and route conditions.",
        SupportedEvents: Enum.GetValues<WorkflowEventKind>(),
        Parameters:
        [
            new("name", "Variable name", TriggerParameterKind.Text, Required: true),
            new("value", "Value", TriggerParameterKind.Text,
                HelpText: "Supports {task.outcome}, {task.branch}, {task.id}, {event}."),
            new("scope", "Scope", TriggerParameterKind.Choice,
                DefaultValue: "run", Choices: ["run", "task"])
        ],
        DefaultDispatch: TriggerDispatchMode.InTransaction);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var name = context.Config.GetString("name")
            ?? throw new InvalidOperationException("Parameter 'name' is required.");

        var value = TriggerTemplate.Render(context.Config.GetString("value"), context);

        await context.Variables.SetAsync(name, value, VariableType.String, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Computes run progress and hands it to the host's sink.
///
/// This is the generalisation of the original engine's UpdateWorkItemProgressTrigger, split along
/// the right line: computing the percentage is engine logic, deciding where the number
/// goes is host logic.
/// </summary>
public sealed class ReportProgressTrigger : IWorkflowTrigger
{
    public string Key => BuiltInTriggerKeys.ReportProgress;

    public TriggerDescriptor Describe() => new(
        Key,
        "Report Progress",
        "Calculates completion for the run (or the completing branch) and reports it to the host.",
        SupportedEvents:
        [
            WorkflowEventKind.TaskCompleted,
            WorkflowEventKind.TaskCancelled,
            WorkflowEventKind.ForkConverged,
            WorkflowEventKind.BranchCompleted,
            WorkflowEventKind.RunCompleted
        ],
        Parameters:
        [
            new("scope", "Scope", TriggerParameterKind.Choice,
                DefaultValue: "auto", Choices: ["auto", "run", "branch", "all-branches"],
                HelpText: "auto reports per-branch for forked tasks, otherwise for the whole run. "
                        + "all-branches reports every branch of the run separately, which is what a "
                        + "convergence needs: one number per branch rather than the run's average.")
        ],
        DefaultDispatch: TriggerDispatchMode.InTransaction);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sink = context.Services?.GetService<IWorkflowProgressSink>();
        if (sink is null)
        {
            // Nothing to report to; not an error.
            return;
        }

        var scope = context.Config.GetString("scope") ?? "auto";

        // One report per branch. A convergence sees every branch at once, and each has its
        // own progress -- collapsing them to a single figure gives the run's average, which
        // is nobody's actual progress. Falls back to the whole run when the run never
        // forked, so the scope is safe to configure on a task that may or may not fork.
        if (scope == "all-branches")
        {
            var branchKeys = WorkflowProgress.BranchKeys(context.Run);

            if (branchKeys.Count == 0)
            {
                await ReportAsync(sink, context, branchKey: null, ct).ConfigureAwait(false);
                return;
            }

            foreach (var key in branchKeys)
            {
                await ReportAsync(sink, context, key, ct).ConfigureAwait(false);
            }

            return;
        }

        var branchKey = scope switch
        {
            "run" => null,
            "branch" => context.Task.AssignedBranchKey,
            _ => context.Task.ForkGroupId.HasValue ? context.Task.AssignedBranchKey : null
        };

        await ReportAsync(sink, context, branchKey, ct).ConfigureAwait(false);
    }

    private static Task ReportAsync(
        IWorkflowProgressSink sink,
        WorkflowTriggerContext context,
        string? branchKey,
        CancellationToken ct)
    {
        var progress = WorkflowProgress.Calculate(context.Run, branchKey);

        return sink.ReportAsync(context.Subject, branchKey, progress, context.ActorId, ct);
    }
}

/// <summary>Hands a message to the host's notification sink, after commit.</summary>
public sealed class NotifyTrigger : IWorkflowTrigger
{
    public string Key => BuiltInTriggerKeys.Notify;

    public TriggerDescriptor Describe() => new(
        Key,
        "Send Notification",
        "Sends a message through the host's notification sink once the transaction commits.",
        SupportedEvents: Enum.GetValues<WorkflowEventKind>(),
        Parameters:
        [
            new("subject", "Subject", TriggerParameterKind.Text, Required: true),
            new("body", "Body", TriggerParameterKind.Template, Required: true),
            new("recipient", "Recipient actor id", TriggerParameterKind.Text,
                HelpText: "Leave blank to send to the task's current assignee.")
        ],
        // After commit, never inline: an email cannot be un-sent if the transaction
        // rolls back.
        DefaultDispatch: TriggerDispatchMode.AfterCommit);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sink = context.Services?.GetService<IWorkflowNotificationSink>();
        if (sink is null)
        {
            return;
        }

        var recipient = context.Config.GetString("recipient") ?? context.Task.AssignedToActorId;

        await sink.SendAsync(new WorkflowNotification(
            context.Subject,
            context.Task.Id,
            recipient,
            TriggerTemplate.Render(context.Config.GetString("subject"), context) ?? string.Empty,
            TriggerTemplate.Render(context.Config.GetString("body"), context) ?? string.Empty,
            context.ActorId), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>POSTs a JSON body to a URL, after commit.</summary>
public sealed class WebhookTrigger(IHttpClientFactory? httpClientFactory = null) : IWorkflowTrigger
{
    public string Key => BuiltInTriggerKeys.Webhook;

    public TriggerDescriptor Describe() => new(
        Key,
        "Call Webhook",
        "POSTs a JSON body to a URL once the transaction commits.",
        SupportedEvents: Enum.GetValues<WorkflowEventKind>(),
        Parameters:
        [
            new("url", "URL", TriggerParameterKind.Url, Required: true),
            new("body", "JSON body", TriggerParameterKind.Template,
                HelpText: "Leave blank to send a default event payload."),
            new("timeoutSeconds", "Timeout (seconds)", TriggerParameterKind.Integer,
                DefaultValue: "30")
        ],
        DefaultDispatch: TriggerDispatchMode.AfterCommit);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var url = context.Config.GetString("url")
            ?? throw new InvalidOperationException("Parameter 'url' is required.");

        var factory = httpClientFactory
            ?? context.Services?.GetService<IHttpClientFactory>()
            ?? throw new InvalidOperationException(
                "The webhook trigger needs IHttpClientFactory registered (services.AddHttpClient()).");

        using var client = factory.CreateClient("workflow.webhook");
        client.Timeout = TimeSpan.FromSeconds(context.Config.GetInt("timeoutSeconds") ?? 30);

        var body = TriggerTemplate.Render(context.Config.GetString("body"), context)
            ?? System.Text.Json.JsonSerializer.Serialize(new
            {
                subjectType = context.Subject.SubjectType,
                subjectId = context.Subject.SubjectId,
                runId = context.Run.Id,
                taskId = context.Task.Id,
                taskType = context.Task.TaskTypeKey,
                @event = context.Event.ToString(),
                outcome = context.Task.OutcomeKey,
                branch = context.Task.AssignedBranchKey
            });

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri(url), content, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Minimal token substitution for trigger configuration values.</summary>
internal static class TriggerTemplate
{
    public static string? Render(string? template, WorkflowTriggerContext context)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        return template
            .Replace("{task.id}", context.Task.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{task.outcome}", context.Task.OutcomeKey ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{task.type}", context.Task.TaskTypeKey, StringComparison.OrdinalIgnoreCase)
            .Replace("{task.name}", context.Task.DisplayName, StringComparison.OrdinalIgnoreCase)
            .Replace("{task.branch}", context.Task.AssignedBranchKey ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{task.assignee}", context.Task.AssignedToActorId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{event}", context.Event.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{subject.type}", context.Subject.SubjectType, StringComparison.OrdinalIgnoreCase)
            .Replace("{subject.id}", context.Subject.SubjectId, StringComparison.OrdinalIgnoreCase);
    }
}
