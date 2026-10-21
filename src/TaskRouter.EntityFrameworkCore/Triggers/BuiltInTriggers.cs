using System.Globalization;
using System.Text.RegularExpressions;

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
    public const string UpdateFields = "workflow.updateFields";
}

/// <summary>Writes a workflow variable. The simplest useful trigger.</summary>
public sealed class SetVariableTrigger : IWorkflowTrigger
{
    public string Key => BuiltInTriggerKeys.SetVariable;

    public TriggerDescriptor Describe() => new(
        Key,
        "Set Workflow Variable",
        "Remembers a value on this run of the workflow, such as who approved or a date, so later steps, "
        + "route conditions and other triggers can read it as {var:name}. Changes nothing on the document.",
        SupportedEvents: Enum.GetValues<WorkflowEventKind>(),
        Parameters:
        [
            new("name", "Variable name", TriggerParameterKind.Text, Required: true),
            new("value", "Value", TriggerParameterKind.Text,
                HelpText: "Supports {task.outcome}, {task.branch}, {task.id}, {event}, {actor}, {today}, {today+Nd}, {today-Nd}, {now}, {var:name}."),
            new("scope", "Scope", TriggerParameterKind.Choice,
                DefaultValue: "run", Choices: ["run", "task"])
        ],
        DefaultDispatch: TriggerDispatchMode.InTransaction,
        Help:
        [
            new("What it does",
            [
                "Stores a named value on this run of the workflow. Nothing on the document changes.",
                "Later triggers read it as {var:name}; route and trigger conditions test it as var.name."
            ]),
            new("Settings",
            [
                "Name: what to call it. Value: what to store; tokens are filled in when the trigger runs.",
                "Scope: run (default) is visible to every later step; task keeps it to this step."
            ]),
            new("Things to know",
            [
                "It is bookkeeping, so it is not listed when somebody is asked to confirm a completion."
            ])
        ]);

    /// <summary>Bookkeeping: nobody completing a step needs to be told a variable is set.</summary>
    public Task<string?> DescribeActionAsync(WorkflowTriggerContext context, CancellationToken ct = default) =>
        Task.FromResult<string?>(string.Empty);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var name = context.Config.GetString("name")
            ?? throw new InvalidOperationException("Parameter 'name' is required.");

        var value = await TriggerTemplate.RenderAsync(context.Config.GetString("value"), context, ct)
            .ConfigureAwait(false);

        // context.Variables is the task's view: it writes against the task. "run" (the
        // default) writes against the run, so a later step and the route conditions see it.
        var scope = context.Config.GetString("scope");
        var variables = string.Equals(scope, "task", StringComparison.OrdinalIgnoreCase)
            ? context.Variables
            : (context.Variables as WorkflowVariables)?.ForRun() ?? context.Variables;

        await variables.SetAsync(name, value, VariableType.String, ct).ConfigureAwait(false);
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
        "Works out how far through the workflow the document is, from the steps completed so far, and hands "
        + "the percentage to the application to store. To set a fixed percentage instead, use Update fields.",
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
        DefaultDispatch: TriggerDispatchMode.InTransaction,
        Help:
        [
            new("What it does",
            [
                "Works out a percentage from the workflow's own steps: steps completed ÷ steps in this run so far, rounded.",
                "Cancelled steps do not count, and a step that was redone counts once.",
                "Hands the percentage to the application, which decides where it is stored."
            ]),
            new("Scope",
            [
                "auto (default): this branch's progress on a step that is part of a fork, otherwise the whole run's.",
                "run: one figure for the whole run. branch: this step's branch only.",
                "all-branches: one figure per branch, which is what a convergence step needs."
            ]),
            new("Things to know",
            [
                "\"So far\" means steps the run has reached, not every step the workflow could have. "
                    + "In a straight A → B → C workflow, completing A reports 50% (A done, B open), not 33%.",
                "To set an exact percentage, use Update fields instead, and do not use both on one workflow: they would overwrite each other."
            ])
        ]);

    public Task<string?> DescribeActionAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult<string?>(context.Services?.GetService<IWorkflowProgressSink>() is null
            ? string.Empty
            : "Update the progress percentage from the steps completed so far");
    }

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

/// <summary>POSTs a JSON body to a URL, after commit.</summary>
public sealed class WebhookTrigger(IHttpClientFactory? httpClientFactory = null) : IWorkflowTrigger
{
    public string Key => BuiltInTriggerKeys.Webhook;

    public TriggerDescriptor Describe() => new(
        Key,
        "Call Webhook",
        "Posts a JSON message to another system's URL once the step's change has been saved. For "
        + "integrations: the other system tells you the URL.",
        SupportedEvents: Enum.GetValues<WorkflowEventKind>(),
        Parameters:
        [
            new("url", "URL", TriggerParameterKind.Url, Required: true),
            new("body", "JSON body", TriggerParameterKind.Template,
                HelpText: "Leave blank to send a default event payload. Renders the {task.*}, {subject.*} and {event} tokens; {actor}, {now}, {today} and {var:name} are sent as written."),
            new("timeoutSeconds", "Timeout (seconds)", TriggerParameterKind.Integer,
                DefaultValue: "30")
        ],
        DefaultDispatch: TriggerDispatchMode.AfterCommit,
        Help:
        [
            new("What it does",
            [
                "Sends an HTTP POST with a JSON body to the URL, for another system to act on.",
                "With no body set it sends the document's type and id, the run id, the step's id and type, the event, the outcome and the branch.",
                "Any response other than success counts as a failure. After commit, a failed call is retried."
            ]),
            new("Things to know",
            [
                "The body fills in {task.*}, {subject.*} and {event} only; {actor}, {today} and {var:name} are sent as written.",
                "Timeout is 30 seconds unless set.",
                "A test run calls the URL for real."
            ])
        ]);

    public Task<string?> DescribeActionAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var url = context.Config.GetString("url");
        var where = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        return Task.FromResult<string?>($"Send a message to another system ({where})");
    }

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
internal static partial class TriggerTemplate
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

    /// <summary>
    /// <see cref="Render"/>, plus the tokens that need the clock, the actor, or the run's
    /// variables: <c>{actor}</c>, <c>{now}</c>, <c>{today}</c>, <c>{today+Nd}</c>,
    /// <c>{today-Nd}</c>, <c>{var:name}</c>. Dates render as ISO-8601, UTC. A variable
    /// that is not set renders empty; any other unknown token passes through literally.
    /// </summary>
    public static async Task<string?> RenderAsync(
        string? template, WorkflowTriggerContext context, CancellationToken ct = default)
    {
        var rendered = Render(template, context);

        if (string.IsNullOrEmpty(rendered) || !rendered.Contains('{', StringComparison.Ordinal))
        {
            return rendered;
        }

        var matches = ContextToken().Matches(rendered);

        if (matches.Count == 0)
        {
            return rendered;
        }

        IReadOnlyDictionary<string, string?>? variables = null;

        if (rendered.Contains("{var:", StringComparison.OrdinalIgnoreCase))
        {
            variables = await context.Variables.GetAllAsync(ct).ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;

        // Render has already substituted the task and subject tokens, and its output is
        // what is scanned here. This second step is one pass: text it substitutes (a
        // variable's value, the actor) is never rescanned, so a variable whose value looks
        // like a token stays as it was written. A task or subject value that looks like one
        // of these tokens would still be rendered.
        return ContextToken().Replace(rendered, match =>
        {
            if (match.Groups["actor"].Success)
            {
                return context.ActorId;
            }

            if (match.Groups["now"].Success)
            {
                return now.ToString("O", CultureInfo.InvariantCulture);
            }

            if (match.Groups["today"].Success)
            {
                var days = match.Groups["n"].Success
                    ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture)
                        * (match.Groups["sign"].Value == "-" ? -1 : 1)
                    : 0;

                return now.Date.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            var name = match.Groups["name"].Value.Trim();

            return variables is not null && variables.TryGetValue(name, out var value)
                ? value ?? string.Empty
                : string.Empty;
        });
    }

    // The offset is at most five digits so it can neither overflow int.Parse nor AddDays;
    // a longer one does not match and passes through literally like any unknown token.
    [GeneratedRegex(
        @"\{(?:(?<actor>actor)|(?<now>now)|(?<today>today)(?:(?<sign>[+-])(?<n>\d{1,5})d)?|var:(?<name>[^}]+))\}",
        RegexOptions.IgnoreCase)]
    private static partial Regex ContextToken();
}
