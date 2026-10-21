using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Triggers;

/// <summary>
/// Hands a message to the host's notification sink, after commit.
///
/// <para>Who gets it is a choice of role — the step's assignee, the next step's, whoever
/// started the run, whoever acted — rather than an actor id typed in, because the person is
/// almost never known when the workflow is drawn. "A specific person" is there for the rest.</para>
///
/// <para><b>Every execution says who it went to, or why it went to nobody.</b> A step can
/// sit unassigned in its org unit's queue, and a notification for its assignee then has no
/// one to go to. That is recorded on the execution as "No one to notify: …" rather than
/// dropped, so an author looking at why nobody heard can see it.</para>
/// </summary>
public sealed class NotifyTrigger(IWorkflowDbContext? db = null, IWorkflowActorResolver? actors = null) : IWorkflowTrigger
{
    /// <summary>The values of the "sendTo" parameter.</summary>
    public static class SendTo
    {
        public const string Assignee = "assignee";
        public const string NextAssignee = "nextAssignee";
        public const string Starter = "starter";
        public const string Actor = "actor";
        public const string Person = "person";
    }

    private static readonly string[] Choices =
        [SendTo.Assignee, SendTo.NextAssignee, SendTo.Starter, SendTo.Actor, SendTo.Person];

    public string Key => BuiltInTriggerKeys.Notify;

    /// <summary>
    /// Fills in "Send to" for a trigger saved before it existed, as the trigger reads it when it
    /// runs: a recipient means that person. Without it the builder shows such a trigger as
    /// "Person assigned this step" with the person hidden, while it goes on sending to them.
    /// </summary>
    internal static Dictionary<string, string?> WithSendTo(string triggerKey, Dictionary<string, string?> configuration)
    {
        if (string.Equals(triggerKey, BuiltInTriggerKeys.Notify, StringComparison.OrdinalIgnoreCase)
            && (!configuration.TryGetValue("sendTo", out var sendTo) || string.IsNullOrWhiteSpace(sendTo))
            && configuration.TryGetValue("recipient", out var recipient) && !string.IsNullOrWhiteSpace(recipient))
        {
            configuration["sendTo"] = SendTo.Person;
        }

        return configuration;
    }

    public TriggerDescriptor Describe() => new(
        Key,
        "Send Notification",
        "Sends a notification through the application once the step's change has been saved. Choose who gets it "
        + "under Send to: for example, the person the next step is assigned to.",
        SupportedEvents: Enum.GetValues<WorkflowEventKind>(),
        Parameters:
        [
            new("sendTo", "Send to", TriggerParameterKind.Choice, Required: true, DefaultValue: SendTo.Assignee,
                Options:
                [
                    new(SendTo.Assignee, "Person assigned this step",
                        "Whoever this step is assigned to when the trigger fires."),
                    new(SendTo.NextAssignee, "Person assigned the next step",
                        "Whoever the step or steps that follow this one are assigned to. Only on \"Step is completed\".",
                        Events: [WorkflowEventKind.TaskCompleted]),
                    new(SendTo.Starter, "Person who started the workflow",
                        "Whoever started this workflow on the document."),
                    new(SendTo.Actor, "Person who did this",
                        "Whoever completed, reassigned or cancelled the step. Nobody, for reminders and overdue "
                        + "alerts, which no person causes."),
                    new(SendTo.Person, "A specific person", "Always the same person, picked below.")
                ]),
            new("recipient", "Person", TriggerParameterKind.Actor, Required: true,
                ShownWhen: new("sendTo", SendTo.Person)),
            new("subject", "Subject", TriggerParameterKind.Text, Required: true),
            new("body", "Body", TriggerParameterKind.Template, Required: true)
        ],
        // After commit, never inline: an email cannot be un-sent if the transaction
        // rolls back.
        DefaultDispatch: TriggerDispatchMode.AfterCommit,
        Help:
        [
            new("What it does",
            [
                "Works out who to send to, fills in the subject and body, and sends one notification per person "
                    + "through the application's notification service.",
                "Records \"Sent to …\", or \"No one to notify: …\" with the reason, so nobody has to guess why someone was not told."
            ]),
            new("Send to",
            [
                "Person assigned this step: whoever holds the step when the trigger fires. On \"Step is completed\" that is "
                    + "whoever completed it.",
                "Person assigned the next step: whoever the steps this completion creates are assigned to; one notification each. "
                    + "Only on \"Step is completed\".",
                "Person who started the workflow.",
                "Person who did this: whoever completed, reassigned or cancelled the step. Nobody for reminders and overdue alerts.",
                "A specific person: always the same person, picked from the directory."
            ]),
            new("Things to know",
            [
                "A step can be waiting unassigned in its section's queue. Then there is no one to notify, and that is recorded. "
                    + "To catch whoever picks it up, add another notification on \"Step is reassigned\".",
                "Runs after commit by default, so nobody is told about a change that was rolled back.",
                "A test run sends real notifications."
            ])
        ]);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sink = context.Services?.GetService<IWorkflowNotificationSink>();
        if (sink is null)
        {
            context.Report.Detail = "Not sent: this application has no notification service set up.";
            return;
        }

        var (recipients, nobody) = await RecipientsAsync(context, ct).ConfigureAwait(false);

        if (recipients.Count == 0)
        {
            context.Report.Detail = $"No one to notify: {string.Join("; ", nobody)}.";
            return;
        }

        var subject = await TriggerTemplate.RenderAsync(context.Config.GetString("subject"), context, ct)
            .ConfigureAwait(false);
        var body = await TriggerTemplate.RenderAsync(context.Config.GetString("body"), context, ct)
            .ConfigureAwait(false);

        var sent = new List<string>();
        var failed = new List<string>();
        Exception? firstFailure = null;

        foreach (var recipient in recipients)
        {
            try
            {
                await sink.SendAsync(new WorkflowNotification(
                    context.Subject,
                    context.Task.Id,
                    recipient,
                    subject ?? string.Empty,
                    body ?? string.Empty,
                    context.ActorId), ct)
                    .ConfigureAwait(false);

                sent.Add(await NameAsync(context, recipient, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstFailure ??= ex;
                failed.Add($"{await NameAsync(context, recipient, ct).ConfigureAwait(false)} ({ex.Message})");
            }
        }

        // Nobody was told, so a retry repeats nothing: fail and let it be retried.
        if (sent.Count == 0 && firstFailure is not null)
        {
            throw new InvalidOperationException($"Could not notify {string.Join(", ", failed)}.", firstFailure);
        }

        // Somebody was told. A retry runs the whole trigger again and would tell them twice, so
        // this succeeds and says who was missed instead.
        context.Report.Detail = $"Sent to {string.Join(", ", sent)}."
            + (failed.Count == 0 ? string.Empty : $" Could not notify {string.Join(", ", failed)}.")
            + (nobody.Count == 0 ? string.Empty : $" No one to notify for {string.Join("; ", nobody)}.");
    }

    public async Task<string?> DescribeActionAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Services?.GetService<IWorkflowNotificationSink>() is null)
        {
            return string.Empty;
        }

        var (recipients, nobody) = await RecipientsAsync(context, ct).ConfigureAwait(false);
        var subject = await TriggerTemplate.RenderAsync(context.Config.GetString("subject"), context, ct)
            .ConfigureAwait(false);

        if (recipients.Count == 0)
        {
            return $"No notification (\"{subject}\") will be sent: no one to notify, {string.Join("; ", nobody)}.";
        }

        var names = new List<string>();

        foreach (var recipient in recipients)
        {
            names.Add(recipient == context.ActorId ? "you" : await NameAsync(context, recipient, ct).ConfigureAwait(false));
        }

        return $"Notify {string.Join(", ", names)}: \"{subject}\""
            + (nobody.Count == 0 ? string.Empty : $" (no one to notify for {string.Join("; ", nobody)})");
    }

    /// <summary>Who the notification goes to, and for each part that went to nobody, why.</summary>
    private async Task<(List<string> Recipients, List<string> Nobody)> RecipientsAsync(
        WorkflowTriggerContext context, CancellationToken ct)
    {
        var recipients = new List<string>();
        var nobody = new List<string>();

        void Add(string? actorId, string whyNot)
        {
            if (string.IsNullOrWhiteSpace(actorId))
            {
                nobody.Add(whyNot);
            }
            else if (!recipients.Contains(actorId, StringComparer.Ordinal))
            {
                recipients.Add(actorId);
            }
        }

        var recipient = context.Config.GetString("recipient");

        // Before "sendTo" existed, a recipient id meant that person and blank meant the assignee.
        // Matched as validation matches it, ignoring case.
        var sendTo = context.Config.GetString("sendTo") is { Length: > 0 } chosen
            ? Choices.FirstOrDefault(c => string.Equals(c, chosen, StringComparison.OrdinalIgnoreCase)) ?? chosen
            : string.IsNullOrWhiteSpace(recipient) ? SendTo.Assignee : SendTo.Person;

        switch (sendTo)
        {
            case SendTo.Assignee:
                Add(context.Task.AssignedToActorId, "this step is not assigned to anyone");
                break;

            case SendTo.NextAssignee:
                await AddNextAssigneesAsync(context, Add, nobody, ct).ConfigureAwait(false);
                break;

            case SendTo.Starter:
                var starter = await Db(context).WorkflowRuns.AsNoTracking()
                    .Where(r => r.Id == context.Run.Id)
                    .Select(r => r.CreatorId)
                    .SingleOrDefaultAsync(ct)
                    .ConfigureAwait(false);
                Add(starter == WorkflowActors.System ? null : starter, "the workflow was not started by a person");
                break;

            case SendTo.Actor:
                Add(context.ActorId == WorkflowActors.System ? null : context.ActorId,
                    "nobody did this; it was raised by the deadline sweep");
                break;

            case SendTo.Person:
                Add(recipient, "no person is chosen");
                break;

            default:
                throw new InvalidOperationException($"'{sendTo}' is not a choice for who to send a notification to.");
        }

        return (recipients, nobody);
    }

    /// <summary>
    /// The steps this completion created, which is every task in the run stamped with the
    /// moment this one completed: the engine creates follow-ons, fork branches and a
    /// convergence step inside the completion, all with the completion's own timestamp.
    /// Found that way rather than through a predecessor column the schema does not have.
    /// </summary>
    private async Task AddNextAssigneesAsync(
        WorkflowTriggerContext context, Action<string?, string> add, List<string> nobody, CancellationToken ct)
    {
        if (context.Event != WorkflowEventKind.TaskCompleted)
        {
            nobody.Add("\"Person assigned the next step\" only works when a step is completed");
            return;
        }

        if (context.Preview is { } preview)
        {
            // Not created yet: the preview says who they would go to.
            if (preview.NextTasks.Count == 0)
            {
                nobody.Add("no step follows this one");
            }

            foreach (var step in preview.NextTasks)
            {
                add(step.AssignedToActorId, $"'{step.DisplayName}', which is not assigned to anyone");
            }

            return;
        }

        var tasks = Db(context).WorkflowTasks.AsNoTracking();
        var taskId = context.Task.Id;

        // Compared with the stored completion time, not the snapshot's, so a column that keeps
        // less precision than DateTime rounds both sides alike.
        var next = await tasks
            .Where(t => t.WorkflowRunId == context.Run.Id
                     && t.Id != taskId
                     && t.Created == tasks.Where(c => c.Id == taskId).Select(c => c.CompletedDate).FirstOrDefault()
                     && t.Status != WorkflowTaskStatus.Cancelled)
            .OrderBy(t => t.Id)
            .Select(t => new { t.AssignedToActorId, t.TaskDefinition!.DisplayName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (next.Count == 0)
        {
            nobody.Add("no step follows this one");
            return;
        }

        foreach (var step in next)
        {
            add(step.AssignedToActorId, $"'{step.DisplayName}', which is not assigned to anyone");
        }
    }

    private IWorkflowDbContext Db(WorkflowTriggerContext context) =>
        db ?? context.Services?.GetService<IWorkflowDbContext>()
        ?? throw new InvalidOperationException("Notifications need the workflow database to find who to send to.");

    /// <summary>"Name (id)" when the directory knows the person, otherwise the id.</summary>
    private async Task<string> NameAsync(WorkflowTriggerContext context, string actorId, CancellationToken ct)
    {
        var directory = actors ?? context.Services?.GetService<IWorkflowActorResolver>();

        try
        {
            var name = directory is null ? null : await directory.GetDisplayNameAsync(actorId, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(name) || name == actorId ? actorId : $"{name} ({actorId})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The directory fails soft everywhere; a name is not worth failing a sent notification over.
            return actorId;
        }
    }
}
