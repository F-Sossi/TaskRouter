using System.Linq.Expressions;
using System.Reflection;

using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Fields;

namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>
/// Declares which fields workflow authors may set. Passed to
/// <c>AddWorkflowFields(fields => ...)</c>.
///
/// <para>Declare a target once for every subject type that shares it with
/// <see cref="ForSubjectTypes"/>, then add the fields only one type has with
/// <see cref="ForSubjectType"/> and <see cref="SubjectFieldsBuilder.Extend{T}"/>. An author
/// sees the union for their workflow's subject type, under one target.</para>
/// </summary>
public sealed class WorkflowFieldsBuilder
{
    private readonly List<TargetDeclaration> _declarations = [];

    public SubjectFieldsBuilder ForSubjectTypes(IEnumerable<string> subjectTypes)
    {
        ArgumentNullException.ThrowIfNull(subjectTypes);
        return new(this, [.. subjectTypes]);
    }

    public SubjectFieldsBuilder ForSubjectType(string subjectType) => ForSubjectTypes([subjectType]);

    internal void Add(TargetDeclaration declaration) => _declarations.Add(declaration);

    /// <summary>
    /// Composes every declaration into a catalog. Throws one exception naming every
    /// problem, so a host fixes its wiring in one pass rather than one restart per mistake.
    /// </summary>
    public WorkflowFieldCatalog Build()
    {
        var problems = new List<string>();
        var bySubjectType = new Dictionary<string, List<TargetRegistration>>(StringComparer.Ordinal);

        foreach (var declaration in _declarations.Where(d => !d.IsExtension))
        {
            foreach (var subjectType in declaration.SubjectTypes)
            {
                var targets = bySubjectType.TryGetValue(subjectType, out var existing)
                    ? existing
                    : bySubjectType[subjectType] = [];

                if (targets.Any(t => t.Key == declaration.Key))
                {
                    problems.Add($"Target '{declaration.Key}' is declared twice for {subjectType}.");
                    continue;
                }

                var target = new TargetRegistration
                {
                    Key = declaration.Key,
                    Label = declaration.Label!,
                    Cardinality = declaration.Cardinality,
                    EntityType = declaration.EntityType,
                    Resolve = declaration.Resolve!
                };

                AddFields(target, declaration, subjectType, problems);
                targets.Add(target);
            }
        }

        foreach (var extension in _declarations.Where(d => d.IsExtension))
        {
            foreach (var subjectType in extension.SubjectTypes)
            {
                var target = bySubjectType.TryGetValue(subjectType, out var targets)
                    ? targets.FirstOrDefault(t => t.Key == extension.Key)
                    : null;

                if (target is null)
                {
                    problems.Add($"{subjectType} has no '{extension.Key}' to extend.");
                    continue;
                }

                if (!target.EntityType.IsAssignableFrom(extension.EntityType))
                {
                    problems.Add(
                        $"Extending '{extension.Key}' for {subjectType}: {extension.EntityType.Name} "
                        + $"does not derive from {target.EntityType.Name}.");
                    continue;
                }

                AddFields(target, extension, subjectType, problems);
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The workflow field catalog is wired wrongly:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
        }

        return new WorkflowFieldCatalog(bySubjectType);
    }

    private static void AddFields(
        TargetRegistration target, TargetDeclaration declaration, string subjectType, List<string> problems)
    {
        foreach (var field in declaration.Fields)
        {
            if (target.Fields.Any(f => f.Key == field.Key))
            {
                problems.Add($"Field '{field.Key}' is declared twice on '{target.Key}' for {subjectType}.");
                continue;
            }

            target.Fields.Add(field);
        }

        target.OnWritten.AddRange(declaration.OnWritten);
    }
}

/// <summary>Declarations that apply to one or more subject types.</summary>
public sealed class SubjectFieldsBuilder
{
    private readonly WorkflowFieldsBuilder _root;
    private readonly IReadOnlyList<string> _subjectTypes;

    internal SubjectFieldsBuilder(WorkflowFieldsBuilder root, IReadOnlyList<string> subjectTypes)
    {
        _root = root;
        _subjectTypes = subjectTypes;
    }

    /// <summary>
    /// A new target. <paramref name="resolve"/> returns the records it means for a given
    /// run and task; it runs on the engine's context, inside its transaction. A
    /// <see cref="FieldCardinality.One"/> target that finds none or several fails the update.
    /// </summary>
    public TargetFieldsBuilder<T> Target<T>(
        string key, string label, FieldCardinality cardinality, Func<FieldResolveContext, IQueryable<T>> resolve)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(resolve);

        var take = cardinality == FieldCardinality.One ? 2 : int.MaxValue;

        var declaration = new TargetDeclaration(_subjectTypes, key, typeof(T), IsExtension: false)
        {
            Label = label,
            Cardinality = cardinality,
            Resolve = async (context, ct) =>
                (await resolve(context).Take(take).ToListAsync(ct).ConfigureAwait(false)).Cast<object>().ToList()
        };

        _root.Add(declaration);
        return new TargetFieldsBuilder<T>(this, declaration);
    }

    /// <summary>
    /// Adds fields to a target declared elsewhere, for these subject types only.
    /// <typeparamref name="T"/> must be the target's entity type or derive from it — for a
    /// document hierarchy, the concrete document class.
    /// </summary>
    public TargetFieldsBuilder<T> Extend<T>(string key) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var declaration = new TargetDeclaration(_subjectTypes, key, typeof(T), IsExtension: true);

        _root.Add(declaration);
        return new TargetFieldsBuilder<T>(this, declaration);
    }
}

/// <summary>Fields on one target.</summary>
public sealed class TargetFieldsBuilder<T> where T : class
{
    private readonly SubjectFieldsBuilder _parent;
    private readonly TargetDeclaration _declaration;

    internal TargetFieldsBuilder(SubjectFieldsBuilder parent, TargetDeclaration declaration)
    {
        _parent = parent;
        _declaration = declaration;
    }

    /// <summary>
    /// A field an author may set. The key is the property name. The kind is inferred from
    /// the property type; pass <see cref="FieldKind.Actor"/> for a text actor id or
    /// <see cref="FieldKind.Lookup"/> (with <paramref name="options"/>) for a key into
    /// another table.
    ///
    /// <para>Give <paramref name="write"/> for any field guarded by host rules — a status
    /// with a state machine above all. Without it the property is assigned directly.</para>
    /// </summary>
    public TargetFieldsBuilder<T> Field<TValue>(
        Expression<Func<T, TValue>> property,
        string label,
        FieldKind? kind = null,
        FieldWriter<T, TValue>? write = null,
        Func<FieldOptionsContext, CancellationToken, Task<IReadOnlyList<FieldChoice>>>? options = null)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        if (property.Body is not MemberExpression { Member: PropertyInfo info }
            || property.Body is MemberExpression { Expression: not ParameterExpression })
        {
            throw new ArgumentException("A field must name a property directly, e.g. d => d.Status.", nameof(property));
        }

        var valueType = typeof(TValue);
        var underlying = Nullable.GetUnderlyingType(valueType) ?? valueType;
        var effective = kind ?? FieldValueConverter.InferKind(valueType);

        if (kind is FieldKind.Actor && valueType != typeof(string))
        {
            throw new ArgumentException($"'{info.Name}' is an actor field, so it must be a string.", nameof(kind));
        }

        if (kind is FieldKind.Lookup && underlying != typeof(int) && underlying != typeof(long) && underlying != typeof(string))
        {
            throw new ArgumentException($"'{info.Name}' is a lookup, so it must be a key: int, long or string.", nameof(kind));
        }

        if (kind is { } explicitKind && explicitKind is not (FieldKind.Actor or FieldKind.Lookup))
        {
            throw new ArgumentException(
                $"Only Actor and Lookup are given explicitly; '{info.Name}' is inferred as {FieldValueConverter.InferKind(valueType)}.",
                nameof(kind));
        }

        if (effective == FieldKind.Lookup && options is null)
        {
            throw new ArgumentException($"Lookup field '{info.Name}' needs an options delegate.", nameof(options));
        }

        if (write is null && info.SetMethod is not { IsPublic: true })
        {
            throw new ArgumentException($"'{info.Name}' has no public setter; give it a write delegate.", nameof(write));
        }

        var getter = property.Compile();

        _declaration.Fields.Add(new FieldRegistration
        {
            Key = info.Name,
            Label = label,
            Kind = effective,
            DeclaringType = typeof(T),
            ValueType = valueType,
            IsNullable = !valueType.IsValueType || Nullable.GetUnderlyingType(valueType) is not null,
            Choices = effective == FieldKind.Choice ? FieldValueConverter.ChoicesOf(valueType) : [],
            Read = entity => getter((T)entity),
            Write = write is null
                ? (entity, value, _, _) =>
                {
                    info.SetValue(entity, value);
                    return Task.CompletedTask;
                }
                : (entity, value, context, ct) => write((T)entity, (TValue)value!, context, ct),
            Options = options
        });

        return this;
    }

    /// <summary>
    /// Runs once per record after its fields change — stamping a modifier, say. Only runs
    /// on records that actually changed.
    /// </summary>
    public TargetFieldsBuilder<T> OnWritten(Func<T, FieldWriteContext, CancellationToken, Task> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);

        _declaration.OnWritten.Add(new OnWrittenRegistration(
            typeof(T), (entity, context, ct) => hook((T)entity, context, ct)));

        return this;
    }

    /// <summary>The next target, for the same subject types.</summary>
    public TargetFieldsBuilder<TNext> Target<TNext>(
        string key, string label, FieldCardinality cardinality, Func<FieldResolveContext, IQueryable<TNext>> resolve)
        where TNext : class =>
        _parent.Target(key, label, cardinality, resolve);

    /// <summary>Extends another target, for the same subject types.</summary>
    public TargetFieldsBuilder<TNext> Extend<TNext>(string key) where TNext : class =>
        _parent.Extend<TNext>(key);
}

internal sealed record TargetDeclaration(
    IReadOnlyList<string> SubjectTypes, string Key, Type EntityType, bool IsExtension)
{
    public string? Label { get; init; }
    public FieldCardinality Cardinality { get; init; }
    public Func<FieldResolveContext, CancellationToken, Task<IReadOnlyList<object>>>? Resolve { get; init; }
    public List<FieldRegistration> Fields { get; } = [];
    public List<OnWrittenRegistration> OnWritten { get; } = [];
}
