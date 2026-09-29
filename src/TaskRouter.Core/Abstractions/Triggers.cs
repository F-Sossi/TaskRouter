using TaskRouter.Core.Model;

namespace TaskRouter.Core.Abstractions;

/// <summary>
/// A trigger implementation. Identified by a string key rather than an enum value,
/// so consumers can register triggers the library has never heard of.
/// </summary>
public interface IWorkflowTrigger
{
    /// <summary>Stable registry key, namespaced by owner, e.g. "workflow.webhook".</summary>
    string Key { get; }

    /// <summary>Metadata the builder UI renders a configuration form from.</summary>
    TriggerDescriptor Describe();

    Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default);
}

public sealed record TriggerDescriptor(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<WorkflowEventKind> SupportedEvents,
    IReadOnlyList<TriggerParameter> Parameters,
    TriggerDispatchMode DefaultDispatch = TriggerDispatchMode.InTransaction,
    TriggerFailurePolicy DefaultFailurePolicy = TriggerFailurePolicy.LogAndContinue,
    Type? CustomEditorComponent = null);

public sealed record TriggerParameter(
    string Name,
    string Label,
    TriggerParameterKind Kind,
    bool Required = false,
    string? DefaultValue = null,
    IReadOnlyList<string>? Choices = null,
    string? HelpText = null);

/// <summary>Typed access to a trigger's stored JSON configuration.</summary>
public interface ITriggerConfig
{
    string? GetString(string name);
    int? GetInt(string name);
    decimal? GetDecimal(string name);
    bool? GetBool(string name);
    IReadOnlyDictionary<string, string?> All { get; }
}

/// <summary>Read/write access to workflow variables from inside a trigger.</summary>
public interface IWorkflowVariables
{
    Task<string?> GetAsync(string name, CancellationToken ct = default);
    Task SetAsync(string name, string? value, VariableType type = VariableType.String, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken ct = default);
}

/// <summary>
/// The engine operations a trigger may invoke. Deliberately a small, enumerated set
/// rather than handing out the DbContext.
///
/// <para><b>These run as the actor who caused the event, and are authorized as that
/// person.</b> The dispatcher passes the acting actor id straight through, so a trigger
/// that cancels a sibling task when a task completes calls
/// <see cref="IWorkflowAuthorizationPolicy"/> as the person who completed it — and if the
/// host's policy says that person may not cancel, the action fails and the dispatcher
/// records the trigger execution as failed. The workflow still advances; the automation
/// silently does not.</para>
///
/// <para>The exception is the deadline sweep, which dispatches under
/// <see cref="Model.WorkflowActors.System"/> and therefore bypasses the gate — so
/// reminders and escalations cannot be broken by a host policy.</para>
///
/// <para>A host authoring triggers that act on tasks should therefore either grant the
/// relevant people the rights those triggers need, or treat the action as automation and
/// give it an identity of its own. Making engine-initiated trigger actions run as
/// <c>System</c> unconditionally was considered and deliberately not done here: it would
/// let anyone who can author a workflow perform any operation on any task, which is a
/// wider grant than this library should make on a host's behalf.</para>
/// </summary>
public interface IWorkflowActions
{
    Task AssignTaskAsync(int taskId, WorkflowAssignment assignment, CancellationToken ct = default);
    Task CancelTaskAsync(int taskId, string? note, CancellationToken ct = default);
    Task RaiseEventAsync(string customEventName, string? payloadJson, CancellationToken ct = default);
}

/// <summary>Everything a trigger is given. Subject-agnostic and built from snapshots.</summary>
public sealed class WorkflowTriggerContext
{
    public required WorkflowSubject Subject { get; init; }
    public required WorkflowEventKind Event { get; init; }
    public string? CustomEventName { get; init; }

    public required WorkflowTaskSnapshot Task { get; init; }
    public required WorkflowRunSnapshot Run { get; init; }
    public BranchContext? Branch { get; init; }

    public required ITriggerConfig Config { get; init; }
    public required string ActorId { get; init; }

    public required IWorkflowVariables Variables { get; init; }
    public required IWorkflowActions Actions { get; init; }

    /// <summary>Host-side resolution for triggers that need domain services.</summary>
    public IServiceProvider? Services { get; init; }
}
