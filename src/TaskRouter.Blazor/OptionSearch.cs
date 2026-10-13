namespace TaskRouter.Blazor;

/// <summary>
/// Narrowing a long list of people or org units by what somebody has typed so far.
///
/// <para>Every word typed has to appear somewhere in the name, in any order and any case, so
/// <c>jo smi</c> finds both "Smith, John" and "Joanna Smithers". Matching a prefix only
/// would make the answer depend on which way round the host writes a name, which the person
/// typing cannot be expected to know.</para>
/// </summary>
internal static class OptionSearch
{
    /// <summary>The options matching <paramref name="text"/>, in the host's own order.
    /// Nothing typed matches everything.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Find(
        IEnumerable<KeyValuePair<string, string>> options, string? text)
    {
        var words = (text ?? string.Empty).Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return words.Length == 0
            ? options
            : options.Where(option => words.All(word =>
                option.Value.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }
}
