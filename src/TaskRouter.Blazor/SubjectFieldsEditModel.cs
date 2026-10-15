using TaskRouter.Core.Fields;

namespace TaskRouter.Blazor;

/// <summary>
/// The field-update editor's state: which target, and which field gets which value.
/// Reads and writes the same two configuration keys the trigger reads.
/// </summary>
internal sealed class SubjectFieldsEditModel
{
    public const string TargetKeyName = "target";
    public const string AssignmentsKeyName = "assignments";

    private readonly IReadOnlyList<SubjectTargetInfo> _targets;

    private SubjectFieldsEditModel(IReadOnlyList<SubjectTargetInfo> targets) => _targets = targets;

    public IReadOnlyList<SubjectTargetInfo> Targets => _targets;

    public string? TargetKey { get; private set; }

    public SubjectTargetInfo? Target => _targets.FirstOrDefault(t => t.Key == TargetKey);

    public List<Row> Rows { get; } = [];

    public static SubjectFieldsEditModel Load(
        IReadOnlyList<SubjectTargetInfo> targets, IReadOnlyDictionary<string, string?> configuration)
    {
        var model = new SubjectFieldsEditModel(targets)
        {
            TargetKey = configuration.GetValueOrDefault(TargetKeyName)
        };

        IReadOnlyList<FieldAssignment> stored;

        try
        {
            stored = FieldAssignments.Parse(configuration.GetValueOrDefault(AssignmentsKeyName));
        }
        catch (FormatException)
        {
            stored = [];
        }

        foreach (var assignment in stored)
        {
            model.Rows.Add(new Row
            {
                StoredKey = assignment.Field,
                Field = model.Target?.Fields.FirstOrDefault(f => f.Key == assignment.Field),
                Value = assignment.Value
            });
        }

        return model;
    }

    public void SelectTarget(string? key)
    {
        if (key == TargetKey)
        {
            return;
        }

        TargetKey = key;
        Rows.Clear();
    }

    public void AddRow() => Rows.Add(new Row());

    public void RemoveRow(int index) => Rows.RemoveAt(index);

    public void SetField(int index, string? key)
    {
        var row = Rows[index];
        row.Field = Target?.Fields.FirstOrDefault(f => f.Key == key);
        row.StoredKey = row.Field?.Key;
        row.Value = null;
    }

    /// <summary>The target's fields, less those other rows already set.</summary>
    public IReadOnlyList<SubjectFieldInfo> AvailableFields(int index)
    {
        var used = Rows.Where((_, i) => i != index).Select(r => r.StoredKey).ToHashSet();
        return [.. (Target?.Fields ?? []).Where(f => !used.Contains(f.Key))];
    }

    public void WriteTo(IDictionary<string, string?> configuration)
    {
        configuration[TargetKeyName] = TargetKey;
        configuration[AssignmentsKeyName] = FieldAssignments.Serialize(
            Rows.Where(r => r.StoredKey is not null).Select(r => new FieldAssignment(r.StoredKey!, r.Value)));
    }

    internal sealed class Row
    {
        /// <summary>The field as the catalog describes it; null when the catalog no longer has it.</summary>
        public SubjectFieldInfo? Field { get; set; }

        /// <summary>The key as stored, kept even when <see cref="Field"/> is null so saving does not drop it.</summary>
        public string? StoredKey { get; set; }

        public string? Value { get; set; }
    }
}
