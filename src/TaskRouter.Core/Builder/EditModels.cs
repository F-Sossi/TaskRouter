using TaskRouter.Core.Model;

namespace TaskRouter.Core.Builder;

/// <summary>
/// Editable shapes the builder UI binds to.
///
/// Kept separate from the entities so the UI never touches EF-tracked objects, and so
/// a Blazor WebAssembly host can serialise them over HTTP unchanged.
/// </summary>
public sealed class WorkflowEditModel
{
    public int DefinitionId { get; set; }
    public int VersionId { get; set; }
    public int Version { get; set; }
    public bool IsPublished { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? SubjectType { get; set; }
    public string? Description { get; set; }

    /// <summary>Attached to a task rather than started against a subject.</summary>
    public bool IsSubWorkflow { get; set; }

    /// <summary>Client-side id of the entry task; matches <see cref="TaskEditModel.LocalId"/>.</summary>
    public Guid? EntryTaskLocalId { get; set; }

    public List<TaskEditModel> Tasks { get; set; } = [];

    /// <summary>Sub-workflows attachable in this version. Version-scoped like routes and
    /// triggers, so it must round-trip or a draft save silently drops it.</summary>
    public List<SubWorkflowAttachmentEditModel> SubWorkflows { get; set; } = [];

    public TaskEditModel? Find(Guid localId) => Tasks.FirstOrDefault(t => t.LocalId == localId);
}

public sealed class TaskEditModel
{
    /// <summary>
    /// Stable identity while editing, before anything is persisted. Routes point at
    /// this rather than a database id, so a workflow can be built and rearranged
    /// entirely client-side and saved once.
    /// </summary>
    public Guid LocalId { get; set; } = Guid.NewGuid();

    public int Id { get; set; }

    public string TaskTypeKey { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    public bool IsRequired { get; set; } = true;
    public bool IsAdHoc { get; set; }
    public bool IsBlocking { get; set; }
    public bool IsForkable { get; set; }
    public bool IsConvergencePoint { get; set; }
    public bool IsTerminal { get; set; }

    public string? AssignmentRoleKey { get; set; }

    /// <summary>
    /// How far ahead of the due date to nudge, in minutes. Null means this task never
    /// nudges. Minutes because that is what the column stores — see
    /// <see cref="TaskRouter.Core.Model.WorkflowTaskDefinition.ReminderLeadTimeMinutes"/>
    /// for why it is not a TimeSpan.
    /// </summary>
    public int? ReminderLeadTimeMinutes { get; set; }

    /// <summary>
    /// The same value in days, which is what a person authoring a workflow thinks in and
    /// what the builder binds to.
    ///
    /// <b>double, not int.</b> An int getter over stored minutes does integer division, so
    /// a 12-hour lead time set programmatically or by an earlier build reads back as 0 —
    /// showing the author a number that is not what is stored, and writing that zero back
    /// on the next save. Nothing warns; the field simply lies.
    /// </summary>
    public double? ReminderLeadTimeDays
    {
        get => ReminderLeadTimeMinutes is { } minutes ? minutes / 1440d : null;
        set => ReminderLeadTimeMinutes = value is { } days
            ? (int)Math.Round(days * 1440d)
            : null;
    }

    public List<OutcomeEditModel> Outcomes { get; set; } = [];
    public List<RouteEditModel> Routes { get; set; } = [];
    public List<TriggerEditModel> Triggers { get; set; } = [];

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? TaskTypeKey : DisplayName!;

    /// <summary>
    /// A detached copy, for editors that must be able to discard their changes.
    /// <see cref="LocalId"/> is preserved: routes point at it, so a copy with a new one
    /// would be a different task.
    /// </summary>
    public TaskEditModel Clone() => new()
    {
        LocalId = LocalId,
        Id = Id,
        TaskTypeKey = TaskTypeKey,
        DisplayName = DisplayName,
        IsRequired = IsRequired,
        IsAdHoc = IsAdHoc,
        IsBlocking = IsBlocking,
        IsForkable = IsForkable,
        IsConvergencePoint = IsConvergencePoint,
        IsTerminal = IsTerminal,
        AssignmentRoleKey = AssignmentRoleKey,
        // The minutes, never the days property: copying days would round-trip the value
        // through a division and a multiplication for no reason, and Math.Round is not
        // guaranteed to land back on the same integer.
        ReminderLeadTimeMinutes = ReminderLeadTimeMinutes,
        Outcomes = [.. Outcomes.Select(o => o.Clone())],
        Routes = [.. Routes.Select(r => r.Clone())],
        Triggers = [.. Triggers.Select(t => t.Clone())]
    };

    /// <summary>Takes on another copy's values. The counterpart to <see cref="Clone"/>:
    /// an editor commits by copying back, so the caller keeps its object identity.</summary>
    public void CopyFrom(TaskEditModel source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Id = source.Id;
        TaskTypeKey = source.TaskTypeKey;
        DisplayName = source.DisplayName;
        IsRequired = source.IsRequired;
        IsAdHoc = source.IsAdHoc;
        IsBlocking = source.IsBlocking;
        IsForkable = source.IsForkable;
        IsConvergencePoint = source.IsConvergencePoint;
        IsTerminal = source.IsTerminal;
        AssignmentRoleKey = source.AssignmentRoleKey;
        ReminderLeadTimeMinutes = source.ReminderLeadTimeMinutes;

        Outcomes = [.. source.Outcomes.Select(o => o.Clone())];
        Routes = [.. source.Routes.Select(r => r.Clone())];
        Triggers = [.. source.Triggers.Select(t => t.Clone())];
    }
}

public sealed class OutcomeEditModel
{
    public int Id { get; set; }
    public string OutcomeKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Order { get; set; }

    public OutcomeEditModel Clone() => new()
    {
        Id = Id,
        OutcomeKey = OutcomeKey,
        DisplayName = DisplayName,
        Order = Order
    };
}

public sealed class RouteEditModel
{
    public int Id { get; set; }
    public string OutcomeKey { get; set; } = string.Empty;

    /// <summary>Target task, by client-side id.</summary>
    public Guid? NextTaskLocalId { get; set; }

    public string? ConditionKey { get; set; }
    public bool IsDefault { get; set; } = true;
    public bool IsReworkRoute { get; set; }
    public int Order { get; set; }

    public RouteEditModel Clone() => new()
    {
        Id = Id,
        OutcomeKey = OutcomeKey,
        NextTaskLocalId = NextTaskLocalId,
        ConditionKey = ConditionKey,
        IsDefault = IsDefault,
        IsReworkRoute = IsReworkRoute,
        Order = Order
    };

    public void CopyFrom(RouteEditModel source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Id = source.Id;
        OutcomeKey = source.OutcomeKey;
        NextTaskLocalId = source.NextTaskLocalId;
        ConditionKey = source.ConditionKey;
        IsDefault = source.IsDefault;
        IsReworkRoute = source.IsReworkRoute;
        Order = source.Order;
    }
}

public sealed class TriggerEditModel
{
    public int Id { get; set; }
    public string TriggerKey { get; set; } = string.Empty;
    public WorkflowEventKind Event { get; set; } = WorkflowEventKind.TaskCompleted;
    public string? CustomEventName { get; set; }

    /// <summary>Parameter values, keyed by parameter name. Serialised to JSON on save.</summary>
    public Dictionary<string, string?> Configuration { get; set; } = [];

    public string? Condition { get; set; }
    public int Order { get; set; }
    public bool IsActive { get; set; } = true;
    public TriggerDispatchMode DispatchMode { get; set; } = TriggerDispatchMode.InTransaction;
    public TriggerFailurePolicy FailurePolicy { get; set; } = TriggerFailurePolicy.LogAndContinue;

    /// <summary>
    /// A detached copy. <see cref="Configuration"/> is copied rather than shared:
    /// changing the selected trigger rewrites it destructively, so an editor working on
    /// the original could not put it back.
    /// </summary>
    public TriggerEditModel Clone() => new()
    {
        Id = Id,
        TriggerKey = TriggerKey,
        Event = Event,
        CustomEventName = CustomEventName,
        Configuration = new Dictionary<string, string?>(Configuration),
        Condition = Condition,
        Order = Order,
        IsActive = IsActive,
        DispatchMode = DispatchMode,
        FailurePolicy = FailurePolicy
    };

    public void CopyFrom(TriggerEditModel source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Id = source.Id;
        TriggerKey = source.TriggerKey;
        Event = source.Event;
        CustomEventName = source.CustomEventName;
        Configuration = new Dictionary<string, string?>(source.Configuration);
        Condition = source.Condition;
        Order = source.Order;
        IsActive = source.IsActive;
        DispatchMode = source.DispatchMode;
        FailurePolicy = source.FailurePolicy;
    }
}

/// <summary>
/// A sub-workflow attachable within this version.
///
/// On <see cref="WorkflowEditModel"/> rather than on a task, because
/// <see cref="TaskLocalId"/> is nullable and a version-wide attachment has no task to
/// sit under.
/// </summary>
public sealed class SubWorkflowAttachmentEditModel
{
    public int Id { get; set; }
    public int SubWorkflowDefinitionId { get; set; }

    /// <summary>Target task by client-side id, or null for any task in the version.</summary>
    public Guid? TaskLocalId { get; set; }

    public bool IsAutomatic { get; set; }
    public bool IsBlocking { get; set; } = true;
    public bool AllowMultiple { get; set; }

    public SubWorkflowAttachmentEditModel Clone() => new()
    {
        Id = Id,
        SubWorkflowDefinitionId = SubWorkflowDefinitionId,
        TaskLocalId = TaskLocalId,
        IsAutomatic = IsAutomatic,
        IsBlocking = IsBlocking,
        AllowMultiple = AllowMultiple
    };

    public void CopyFrom(SubWorkflowAttachmentEditModel source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Id = source.Id;
        SubWorkflowDefinitionId = source.SubWorkflowDefinitionId;
        TaskLocalId = source.TaskLocalId;
        IsAutomatic = source.IsAutomatic;
        IsBlocking = source.IsBlocking;
        AllowMultiple = source.AllowMultiple;
    }
}

public sealed record TaskTypeOption(int Id, string Key, string DisplayName);

/// <summary>An outcome from the catalogue, for the builder's outcome picker.</summary>
public sealed record OutcomeTypeOption(int Id, string Key, string DisplayName);

/// <summary>
/// Both catalogues as they actually stand, for a screen that manages them.
/// </summary>
/// <param name="UncataloguedOutcomeKeys">
/// Outcome keys used by real tasks that no catalogue entry covers.
///
/// <para>The normal state of a database that had workflows before it had a catalogue, and
/// the reason this list exists rather than being hidden: those keys are what workflows
/// actually route on, so a catalogue that does not mention them is the one that is wrong.
/// Adopting one is a single click, and doing so lower-cases it — which is how two spellings
/// of the same outcome become one.</para>
/// </param>
public sealed record TypeInventory(
    IReadOnlyList<TypeInventoryItem> TaskTypes,
    IReadOnlyList<TypeInventoryItem> Outcomes,
    IReadOnlyList<string> UncataloguedOutcomeKeys);

/// <param name="UsedBy">
/// How many task definitions use it — across every workflow and every version, so a
/// superseded version still counts. Retiring something in use is allowed and normal: it
/// stops being offered for new work and leaves existing workflows alone.
/// </param>
public sealed record TypeInventoryItem(
    int Id, string Key, string DisplayName, bool IsArchived, int UsedBy);

/// <param name="IsPublished">
/// Whether this version was ever published. <b>It stays true when a later version supersedes
/// it</b>, because runs pinned to it still have to resolve their routes — so on its own it
/// does not tell a reader which version actually runs. Use
/// <paramref name="IsLatest"/> for that.
/// </param>
/// <param name="IsLatest">
/// Whether this is the version a new run would start on. Exactly one version of a published
/// definition has it.
///
/// <para>Without this a list of versions shows every one of them as "Published" and a person
/// cannot tell the current process from three superseded ones — which is what the first host's
/// workflow screen did.</para>
/// </param>
public sealed record WorkflowSummary(
    int DefinitionId,
    int VersionId,
    string Name,
    string? SubjectType,
    int Version,
    bool IsPublished,
    int TaskCount,
    bool IsSubWorkflow = false,
    bool IsLatest = false);
