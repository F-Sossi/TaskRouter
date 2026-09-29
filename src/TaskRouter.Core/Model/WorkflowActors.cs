namespace TaskRouter.Core.Model;

/// <summary>
/// Actor ids the engine itself uses. Opaque strings, like every other actor id here —
/// the engine never models a user.
/// </summary>
public static class WorkflowActors
{
    /// <summary>
    /// Who acted when nobody did. The reminder sweeper dispatches under this, and it
    /// lands in the TriggerExecution audit trail.
    ///
    /// Not the task's assignee, which would be a lie — that person did not do this, and
    /// the audit trail is the one place that distinction is recoverable. Not an empty
    /// string either: TriggerDispatcher's guards reject one.
    ///
    /// Prefixed so a host reading its own audit log can tell it apart from an id of its
    /// own at a glance.
    /// </summary>
    public const string System = "workflow:system";
}
