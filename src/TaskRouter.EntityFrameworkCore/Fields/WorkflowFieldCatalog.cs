using System.Diagnostics.CodeAnalysis;

using TaskRouter.Core.Fields;

namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>
/// The fields a host lets workflow authors set, per subject type. Built once at startup by
/// <see cref="WorkflowFieldsBuilder"/> and registered as a singleton.
/// </summary>
public sealed class WorkflowFieldCatalog
{
    private readonly Dictionary<string, List<TargetRegistration>> _bySubjectType;

    internal WorkflowFieldCatalog(Dictionary<string, List<TargetRegistration>> bySubjectType) =>
        _bySubjectType = bySubjectType;

    /// <summary>The targets an author may pick on a workflow of this subject type, in declaration order.</summary>
    public IReadOnlyList<SubjectTargetInfo> Describe(string subjectType) =>
        _bySubjectType.TryGetValue(subjectType, out var targets)
            ? [.. targets.Select(t => t.Describe())]
            : [];

    internal bool TryGetTarget(
        string subjectType, string targetKey, [NotNullWhen(true)] out TargetRegistration? target)
    {
        target = _bySubjectType.TryGetValue(subjectType, out var targets)
            ? targets.FirstOrDefault(t => t.Key == targetKey)
            : null;

        return target is not null;
    }
}

internal sealed class TargetRegistration
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required FieldCardinality Cardinality { get; init; }
    public required Type EntityType { get; init; }
    public required Func<FieldResolveContext, CancellationToken, Task<IReadOnlyList<object>>> Resolve { get; init; }
    public List<FieldRegistration> Fields { get; } = [];
    public List<OnWrittenRegistration> OnWritten { get; } = [];

    public SubjectTargetInfo Describe() =>
        new(Key, Label, Cardinality, [.. Fields.Select(f => f.Describe())]);
}

internal sealed class FieldRegistration
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required FieldKind Kind { get; init; }
    public required Type DeclaringType { get; init; }
    public required Type ValueType { get; init; }
    public required bool IsNullable { get; init; }
    public required IReadOnlyList<FieldChoice> Choices { get; init; }

    /// <remarks>Callers must check <see cref="AppliesTo"/> first: extension fields are declared on a derived type under a base target.</remarks>
    public required Func<object, object?> Read { get; init; }

    /// <remarks>Callers must check <see cref="AppliesTo"/> first: extension fields are declared on a derived type under a base target.</remarks>
    public required Func<object, object?, FieldWriteContext, CancellationToken, Task> Write { get; init; }
    public Func<FieldOptionsContext, CancellationToken, Task<IReadOnlyList<FieldChoice>>>? Options { get; init; }

    internal bool AppliesTo(object entity) => DeclaringType.IsInstanceOfType(entity);

    public SubjectFieldInfo Describe() => new(Key, Label, Kind, IsNullable, Choices);
}

/// <summary>An <c>OnWritten</c> hook, which only runs on records of the type it was declared for.</summary>
internal sealed record OnWrittenRegistration(
    Type DeclaringType, Func<object, FieldWriteContext, CancellationToken, Task> Run)
{
    internal bool AppliesTo(object entity) => DeclaringType.IsInstanceOfType(entity);
}
