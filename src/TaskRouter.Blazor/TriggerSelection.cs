using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;

namespace TaskRouter.Blazor;

/// <summary>
/// What choosing a trigger action does to the trigger being edited. Lives outside the
/// dialog so a unit test can reach it; the test project has no component renderer.
/// </summary>
internal static class TriggerSelection
{
    /// <summary>
    /// Points <paramref name="edit"/> at <paramref name="descriptor"/>: adopts its dispatch
    /// mode and failure policy, drops configuration belonging to whatever was selected
    /// before, fills in parameter defaults, and moves the event onto one the trigger handles.
    ///
    /// <para>Runs only when the action changes, never on reopening a saved trigger, so the
    /// dispatch and failure choices it replaces were made for a different action. Keeping
    /// them would be wrong more often than not: a webhook's AfterCommit, carried over to a
    /// field update, leaves a refused write nothing to roll back.</para>
    /// </summary>
    /// <param name="offered">
    /// The events the builder offers on this step, when it knows them. The event lands on
    /// one of these — "step is completed" by preference — rather than on the descriptor's
    /// first event, which may be one that never fires here.
    /// </param>
    public static void Apply(
        TriggerEditModel edit, TriggerDescriptor descriptor, IReadOnlyList<WorkflowEventKind>? offered = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(descriptor);

        edit.TriggerKey = descriptor.Key;
        edit.DispatchMode = descriptor.DefaultDispatch;
        edit.FailurePolicy = descriptor.DefaultFailurePolicy;

        var valid = descriptor.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in edit.Configuration.Keys.Where(k => !valid.Contains(k)).ToList())
        {
            edit.Configuration.Remove(stale);
        }

        foreach (var p in descriptor.Parameters.Where(p => p.DefaultValue is not null))
        {
            edit.Configuration.TryAdd(p.Name, p.DefaultValue);
        }

        var events = offered ?? descriptor.SupportedEvents;

        if (!events.Contains(edit.Event) && events.Count > 0)
        {
            edit.Event = events.Contains(WorkflowEventKind.TaskCompleted)
                ? WorkflowEventKind.TaskCompleted
                : events[0];
        }
    }
}
