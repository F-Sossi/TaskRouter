using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Fields;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>
/// Sets fields on the run's subject, or on records the host relates to it, as the host's
/// <see cref="WorkflowFieldCatalog"/> allows.
///
/// <para>Registered by <c>AddWorkflowFields</c>, so a host that declares no fields never
/// offers it. Fails as a whole: every assignment on every record, or none.</para>
///
/// <para>Order of work is deliberate. Everything that can be checked without touching
/// data — target, fields, values, permission — is checked first, so a misconfigured
/// trigger fails before any record is loaded. Then the undo scope begins, which saves the
/// engine's pending work: records added earlier in the same event are in the database
/// before the target's resolver looks for them. Only then does it resolve and write.</para>
///
/// <para>Meant to run <see cref="TriggerDispatchMode.InTransaction"/>, its default. It can be
/// dispatched after commit or in the background, but then: <see cref="TriggerFailurePolicy.FailOperation"/>
/// cannot undo the step, which has already committed; the authorization policy runs with
/// no HTTP request behind it, so it must judge by the context's actor id and not by an
/// HttpContext; and a failed attempt's "Undone:" detail is not recorded, because the
/// outbox records only the outcome of a failed attempt.</para>
///
/// <para><b>A test run writes nothing.</b> The builder's test pane runs against a made-up
/// subject with no records behind it, and a test run is hard-deleted afterwards, which
/// could not take back a write to a real one. So on a test run the configuration is
/// checked against the subject type the workflow is published for, the values are
/// rendered, and the execution reports what would have been set.</para>
/// </summary>
public sealed class UpdateFieldsTrigger(
    WorkflowFieldCatalog catalog,
    IWorkflowDbContext db,
    IWorkflowAuthorizationPolicy policy,
    ILogger<UpdateFieldsTrigger> logger) : IWorkflowTrigger
{
    private const string NothingToChange = "Nothing to change.";

    public string Key => BuiltInTriggerKeys.UpdateFields;

    public TriggerDescriptor Describe() => new(
        Key,
        "Update fields",
        "Sets fields on the document this workflow runs against, or on records related to it such as its "
        + "work items, to a fixed or calculated value. For example: set the work item's completion to 60 when "
        + "this step is completed.",
        SupportedEvents: Enum.GetValues<WorkflowEventKind>(),
        Parameters:
        [
            new("target", "Target", TriggerParameterKind.FieldTarget, Required: true),
            new("assignments", "Fields", TriggerParameterKind.FieldAssignments, Required: true,
                HelpText: "Values may use {actor}, {today}, {today+Nd}, {now}, {var:name} and the task tokens.")
        ],
        // In the transaction, so the change and the step that caused it commit together.
        DefaultDispatch: TriggerDispatchMode.InTransaction,
        // A step whose document change was refused has not really happened.
        DefaultFailurePolicy: TriggerFailurePolicy.FailOperation,
        Help:
        [
            new("What it does",
            [
                "Sets fields on the document, or on records related to it, to a fixed value or a token such as {today} or {actor}.",
                "First checks the target, the fields, each value, and that the person may make the change. "
                    + "A bad setup fails here, before anything is touched.",
                "Writes only the values that actually change, then saves them together. If any part fails, none of it is kept.",
                "Records exactly what changed, for example \"Completion %: 0 → 60\"."
            ]),
            new("Settings",
            [
                "Target: which record or records: the document, or the ones the application relates to it.",
                "Fields: one or more field and value pairs."
            ]),
            new("Things to know",
            [
                "Runs as part of saving the step, and a failure stops the step completing. Choose Log and continue to let the step complete anyway.",
                "A test run checks everything and reports what it would set, but writes nothing.",
                "Somebody completing the step is shown what will be set and asked to confirm."
            ])
        ]);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subjectType = await SubjectTypeAsync(context, ct).ConfigureAwait(false);

        if (subjectType is null)
        {
            context.Report.Detail = "Test run, nothing written. This workflow is not tied to one kind of "
                + "document, so there is nothing to check its field updates against.";
            return;
        }

        var (target, assignments, planned) = await PlanAsync(context, subjectType, ct).ConfigureAwait(false);

        if (context.Run.IsTest)
        {
            context.Report.Detail = $"Test run, nothing written. Would set on '{target.Label}': " + Summary(planned);
            return;
        }

        await WorkflowAuthorization.EnsureAllowedAsync(
            policy,
            WorkflowOperation.UpdateSubjectFields,
            new WorkflowAuthorizationContext(context.ActorId, context.Task, context.Subject, null)
            {
                FieldTarget = target.Key,
                Fields = [.. assignments.Select(a => a.Field)]
            },
            logger,
            ct).ConfigureAwait(false);

        var services = context.Services
            ?? throw new InvalidOperationException("Field updates need the trigger's service provider.");

        var write = new FieldWriteContext(context.ActorId, context.Subject, context.Task, services);
        var changes = new List<string>();

        // Not `await using`: the scope must be told why it is being abandoned, so that if the
        // undo itself fails its error carries the original refusal rather than replacing it.
        var scope = await FieldWriteScope.BeginAsync(db, ct).ConfigureAwait(false);

        try
        {
            var entities = await target.Resolve(new FieldResolveContext(context.Subject, context.Task, services), ct)
                .ConfigureAwait(false);

            if (target.Cardinality == FieldCardinality.One && entities.Count != 1)
            {
                // The resolver takes at most two, so "more than one" is all the count can say.
                throw new InvalidOperationException(entities.Count == 0
                    ? $"'{target.Label}' found nothing to update."
                    : $"'{target.Label}' matched more than one record; it must match exactly one.");
            }

            if (entities.Count == 0)
            {
                // A Many target with nothing under it: success, with nothing written. The
                // scope began before resolving, so it is closed here with nothing in it.
                await scope.SaveAndCompleteAsync(ct).ConfigureAwait(false);
                context.Report.Detail = NothingToChange;
                return;
            }

            foreach (var entity in entities)
            {
                var changed = false;

                foreach (var (field, value) in planned)
                {
                    if (!field.AppliesTo(entity))
                    {
                        // The host's resolver returned a type the field is not declared on.
                        throw new InvalidOperationException(
                            $"This step's field update is misconfigured: '{target.Label}' found a record without "
                            + $"'{field.Label}'. Ask whoever maintains this workflow.");
                    }

                    var before = field.Read(entity);

                    if (Equals(before, value))
                    {
                        continue;
                    }

                    await field.Write(entity, value, write, ct).ConfigureAwait(false);
                    changed = true;

                    changes.Add(
                        $"{target.Key} {scope.KeyOf(entity)} {field.Label}: "
                        + $"{Show(field, before)} → {Show(field, value)}");
                }

                if (changed)
                {
                    foreach (var hook in target.OnWritten.Where(h => h.AppliesTo(entity)))
                    {
                        await hook.Run(entity, write, ct).ConfigureAwait(false);
                    }
                }
            }

            await scope.SaveAndCompleteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Recorded before the undo, so a failure says how far it got — and that none of it stuck.
            context.Report.Detail = changes.Count == 0 ? null : "Undone: " + string.Join("; ", changes);

            // Returns normally when the undo succeeds, so the original refusal is what is rethrown.
            // Throws a composite (original + undo failure) when it does not, having rolled back
            // the whole transaction so nothing partial can commit.
            await scope.AbandonAsync(ex).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }

        context.Report.Detail = changes.Count == 0 ? NothingToChange : string.Join("; ", changes);
    }

    public async Task<string?> DescribeActionAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (await SubjectTypeAsync(context, ct).ConfigureAwait(false) is not { } subjectType)
        {
            return "Update fields (test run: nothing will be written)";
        }

        try
        {
            var (target, _, planned) = await PlanAsync(context, subjectType, ct).ConfigureAwait(false);

            return $"Set {Summary(planned)} on {Lower(target.Label)}"
                + (context.Run.IsTest ? " (test run: checked, nothing will be written)" : string.Empty);
        }
        catch (InvalidOperationException ex)
        {
            // Worth knowing before completing: by default a failed update stops the step.
            return $"Update fields, which will fail: {ex.Message}";
        }
    }

    /// <summary>
    /// The run's subject type, or on a test run the one its workflow is published for:
    /// a test run's subject is made up, so it is the workflow's own type that says what its
    /// field updates may touch. Null when a test run's workflow names no type.
    /// </summary>
    private async Task<string?> SubjectTypeAsync(WorkflowTriggerContext context, CancellationToken ct) =>
        context.Run.IsTest
            ? await PublishedForAsync(context.Run.WorkflowDefinitionVersionId, ct).ConfigureAwait(false)
            : context.Subject.SubjectType;

    /// <summary>
    /// Everything that can be checked without touching data: the target, the fields, and
    /// each value rendered and converted. Throws, in a sentence, on the first problem.
    /// </summary>
    private async Task<(TargetRegistration Target, IReadOnlyList<FieldAssignment> Assignments,
        List<(FieldRegistration Field, object? Value)> Planned)> PlanAsync(
        WorkflowTriggerContext context, string subjectType, CancellationToken ct)
    {
        var targetKey = context.Config.GetString("target");

        if (string.IsNullOrWhiteSpace(targetKey))
        {
            throw new InvalidOperationException("This step's field update has no target configured.");
        }

        if (!catalog.TryGetTarget(subjectType, targetKey, out var target))
        {
            throw new InvalidOperationException(UnknownTarget(targetKey, subjectType));
        }

        var assignments = FieldAssignments.Parse(context.Config.GetString("assignments"));

        if (assignments.Count == 0)
        {
            throw new InvalidOperationException("This step's field update has no fields configured.");
        }

        var planned = new List<(FieldRegistration Field, object? Value)>();

        foreach (var assignment in assignments)
        {
            var field = target.Fields.FirstOrDefault(f => f.Key == assignment.Field)
                ?? throw new InvalidOperationException(
                    $"{subjectType} has no field '{assignment.Field}' on '{target.Label}'.");

            var text = await TriggerTemplate.RenderAsync(assignment.Value, context, ct).ConfigureAwait(false);

            if (!FieldValueConverter.TryConvert(text, field.ValueType, out var value, out var error))
            {
                throw new InvalidOperationException($"{field.Label}: {error}");
            }

            planned.Add((field, value));
        }

        return (target, assignments, planned);
    }

    private static string Summary(List<(FieldRegistration Field, object? Value)> planned) =>
        string.Join("; ", planned.Select(p => $"{p.Field.Label} → {Show(p.Field, p.Value)}"));

    // "This document" reads as "this document" mid-sentence; an acronym-led label is left alone.
    private static string Lower(string label) =>
        label.Length > 1 && char.IsUpper(label[0]) && !char.IsUpper(label[1])
            ? char.ToLowerInvariant(label[0]) + label[1..]
            : label;

    /// <summary>The subject type a test run's workflow version is published for; null means any.</summary>
    private Task<string?> PublishedForAsync(int versionId, CancellationToken ct) =>
        db.WorkflowDefinitionVersions
            .Where(v => v.Id == versionId)
            .Select(v => v.SubjectType)
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// The refusal for a target the subject type does not have. The builder's publish check
    /// (FIELDS_UNKNOWN_TARGET) says the same, after the task's label.
    /// </summary>
    internal static string UnknownTarget(string targetKey, string subjectType) =>
        $"'{targetKey}' is not something a {subjectType} workflow can update.";

    /// <summary>
    /// A value as the change summary shows it: a choice by the label the author picked it
    /// by, falling back to the member name; anything else as <see cref="FieldValueConverter.Format"/> has it.
    /// </summary>
    private static string Show(FieldRegistration field, object? value)
    {
        var text = FieldValueConverter.Format(value);

        if (text is not null && field.Kind == FieldKind.Choice)
        {
            text = field.Choices.FirstOrDefault(c => c.Value == text)?.Label ?? text;
        }

        return text ?? "(empty)";
    }
}
