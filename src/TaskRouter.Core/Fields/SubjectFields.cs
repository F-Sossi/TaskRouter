using System.Text.Json;

namespace TaskRouter.Core.Fields;

/// <summary>What sort of value a field holds, which decides the control the builder shows.</summary>
public enum FieldKind
{
    Text,
    Number,
    Boolean,
    Date,
    /// <summary>An enum. Stored by member name.</summary>
    Choice,
    /// <summary>An actor id, picked from the host's directory.</summary>
    Actor,
    /// <summary>A key into some other host table, picked from options the host supplies.</summary>
    Lookup
}

/// <summary>Whether a target must find exactly one record, or may find any number.</summary>
public enum FieldCardinality
{
    One,
    Many
}

/// <summary>One option in a dropdown: the stored value and what the author sees.</summary>
public sealed record FieldChoice(string Value, string Label);

/// <summary>A field as the builder sees it — no delegates, so it can cross HTTP.</summary>
public sealed record SubjectFieldInfo(
    string Key,
    string Label,
    FieldKind Kind,
    bool IsNullable,
    IReadOnlyList<FieldChoice> Choices);

/// <summary>A target as the builder sees it: what it is called and which fields it has.</summary>
public sealed record SubjectTargetInfo(
    string Key,
    string Label,
    FieldCardinality Cardinality,
    IReadOnlyList<SubjectFieldInfo> Fields);

/// <summary>One "set this field to this" line. A null value clears the field.</summary>
public sealed record FieldAssignment(string Field, string? Value);

/// <summary>
/// The stored form of a trigger's assignment list.
///
/// <para>Trigger configuration is a flat map of strings, so the list is kept as JSON inside
/// one parameter rather than widening the configuration format for one trigger.</para>
/// </summary>
public static class FieldAssignments
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<FieldAssignment> assignments) =>
        JsonSerializer.Serialize(assignments, Json);

    public static IReadOnlyList<FieldAssignment> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<FieldAssignment>>(json, Json) ?? [];

            if (parsed.Any(a => a is null || string.IsNullOrWhiteSpace(a.Field)))
            {
                throw new FormatException("The field assignments are not valid.");
            }

            return parsed;
        }
        catch (JsonException ex)
        {
            throw new FormatException("The field assignments are not valid JSON.", ex);
        }
    }
}
