using TaskRouter.Core.Abstractions;

namespace TaskRouter.Core.Builder;

/// <summary>
/// What every trigger has in common, for the builder's info button: the settings below the
/// action's own, and the tokens its values can use. Each trigger's own help is on its
/// <see cref="TriggerDescriptor.Help"/>.
/// </summary>
public static class TriggerHelp
{
    public static IReadOnlyList<TriggerHelpSection> Common { get; } =
    [
        new("Settings every trigger has",
        [
            "Fires when: the event on this step that sets the trigger off. Only events that can happen on this step are offered.",
            "Condition (optional): for example task.outcome == 'rejected'. When it is false the trigger is skipped. "
                + "Paths: task.status, task.outcome, task.type, task.branch, task.assignee, run.status, var.NAME; "
                + "operators ==, !=, contains, startsWith, combined with && and ||.",
            "Dispatch, In transaction: runs as part of saving the step. If it fails under \"Fail the step\", the step is not saved either.",
            "Dispatch, After commit: queued, and run once the step is saved; retried if it fails. Right for anything that "
                + "cannot be taken back, such as a notification or a call to another system.",
            "Dispatch, Background: the same as After commit, picked up straight away rather than at the next poll.",
            "Failure policy: Fail the step stops the step completing; Log and continue lets it complete and records the error.",
            "Active: an inactive trigger never fires but keeps its settings. Order: triggers on the same event run in this order.",
            "Every run of a trigger is recorded with what it did, or why it did nothing."
        ]),
        new("Tokens you can use in values",
        [
            "{task.id}, {task.name}, {task.type}, {task.outcome}, {task.branch}, {task.assignee}: about the step that fired.",
            "{event}, {subject.type}, {subject.id}: what happened, and to which document.",
            "{actor}: who did it. {now}, {today}, {today+5d}, {today-5d}: dates, in UTC.",
            "{var:name}: a workflow variable. Unset variables come out empty."
        ])
    ];
}
