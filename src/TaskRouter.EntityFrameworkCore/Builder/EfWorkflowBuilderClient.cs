using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Builder;
using TaskRouter.Core.Model;
using TaskRouter.Core.Validation;
using TaskRouter.EntityFrameworkCore.Runner;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.EntityFrameworkCore.Builder;

/// <summary>
/// The in-process half of the builder UI.
///
/// <see cref="IWorkflowBuilderClient"/> exists so the same components can run in a
/// Blazor Server host that talks to the engine directly and in a WebAssembly host that
/// talks to it over HTTP. This is the first shape: it reads and writes the definition
/// tables through the host's own DbContext. A WASM host would implement the same
/// interface against controllers that call into something like this.
///
/// Two rules it enforces, both of which come straight from the versioning design:
///
///  1. A published version is never edited in place. Saving over one creates a new
///     draft version instead, because runs are pinned to a version id and mutating it
///     would silently reroute work already in flight — exactly the the original system failure the
///     versioning exists to prevent.
///  2. A draft may be saved while still incomplete. Only structural problems that make
///     the graph unpersistable or meaningless block a save; the full graph validation
///     (entry point, reachability, dead ends, fork rules) is enforced on publish.
///     Requiring a valid graph on every save would make the builder unusable for
///     building anything non-trivial.
/// </summary>
public sealed class EfWorkflowBuilderClient(
    IWorkflowDbContext db,
    IWorkflowTriggerRegistry triggers,
    IEnumerable<IRouteConditionEvaluator> conditions,
    WorkflowBuilderOptions options,
    IWorkflowEditorActorAccessor actors) : IWorkflowBuilderClient
{
    /// <summary>
    /// Who is editing. Every row this class writes is attributed to them, so a host without
    /// authentication still has to answer — see <see cref="IWorkflowEditorActorAccessor"/>.
    /// </summary>
    private string Actor => actors.ActorId;

    // ───────────────────────────────── Reads ─────────────────────────────────

    public async Task<IReadOnlyList<WorkflowSummary>> GetWorkflowsAsync(CancellationToken ct = default) =>
        await db.WorkflowDefinitionVersions
            .Include(v => v.WorkflowDefinition)
            .AsNoTracking()
            .Where(v => !v.IsArchived)
            .OrderBy(v => v.WorkflowDefinition!.Name)
            .ThenByDescending(v => v.Version)
            .Select(v => new WorkflowSummary(
                v.WorkflowDefinitionId,
                v.Id,
                v.WorkflowDefinition!.Name,
                v.SubjectType,
                v.Version,
                v.IsPublished,
                v.Tasks.Count(t => !t.IsArchived),
                v.WorkflowDefinition.IsSubWorkflow,
                v.IsLatest))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Version id 0 means "a workflow that does not exist yet" — the builder opens on a
    /// blank model rather than waiting forever on a null.
    /// </summary>
    public async Task<WorkflowEditModel?> GetWorkflowAsync(int versionId, CancellationToken ct = default)
    {
        if (versionId == 0)
        {
            return new WorkflowEditModel { Version = 1 };
        }

        var version = await LoadGraphAsync(versionId, tracking: false, ct).ConfigureAwait(false);

        return version is null ? null : ToEditModel(version);
    }

    public async Task<IReadOnlyList<TaskTypeOption>> GetTaskTypesAsync(CancellationToken ct = default) =>
        await db.WorkflowTaskTypes
            .AsNoTracking()
            .Where(t => !t.IsArchived)
            .OrderBy(t => t.DisplayName)
            .Select(t => new TaskTypeOption(t.Id, t.Key, t.DisplayName))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<TaskTypeOption> CreateTaskTypeAsync(
        string key, string displayName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var existing = await db.WorkflowTaskTypes
            .SingleOrDefaultAsync(t => t.Key == key, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return new TaskTypeOption(existing.Id, existing.Key, existing.DisplayName);
        }

        var now = DateTime.UtcNow;
        var type = new TaskTypeDefinition
        {
            Key = key,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName,
            CreatorId = Actor,
            ModifierId = Actor,
            Created = now,
            Modified = now
        };

        db.WorkflowTaskTypes.Add(type);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new TaskTypeOption(type.Id, type.Key, type.DisplayName);
    }

    public Task<IReadOnlyList<TriggerDescriptor>> GetTriggerDescriptorsAsync(CancellationToken ct = default) =>
        Task.FromResult(triggers.Descriptors);

    /// <summary>
    /// The role keys this host's assignment resolver understands, as configured on
    /// <see cref="WorkflowBuilderOptions"/>.
    ///
    /// <para>The one thing here the library cannot work out for itself: a role key is opaque
    /// to the engine and only the host's resolver interprets it, so there is nothing to
    /// enumerate. Everything else the builder offers as a choice — task types, trigger
    /// descriptors, route conditions, sub-workflows — is derived from what is registered or
    /// stored.</para>
    /// </summary>
    public Task<IReadOnlyList<string>> GetAssignmentRolesAsync(CancellationToken ct = default) =>
        Task.FromResult(options.AssignmentRoles);

    public Task<IReadOnlyList<string>> GetSubjectTypesAsync(CancellationToken ct = default) =>
        Task.FromResult(options.SubjectTypes);

    public async Task<IReadOnlyList<OutcomeTypeOption>> GetOutcomeTypesAsync(
        CancellationToken ct = default)
    {
        var catalogue = await db.WorkflowOutcomeTypes
            .AsNoTracking()
            .Where(o => !o.IsArchived)
            .OrderBy(o => o.DisplayName)
            .Select(o => new OutcomeTypeOption(o.Id, o.Key, o.DisplayName))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The host's list narrows the catalogue rather than replacing it. A host that
        // translates outcome keys into a closed vocabulary of its own must not be able to
        // have an author pick one it cannot read; a host that sets nothing gets everything.
        return options.Outcomes.Count == 0
            ? catalogue
            : [.. catalogue.Where(o => options.Outcomes.Contains(o.Key, StringComparer.OrdinalIgnoreCase))];
    }

    public async Task<OutcomeTypeOption> CreateOutcomeTypeAsync(
        string key, string displayName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        // Canonicalised on the way in. This key is compared as a string in two places that
        // did not agree about case -- a route resolved in SQL, under the database's
        // collation, and a host's route condition in C#, ordinally -- so a catalogue holding
        // both "Approved" and "approved" would hand authors the defect it exists to prevent.
        var canonical = key.Trim().ToLowerInvariant();

        var existing = await db.WorkflowOutcomeTypes
            .SingleOrDefaultAsync(o => o.Key == canonical, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return new OutcomeTypeOption(existing.Id, existing.Key, existing.DisplayName);
        }

        var now = DateTime.UtcNow;
        var row = new OutcomeTypeDefinition
        {
            Key = canonical,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? canonical : displayName.Trim(),
            CreatorId = Actor,
            ModifierId = Actor,
            Created = now,
            Modified = now
        };

        db.WorkflowOutcomeTypes.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new OutcomeTypeOption(row.Id, row.Key, row.DisplayName);
    }

    public async Task<TypeInventory> GetTypeInventoryAsync(CancellationToken ct = default)
    {
        // Usage is counted across every version, superseded ones included. A type used only
        // by a version nobody starts runs on any more is still a type somebody can read the
        // history of, so "unused" has to mean unused everywhere.
        var taskTypes = await db.WorkflowTaskTypes
            .AsNoTracking()
            .OrderBy(t => t.IsArchived).ThenBy(t => t.DisplayName)
            .Select(t => new TypeInventoryItem(
                t.Id,
                t.Key,
                t.DisplayName,
                t.IsArchived,
                db.WorkflowTaskDefinitions.Count(d => d.TaskTypeDefinitionId == t.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var outcomes = await db.WorkflowOutcomeTypes
            .AsNoTracking()
            .OrderBy(o => o.IsArchived).ThenBy(o => o.DisplayName)
            .Select(o => new TypeInventoryItem(
                o.Id,
                o.Key,
                o.DisplayName,
                o.IsArchived,
                db.WorkflowTaskOutcomes.Count(d => d.OutcomeKey == o.Key)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var inUse = await db.WorkflowTaskOutcomes
            .AsNoTracking()
            .Select(o => o.OutcomeKey)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Compared case-insensitively, so "Approved" counts as covered by a catalogued
        // "approved" rather than being offered for adoption a second time.
        var catalogued = outcomes.Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new TypeInventory(
            taskTypes,
            outcomes,
            [.. inUse.Where(key => !catalogued.Contains(key)).Order(StringComparer.OrdinalIgnoreCase)]);
    }

    public async Task RestoreTaskTypeAsync(int taskTypeId, CancellationToken ct = default) =>
        await SetTaskTypeArchivedAsync(taskTypeId, archived: false, ct).ConfigureAwait(false);

    public async Task RestoreOutcomeTypeAsync(int outcomeTypeId, CancellationToken ct = default) =>
        await SetOutcomeTypeArchivedAsync(outcomeTypeId, archived: false, ct).ConfigureAwait(false);

    private async Task SetTaskTypeArchivedAsync(int taskTypeId, bool archived, CancellationToken ct)
    {
        var row = await db.WorkflowTaskTypes
            .SingleOrDefaultAsync(t => t.Id == taskTypeId, ct).ConfigureAwait(false);

        if (row is null || row.IsArchived == archived)
        {
            return;
        }

        row.IsArchived = archived;
        row.ModifierId = Actor;
        row.Modified = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task SetOutcomeTypeArchivedAsync(int outcomeTypeId, bool archived, CancellationToken ct)
    {
        var row = await db.WorkflowOutcomeTypes
            .SingleOrDefaultAsync(o => o.Id == outcomeTypeId, ct).ConfigureAwait(false);

        if (row is null || row.IsArchived == archived)
        {
            return;
        }

        row.IsArchived = archived;
        row.ModifierId = Actor;
        row.Modified = DateTime.UtcNow;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ArchiveTaskTypeAsync(int taskTypeId, CancellationToken ct = default) =>
        await SetTaskTypeArchivedAsync(taskTypeId, archived: true, ct).ConfigureAwait(false);

    public async Task ArchiveOutcomeTypeAsync(int outcomeTypeId, CancellationToken ct = default) =>
        await SetOutcomeTypeArchivedAsync(outcomeTypeId, archived: true, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<string>> GetRouteConditionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(
            [.. conditions.Select(c => c.ConditionKey).Distinct(StringComparer.OrdinalIgnoreCase).Order()]);

    public async Task<IReadOnlyList<SubWorkflowDefinitionOption>> GetSubWorkflowDefinitionsAsync(
        CancellationToken ct = default) =>
        await db.WorkflowDefinitions
            .AsNoTracking()
            .Where(d => d.IsSubWorkflow && !d.IsArchived)
            .OrderBy(d => d.Name)
            .Select(d => new SubWorkflowDefinitionOption(
                d.Id,
                d.Name,
                db.WorkflowDefinitionVersions.Any(v =>
                    v.WorkflowDefinitionId == d.Id && v.IsPublished && !v.IsArchived)))
            .ToListAsync(ct).ConfigureAwait(false);

    // ─────────────────────────────── Validation ───────────────────────────────

    /// <summary>
    /// Runs the real validator against the unsaved model. The model has no database ids
    /// yet, so tasks are given synthetic ones for the duration of the check — the
    /// validator only ever compares ids to each other.
    /// </summary>
    public Task<IReadOnlyList<ValidationError>> ValidateAsync(
        WorkflowEditModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var errors = new List<ValidationError>();
        errors.AddRange(StructuralErrors(model));
        errors.AddRange(WorkflowDefinitionValidator.Validate(ToTransientVersion(model)));

        return Task.FromResult<IReadOnlyList<ValidationError>>(errors);
    }

    /// <summary>
    /// Problems that make a graph unpersistable or meaningless, as opposed to merely
    /// incomplete. These block even a draft save; everything else waits for publish.
    /// </summary>
    private List<ValidationError> StructuralErrors(WorkflowEditModel model)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            errors.Add(new("WF_NO_NAME", "The workflow needs a name."));
        }

        var localIds = model.Tasks.Select(t => t.LocalId).ToHashSet();

        foreach (var task in model.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.TaskTypeKey))
            {
                errors.Add(new("TASK_NO_TYPE", $"'{task.Label}' has no task type."));
            }

            foreach (var route in task.Routes)
            {
                if (route.NextTaskLocalId is not { } next || !localIds.Contains(next))
                {
                    errors.Add(new("ROUTE_NO_TARGET",
                        $"A '{route.OutcomeKey}' route on '{task.Label}' points at no task."));
                }
            }

            foreach (var trigger in task.Triggers)
            {
                var descriptor = triggers.Descriptors
                    .FirstOrDefault(d => string.Equals(d.Key, trigger.TriggerKey, StringComparison.OrdinalIgnoreCase));

                if (descriptor is null)
                {
                    errors.Add(new("TRIGGER_UNKNOWN",
                        $"'{task.Label}' uses trigger '{trigger.TriggerKey}', which is not registered."));
                    continue;
                }

                // Catch a misconfigured trigger at save time rather than at 2am.
                foreach (var problem in TriggerConfig.Validate(descriptor, Serialise(trigger.Configuration)))
                {
                    errors.Add(new("TRIGGER_CONFIG",
                        $"{descriptor.DisplayName} on '{task.Label}': {problem}"));
                }
            }
        }

        // A dangling attachment is caught here for the same reason ROUTE_NO_TARGET is:
        // it is a client-side reference problem. ToTransientVersion can only resolve a
        // local id to a task id or to null, and null there is a legitimate version-wide
        // attachment — so the validator cannot tell the two apart and this is the only
        // place the distinction still exists.
        foreach (var attachment in model.SubWorkflows)
        {
            if (attachment.TaskLocalId is { } target && !localIds.Contains(target))
            {
                errors.Add(new("ATTACHMENT_NO_TARGET",
                    $"A sub-workflow attachment for definition {attachment.SubWorkflowDefinitionId} " +
                    "points at no task."));
            }
        }

        // Structural because it is literally unpersistable: a unique index covers
        // (version, task, sub-workflow), so without this the save dies on the index
        // rather than returning a failed SaveResult.
        foreach (var duplicate in model.SubWorkflows
            .GroupBy(a => (a.TaskLocalId, a.SubWorkflowDefinitionId))
            .Where(g => g.Count() > 1))
        {
            errors.Add(new("ATTACHMENT_DUPLICATE",
                $"Sub-workflow definition {duplicate.Key.SubWorkflowDefinitionId} is attached twice " +
                "at the same scope. Use AllowMultiple for concurrent instances instead."));
        }

        return errors;
    }

    // ───────────────────────────────── Writes ─────────────────────────────────

    public async Task<SaveResult> SaveAsync(WorkflowEditModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var structural = StructuralErrors(model);
        if (structural.Count > 0)
        {
            return SaveResult.Failed(structural);
        }

        var versionId = await PersistAsync(model, ct).ConfigureAwait(false);
        return SaveResult.Ok(versionId);
    }

    public async Task<SaveResult> PublishAsync(WorkflowEditModel model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var errors = await ValidateAsync(model, ct).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return SaveResult.Failed(errors);
        }

        var versionId = await PersistAsync(model, ct).ConfigureAwait(false);

        // Validate what actually landed, not just what was submitted.
        var saved = await LoadGraphAsync(versionId, tracking: true, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Version {versionId} vanished during publish.");

        var persistedErrors = WorkflowDefinitionValidator.Validate(saved);
        if (persistedErrors.Count > 0)
        {
            // The draft stays saved; it just does not become the version runs start on.
            return SaveResult.Failed(persistedErrors);
        }

        // StartRunAsync selects on IsPublished && IsLatest with SingleOrDefault, so
        // exactly one version per definition may hold that pair.
        var siblings = await db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinitionId == saved.WorkflowDefinitionId && v.Id != saved.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var sibling in siblings)
        {
            sibling.IsLatest = false;
        }

        saved.IsPublished = true;
        saved.IsLatest = true;
        saved.PublishedAt = DateTime.UtcNow;
        saved.Modified = DateTime.UtcNow;
        saved.ModifierId = Actor;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return SaveResult.Ok(versionId);
    }

    public async Task<int> CreateDraftVersionAsync(int fromVersionId, CancellationToken ct = default)
    {
        var model = await GetWorkflowAsync(fromVersionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow version {fromVersionId} not found.");

        // Clearing the version id is what makes PersistAsync branch to a new version;
        // the definition id carries the copy onto the same workflow.
        model.VersionId = 0;
        model.IsPublished = false;

        return await PersistAsync(model, ct).ConfigureAwait(false);
    }

    public async Task<int> DuplicateWorkflowAsync(
        int fromVersionId, string newName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException(
                "A duplicate needs a name of its own.", nameof(newName));
        }

        var model = await GetWorkflowAsync(fromVersionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Workflow version {fromVersionId} not found.");

        // Clearing *both* ids is the whole difference from CreateDraftVersionAsync: with
        // no definition id, PersistCoreAsync mints a new WorkflowDefinition and numbers
        // its first version 1, so the copy is a separate workflow rather than the next
        // version of this one.
        //
        // Everything else rides along untouched. The edit model already carries the full
        // version-scoped graph -- tasks, outcomes, routes, triggers, sub-workflow
        // attachments -- and PersistCoreAsync creates fresh rows for all of it regardless
        // of the ids it is handed, wiring routes to outcomes by object reference. So the
        // stale database ids left on the model are inert, and the copy shares no row with
        // its source.
        model.DefinitionId = 0;
        model.VersionId = 0;
        model.Name = newName.Trim();
        model.IsPublished = false;

        return await PersistAsync(model, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────── Persistence ───────────────────────────────

    /// <summary>
    /// Compares (task, outcome key) pairs with the key case-insensitively — the same way the
    /// engine resolves an outcome to its row, and the same way completion validates one.
    /// </summary>
    private static readonly IEqualityComparer<(Guid Task, string Key)> OutcomeKeyComparer =
        new OutcomeKeyPairComparer();

    private sealed class OutcomeKeyPairComparer : IEqualityComparer<(Guid Task, string Key)>
    {
        public bool Equals((Guid Task, string Key) x, (Guid Task, string Key) y) =>
            x.Task == y.Task && string.Equals(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid Task, string Key) obj) =>
            HashCode.Combine(obj.Task, obj.Key.ToUpperInvariant());
    }

    private Task<int> PersistAsync(WorkflowEditModel model, CancellationToken ct) =>
        // Through WorkflowTransaction rather than BeginTransaction: this host enables
        // EnableRetryOnFailure, and a provider configured that way refuses a
        // user-initiated transaction taken outside its execution strategy.
        WorkflowTransaction.ExecuteAsync(db, token => PersistCoreAsync(model, token), ct);

    private async Task<int> PersistCoreAsync(WorkflowEditModel model, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var definition = model.DefinitionId == 0
            ? null
            : await db.WorkflowDefinitions
                .SingleOrDefaultAsync(d => d.Id == model.DefinitionId, ct).ConfigureAwait(false);

        if (definition is null)
        {
            definition = new WorkflowDefinition
            {
                Name = model.Name,
                Description = NullIfBlank(model.Description),
                IsSubWorkflow = model.IsSubWorkflow,
                CreatorId = Actor,
                ModifierId = Actor,
                Created = now,
                Modified = now
            };
            db.WorkflowDefinitions.Add(definition);
        }
        else
        {
            // Name and description identify the logical workflow, so an edit to them
            // is deliberately global. Subject type is not here: it rides the version.
            definition.Name = model.Name;
            definition.Description = NullIfBlank(model.Description);
            definition.IsSubWorkflow = model.IsSubWorkflow;
            definition.ModifierId = Actor;
            definition.Modified = now;
        }

        var version = model.VersionId == 0
            ? null
            : await LoadGraphAsync(model.VersionId, tracking: true, ct).ConfigureAwait(false);

        if (version is not null && version.IsPublished)
        {
            // Rule 1: a published version is immutable. Editing one forks a draft.
            version = null;
        }

        if (version is null)
        {
            var highest = definition.Id == 0
                ? 0
                : await db.WorkflowDefinitionVersions
                    .Where(v => v.WorkflowDefinitionId == definition.Id)
                    .MaxAsync(v => (int?)v.Version, ct).ConfigureAwait(false) ?? 0;

            version = new WorkflowDefinitionVersion
            {
                WorkflowDefinitionId = 0,
                WorkflowDefinition = definition,
                Version = highest + 1,
                SubjectType = NullIfBlank(model.SubjectType),
                CarryAssignmentForward = model.CarryAssignmentForward,
                IsPublished = false,
                IsLatest = false,
                CreatorId = Actor,
                ModifierId = Actor,
                Created = now,
                Modified = now
            };
            db.WorkflowDefinitionVersions.Add(version);
        }
        else
        {
            await ClearGraphAsync(version, ct).ConfigureAwait(false);
            version.SubjectType = NullIfBlank(model.SubjectType);
            version.CarryAssignmentForward = model.CarryAssignmentForward;
            version.ModifierId = Actor;
            version.Modified = now;
        }

        var types = await db.WorkflowTaskTypes.ToDictionaryAsync(
            t => t.Key, StringComparer.OrdinalIgnoreCase, ct).ConfigureAwait(false);

        var byLocalId = new Dictionary<Guid, WorkflowTaskDefinition>();

        // (task, outcome key) -> the outcome row just created for it. Routes point at these
        // rows, and none of them has a database id until the save below, so they have to be
        // wired by reference.
        var outcomeRows = new Dictionary<(Guid Task, string Key), TaskOutcomeDefinition>(
            OutcomeKeyComparer);

        foreach (var task in model.Tasks)
        {
            if (!types.TryGetValue(task.TaskTypeKey, out var type))
            {
                // A task type the builder invented. Task types are data rows precisely
                // so this is a row insert rather than a code change.
                type = new TaskTypeDefinition
                {
                    Key = task.TaskTypeKey,
                    DisplayName = NullIfBlank(task.DisplayName) ?? task.TaskTypeKey,
                    CreatorId = Actor,
                    ModifierId = Actor,
                    Created = now,
                    Modified = now
                };
                db.WorkflowTaskTypes.Add(type);
                types[task.TaskTypeKey] = type;
            }

            var definitionRow = new WorkflowTaskDefinition
            {
                WorkflowDefinitionVersionId = 0,
                WorkflowDefinitionVersion = version,
                TaskTypeDefinitionId = 0,
                TaskType = type,
                DisplayName = NullIfBlank(task.DisplayName),
                IsRequired = task.IsRequired,
                IsAdHoc = task.IsAdHoc,
                IsBlocking = task.IsBlocking,
                IsForkable = task.IsForkable,
                IsConvergencePoint = task.IsConvergencePoint,
                IsTerminal = task.IsTerminal,
                AssignmentRoleKey = NullIfBlank(task.AssignmentRoleKey),
                ReminderLeadTimeMinutes = task.ReminderLeadTimeMinutes,
                CreatorId = Actor,
                ModifierId = Actor,
                Created = now,
                Modified = now
            };

            version.Tasks.Add(definitionRow);
            db.WorkflowTaskDefinitions.Add(definitionRow);
            byLocalId[task.LocalId] = definitionRow;

            var order = 0;
            foreach (var outcome in task.Outcomes)
            {
                var outcomeRow = new TaskOutcomeDefinition
                {
                    TaskDefinitionId = 0,
                    TaskDefinition = definitionRow,
                    OutcomeKey = outcome.OutcomeKey,
                    DisplayName = string.IsNullOrWhiteSpace(outcome.DisplayName)
                        ? outcome.OutcomeKey
                        : outcome.DisplayName,
                    Order = outcome.Order == 0 ? ++order : outcome.Order,
                    CreatorId = Actor,
                    ModifierId = Actor,
                    Created = now,
                    Modified = now
                };

                db.WorkflowTaskOutcomes.Add(outcomeRow);

                // Routes point at the outcome row rather than naming its key, so the rows
                // have to be findable while routes are built below. None of them has a
                // database id yet, which is why this holds the entity rather than an id.
                outcomeRows[(task.LocalId, outcome.OutcomeKey)] = outcomeRow;
            }
        }

        // Routes come second: every target has an entity by now, even though none of
        // them has a database id yet. EF fixes the foreign keys up on save.
        foreach (var task in model.Tasks)
        {
            var from = byLocalId[task.LocalId];

            foreach (var route in task.Routes)
            {
                if (route.NextTaskLocalId is not { } next || !byLocalId.TryGetValue(next, out var to))
                {
                    continue;   // StructuralErrors already rejected this
                }

                if (!outcomeRows.TryGetValue((task.LocalId, route.OutcomeKey), out var outcomeRow))
                {
                    // A route naming an outcome its task does not declare. It was writable
                    // before routes carried an outcome id, and simply never fired; the
                    // foreign key refuses it now, and the validator reports it as
                    // ROUTE_NO_OUTCOME before a save is attempted.
                    continue;
                }

                db.WorkflowTaskRoutes.Add(new TaskRoute
                {
                    TaskDefinitionId = 0,
                    TaskDefinition = from,
                    TaskOutcomeDefinitionId = 0,
                    Outcome = outcomeRow,
                    NextTaskDefinitionId = 0,
                    NextTaskDefinition = to,
                    ConditionKey = NullIfBlank(route.ConditionKey),
                    IsDefault = route.IsDefault,
                    IsReworkRoute = route.IsReworkRoute,
                    Order = route.Order,
                    CreatorId = Actor,
                    ModifierId = Actor,
                    Created = now,
                    Modified = now
                });
            }

            foreach (var trigger in task.Triggers)
            {
                db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
                {
                    TaskDefinitionId = 0,
                    TaskDefinition = from,
                    TriggerKey = trigger.TriggerKey,
                    Event = trigger.Event,
                    CustomEventName = NullIfBlank(trigger.CustomEventName),
                    Configuration = Serialise(trigger.Configuration),
                    Condition = NullIfBlank(trigger.Condition),
                    Order = trigger.Order,
                    IsActive = trigger.IsActive,
                    DispatchMode = trigger.DispatchMode,
                    FailurePolicy = trigger.FailurePolicy,
                    CreatorId = Actor,
                    ModifierId = Actor,
                    Created = now,
                    Modified = now
                });
            }
        }

        // After the task loop, so a task-scoped attachment resolves to the row created
        // in *this* save rather than the one just deleted.
        foreach (var attachment in model.SubWorkflows)
        {
            WorkflowTaskDefinition? target = null;

            if (attachment.TaskLocalId is { } localId
                && !byLocalId.TryGetValue(localId, out target))
            {
                continue;   // names a task that no longer exists; drop it
            }

            db.WorkflowSubWorkflowAttachments.Add(new SubWorkflowAttachment
            {
                SubWorkflowDefinitionId = attachment.SubWorkflowDefinitionId,
                WorkflowDefinitionVersionId = 0,
                DefinitionVersion = version,
                TaskDefinitionId = null,
                TaskDefinition = target,
                IsAutomatic = attachment.IsAutomatic,
                IsBlocking = attachment.IsBlocking,
                AllowMultiple = attachment.AllowMultiple,
                CreatorId = Actor,
                ModifierId = Actor,
                Created = now,
                Modified = now
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // The entry point can only be recorded once its task definition has an id.
        version.EntryTaskDefinitionId =
            model.EntryTaskLocalId is { } entry && byLocalId.TryGetValue(entry, out var entryRow)
                ? entryRow.Id
                : null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return version.Id;
    }

    /// <summary>
    /// Empties a draft version's graph so it can be rewritten from the model.
    ///
    /// Safe for real work only because published versions never reach here: runs pin to a
    /// version, so deleting task definitions out from under one would orphan in-flight
    /// tasks. Test runs are the exception, and they are discarded below rather than
    /// protected — see <see cref="DiscardTestRunsAsync"/>.
    /// </summary>
    private async Task ClearGraphAsync(WorkflowDefinitionVersion version, CancellationToken ct)
    {
        await DiscardTestRunsAsync(version, ct).ConfigureAwait(false);

        // Drop the entry reference first, or deleting the task it points at fails.
        version.EntryTaskDefinitionId = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var taskIds = version.Tasks.Select(t => t.Id).ToList();

        db.WorkflowTriggerDefinitions.RemoveRange(
            await db.WorkflowTriggerDefinitions
                .Where(t => taskIds.Contains(t.TaskDefinitionId)).ToListAsync(ct).ConfigureAwait(false));

        db.WorkflowTaskRoutes.RemoveRange(
            await db.WorkflowTaskRoutes
                .Where(r => taskIds.Contains(r.TaskDefinitionId) || taskIds.Contains(r.NextTaskDefinitionId))
                .ToListAsync(ct).ConfigureAwait(false));

        db.WorkflowTaskOutcomes.RemoveRange(
            await db.WorkflowTaskOutcomes
                .Where(o => taskIds.Contains(o.TaskDefinitionId)).ToListAsync(ct).ConfigureAwait(false));

        // Explicit rather than by FK cascade: a version-wide attachment names no task
        // and so would not cascade, and the task foreign key is NoAction because two
        // cascade paths from version to attachment are illegal in SQL Server. Without
        // this, re-saving a draft either duplicates a version-wide attachment past its
        // unique index or trips the NoAction key on a task-scoped one.
        db.WorkflowSubWorkflowAttachments.RemoveRange(
            await db.WorkflowSubWorkflowAttachments
                .Where(a => a.WorkflowDefinitionVersionId == version.Id)
                .ToListAsync(ct).ConfigureAwait(false));

        db.WorkflowTaskDefinitions.RemoveRange(version.Tasks.ToList());
        version.Tasks.Clear();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards any test run against this draft, because the graph underneath it is about
    /// to be rewritten.
    ///
    /// Trying a draft out leaves a run pinned to the draft version, and that run's tasks
    /// reference the very task definitions the rewrite deletes. Without this, the act of
    /// testing a draft made it permanently unsaveable and unpublishable, and the only
    /// signal was a 500 from a foreign key.
    ///
    /// The run cannot be salvaged: a re-save mints new task definition ids, so there is
    /// nothing left for its tasks to point at and no sense in which it is still a run of
    /// this workflow. It goes with the graph it was exercising. Published versions never
    /// reach here, so this can never touch real work — but assert it anyway, because the
    /// cost of being wrong is deleting somebody's tasks.
    /// </summary>
    private async Task DiscardTestRunsAsync(WorkflowDefinitionVersion version, CancellationToken ct)
    {
        var runs = await db.WorkflowRuns
            .Where(r => r.WorkflowDefinitionVersionId == version.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var run in runs)
        {
            if (!run.IsTest)
            {
                throw new InvalidOperationException(
                    $"Run {run.Id} is real work on version {version.Id}, which is being " +
                    "rewritten. A published version should never have reached this path.");
            }

            await TestRunTeardown.DiscardAsync(db, run, ct).ConfigureAwait(false);
        }
    }

    // ───────────────────────────────── Mapping ─────────────────────────────────

    private Task<WorkflowDefinitionVersion?> LoadGraphAsync(int versionId, bool tracking, CancellationToken ct)
    {
        var query = db.WorkflowDefinitionVersions
            .Include(v => v.WorkflowDefinition)
            .Include(v => v.Tasks).ThenInclude(t => t.TaskType)
            .Include(v => v.Tasks).ThenInclude(t => t.ValidOutcomes)
            // The route's outcome as well as the route: a route names its outcome by id now,
            // and the edit model still speaks in keys, so without this every route comes back
            // with a blank outcome and the editor loses the wiring it is showing.
            .Include(v => v.Tasks).ThenInclude(t => t.OutgoingRoutes).ThenInclude(r => r.Outcome)
            .Include(v => v.Tasks).ThenInclude(t => t.Triggers)
            .Include(v => v.SubWorkflowAttachments)
            .Where(v => v.Id == versionId);

        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return query.SingleOrDefaultAsync(ct)!;
    }

    private static WorkflowEditModel ToEditModel(WorkflowDefinitionVersion version)
    {
        var tasks = version.Tasks.Where(t => !t.IsArchived).ToList();

        // The builder works in client-side ids so a graph can be rearranged before it
        // is saved. Rebuild that mapping on every load.
        var localIds = tasks.ToDictionary(t => t.Id, _ => Guid.NewGuid());

        var model = new WorkflowEditModel
        {
            DefinitionId = version.WorkflowDefinitionId,
            VersionId = version.Id,
            Version = version.Version,
            IsPublished = version.IsPublished,
            Name = version.WorkflowDefinition?.Name ?? string.Empty,
            SubjectType = version.SubjectType,
            CarryAssignmentForward = version.CarryAssignmentForward,
            Description = version.WorkflowDefinition?.Description,
            IsSubWorkflow = version.WorkflowDefinition?.IsSubWorkflow ?? false,
            EntryTaskLocalId = EntryTaskDefinitionLocalId(version, localIds)
        };

        foreach (var task in tasks)
        {
            model.Tasks.Add(new TaskEditModel
            {
                LocalId = localIds[task.Id],
                Id = task.Id,
                TaskTypeKey = task.TaskType?.Key ?? string.Empty,
                DisplayName = task.DisplayName,
                IsRequired = task.IsRequired,
                IsAdHoc = task.IsAdHoc,
                IsBlocking = task.IsBlocking,
                IsForkable = task.IsForkable,
                IsConvergencePoint = task.IsConvergencePoint,
                IsTerminal = task.IsTerminal,
                AssignmentRoleKey = task.AssignmentRoleKey,
                ReminderLeadTimeMinutes = task.ReminderLeadTimeMinutes,
                Outcomes = [.. task.ValidOutcomes
                    .Where(o => !o.IsArchived)
                    .OrderBy(o => o.Order)
                    .Select(o => new OutcomeEditModel
                    {
                        Id = o.Id,
                        OutcomeKey = o.OutcomeKey,
                        DisplayName = o.DisplayName,
                        Order = o.Order
                    })],
                Routes = [.. task.OutgoingRoutes
                    .Where(r => !r.IsArchived)
                    .OrderBy(r => r.Order)
                    .Select(r => new RouteEditModel
                    {
                        Id = r.Id,
                        OutcomeKey = r.Outcome?.OutcomeKey ?? string.Empty,
                        NextTaskLocalId = localIds.TryGetValue(r.NextTaskDefinitionId, out var g) ? g : null,
                        ConditionKey = r.ConditionKey,
                        IsDefault = r.IsDefault,
                        IsReworkRoute = r.IsReworkRoute,
                        Order = r.Order
                    })],
                Triggers = [.. task.Triggers
                    .Where(t => !t.IsArchived)
                    .OrderBy(t => t.Order)
                    .Select(t => new TriggerEditModel
                    {
                        Id = t.Id,
                        TriggerKey = t.TriggerKey,
                        Event = t.Event,
                        CustomEventName = t.CustomEventName,
                        Configuration = new Dictionary<string, string?>(TriggerConfig.Parse(t.Configuration).All),
                        Condition = t.Condition,
                        Order = t.Order,
                        IsActive = t.IsActive,
                        DispatchMode = t.DispatchMode,
                        FailurePolicy = t.FailurePolicy
                    })]
            });
        }

        // `localIds` is the same database-id → local-id map the routes use, so an
        // attachment ends up pointing at exactly the task a route would.
        model.SubWorkflows =
        [
            .. version.SubWorkflowAttachments
                .Where(a => !a.IsArchived)
                .Select(a => new SubWorkflowAttachmentEditModel
                {
                    Id = a.Id,
                    SubWorkflowDefinitionId = a.SubWorkflowDefinitionId,
                    TaskLocalId = a.TaskDefinitionId is { } id
                                  && localIds.TryGetValue(id, out var local)
                        ? local
                        : null,
                    IsAutomatic = a.IsAutomatic,
                    IsBlocking = a.IsBlocking,
                    AllowMultiple = a.AllowMultiple
                })
        ];

        return model;
    }

    private static Guid? EntryTaskDefinitionLocalId(
        WorkflowDefinitionVersion version, Dictionary<int, Guid> localIds) =>
        version.EntryTaskDefinitionId is { } id && localIds.TryGetValue(id, out var g) ? g : null;

    /// <summary>
    /// Projects the edit model onto the entity shape the validator expects. Nothing here
    /// is persisted; the ids exist only so routes and the entry point have something to
    /// point at.
    /// </summary>
    private static WorkflowDefinitionVersion ToTransientVersion(WorkflowEditModel model)
    {
        var ids = new Dictionary<Guid, int>();
        var next = 1;

        foreach (var task in model.Tasks)
        {
            ids[task.LocalId] = next++;
        }

        var version = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = model.DefinitionId,
            Version = model.Version,
            EntryTaskDefinitionId =
                model.EntryTaskLocalId is { } entry && ids.TryGetValue(entry, out var entryId)
                    ? entryId
                    : null
        };

        foreach (var task in model.Tasks)
        {
            var row = new WorkflowTaskDefinition
            {
                Id = ids[task.LocalId],
                WorkflowDefinitionVersionId = model.VersionId,
                TaskTypeDefinitionId = 0,
                TaskType = new TaskTypeDefinition
                {
                    Key = task.TaskTypeKey,
                    DisplayName = task.Label
                },
                DisplayName = task.DisplayName,
                IsRequired = task.IsRequired,
                IsAdHoc = task.IsAdHoc,
                IsBlocking = task.IsBlocking,
                IsForkable = task.IsForkable,
                IsConvergencePoint = task.IsConvergencePoint,
                IsTerminal = task.IsTerminal,
                AssignmentRoleKey = task.AssignmentRoleKey
            };

            foreach (var outcome in task.Outcomes)
            {
                row.ValidOutcomes.Add(new TaskOutcomeDefinition
                {
                    TaskDefinitionId = row.Id,
                    OutcomeKey = outcome.OutcomeKey,
                    DisplayName = outcome.DisplayName,
                    Order = outcome.Order
                });
            }

            foreach (var route in task.Routes)
            {
                if (route.NextTaskLocalId is not { } target || !ids.TryGetValue(target, out var targetId))
                {
                    continue;   // reported separately by StructuralErrors
                }

                // By reference, not by id: nothing in this graph is saved, so every id is
                // zero and an id would make every route point at the same outcome. A route
                // naming an outcome the task does not declare gets a null here, and the
                // validator reports it as ROUTE_NO_OUTCOME.
                var outcomeRow = row.ValidOutcomes.FirstOrDefault(o =>
                    string.Equals(o.OutcomeKey, route.OutcomeKey, StringComparison.OrdinalIgnoreCase));

                row.OutgoingRoutes.Add(new TaskRoute
                {
                    TaskDefinitionId = row.Id,
                    TaskOutcomeDefinitionId = outcomeRow?.Id ?? 0,
                    Outcome = outcomeRow,
                    NextTaskDefinitionId = targetId,
                    ConditionKey = route.ConditionKey,
                    IsDefault = route.IsDefault,
                    IsReworkRoute = route.IsReworkRoute,
                    Order = route.Order
                });
            }

            version.Tasks.Add(row);
        }

        // Without this the four attachment rules in the validator are unreachable from
        // ValidateAsync, which is the only validation the builder UI sees before a save.
        version.SubWorkflowAttachments =
        [
            .. model.SubWorkflows.Select(a => new SubWorkflowAttachment
            {
                SubWorkflowDefinitionId = a.SubWorkflowDefinitionId,
                WorkflowDefinitionVersionId = model.VersionId,
                // Resolved through the same synthetic ids the tasks get, so
                // attachment.unknown-task compares like with like. An unresolvable local
                // id lands as null and is reported separately by StructuralErrors, the
                // same division of labour a route pointing at a deleted task gets.
                TaskDefinitionId = a.TaskLocalId is { } localId && ids.TryGetValue(localId, out var id)
                    ? id
                    : null,
                IsAutomatic = a.IsAutomatic,
                IsBlocking = a.IsBlocking,
                AllowMultiple = a.AllowMultiple
            })
        ];

        return version;
    }

    private static string? Serialise(Dictionary<string, string?> configuration)
    {
        var populated = configuration
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .ToDictionary(p => p.Key, p => p.Value);

        return populated.Count == 0 ? null : JsonSerializer.Serialize(populated);
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
