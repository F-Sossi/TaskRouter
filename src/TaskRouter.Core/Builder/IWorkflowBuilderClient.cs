using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Validation;

namespace TaskRouter.Core.Builder;

/// <summary>
/// What the builder UI needs from the host.
///
/// An abstraction rather than a direct dependency on the engine, so the same
/// components work in a Blazor Server host talking to the engine in-process and in a
/// WebAssembly host talking to it over HTTP. Only the implementation differs.
/// </summary>
public interface IWorkflowBuilderClient
{
    Task<IReadOnlyList<WorkflowSummary>> GetWorkflowsAsync(CancellationToken ct = default);

    Task<WorkflowEditModel?> GetWorkflowAsync(int versionId, CancellationToken ct = default);

    Task<IReadOnlyList<TaskTypeOption>> GetTaskTypesAsync(CancellationToken ct = default);

    Task<TaskTypeOption> CreateTaskTypeAsync(
        string key, string displayName, CancellationToken ct = default);

    /// <summary>Trigger metadata, so the builder can render a form per trigger.</summary>
    Task<IReadOnlyList<TriggerDescriptor>> GetTriggerDescriptorsAsync(CancellationToken ct = default);

    /// <summary>Assignment role keys the host understands.</summary>
    Task<IReadOnlyList<string>> GetAssignmentRolesAsync(CancellationToken ct = default);

    /// <summary>
    /// The outcomes an author may give a task — the organisation's catalogue.
    ///
    /// <para>Narrowed to <c>WorkflowBuilderOptions.Outcomes</c> when a host sets it. A host
    /// that translates outcome keys into a vocabulary of its own wants exactly that; one
    /// whose outcomes are open sets nothing and gets the whole catalogue.</para>
    /// </summary>
    Task<IReadOnlyList<OutcomeTypeOption>> GetOutcomeTypesAsync(CancellationToken ct = default);

    /// <summary>
    /// Adds an outcome to the catalogue, or returns the existing one with that key.
    ///
    /// <para>The key is lower-cased and trimmed. It is matched as a string when a route is
    /// resolved and when a host translates it, and the two comparisons did not agree on case
    /// — so one spelling per outcome is the point of having a catalogue at all.</para>
    /// </summary>
    Task<OutcomeTypeOption> CreateOutcomeTypeAsync(
        string key, string displayName, CancellationToken ct = default);

    /// <summary>
    /// Everything in both catalogues, archived rows included, with how much uses each.
    ///
    /// <para>What a management screen needs and the pickers deliberately do not: they hide
    /// archived rows, because their job is to offer what an author may choose. This one shows
    /// the state of the database.</para>
    /// </summary>
    Task<TypeInventory> GetTypeInventoryAsync(CancellationToken ct = default);

    /// <summary>Brings a retired task type back into the picker.</summary>
    Task RestoreTaskTypeAsync(int taskTypeId, CancellationToken ct = default);

    /// <summary>Brings a retired outcome back into the picker.</summary>
    Task RestoreOutcomeTypeAsync(int outcomeTypeId, CancellationToken ct = default);

    /// <summary>
    /// Retires a task type. It stops being offered; workflows already using it are untouched.
    ///
    /// <para>Retiring rather than deleting, because task definitions reference a task type by
    /// id — deleting one in use would orphan them, and the database refuses it anyway.</para>
    /// </summary>
    Task ArchiveTaskTypeAsync(int taskTypeId, CancellationToken ct = default);

    /// <summary>
    /// Retires an outcome from the catalogue. Published workflows keep theirs: they copied
    /// the key rather than pointing at this row.
    /// </summary>
    Task ArchiveOutcomeTypeAsync(int outcomeTypeId, CancellationToken ct = default);

    /// <summary>
    /// Subject types a workflow may be authored against. Empty means the host has not said,
    /// and the builder offers a free-text field instead of a list.
    /// </summary>
    Task<IReadOnlyList<string>> GetSubjectTypesAsync(CancellationToken ct = default);

    /// <summary>Route condition keys the host has registered.</summary>
    Task<IReadOnlyList<string>> GetRouteConditionsAsync(CancellationToken ct = default);

    /// <summary>Definitions flagged as sub-workflows, for the attachment picker.</summary>
    Task<IReadOnlyList<SubWorkflowDefinitionOption>> GetSubWorkflowDefinitionsAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<ValidationError>> ValidateAsync(
        WorkflowEditModel model, CancellationToken ct = default);

    /// <summary>Saves as a draft. Returns validation errors, or empty on success.</summary>
    Task<SaveResult> SaveAsync(WorkflowEditModel model, CancellationToken ct = default);

    /// <summary>
    /// Publishes a draft, making it the version new runs start on. In-flight runs stay
    /// pinned to whichever version they started with.
    /// </summary>
    Task<SaveResult> PublishAsync(WorkflowEditModel model, CancellationToken ct = default);

    /// <summary>Creates a new draft version copied from an existing one.</summary>
    Task<int> CreateDraftVersionAsync(int fromVersionId, CancellationToken ct = default);

    /// <summary>
    /// Copies a version's whole graph onto a <b>new workflow</b> under a new name, and
    /// returns the new draft version's id.
    ///
    /// <para>The difference from <see cref="CreateDraftVersionAsync"/> is which thing is
    /// new. That makes another <em>version</em> of the same workflow, so publishing it
    /// supersedes what came before and every run follows the new rules. This makes a
    /// separate <em>workflow</em>: versioned from 1, published independently, and editable
    /// without touching the original or the runs pinned to it.</para>
    ///
    /// <para>Everything version-scoped comes across — tasks, outcomes, routes, triggers and
    /// sub-workflow attachments — with freshly minted ids throughout. Pre-assignments do
    /// not, because they hang off a run rather than a version; there is nothing to copy.
    /// The copy always lands unpublished, whatever the source was.</para>
    /// </summary>
    /// <param name="newName">Name for the new workflow. Required: the two are otherwise
    /// indistinguishable in a list, which is where somebody has to pick between them.</param>
    Task<int> DuplicateWorkflowAsync(
        int fromVersionId, string newName, CancellationToken ct = default);
}

public sealed record SaveResult(
    bool Success,
    int VersionId,
    IReadOnlyList<ValidationError> Errors)
{
    public static SaveResult Failed(IReadOnlyList<ValidationError> errors) =>
        new(false, 0, errors);

    public static SaveResult Ok(int versionId) => new(true, versionId, []);
}

/// <summary><paramref name="HasPublishedVersion"/> because an attachment to a
/// sub-workflow with no published version cannot spawn — the engine throws at spawn
/// time, so the builder warns rather than letting it be discovered in production.</summary>
public sealed record SubWorkflowDefinitionOption(
    int DefinitionId, string Name, bool HasPublishedVersion);
