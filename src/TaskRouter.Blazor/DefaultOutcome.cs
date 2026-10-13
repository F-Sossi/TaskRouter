using TaskRouter.Core.Builder;
using TaskRouter.Core.Runner;

namespace TaskRouter.Blazor;

/// <summary>
/// The outcome a task gets when nobody chose one for it: plain "Complete".
///
/// <para>A task is finished by choosing one of its outcomes, so a task with none cannot be
/// finished from a screen, and when it is the last step neither can the run. This is the one
/// answer both ends give: the builder puts it on a step that needs one, and the runner offers
/// it for workflows published before the builder did.</para>
/// </summary>
internal static class DefaultOutcome
{
    public const string Key = "completed";
    public const string Label = "Complete";

    /// <summary>
    /// What the runner offers for a task: its own outcomes, or the default when it declares
    /// none. The engine accepts any key for such a task, so this is a button it will honour.
    /// </summary>
    public static IReadOnlyList<OutcomeOption> IfNone(IReadOnlyList<OutcomeOption> declared) =>
        declared.Count > 0 ? declared : [new OutcomeOption(Key, Label, 1)];

    /// <summary>
    /// The outcome the builder may add for the author, or null when it should not guess.
    ///
    /// <para>A host that publishes a list of outcomes it understands has said what the
    /// vocabulary is. Adding a key outside it would put a step in the workflow that the host
    /// cannot read, so there the author picks one instead.</para>
    /// </summary>
    public static OutcomeEditModel? For(IReadOnlyList<OutcomeTypeOption> catalogue)
    {
        if (catalogue.Count == 0)
        {
            return new OutcomeEditModel { OutcomeKey = Key, DisplayName = Label, Order = 1 };
        }

        var listed = catalogue.FirstOrDefault(o =>
            string.Equals(o.Key, Key, StringComparison.OrdinalIgnoreCase));

        return listed is null
            ? null
            : new OutcomeEditModel { OutcomeKey = listed.Key, DisplayName = listed.DisplayName, Order = 1 };
    }
}
