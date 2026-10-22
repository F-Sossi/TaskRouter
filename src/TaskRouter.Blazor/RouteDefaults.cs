using TaskRouter.Core.Builder;

namespace TaskRouter.Blazor;

/// <summary>
/// Whether a route in the editor should be its outcome's default. Lives outside the dialog
/// so a unit test can reach it; the test project has no component renderer.
///
/// <para>A default is the fallback: it is taken only when no other route for the outcome
/// matches, and it cannot carry a condition (the validator refuses one). Every route used to
/// start out as a default, so picking a condition left a route that either ignored its
/// condition or, once the validator learned the rule, could not be published until somebody
/// found the switch.</para>
/// </summary>
internal static class RouteDefaults
{
    /// <summary>
    /// The default a route should start with: on when it is the first route for its outcome
    /// and has no condition, off otherwise.
    /// </summary>
    public static bool Suggest(RouteEditModel edit, IEnumerable<RouteEditModel> siblings)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(siblings);

        if (!string.IsNullOrWhiteSpace(edit.ConditionKey))
        {
            return false;
        }

        return !siblings.Any(r => !ReferenceEquals(r, edit)
            && r.IsDefault
            && string.Equals(r.OutcomeKey, edit.OutcomeKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies a condition change: a route with a condition is never the default.
    /// </summary>
    public static void ConditionChanged(RouteEditModel edit, string? conditionKey)
    {
        ArgumentNullException.ThrowIfNull(edit);

        edit.ConditionKey = string.IsNullOrWhiteSpace(conditionKey) ? null : conditionKey;

        if (edit.ConditionKey is not null)
        {
            edit.IsDefault = false;
        }
    }
}
