using TaskRouter.Core.Model;

namespace TaskRouter.Core.Builder;

/// <summary>
/// What each <see cref="WorkflowEventKind"/> means to somebody authoring a workflow, and on
/// which steps it can fire at all.
///
/// <para>A trigger hangs off one step and fires only when the engine raises an event
/// <b>against that step</b>. Most events are raised against a particular kind of step — "the
/// run started" against the first one, "the branches converged" against the convergence
/// point — so the same event on any other step is a trigger that saves, publishes, and never
/// runs. The builder offers only the events that can fire on the step being edited, and the
/// publish check refuses the rest; both ask <see cref="WhyNever"/>.</para>
/// </summary>
public static class TriggerEvents
{
    /// <summary>The event's name as the builder shows it.</summary>
    public static string Label(WorkflowEventKind kind) => kind switch
    {
        WorkflowEventKind.RunStarted => "Workflow starts",
        WorkflowEventKind.RunCompleted => "Workflow finishes",
        WorkflowEventKind.RunCancelled => "Workflow is cancelled",
        WorkflowEventKind.TaskCreated => "Step is reached",
        WorkflowEventKind.TaskAssigned => "Step is reassigned",
        WorkflowEventKind.TaskCompleted => "Step is completed",
        WorkflowEventKind.TaskCancelled => "Step is cancelled",
        WorkflowEventKind.TaskForked => "Step splits into branches",
        WorkflowEventKind.BranchAdded => "Branch is added later",
        WorkflowEventKind.BranchCompleted => "A branch is completed",
        WorkflowEventKind.ForkConverged => "All branches come back together",
        WorkflowEventKind.SelectiveRejection => "Some branches are sent back",
        WorkflowEventKind.SubWorkflowStarted => "Sub-workflow starts",
        WorkflowEventKind.SubWorkflowCompleted => "Sub-workflow finishes",
        WorkflowEventKind.SubWorkflowCancelled => "Sub-workflow is cancelled",
        WorkflowEventKind.TaskDueSoon => "Step is nearly due",
        WorkflowEventKind.TaskOverdue => "Step is overdue",
        WorkflowEventKind.Custom => "Custom event",
        _ => kind.ToString()
    };

    /// <summary>One or two sentences saying when the event fires, for the builder's picker.</summary>
    public static string Description(WorkflowEventKind kind) => kind switch
    {
        WorkflowEventKind.RunStarted =>
            "When a workflow is started on a document. Fires on the first step only.",
        WorkflowEventKind.RunCompleted =>
            "When the last open step closes and the workflow is done. Fires on whichever step finished last, usually the final one.",
        WorkflowEventKind.RunCancelled =>
            "When somebody cancels the whole workflow. Fires on a step that was still open at the time.",
        WorkflowEventKind.TaskCreated =>
            "When the workflow arrives at this step and it lands in somebody's inbox.",
        WorkflowEventKind.TaskAssigned =>
            "When this step is handed to a different person after it was created.",
        WorkflowEventKind.TaskCompleted =>
            "When somebody finishes this step by choosing one of its outcomes. The most common choice.",
        WorkflowEventKind.TaskCancelled =>
            "When this step is cancelled rather than finished.",
        WorkflowEventKind.TaskForked =>
            "When this step splits into one copy per branch (for example one per section). Forkable steps only.",
        WorkflowEventKind.BranchAdded =>
            "When a branch is added to this step after it already split. Forkable steps only.",
        WorkflowEventKind.BranchCompleted =>
            "When one branch's copy of this step is completed, before the others are. Fires on steps that run per branch.",
        WorkflowEventKind.ForkConverged =>
            "When every branch has finished and the work comes back together at this step. Convergence points only.",
        WorkflowEventKind.SelectiveRejection =>
            "When the reviewer at this step sends some branches back for rework. Convergence points only.",
        WorkflowEventKind.SubWorkflowStarted =>
            "When a sub-workflow is started from this step. Steps with a sub-workflow attached only.",
        WorkflowEventKind.SubWorkflowCompleted =>
            "When a sub-workflow started from this step finishes. Steps with a sub-workflow attached only.",
        WorkflowEventKind.SubWorkflowCancelled =>
            "When a sub-workflow started from this step is abandoned. Steps with a sub-workflow attached only.",
        WorkflowEventKind.TaskDueSoon =>
            "When this step is still open inside its reminder lead time. Needs a reminder lead time on the step.",
        WorkflowEventKind.TaskOverdue =>
            "When this step's due date passes and it is still open. Steps without a due date never fire it.",
        WorkflowEventKind.Custom =>
            "When the host application's own code raises an event with the name given below. Nothing built in raises one.",
        _ => string.Empty
    };

    /// <summary>
    /// Why <paramref name="kind"/> can never fire on <paramref name="task"/>, or null when it
    /// can. Only the clear-cut cases: an event whose firing depends on data (an overdue date,
    /// whether a step happens to finish last) is always allowed.
    /// </summary>
    public static string? WhyNever(WorkflowEventKind kind, TaskEditModel task, WorkflowEditModel workflow)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workflow);

        return kind switch
        {
            WorkflowEventKind.RunStarted when workflow.EntryTaskLocalId != task.LocalId =>
                "it only fires on the first step",
            WorkflowEventKind.TaskForked or WorkflowEventKind.BranchAdded when !task.IsForkable =>
                "the step is not forkable",
            WorkflowEventKind.ForkConverged or WorkflowEventKind.SelectiveRejection when !task.IsConvergencePoint =>
                "the step is not a convergence point",
            WorkflowEventKind.SubWorkflowStarted
                or WorkflowEventKind.SubWorkflowCompleted
                or WorkflowEventKind.SubWorkflowCancelled when !HasSubWorkflow(task, workflow) =>
                "the step has no sub-workflow attached",
            WorkflowEventKind.TaskDueSoon when task.ReminderLeadTimeMinutes is null =>
                "the step has no reminder lead time",
            _ => null
        };
    }

    // A version-wide attachment can be started from any step but a terminal one.
    private static bool HasSubWorkflow(TaskEditModel task, WorkflowEditModel workflow) =>
        workflow.SubWorkflows.Any(a => a.TaskLocalId == task.LocalId || (a.TaskLocalId is null && !task.IsTerminal));
}
