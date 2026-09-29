using System.Globalization;
using System.Text.Json;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Triggers;

/// <summary>
/// Resolves trigger keys to implementations, and exposes descriptors so a builder UI
/// can render configuration forms without knowing any trigger in advance.
/// </summary>
public interface IWorkflowTriggerRegistry
{
    bool TryGet(string key, out IWorkflowTrigger trigger);
    IReadOnlyList<TriggerDescriptor> Descriptors { get; }
}

public sealed class WorkflowTriggerRegistry : IWorkflowTriggerRegistry
{
    private readonly Dictionary<string, IWorkflowTrigger> _byKey;

    public WorkflowTriggerRegistry(IEnumerable<IWorkflowTrigger> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var all = triggers.ToList();

        // Fail at startup with a message naming the culprits, rather than letting
        // ToDictionary throw an ArgumentException at first use the way the original engine's
        // trigger map did (review finding M6).
        var duplicates = all
            .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Count > 0)
        {
            var detail = string.Join("; ", duplicates.Select(g =>
                $"'{g.Key}' registered by {string.Join(" and ", g.Select(t => t.GetType().Name))}"));

            throw new InvalidOperationException($"Duplicate workflow trigger keys: {detail}");
        }

        _byKey = all.ToDictionary(t => t.Key, StringComparer.OrdinalIgnoreCase);
        Descriptors = all.Select(t => t.Describe()).ToList();
    }

    public IReadOnlyList<TriggerDescriptor> Descriptors { get; }

    public bool TryGet(string key, out IWorkflowTrigger trigger) =>
        _byKey.TryGetValue(key, out trigger!);
}

/// <summary>Typed reader over a trigger definition's stored JSON configuration.</summary>
public sealed class TriggerConfig : ITriggerConfig
{
    private readonly Dictionary<string, string?> _values;

    private TriggerConfig(Dictionary<string, string?> values) => _values = values;

    public static TriggerConfig Empty { get; } = new([]);

    public static TriggerConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText()
            };
        }

        return new TriggerConfig(values);
    }

    public IReadOnlyDictionary<string, string?> All => _values;

    public string? GetString(string name) =>
        _values.TryGetValue(name, out var v) ? v : null;

    public int? GetInt(string name) =>
        int.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    public decimal? GetDecimal(string name) =>
        decimal.TryParse(GetString(name), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)
            ? v : null;

    public bool? GetBool(string name) =>
        bool.TryParse(GetString(name), out var v) ? v : null;

    /// <summary>
    /// Validates stored configuration against a trigger's declared parameters, so a
    /// misconfigured trigger is rejected when it is saved rather than failing at 2am.
    /// </summary>
    public static IReadOnlyList<string> Validate(TriggerDescriptor descriptor, string? json)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var config = Parse(json);
        var errors = new List<string>();

        foreach (var p in descriptor.Parameters)
        {
            var raw = config.GetString(p.Name);

            if (p.Required && string.IsNullOrWhiteSpace(raw))
            {
                errors.Add($"'{p.Label}' is required.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            switch (p.Kind)
            {
                case TriggerParameterKind.Integer when config.GetInt(p.Name) is null:
                    errors.Add($"'{p.Label}' must be a whole number.");
                    break;
                case TriggerParameterKind.Decimal when config.GetDecimal(p.Name) is null:
                    errors.Add($"'{p.Label}' must be a number.");
                    break;
                case TriggerParameterKind.Boolean when config.GetBool(p.Name) is null:
                    errors.Add($"'{p.Label}' must be true or false.");
                    break;
                case TriggerParameterKind.Choice
                    when p.Choices is not null && !p.Choices.Contains(raw, StringComparer.OrdinalIgnoreCase):
                    errors.Add($"'{p.Label}' must be one of: {string.Join(", ", p.Choices)}.");
                    break;
                case TriggerParameterKind.Url
                    when !Uri.TryCreate(raw, UriKind.Absolute, out _):
                    errors.Add($"'{p.Label}' must be an absolute URL.");
                    break;
                default:
                    break;
            }
        }

        return errors;
    }
}
