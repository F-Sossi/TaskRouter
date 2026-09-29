using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using DemoDocuments.Server.Data;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Escalation, which the engine deliberately does not provide.
///
/// The engine raises <see cref="WorkflowEventKind.TaskOverdue"/> and stops there. It has
/// no notion of a supervisor, because who sits above a late person is a fact about an
/// organisation and every host models that differently — a seam in the library would be
/// a seam every host had to implement to use a feature most of them want one line of.
///
/// So this is a host trigger walking a host org chart:
/// <c>AssignedBranchKey</c> is the demo's section <c>Code</c>, the section knows its
/// Section Lead, and the division above it knows its head.
///
/// <para>It cannot be the built-in <c>workflow.notify</c> trigger. That one reads its
/// recipient straight from stored configuration and never renders it through
/// TriggerTemplate, and no token resolves an org chart anyway — but a seeded
/// configuration is static while the Section Lead above a task depends on which section the task
/// landed in.</para>
/// </summary>
public sealed class DemoEscalationTrigger(DemoDbContext db, ILogger<DemoEscalationTrigger> logger)
    : IWorkflowTrigger
{
    public const string TriggerKey = "demo.escalate";

    public string Key => TriggerKey;

    public TriggerDescriptor Describe() => new(
        Key,
        "Escalate to Supervisor",
        "Notifies the person above the task's assignee: the section's Section Lead, or the "
        + "division head when the Section Lead is the one who is late. The body template supports "
        + "only {task.id} and {task.assignee}; any other token passes through literally.",
        // Only TaskOverdue. Every other built-in trigger declares the whole enum, but
        // this one means nothing on TaskCreated, and the builder's event picker filters
        // by exactly this list — so narrowing it is what stops somebody authoring an
        // escalation that can never make sense.
        SupportedEvents: [WorkflowEventKind.TaskOverdue],
        Parameters:
        [
            new("subject", "Subject", TriggerParameterKind.Text, Required: true),
            new("body", "Body", TriggerParameterKind.Template, Required: true)
        ],
        // After commit, never inline: a message to somebody's supervisor cannot be
        // un-sent if the transaction rolls back.
        DefaultDispatch: TriggerDispatchMode.AfterCommit);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sink = context.Services?.GetService<IWorkflowNotificationSink>();
        if (sink is null)
        {
            logger.LogWarning(
                "No IWorkflowNotificationSink is registered; task {TaskId}'s escalation " +
                "has nowhere to go and is being dropped.", context.Task.Id);
            return;
        }

        var recipient = await SupervisorOfAsync(
            context.Task.Id, context.Task.AssignedBranchKey, context.Task.AssignedToActorId, ct)
            .ConfigureAwait(false);

        // Nobody above them, or no org position at all. Silence beats guessing: a
        // notification sent to the wrong person is worse than one not sent.
        // SupervisorOfAsync has already logged why, so the audit trail still shows a
        // missed escalation even though TriggerExecution.Status records Succeeded.
        if (recipient is null)
        {
            return;
        }

        await sink.SendAsync(new WorkflowNotification(
            context.Subject,
            context.Task.Id,
            recipient,
            context.Config.GetString("subject") ?? "Task overdue",
            Render(context.Config.GetString("body"), context),
            context.ActorId), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// One step up the org chart. Normally the section's Section Lead — but when the late person
    /// <i>is</i> that Section Lead, one step up is the division head, because escalating to the
    /// person who is already late is not an escalation. The rule is exactly two rungs
    /// deep, both hard-coded to this demo's org chart; a third rung (or a deeper chart
    /// generally) means extending this method, not just seeding another trigger.
    /// </summary>
    private async Task<string?> SupervisorOfAsync(
        int taskId, string? branchKey, string? assignee, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(branchKey))
        {
            logger.LogInformation(
                "Task {TaskId} has no branch key, so it has no org position to escalate " +
                "from; sending nothing.", taskId);
            return null;
        }

        var section = await db.Sections
            .AsNoTracking()
            .Include(s => s.Division)
            .SingleOrDefaultAsync(s => s.Code == branchKey, ct)
            .ConfigureAwait(false);

        if (section is null)
        {
            logger.LogWarning(
                "Task {TaskId}'s branch key '{BranchKey}' names no seeded section; that's " +
                "a data fault, not a missing assignment, so sending nothing rather than " +
                "guessing a recipient.", taskId, branchKey);
            return null;
        }

        // Nobody above the top. Without this the two-rung rule below would fall through to
        // its else arm and notify the Section Lead — down the org chart, to somebody with no
        // authority over the person who is late. The demo never seeds this case, which is
        // exactly why it is worth guarding: this trigger's job is to be copied, and the
        // next org chart will have a rung this one does not.
        if (string.Equals(section.Division?.DivisionHeadActorId, assignee, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Task {TaskId} (branch key '{BranchKey}') is already assigned to the " +
                "division head; there is nobody further up, so sending nothing.",
                taskId, branchKey);
            return null;
        }

        if (string.Equals(section.SectionLeadActorId, assignee, StringComparison.Ordinal))
        {
            if (section.Division?.DivisionHeadActorId is null)
            {
                logger.LogWarning(
                    "Task {TaskId}'s section '{BranchKey}' resolves to a division with no " +
                    "DivisionHeadActorId; sending nothing rather than guessing a recipient.",
                    taskId, branchKey);
            }

            return section.Division?.DivisionHeadActorId;
        }

        if (section.SectionLeadActorId is null)
        {
            logger.LogWarning(
                "Task {TaskId}'s section '{BranchKey}' has no SectionLeadActorId; sending " +
                "nothing rather than guessing a recipient.", taskId, branchKey);
        }

        return section.SectionLeadActorId;
    }

    /// <summary>
    /// The two tokens worth having here. TriggerTemplate in the library is internal, and
    /// duplicating its whole token set for a demo trigger would be worse than supporting
    /// the two a supervisor actually needs: which task, and who was holding it.
    ///
    /// <c>{task.assignee}</c> falls back to <see cref="string.Empty"/>, not something
    /// friendlier, to match the built-in <c>workflow.notify</c> trigger's substitution for
    /// the same token name. The name is shared; a host author who learned it from the
    /// built-in should not get different output here.
    /// </summary>
    private static string Render(string? template, WorkflowTriggerContext context) =>
        (template ?? string.Empty)
            .Replace("{task.id}",
                context.Task.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase)
            .Replace("{task.assignee}",
                context.Task.AssignedToActorId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
}
