using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;

namespace TaskRouter.Blazor;

/// <summary>
/// What choosing a trigger action does to the trigger being edited. Lives outside the
/// dialog so a unit test can reach it; the test project has no component renderer.
/// </summary>
internal static class TriggerSelection
{
    /// <summary>
    /// Points <paramref name="edit"/> at <paramref name="descriptor"/>: drops configuration
    /// belonging to whatever was selected before, fills in parameter defaults, and moves the
    /// event onto one the trigger handles.
    ///
    /// <para>Dispatch mode and failure policy follow the descriptor only for a new trigger.
    /// Those are the trigger's own judgement of how it should run (a field update rolls the
    /// operation back when a write is refused), and a new trigger has no choice of its own to
    /// lose. An existing trigger's saved choice was somebody's decision, so changing its action
    /// leaves it alone.</para>
    /// </summary>
    public static void Apply(TriggerEditModel edit, TriggerDescriptor descriptor, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(descriptor);

        edit.TriggerKey = descriptor.Key;

        if (isNew)
        {
            edit.DispatchMode = descriptor.DefaultDispatch;
            edit.FailurePolicy = descriptor.DefaultFailurePolicy;
        }

        var valid = descriptor.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in edit.Configuration.Keys.Where(k => !valid.Contains(k)).ToList())
        {
            edit.Configuration.Remove(stale);
        }

        foreach (var p in descriptor.Parameters.Where(p => p.DefaultValue is not null))
        {
            edit.Configuration.TryAdd(p.Name, p.DefaultValue);
        }

        if (!descriptor.SupportedEvents.Contains(edit.Event) && descriptor.SupportedEvents.Count > 0)
        {
            edit.Event = descriptor.SupportedEvents[0];
        }
    }
}
