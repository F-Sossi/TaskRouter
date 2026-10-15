# Field-Update Triggers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a workflow author build a trigger that sets fields on the run's document (and the host's related records) from the builder, over a field catalog the host declares in code, with the host's rules still enforced.

**Architecture:** Core gains the serialisable vocabulary (kinds, descriptors, assignments, value conversion). EntityFrameworkCore gains the catalog and its fluent builder, the built-in `workflow.updateFields` trigger, an undo scope that makes a refused update leave nothing behind, and builder-client support (describe, search options, validate). AspNetCore and Blazor expose it to the builder UI. The demo host declares a catalog over its documents and work items, with a small status state machine so refusals are real.

**Tech Stack:** .NET 10, EF Core 10 (SQL Server), MSTest 4, ASP.NET Core minimal APIs, Blazor + MudBlazor.

**Spec:** `docs/superpowers/specs/2026-10-14-field-update-triggers-design.md`

**Scope:** this repository only. The TRDIS wiring (`TrdisWorkflowFields*.cs`, `TransitionToAsync` on its two services) is a follow-up plan against `../Dev`, written once this ships as a preview package.

**Deviation from the spec, deliberate:** the catalog and its builder live in **EntityFrameworkCore**, not Core, because a target's resolver is an `IQueryable<T>` that must be materialised with EF. Core keeps everything that crosses the wire. The demo does not seed a field-update workflow (it would change the trigger counts existing tests assert on); the browser check in Task 13 authors one instead. Two spec checks are dropped as not worth their cost: the startup check that every catalog subject type is in `WorkflowBuilderOptions.SubjectTypes` (the options are configured separately and a mismatch is harmless — the type is simply never offered), and refusing `LogAndContinue` at publish on providers without savepoints. In its place, `FieldWriteScope.BeginAsync` refuses at run time, before writing anything, when the transaction reports no savepoint support — which SQL Server does when `MultipleActiveResultSets=True` (found in Task 5's review; the original claim that every supported provider has savepoints was wrong).

---

## Conventions for whoever executes this

- Work in `/home/frank/repos/TaskRouter/.worktrees/field-triggers`. Never `cd` to the main checkout.
- Integration tests need SQL Server: `SA_PASSWORD` (or `WORKFLOW_TEST_CONNECTION`) must be set, as for the existing suite.
- Run one class: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~<ClassName>"`
- Run everything: `dotnet test TaskRouter.slnx` — baseline is **551 passing**.
- Build must stay at **0 code warnings** (the NU1902/NU1903 package advisories are pre-existing and out of scope).
- Commit messages follow the repo: one plain-English sentence describing the change, no `feat:` prefixes, and end with:

```
Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01LTmJEwgedh8dpBB1fZppCz
```

- Match the surrounding code: XML doc comments explain *why*, `ConfigureAwait(false)` on every await in `src/`, file-scoped namespaces.

## File map

| File | Responsibility |
|---|---|
| `src/TaskRouter.Core/Fields/SubjectFields.cs` (new) | `FieldKind`, `FieldCardinality`, `FieldChoice`, `SubjectFieldInfo`, `SubjectTargetInfo`, `FieldAssignment`, `FieldAssignments` |
| `src/TaskRouter.Core/Fields/FieldValueConverter.cs` (new) | Text → typed value, kind inference, enum choices, display formatting |
| `src/TaskRouter.Core/Abstractions/Triggers.cs` | `TriggerExecutionReport`, `WorkflowTriggerContext.Report` |
| `src/TaskRouter.Core/Abstractions/Authorization.cs` | `WorkflowOperation.UpdateSubjectFields`, `FieldTarget`/`Fields` on the context |
| `src/TaskRouter.Core/Model/Enums.cs` | `TriggerParameterKind.FieldTarget`, `.FieldAssignments` |
| `src/TaskRouter.Core/Model/Entities.cs` | `TriggerExecution.Detail` |
| `src/TaskRouter.Core/Builder/IWorkflowBuilderClient.cs` | `GetSubjectFieldsAsync`, `SearchFieldOptionsAsync` |
| `src/TaskRouter.EntityFrameworkCore/Fields/FieldContexts.cs` (new) | `FieldResolveContext`, `FieldWriteContext`, `FieldOptionsContext`, `FieldWriter<,>` |
| `src/TaskRouter.EntityFrameworkCore/Fields/WorkflowFieldsBuilder.cs` (new) | Fluent declaration API and composition into a catalog |
| `src/TaskRouter.EntityFrameworkCore/Fields/WorkflowFieldCatalog.cs` (new) | The built catalog and its internal registrations |
| `src/TaskRouter.EntityFrameworkCore/Fields/FieldWriteScope.cs` (new) | Savepoint + change-tracker revert |
| `src/TaskRouter.EntityFrameworkCore/Fields/UpdateFieldsTrigger.cs` (new) | `workflow.updateFields` |
| `src/TaskRouter.EntityFrameworkCore/Triggers/BuiltInTriggers.cs` | `UpdateFields` key, `TriggerTemplate.RenderAsync`, SetVariable/Notify use it |
| `src/TaskRouter.EntityFrameworkCore/Triggers/TriggerDispatcher.cs`, `OutboxProcessor.cs` | Copy `Report.Detail` onto the execution |
| `src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs` | `AddWorkflowFields` |
| `src/TaskRouter.EntityFrameworkCore/Builder/EfWorkflowBuilderClient.cs` | Describe, search, validate |
| `src/TaskRouter.AspNetCore/BuilderEndpoints.cs` | Two GET routes |
| `src/TaskRouter.Blazor/Http/HttpWorkflowBuilderClient.cs` | Two client calls |
| `src/TaskRouter.Blazor/SubjectFieldsEditModel.cs` (new) | Editor state, testable without a renderer |
| `src/TaskRouter.Blazor/SubjectFieldsEditor.razor` (new) | The target/field/value editor |
| `src/TaskRouter.Blazor/TriggerConfigForm.razor`, `TriggerEditDialog.razor`, `WorkflowBuilder.razor` | Pass the subject type through; render the editor |
| `samples/DemoDocuments.Server/Domain/DemoDomain.cs` | `DocumentStatus.OnHold` |
| `samples/DemoDocuments.Server/Workflow/DemoDocumentStatusRules.cs` (new) | Demo status transitions |
| `samples/DemoDocuments.Server/Workflow/DemoWorkflowFields.cs` (new) | The demo's catalog |
| `samples/DemoDocuments.Server/Workflow/DemoAuthorizationPolicy.cs` | Allow `UpdateSubjectFields` to whoever is doing the task |
| `samples/DemoDocuments.Server/Program.cs` | `.AddWorkflowFields(DemoWorkflowFields.Configure)` |
| `samples/DemoDocuments.Server/Data/Migrations/*TriggerExecutionDetail*` (generated) | The new column |
| `tests/TaskRouter.Tests/TestHost.cs`, `EndpointTestHost.cs` | Register the demo catalog |
| `tests/TaskRouter.Tests/FieldValueConverterTests.cs` (new) | Conversion |
| `tests/TaskRouter.Tests/FieldCatalogTests.cs` (new) | Declaration and composition |
| `tests/TaskRouter.Tests/TriggerReportTests.cs` (new) | Detail recording and tokens |
| `tests/TaskRouter.Tests/UpdateFieldsTriggerTests.cs` (new) | End-to-end behaviour |
| `tests/TaskRouter.Tests/SubjectFieldsBuilderTests.cs` (new) | Builder client + HTTP |
| `tests/TaskRouter.Tests/SubjectFieldsEditModelTests.cs` (new) | Editor state |

---

### Task 1: Field vocabulary in Core

**Files:**
- Create: `src/TaskRouter.Core/Fields/SubjectFields.cs`
- Create: `src/TaskRouter.Core/Fields/FieldValueConverter.cs`
- Test: `tests/TaskRouter.Tests/FieldValueConverterTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.ComponentModel.DataAnnotations;

using TaskRouter.Core.Fields;

namespace TaskRouter.Tests;

/// <summary>
/// The one place an author's text becomes a typed value. Everything stored in a trigger's
/// configuration is a string, so every field write goes through here.
/// </summary>
[TestClass]
public class FieldValueConverterTests
{
    private enum Phase
    {
        [Display(Name = "Not started")] Scheduled = 0,
        [Display(Name = "On Hold")] Hold = 4,
        Done = 5
    }

    [TestMethod]
    public void An_enum_is_written_by_member_name()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("Hold", typeof(Phase), out var value, out _));
        Assert.AreEqual(Phase.Hold, value);
    }

    [TestMethod]
    public void An_enum_number_is_refused_so_a_renumbering_cannot_change_meaning()
    {
        Assert.IsFalse(FieldValueConverter.TryConvert("4", typeof(Phase), out _, out var error));
        StringAssert.Contains(error, "Hold");
    }

    [TestMethod]
    public void An_enum_display_name_is_refused()
    {
        Assert.IsFalse(FieldValueConverter.TryConvert("On Hold", typeof(Phase), out _, out _));
    }

    [TestMethod]
    public void Null_clears_a_nullable_field()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert(null, typeof(DateTime?), out var value, out _));
        Assert.IsNull(value);
    }

    [TestMethod]
    public void Empty_clears_a_nullable_non_text_field()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("", typeof(DateTime?), out var value, out _));
        Assert.IsNull(value);
    }

    [TestMethod]
    public void Empty_text_stays_empty_text()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("", typeof(string), out var value, out _));
        Assert.AreEqual("", value);
    }

    [TestMethod]
    public void Null_is_refused_for_a_non_nullable_field()
    {
        Assert.IsFalse(FieldValueConverter.TryConvert(null, typeof(Phase), out _, out var error));
        Assert.AreEqual("A value is required.", error);
    }

    [TestMethod]
    public void Dates_bools_and_numbers_parse_invariantly()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("2026-10-20", typeof(DateTime), out var date, out _));
        Assert.AreEqual(new DateTime(2026, 10, 20), date);

        Assert.IsTrue(FieldValueConverter.TryConvert("true", typeof(bool), out var flag, out _));
        Assert.AreEqual(true, flag);

        Assert.IsTrue(FieldValueConverter.TryConvert("2.5", typeof(double), out var hours, out _));
        Assert.AreEqual(2.5, hours);

        Assert.IsTrue(FieldValueConverter.TryConvert("12", typeof(int?), out var id, out _));
        Assert.AreEqual(12, id);
    }

    [TestMethod]
    public void Unparseable_text_says_what_was_wrong()
    {
        Assert.IsFalse(FieldValueConverter.TryConvert("next week", typeof(DateTime), out _, out var error));
        Assert.AreEqual("'next week' is not a date.", error);
    }

    [TestMethod]
    public void Kinds_are_inferred_from_the_property_type()
    {
        Assert.AreEqual(FieldKind.Text, FieldValueConverter.InferKind(typeof(string)));
        Assert.AreEqual(FieldKind.Boolean, FieldValueConverter.InferKind(typeof(bool)));
        Assert.AreEqual(FieldKind.Date, FieldValueConverter.InferKind(typeof(DateTime?)));
        Assert.AreEqual(FieldKind.Number, FieldValueConverter.InferKind(typeof(int)));
        Assert.AreEqual(FieldKind.Choice, FieldValueConverter.InferKind(typeof(Phase?)));
    }

    [TestMethod]
    public void An_unsupported_property_type_is_refused_when_declared()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => FieldValueConverter.InferKind(typeof(Guid)));
    }

    [TestMethod]
    public void Choices_use_the_display_name_and_fall_back_to_the_member_name()
    {
        var choices = FieldValueConverter.ChoicesOf(typeof(Phase?));

        CollectionAssert.AreEqual(
            new[] { new FieldChoice("Scheduled", "Not started"), new FieldChoice("Hold", "On Hold"), new FieldChoice("Done", "Done") },
            choices.ToArray());
    }

    [TestMethod]
    public void Values_format_for_the_change_summary()
    {
        Assert.IsNull(FieldValueConverter.Format(null));
        Assert.AreEqual("Hold", FieldValueConverter.Format(Phase.Hold));
        Assert.AreEqual("2026-10-20", FieldValueConverter.Format(new DateTime(2026, 10, 20)));
        Assert.AreEqual("2.5", FieldValueConverter.Format(2.5));
    }

    [TestMethod]
    public void Assignments_round_trip_through_their_stored_json()
    {
        var json = FieldAssignments.Serialize([new("Status", "Hold"), new("Comments", null)]);

        var back = FieldAssignments.Parse(json);

        CollectionAssert.AreEqual(new[] { new FieldAssignment("Status", "Hold"), new FieldAssignment("Comments", null) }, back.ToArray());
    }

    [TestMethod]
    public void Malformed_assignments_are_a_format_error()
    {
        Assert.ThrowsExactly<FormatException>(() => FieldAssignments.Parse("{not json"));
    }

    [TestMethod]
    public void No_assignments_parse_as_empty()
    {
        Assert.IsEmpty(FieldAssignments.Parse(null));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~FieldValueConverterTests"`
Expected: build error — `TaskRouter.Core.Fields` does not exist.

- [ ] **Step 3: Write `SubjectFields.cs`**

```csharp
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
            return JsonSerializer.Deserialize<List<FieldAssignment>>(json, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new FormatException("The field assignments are not valid JSON.", ex);
        }
    }
}
```

- [ ] **Step 4: Write `FieldValueConverter.cs`**

```csharp
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;

namespace TaskRouter.Core.Fields;

/// <summary>
/// Turns an author's text into the value a property holds, and back for display.
///
/// <para>Enums convert by member name only. A number would silently change meaning if the
/// host renumbered the enum, and a display name would break on a relabel; the member name
/// is the one spelling a host changes deliberately.</para>
/// </summary>
public static class FieldValueConverter
{
    public static bool TryConvert(string? text, Type type, out object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(type);

        value = null;
        error = null;

        var underlying = Nullable.GetUnderlyingType(type);
        var target = underlying ?? type;
        var nullable = underlying is not null || !type.IsValueType;

        if (text is null || (text.Length == 0 && target != typeof(string)))
        {
            if (nullable)
            {
                return true;
            }

            error = "A value is required.";
            return false;
        }

        if (target == typeof(string))
        {
            value = text;
            return true;
        }

        if (target.IsEnum)
        {
            // Enum.TryParse accepts "4" as well as "Hold"; the digit check refuses the first.
            if (!text.Any(char.IsDigit)
                && Enum.TryParse(target, text, ignoreCase: false, out var parsed)
                && Enum.IsDefined(target, parsed!))
            {
                value = parsed;
                return true;
            }

            error = $"'{text}' is not one of {string.Join(", ", Enum.GetNames(target))}.";
            return false;
        }

        var invariant = CultureInfo.InvariantCulture;

        (bool ok, object? parsedValue, string what) result = target switch
        {
            _ when target == typeof(bool) => (bool.TryParse(text, out var b), b, "true or false"),
            _ when target == typeof(DateTime) => (
                DateTime.TryParse(text, invariant, DateTimeStyles.RoundtripKind, out var d), d, "a date"),
            _ when target == typeof(int) => (
                int.TryParse(text, NumberStyles.Integer, invariant, out var i), i, "a whole number"),
            _ when target == typeof(long) => (
                long.TryParse(text, NumberStyles.Integer, invariant, out var l), l, "a whole number"),
            _ when target == typeof(double) => (
                double.TryParse(text, NumberStyles.Float, invariant, out var f), f, "a number"),
            _ when target == typeof(decimal) => (
                decimal.TryParse(text, NumberStyles.Number, invariant, out var m), m, "a number"),
            _ => throw new NotSupportedException($"Fields of type {target.Name} are not supported.")
        };

        if (result.ok)
        {
            value = result.parsedValue;
            return true;
        }

        error = $"'{text}' is not {result.what}.";
        return false;
    }

    /// <summary>The kind a property's type implies. Throws for a type no field may have.</summary>
    public static FieldKind InferKind(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string)) return FieldKind.Text;
        if (target == typeof(bool)) return FieldKind.Boolean;
        if (target == typeof(DateTime)) return FieldKind.Date;
        if (target.IsEnum) return FieldKind.Choice;
        if (target == typeof(int) || target == typeof(long)
            || target == typeof(double) || target == typeof(decimal)) return FieldKind.Number;

        throw new NotSupportedException(
            $"A field of type {target.Name} cannot be offered to authors. Supported: text, "
            + "whole and decimal numbers, true/false, dates, and enums.");
    }

    /// <summary>An enum's members, labelled by <c>[Display(Name)]</c> where the host gave one.</summary>
    public static IReadOnlyList<FieldChoice> ChoicesOf(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);

        var target = Nullable.GetUnderlyingType(enumType) ?? enumType;

        return [.. target.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => new FieldChoice(
                f.Name,
                f.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? f.Name))];
    }

    /// <summary>A value as the change summary shows it. Null means empty.</summary>
    public static string? Format(object? value) => value switch
    {
        null => null,
        DateTime d when d.TimeOfDay == TimeSpan.Zero => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };
}
```

Note: `ChoicesOf` relies on `GetFields` returning members in declaration order, which the runtime does for enums.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~FieldValueConverterTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/TaskRouter.Core/Fields tests/TaskRouter.Tests/FieldValueConverterTests.cs
git commit -m "Give Core the words for a field an author may set, and the conversion into it"
```

---

### Task 2: Model additions, and the migration they need

**Files:**
- Modify: `src/TaskRouter.Core/Model/Entities.cs` (class `TriggerExecution`, ~line 592)
- Modify: `src/TaskRouter.Core/Abstractions/Triggers.cs`
- Modify: `src/TaskRouter.Core/Abstractions/Authorization.cs` (enum `WorkflowOperation`, record `WorkflowAuthorizationContext`)
- Modify: `src/TaskRouter.Core/Model/Enums.cs` (enum `TriggerParameterKind`, ~line 139)
- Create (generated): `samples/DemoDocuments.Server/Data/Migrations/<timestamp>_TriggerExecutionDetail.cs` and `.Designer.cs`; modifies `DemoDbContextModelSnapshot.cs`

- [ ] **Step 1: Add `Detail` to `TriggerExecution`**, directly after `Error`:

```csharp
    public string? Error { get; set; }

    /// <summary>
    /// What the trigger reports it did, beyond succeeding or failing — for a field update,
    /// each change as "before → after". Null for triggers that report nothing.
    /// </summary>
    public string? Detail { get; set; }
```

- [ ] **Step 2: Add the report to the trigger context.** In `Triggers.cs`, add to `WorkflowTriggerContext` after `Services`:

```csharp
    /// <summary>
    /// What this execution should record about itself. A fresh one per trigger execution;
    /// the dispatcher copies <see cref="TriggerExecutionReport.Detail"/> onto the
    /// execution row whether the trigger succeeds or throws.
    /// </summary>
    public TriggerExecutionReport Report { get; } = new();
```

and at the end of the file:

```csharp
/// <summary>What a trigger wants recorded about what it did.</summary>
public sealed class TriggerExecutionReport
{
    public string? Detail { get; set; }
}
```

- [ ] **Step 3: Add the operation.** In `Authorization.cs`, add as the **last** member of `WorkflowOperation` (after `PreAssignTask`):

```csharp
    PreAssignTask,

    /// <summary>
    /// A field-update trigger writing to the run's subject or a record related to it.
    /// Checked as the person whose action raised the event, like every other trigger
    /// action; the deadline sweep runs as <see cref="Model.WorkflowActors.System"/> and is
    /// not checked. <see cref="WorkflowAuthorizationContext.FieldTarget"/> and
    /// <see cref="WorkflowAuthorizationContext.Fields"/> say what is being written.
    /// </summary>
    UpdateSubjectFields,
```

and replace the `WorkflowAuthorizationContext` record with:

```csharp
public sealed record WorkflowAuthorizationContext(
    string ActorId,
    WorkflowTaskSnapshot? Task,
    WorkflowSubject? Subject,
    int? WorkflowDefinitionId)
{
    /// <summary>For <see cref="WorkflowOperation.UpdateSubjectFields"/>: the target's key.</summary>
    public string? FieldTarget { get; init; }

    /// <summary>For <see cref="WorkflowOperation.UpdateSubjectFields"/>: the field keys being set.</summary>
    public IReadOnlyList<string> Fields { get; init; } = [];
}
```

Keep any XML doc comment that sits above the record.

- [ ] **Step 4: Add the parameter kinds.** In `Enums.cs`, append to `TriggerParameterKind` after `Template`:

```csharp
    Template,
    /// <summary>A target from the host's field catalog for the workflow's subject type.</summary>
    FieldTarget,
    /// <summary>A JSON list of field assignments against the configured target.</summary>
    FieldAssignments
```

- [ ] **Step 5: Build, and confirm the migration guard now fails**

Run: `dotnet build TaskRouter.slnx -v q` — expected 0 errors.
Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~MigrationTests"`
Expected: `The_model_and_the_migrations_have_not_drifted` FAILS (the model has a column no migration adds).

- [ ] **Step 6: Scaffold the migration**

```bash
dotnet ef migrations add TriggerExecutionDetail \
  --project samples/DemoDocuments.Server --context DemoDbContext \
  --output-dir Data/Migrations
```

Open the generated `<timestamp>_TriggerExecutionDetail.cs`. `Up` must contain exactly one `AddColumn<string>` named `Detail` on `TaskRouterTriggerExecutions`, `nullable: true`; `Down` drops it. If it contains anything else, stop and investigate — something else has drifted.

- [ ] **Step 7: Run the migration guard and the authorization tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~AuthorizationTests"`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/TaskRouter.Core samples/DemoDocuments.Server/Data/Migrations
git commit -m "Let a trigger record what it did, and name the operation a field update is checked as"
```

---

### Task 3: Record a trigger's report on its execution

**Files:**
- Modify: `src/TaskRouter.EntityFrameworkCore/Triggers/TriggerDispatcher.cs` (`RunInlineAsync`)
- Modify: `src/TaskRouter.EntityFrameworkCore/Triggers/OutboxProcessor.cs` (`ExecuteIsolatedAsync` and its caller)
- Test: `tests/TaskRouter.Tests/TriggerReportTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// A trigger's own account of what it did reaches the execution row — inline and from the
/// outbox, on success and on failure — and the tokens a field value may use render.
/// </summary>
[TestClass]
public class TriggerReportTests
{
    private TestHost _host = null!;
    private int _workflowId;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync(withTriggers: true, configure: s =>
        {
            s.AddScoped<IWorkflowTrigger, ReportingTrigger>();
            s.AddScoped<IWorkflowTrigger, FailingReportingTrigger>();
        });

        _workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();
    }

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    [TestMethod]
    public async Task An_inline_trigger_report_lands_on_its_execution()
    {
        var execution = await CompleteWithTriggerAsync(ReportingTrigger.TriggerKey, TriggerDispatchMode.InTransaction);

        Assert.AreEqual(TriggerExecutionStatus.Succeeded, execution.Status, execution.Error);
        Assert.AreEqual("did the thing", execution.Detail);
    }

    [TestMethod]
    public async Task A_failing_trigger_still_reports_how_far_it_got()
    {
        var execution = await CompleteWithTriggerAsync(FailingReportingTrigger.TriggerKey, TriggerDispatchMode.InTransaction);

        Assert.AreEqual(TriggerExecutionStatus.Failed, execution.Status);
        Assert.AreEqual("got halfway", execution.Detail);
    }

    [TestMethod]
    public async Task An_after_commit_trigger_report_lands_on_its_execution()
    {
        await CompleteWithTriggerAsync(ReportingTrigger.TriggerKey, TriggerDispatchMode.AfterCommit, drain: true);

        var execution = await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Where(e => e.Detail != null)
            .SingleAsync();

        Assert.AreEqual("did the thing", execution.Detail);
    }

    [TestMethod]
    public async Task Value_tokens_render_actor_dates_and_variables()
    {
        var (runId, entryTaskId, entryDefinitionId) = await StartAsync();

        _host.Db.WorkflowVariables.Add(new WorkflowVariable
        {
            WorkflowRunId = runId,
            Name = "colour",
            Value = "blue",
            CreatorId = "system",
            ModifierId = "system"
        });

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefinitionId,
            TriggerKey = BuiltInTriggerKeys.SetVariable,
            Event = WorkflowEventKind.TaskCompleted,
            Configuration = """{"name":"probe","value":"{actor}|{today}|{today+2d}|{today-1d}|{var:colour}|{var:missing}|{nope}"}""",
            CreatorId = "system",
            ModifierId = "system"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        var probe = await _host.Db.WorkflowVariables.AsNoTracking()
            .Where(v => v.WorkflowRunId == runId && v.Name == "probe")
            .Select(v => v.Value)
            .SingleAsync();

        var today = DateTime.UtcNow.Date;
        string Day(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        Assert.AreEqual(
            $"user-originator|{Day(today)}|{Day(today.AddDays(2))}|{Day(today.AddDays(-1))}|blue||{{nope}}",
            probe);
    }

    private async Task<(int RunId, int EntryTaskId, int EntryDefinitionId)> StartAsync()
    {
        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, _workflowId, "user-originator")).Unwrap();
        var entryTaskId = run.Tasks.Single().Id;

        var definitionId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == entryTaskId).Select(t => t.TaskDefinitionId).SingleAsync();

        return (run.Id, entryTaskId, definitionId);
    }

    private async Task<TriggerExecution> CompleteWithTriggerAsync(
        string key, TriggerDispatchMode mode, bool drain = false)
    {
        var (_, entryTaskId, entryDefinitionId) = await StartAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefinitionId,
            TriggerKey = key,
            Event = WorkflowEventKind.TaskCompleted,
            DispatchMode = mode,
            FailurePolicy = TriggerFailurePolicy.LogAndContinue,
            CreatorId = "system",
            ModifierId = "system"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

        if (drain)
        {
            await _host.Outbox.ProcessPendingAsync();
        }

        var keyDefinitionId = await _host.Db.WorkflowTriggerDefinitions
            .Where(d => d.TriggerKey == key).Select(d => d.Id).SingleAsync();

        return await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Where(e => e.TriggerDefinitionId == keyDefinitionId)
            .SingleAsync();
    }

    private sealed class ReportingTrigger : IWorkflowTrigger
    {
        public const string TriggerKey = "test.report";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() =>
            new(Key, "Report", "Reports.", Enum.GetValues<WorkflowEventKind>(), []);

        public Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            context.Report.Detail = "did the thing";
            return Task.CompletedTask;
        }
    }

    private sealed class FailingReportingTrigger : IWorkflowTrigger
    {
        public const string TriggerKey = "test.report-fail";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() =>
            new(Key, "Report and fail", "Reports, then fails.", Enum.GetValues<WorkflowEventKind>(), []);

        public Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            context.Report.Detail = "got halfway";
            throw new InvalidOperationException("stopped");
        }
    }
}
```

Before running, check the name of the outbox drain method on `IWorkflowOutboxProcessor` (`grep -n "Task" src/TaskRouter.EntityFrameworkCore/Triggers/OutboxProcessor.cs | head`) and use it in place of `ProcessPendingAsync` if it differs; `TriggerRuntimeTests` drains the outbox the same way.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~TriggerReportTests"`
Expected: the three report tests fail with `Detail` null; the token test fails (`{actor}` etc. come back literally).

- [ ] **Step 3: Copy the report in the dispatcher.** In `TriggerDispatcher.RunInlineAsync`, set `Detail` in both arms:

```csharp
        try
        {
            await trigger.ExecuteAsync(context, ct).ConfigureAwait(false);

            execution.Status = TriggerExecutionStatus.Succeeded;
            execution.Detail = context.Report.Detail;
            execution.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // The recorded outcome is the real one. The original engine wrote "executed successfully"
            // into the audit log regardless of what happened (review finding H3).
            execution.Status = TriggerExecutionStatus.Failed;
            execution.Error = ex.Message;
            execution.Detail = context.Report.Detail;
            execution.CompletedAt = DateTime.UtcNow;
```

(the rest of the catch block is unchanged).

- [ ] **Step 4: Copy the report in the outbox.** Change `ExecuteIsolatedAsync` to return the detail:

```csharp
    private async Task<string?> ExecuteIsolatedAsync(
        WorkflowOutboxMessage message, TriggerDefinition definition, CancellationToken ct)
    {
        var scopeFactory = services.GetService<IServiceScopeFactory>();

        if (scopeFactory is null)
        {
            var inlineContext = await BuildContextAsync(db, services, message, definition, ct)
                .ConfigureAwait(false);

            var inlineTrigger = Resolve(services, definition.TriggerKey);
            await inlineTrigger.ExecuteAsync(inlineContext, ct).ConfigureAwait(false);
            return inlineContext.Report.Detail;
        }

        using var scope = scopeFactory.CreateScope();

        var scopedDb = scope.ServiceProvider.GetRequiredService<IWorkflowDbContext>();
        var scopedTrigger = Resolve(scope.ServiceProvider, definition.TriggerKey);
        WorkflowTriggerContext? context = null;

        // Through WorkflowTransaction, not BeginTransaction: a provider configured with
        // EnableRetryOnFailure refuses a user-initiated transaction outside its execution
        // strategy, and any host that expects to survive a transient network blip has it on.
        await WorkflowTransaction.ExecuteAsync(scopedDb, async token =>
        {
            context = await BuildContextAsync(scopedDb, scope.ServiceProvider, message, definition, token)
                .ConfigureAwait(false);

            await scopedTrigger.ExecuteAsync(context, token).ConfigureAwait(false);

            // The trigger is not required to save: anything it changed on the scoped
            // context is written by the transaction helper, which is what makes "its
            // writes commit together" true.
        }, ct).ConfigureAwait(false);

        return context?.Report.Detail;
    }
```

and in the caller (~line 122):

```csharp
        try
        {
            execution.Detail = await ExecuteIsolatedAsync(message, definition, ct).ConfigureAwait(false);
```

A failed outbox attempt leaves `Detail` as it was: its writes rolled back with its own transaction, so there is nothing true to report.

- [ ] **Step 5: Add the tokens.** In `BuiltInTriggers.cs`, make `TriggerTemplate` partial and add `RenderAsync` below `Render` (add `using System.Globalization;` and `using System.Text.RegularExpressions;` at the top of the file):

```csharp
/// <summary>Minimal token substitution for trigger configuration values.</summary>
internal static partial class TriggerTemplate
{
    // Render(...) unchanged.

    /// <summary>
    /// <see cref="Render"/>, plus the tokens that need the clock, the actor, or the run's
    /// variables: <c>{actor}</c>, <c>{now}</c>, <c>{today}</c>, <c>{today+Nd}</c>,
    /// <c>{today-Nd}</c>, <c>{var:name}</c>. Dates render as ISO-8601, UTC. A variable
    /// that is not set renders empty; any other unknown token passes through literally.
    /// </summary>
    public static async Task<string?> RenderAsync(
        string? template, WorkflowTriggerContext context, CancellationToken ct = default)
    {
        var rendered = Render(template, context);

        if (string.IsNullOrEmpty(rendered) || !rendered.Contains('{', StringComparison.Ordinal))
        {
            return rendered;
        }

        var now = DateTime.UtcNow;

        rendered = rendered
            .Replace("{actor}", context.ActorId, StringComparison.OrdinalIgnoreCase)
            .Replace("{now}", now.ToString("O", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        rendered = TodayToken().Replace(rendered, match =>
        {
            var days = match.Groups["n"].Success
                ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture)
                    * (match.Groups["sign"].Value == "-" ? -1 : 1)
                : 0;

            return now.Date.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        });

        if (VariableToken().IsMatch(rendered))
        {
            var variables = await context.Variables.GetAllAsync(ct).ConfigureAwait(false);

            rendered = VariableToken().Replace(rendered, match =>
                variables.TryGetValue(match.Groups["name"].Value, out var value) ? value ?? string.Empty : string.Empty);
        }

        return rendered;
    }

    [GeneratedRegex(@"\{today(?:(?<sign>[+-])(?<n>\d+)d)?\}", RegexOptions.IgnoreCase)]
    private static partial Regex TodayToken();

    [GeneratedRegex(@"\{var:(?<name>[^}]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex VariableToken();
}
```

Then switch the two existing users to it. In `SetVariableTrigger.ExecuteAsync`:

```csharp
        var value = await TriggerTemplate.RenderAsync(context.Config.GetString("value"), context, ct)
            .ConfigureAwait(false);
```

In `NotifyTrigger.ExecuteAsync`, render subject and body before building the notification:

```csharp
        var subject = await TriggerTemplate.RenderAsync(context.Config.GetString("subject"), context, ct)
            .ConfigureAwait(false);
        var body = await TriggerTemplate.RenderAsync(context.Config.GetString("body"), context, ct)
            .ConfigureAwait(false);

        await sink.SendAsync(new WorkflowNotification(
            context.Subject,
            context.Task.Id,
            recipient,
            subject ?? string.Empty,
            body ?? string.Empty,
            context.ActorId), ct)
            .ConfigureAwait(false);
```

Update the `value` parameter's `HelpText` on `SetVariableTrigger` to list the new tokens:
`"Supports {task.outcome}, {task.branch}, {task.id}, {event}, {actor}, {today}, {today+Nd}, {now}, {var:name}."`

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~TriggerReportTests|FullyQualifiedName~TriggerRuntimeTests|FullyQualifiedName~EscalationTests"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add src/TaskRouter.EntityFrameworkCore/Triggers tests/TaskRouter.Tests/TriggerReportTests.cs
git commit -m "Record what a trigger says it did, and render actor, date and variable tokens"
```

---

### Task 4: The field catalog and its builder

**Files:**
- Create: `src/TaskRouter.EntityFrameworkCore/Fields/FieldContexts.cs`
- Create: `src/TaskRouter.EntityFrameworkCore/Fields/WorkflowFieldCatalog.cs`
- Create: `src/TaskRouter.EntityFrameworkCore/Fields/WorkflowFieldsBuilder.cs`
- Modify: `src/TaskRouter.EntityFrameworkCore/TaskRouter.EntityFrameworkCore.csproj` (InternalsVisibleTo)
- Test: `tests/TaskRouter.Tests/FieldCatalogTests.cs`

- [ ] **Step 1: Let the tests see internals.** In the EF csproj, add next to the other `ItemGroup`s (same form as `TaskRouter.Blazor.csproj` line 18):

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="TaskRouter.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

```csharp
using DemoDocuments.Server.Domain;

using TaskRouter.Core.Fields;
using TaskRouter.EntityFrameworkCore.Fields;

namespace TaskRouter.Tests;

/// <summary>
/// How a host's declarations compose into the catalog an author sees: base fields for
/// every document type, per-type extensions on top, and every wiring mistake refused when
/// the application starts rather than when a task completes.
/// </summary>
[TestClass]
public class FieldCatalogTests
{
    private static IQueryable<DocumentBase> NoDocuments(FieldResolveContext _) =>
        Array.Empty<DocumentBase>().AsQueryable();

    private static IQueryable<WorkItem> NoWorkItems(FieldResolveContext _) =>
        Array.Empty<WorkItem>().AsQueryable();

    private static WorkflowFieldsBuilder Base()
    {
        var fields = new WorkflowFieldsBuilder();

        fields.ForSubjectTypes(["ChangeRequest", "Drawing"])
            .Target<DocumentBase>("document", "This document", FieldCardinality.One, NoDocuments)
                .Field(d => d.Status, "Status")
                .Field(d => d.Comments, "Comments")
            .Target<WorkItem>("allWorkItems", "All work items", FieldCardinality.Many, NoWorkItems)
                .Field(w => w.WorkItemState, "State");

        return fields;
    }

    [TestMethod]
    public void Base_fields_apply_to_every_listed_subject_type()
    {
        var catalog = Base().Build();

        foreach (var type in new[] { "ChangeRequest", "Drawing" })
        {
            var targets = catalog.Describe(type);

            CollectionAssert.AreEqual(new[] { "document", "allWorkItems" }, targets.Select(t => t.Key).ToArray());
            CollectionAssert.AreEqual(new[] { "Status", "Comments" },
                targets[0].Fields.Select(f => f.Key).ToArray());
        }
    }

    [TestMethod]
    public void An_extension_adds_fields_to_one_subject_type_only()
    {
        var fields = Base();

        fields.ForSubjectType("ChangeRequest")
            .Extend<ChangeRequest>("document")
                .Field(c => c.Urgency, "Urgency");

        var catalog = fields.Build();

        CollectionAssert.Contains(
            catalog.Describe("ChangeRequest")[0].Fields.Select(f => f.Key).ToList(), "Urgency");
        CollectionAssert.DoesNotContain(
            catalog.Describe("Drawing")[0].Fields.Select(f => f.Key).ToList(), "Urgency");
    }

    [TestMethod]
    public void An_unknown_subject_type_has_no_targets()
    {
        Assert.IsEmpty(Base().Build().Describe("Invoice"));
    }

    [TestMethod]
    public void An_enum_field_describes_its_choices()
    {
        var status = Base().Build().Describe("Drawing")[0].Fields.Single(f => f.Key == "Status");

        Assert.AreEqual(FieldKind.Choice, status.Kind);
        CollectionAssert.Contains(status.Choices.Select(c => c.Value).ToList(), "Scheduled");
    }

    [TestMethod]
    public void A_field_declared_twice_on_one_target_is_refused()
    {
        var fields = Base();

        fields.ForSubjectType("ChangeRequest")
            .Extend<ChangeRequest>("document")
                .Field(c => c.Comments, "Comments again");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => fields.Build());
        StringAssert.Contains(error.Message, "'Comments' is declared twice on 'document' for ChangeRequest");
    }

    [TestMethod]
    public void A_target_declared_twice_for_a_subject_type_is_refused()
    {
        var fields = Base();

        fields.ForSubjectType("Drawing")
            .Target<DocumentBase>("document", "Again", FieldCardinality.One, NoDocuments);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => fields.Build());
        StringAssert.Contains(error.Message, "'document' is declared twice for Drawing");
    }

    [TestMethod]
    public void Extending_a_target_that_does_not_exist_is_refused()
    {
        var fields = Base();

        fields.ForSubjectType("ChangeRequest")
            .Extend<ChangeRequest>("theDocument")
                .Field(c => c.Urgency, "Urgency");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => fields.Build());
        StringAssert.Contains(error.Message, "ChangeRequest has no 'theDocument' to extend");
    }

    [TestMethod]
    public void Extending_with_an_unrelated_entity_type_is_refused()
    {
        var fields = Base();

        fields.ForSubjectType("ChangeRequest")
            .Extend<WorkItem>("document")
                .Field(w => w.WorkItemState, "State");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => fields.Build());
        StringAssert.Contains(error.Message, "WorkItem does not derive from DocumentBase");
    }

    [TestMethod]
    public void A_field_must_name_a_property()
    {
        var target = new WorkflowFieldsBuilder().ForSubjectType("Drawing")
            .Target<DocumentBase>("document", "This document", FieldCardinality.One, NoDocuments);

        Assert.ThrowsExactly<ArgumentException>(() => target.Field(d => d.Title + "!", "Shouty title"));
    }

    [TestMethod]
    public void A_lookup_without_options_is_refused()
    {
        var target = new WorkflowFieldsBuilder().ForSubjectType("ChangeRequest")
            .Target<WorkItem>("w", "W", FieldCardinality.One, NoWorkItems);

        Assert.ThrowsExactly<ArgumentException>(() => target.Field(w => w.AssignedSectionId, "Section", FieldKind.Lookup));
    }

    [TestMethod]
    public void An_actor_field_must_be_text()
    {
        var target = new WorkflowFieldsBuilder().ForSubjectType("ChangeRequest")
            .Target<WorkItem>("w", "W", FieldCardinality.One, NoWorkItems);

        Assert.ThrowsExactly<ArgumentException>(() => target.Field(w => w.AssignedSectionId, "Section", FieldKind.Actor));
    }

    [TestMethod]
    public void The_default_writer_assigns_the_property()
    {
        var catalog = Base().Build();
        Assert.IsTrue(catalog.TryGetTarget("Drawing", "document", out var target));

        var drawing = new Drawing { Title = "t", DocNumber = "n" };
        var comments = target.Fields.Single(f => f.Key == "Comments");

        comments.Write(drawing, "hello", null!, CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual("hello", comments.Read(drawing));
    }
}
```

Before running, check `Drawing`'s required members in `samples/DemoDocuments.Server/Domain/DemoDomain.cs` and add any the initializer above is missing.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~FieldCatalogTests"`
Expected: build error — `TaskRouter.EntityFrameworkCore.Fields` does not exist.

- [ ] **Step 4: Write `FieldContexts.cs`**

```csharp
using Microsoft.Extensions.DependencyInjection;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>What a target's resolver is given to find its records.</summary>
public sealed class FieldResolveContext(
    WorkflowSubject subject, WorkflowTaskSnapshot task, IServiceProvider services)
{
    public WorkflowSubject Subject { get; } = subject;

    /// <summary>The task whose event fired the trigger — its branch key finds "this section's" records.</summary>
    public WorkflowTaskSnapshot Task { get; } = task;

    public IServiceProvider Services { get; } = services;

    /// <summary>
    /// The host's context, as the same instance the engine is writing through. Resolving
    /// the concrete context type from the container would give the same scoped instance in
    /// every host wired by <c>AddTaskRouterFor</c>, but this cannot be got wrong.
    /// </summary>
    public TContext Db<TContext>() where TContext : class =>
        Services.GetRequiredService<IWorkflowDbContext>() as TContext
        ?? throw new InvalidOperationException(
            $"IWorkflowDbContext is not a {typeof(TContext).Name}.");
}

/// <summary>What a field's writer and a target's <c>OnWritten</c> hook are given.</summary>
public sealed class FieldWriteContext(string actorId, IServiceProvider services)
{
    /// <summary>Who caused the event. <see cref="WorkflowActors.System"/> for the deadline sweep.</summary>
    public string ActorId { get; } = actorId;

    public bool IsSystem => ActorId == WorkflowActors.System;

    public IServiceProvider Services { get; } = services;

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();
}

/// <summary>What a lookup field's options delegate is given.</summary>
public sealed class FieldOptionsContext(string? search, IServiceProvider services)
{
    /// <summary>What the author has typed so far. Null or empty means "the first few".</summary>
    public string? Search { get; } = search;

    public IServiceProvider Services { get; } = services;

    public TContext Db<TContext>() where TContext : class =>
        Services.GetRequiredService<IWorkflowDbContext>() as TContext
        ?? throw new InvalidOperationException(
            $"IWorkflowDbContext is not a {typeof(TContext).Name}.");
}

/// <summary>
/// Writes one field on one record. Throwing refuses the write — the message is what the
/// person completing the task sees, so make it a sentence about their document.
/// </summary>
public delegate Task FieldWriter<in TEntity, in TValue>(
    TEntity entity, TValue value, FieldWriteContext context, CancellationToken ct);
```

- [ ] **Step 5: Write `WorkflowFieldCatalog.cs`**

```csharp
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
    public required Func<object, object?> Read { get; init; }
    public required Func<object, object?, FieldWriteContext, CancellationToken, Task> Write { get; init; }
    public Func<FieldOptionsContext, CancellationToken, Task<IReadOnlyList<FieldChoice>>>? Options { get; init; }

    public SubjectFieldInfo Describe() => new(Key, Label, Kind, IsNullable, Choices);
}

/// <summary>An <c>OnWritten</c> hook, which only runs on records of the type it was declared for.</summary>
internal sealed record OnWrittenRegistration(
    Type DeclaringType, Func<object, FieldWriteContext, CancellationToken, Task> Run);
```

- [ ] **Step 6: Write `WorkflowFieldsBuilder.cs`**

```csharp
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
```

Note on `Resolve`: the test catalog's resolvers return `Array.Empty<T>().AsQueryable()`, which `ToListAsync` refuses (not an EF query). The catalog tests never call `Resolve`, so that is fine; do not "fix" it by falling back to `ToList`.

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~FieldCatalogTests"`
Expected: all pass.

- [ ] **Step 8: Commit**

```bash
git add src/TaskRouter.EntityFrameworkCore tests/TaskRouter.Tests/FieldCatalogTests.cs
git commit -m "Let a host declare the fields authors may set, shared across document types and extended per type"
```

---

### Task 5: The undo scope

**Files:**
- Create: `src/TaskRouter.EntityFrameworkCore/Fields/FieldWriteScope.cs`
- Test: add to `tests/TaskRouter.Tests/UpdateFieldsTriggerTests.cs` (created here, grown in Task 7)

Why this exists, for the executor: under `LogAndContinue` a refused update must leave **nothing** changed while the workflow still advances. A host writer may call `SaveChangesAsync` itself (TRDIS's services do), so its writes reach the database inside the engine's transaction; and EF keeps the changed values in its change tracker, so the engine's next save would write them again. The scope handles both: it flushes the engine's own pending work first (so a rollback cannot take that with it), takes a savepoint, watches which entities change, and on failure rolls back to the savepoint and reloads or detaches exactly those entities.

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;

using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Fields;

namespace TaskRouter.Tests;

[TestClass]
public class UpdateFieldsTriggerTests
{
    private TestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await TestHost.CreateAsync(withTriggers: true);

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    // ───────────────────────────── The undo scope ─────────────────────────────

    [TestMethod]
    public async Task An_abandoned_scope_undoes_saved_and_unsaved_writes_but_keeps_earlier_work()
    {
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            // Pending engine work from before the trigger: must survive.
            document.Description = "engine wrote this";

            await using (var scope = await FieldWriteScope.BeginAsync(db, ct))
            {
                document.Comments = "saved by the host";
                await db.SaveChangesAsync(ct);

                document.Revision = "B";                                   // unsaved
                db.Sections.Add(new Section { Code = "TMP", Name = "Temp" }); // new
                // no CompleteAsync: abandoned
            }

            Assert.AreEqual(EntityState.Unchanged, db.Entry(document).State);
            Assert.IsNull(document.Comments);
            Assert.AreEqual("-", document.Revision);
            Assert.IsFalse(db.ChangeTracker.Entries<Section>().Any(e => e.Entity.Code == "TMP"));
        });

        db.ChangeTracker.Clear();

        var stored = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.AreEqual("engine wrote this", stored.Description);
        Assert.IsNull(stored.Comments);
        Assert.IsFalse(await db.Sections.AnyAsync(s => s.Code == "TMP"));
    }

    [TestMethod]
    public async Task A_completed_scope_keeps_its_writes()
    {
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            await using var scope = await FieldWriteScope.BeginAsync(db, ct);
            document.Comments = "kept";
            await scope.SaveAndCompleteAsync(ct);
        });

        db.ChangeTracker.Clear();
        Assert.AreEqual("kept", (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id)).Comments);
    }

    // ───────────────────────────── Fixtures ─────────────────────────────

    private async Task<ChangeRequest> NewChangeRequestAsync()
    {
        var document = new ChangeRequest
        {
            Title = "Pump room rewire",
            DocNumber = $"CR-{Guid.NewGuid():N}"[..12],
            Originator = "user-originator"
        };

        _host.Db.ChangeRequests.Add(document);
        await _host.Db.SaveChangesAsync();
        return document;
    }
}
```

`WorkflowTransaction.ExecuteAsync`'s exact signature: check `src/TaskRouter.EntityFrameworkCore/WorkflowTransaction.cs` (it takes the context, a `Func<CancellationToken, Task>` and a token; `OutboxProcessor` calls it that way). If it is `internal`, the `InternalsVisibleTo` from Task 4 covers it. Also confirm the demo `ChangeRequest`'s required members against `DemoDomain.cs` and fill in any missing from `NewChangeRequestAsync`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~UpdateFieldsTriggerTests"`
Expected: build error — `FieldWriteScope` does not exist.

- [ ] **Step 3: Write `FieldWriteScope.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>
/// Makes a field update all-or-nothing, even when the failure policy lets the workflow
/// carry on.
///
/// <para>Begins by saving the engine's own pending changes, so that rolling back to the
/// savepoint cannot take those with it. Then watches the change tracker: on abandonment it
/// rolls the database back to the savepoint and returns every entity that changed in
/// between to its stored state — reloading the ones that existed, detaching the ones that
/// were new — so the engine's next save does not quietly write them again.</para>
///
/// <para>Without an ambient transaction there is no savepoint, so writes a host already saved
/// stay saved; the tracker is still reverted. The engine always runs triggers inside one.</para>
/// </summary>
internal sealed class FieldWriteScope : IAsyncDisposable
{
    private readonly DbContext _db;
    private readonly IDbContextTransaction? _transaction;
    private readonly string _savepoint;
    private readonly Dictionary<object, bool> _touched = new(ReferenceEqualityComparer.Instance);
    private bool _completed;

    private FieldWriteScope(DbContext db, IDbContextTransaction? transaction, string savepoint)
    {
        _db = db;
        _transaction = transaction;
        _savepoint = savepoint;
    }

    public static async Task<FieldWriteScope> BeginAsync(IWorkflowDbContext workflowDb, CancellationToken ct)
    {
        var db = workflowDb as DbContext
            ?? throw new InvalidOperationException("Field updates need IWorkflowDbContext to be an EF Core DbContext.");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var transaction = db.Database.CurrentTransaction;
        var savepoint = "taskrouter_fields_" + Guid.NewGuid().ToString("N")[..8];

        if (transaction is not null)
        {
            await transaction.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
        }

        var scope = new FieldWriteScope(db, transaction, savepoint);
        db.ChangeTracker.Tracked += scope.OnTracked;
        db.ChangeTracker.StateChanged += scope.OnStateChanged;
        return scope;
    }

    /// <summary>A record's identity for the change summary: its CLR type and key values.</summary>
    public string Describe(object entity)
    {
        var entry = _db.Entry(entity);
        var key = entry.Metadata.FindPrimaryKey();

        var id = key is null
            ? "?"
            : string.Join(",", key.Properties.Select(p => entry.Property(p.Name).CurrentValue));

        return $"{entry.Metadata.ClrType.Name} {id}";
    }

    /// <summary>
    /// Saves inside the savepoint — so a database refusal (a constraint, say) is still this
    /// update's failure and still undone — then keeps everything.
    /// </summary>
    public async Task SaveAndCompleteAsync(CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (_transaction is not null)
        {
            await _transaction.ReleaseSavepointAsync(_savepoint, ct).ConfigureAwait(false);
        }

        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        _db.ChangeTracker.Tracked -= OnTracked;
        _db.ChangeTracker.StateChanged -= OnStateChanged;

        if (_completed)
        {
            return;
        }

        if (_transaction is not null)
        {
            await _transaction.RollbackToSavepointAsync(_savepoint).ConfigureAwait(false);
        }

        foreach (var (entity, wasNew) in _touched)
        {
            var entry = _db.Entry(entity);

            if (wasNew)
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.State != EntityState.Detached)
            {
                // Detaches the entity if its row no longer exists, which is right too.
                await entry.ReloadAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnTracked(object? sender, EntityTrackedEventArgs e) =>
        _touched.TryAdd(e.Entry.Entity, !e.FromQuery);

    private void OnStateChanged(object? sender, EntityStateChangedEventArgs e) =>
        _touched.TryAdd(e.Entry.Entity, false);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~UpdateFieldsTriggerTests"`
Expected: both pass. If the first fails on `document.Comments` still being set, the `StateChanged` event did not fire before `SaveChangesAsync` — check that `ChangeTracker.AutoDetectChangesEnabled` is on for `DemoDbContext` (it is by default).

- [ ] **Step 5: Commit**

```bash
git add src/TaskRouter.EntityFrameworkCore/Fields/FieldWriteScope.cs tests/TaskRouter.Tests/UpdateFieldsTriggerTests.cs
git commit -m "Undo a refused field update completely, even one the host already saved"
```

---

### Task 6: The demo's status rules and catalog

**Files:**
- Modify: `samples/DemoDocuments.Server/Domain/DemoDomain.cs` (enum `DocumentStatus`, ~line 17)
- Create: `samples/DemoDocuments.Server/Workflow/DemoDocumentStatusRules.cs`
- Create: `samples/DemoDocuments.Server/Workflow/DemoWorkflowFields.cs`
- Modify: `samples/DemoDocuments.Server/Workflow/DemoAuthorizationPolicy.cs` (~line 58)

No test step of its own: Task 7's tests drive all of this through the trigger. It has to exist first because they run against the demo's catalog.

- [ ] **Step 1: Add `OnHold` as the last member of the demo's `DocumentStatus`**

```csharp
public enum DocumentStatus
{
    Scheduled,
    InProgress,
    Complete,
    Cancelled,
    OnHold
}
```

Appended, not inserted: the column stores the number, so inserting would re-mean every stored row. No migration is needed.

- [ ] **Step 2: Write `DemoDocumentStatusRules.cs`**

```csharp
using DemoDocuments.Server.Domain;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Which status a document may move to from which — the demo's stand-in for the state
/// machine a real host already has.
///
/// <para>It exists so a workflow that tries to put a finished document on hold is refused
/// here, by the host, rather than the field update writing whatever it is told. That is
/// the point of a field having a writer: the host's rules still hold.</para>
/// </summary>
public static class DemoDocumentStatusRules
{
    private static readonly Dictionary<DocumentStatus, DocumentStatus[]> Allowed = new()
    {
        [DocumentStatus.Scheduled] = [DocumentStatus.InProgress, DocumentStatus.OnHold, DocumentStatus.Cancelled],
        [DocumentStatus.InProgress] = [DocumentStatus.Complete, DocumentStatus.OnHold, DocumentStatus.Cancelled],
        [DocumentStatus.OnHold] = [DocumentStatus.InProgress, DocumentStatus.Cancelled],
        [DocumentStatus.Complete] = [],
        [DocumentStatus.Cancelled] = []
    };

    public static void MoveTo(DocumentBase document, DocumentStatus target)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Status == target)
        {
            return;
        }

        if (!Allowed[document.Status].Contains(target))
        {
            throw new InvalidOperationException(
                $"{document.DocNumber} cannot move from {document.Status} to {target}.");
        }

        document.Status = target;
    }
}
```

- [ ] **Step 3: Write `DemoWorkflowFields.cs`**

```csharp
using System.Globalization;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;

using TaskRouter.Core.Fields;
using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore.Fields;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// The fields the demo lets workflow authors set.
///
/// <para>Two layers, which is the pattern a host with many document types follows. Every
/// document type gets the document's common fields and the two work-item targets. A
/// change request additionally gets its own fields, added to the same "This document"
/// target — so an author on a change-request workflow sees one target with more in it,
/// and an author on a drawing workflow never sees fields a drawing does not have.</para>
///
/// <para>A branch key in this demo is a section's <c>Code</c>, which is why the
/// branch work item is found by <c>AssignedSection.Code</c>. A host that keys branches
/// differently resolves differently; that is why resolution is the host's code.</para>
/// </summary>
public static class DemoWorkflowFields
{
    public static readonly string[] DocumentSubjectTypes =
        [nameof(DemoDocumentType.ChangeRequest), nameof(DemoDocumentType.Drawing)];

    public static void Configure(WorkflowFieldsBuilder fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        fields.ForSubjectTypes(DocumentSubjectTypes)
            .Target<DocumentBase>("document", "This document", FieldCardinality.One, context =>
            {
                var id = DocumentId(context.Subject);
                return context.Db<DemoDbContext>().Documents.Where(d => d.Id == id);
            })
                .Field(d => d.Status, "Status", write: (document, status, _, _) =>
                {
                    DemoDocumentStatusRules.MoveTo(document, status);
                    return Task.CompletedTask;
                })
                .Field(d => d.DueDate, "Due date")
                .Field(d => d.InternalDueDate, "Internal due date")
                .Field(d => d.Comments, "Comments")
            .Target<WorkItem>("branchWorkItem", "Work item for this task's section", FieldCardinality.One, context =>
            {
                var id = DocumentId(context.Subject);
                var section = context.Task.AssignedBranchKey;
                return context.Db<DemoDbContext>().WorkItems
                    .Where(w => w.DocumentId == id && !w.IsArchived && w.AssignedSection!.Code == section);
            })
                .Field(w => w.WorkItemState, "State")
                .Field(w => w.CurrentlyAssignedActorId, "Assigned to", FieldKind.Actor)
                .OnWritten(Touch)
            .Target<WorkItem>("allWorkItems", "All work items on this document", FieldCardinality.Many, context =>
            {
                var id = DocumentId(context.Subject);
                return context.Db<DemoDbContext>().WorkItems
                    .Where(w => w.DocumentId == id && !w.IsArchived);
            })
                .Field(w => w.WorkItemState, "State")
                .OnWritten(Touch);

        fields.ForSubjectType(nameof(DemoDocumentType.ChangeRequest))
            .Extend<ChangeRequest>("document")
                .Field(c => c.Urgency, "Urgency")
                .Field(c => c.IsReplyRequired, "Reply required")
                .Field(c => c.DateReplyRequiredBy, "Reply required by")
                .Field(c => c.CogCoordinatorActorId, "Cog coordinator", FieldKind.Actor);
    }

    private static Task Touch(WorkItem item, FieldWriteContext context, CancellationToken ct)
    {
        item.Modified = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    // An unparseable id is a subject that is not one of our documents: resolve nothing,
    // and the update reports there was nothing to change.
    private static int DocumentId(WorkflowSubject subject) =>
        int.TryParse(subject.SubjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
}
```

Check against `DemoDomain.cs` that every property named exists with a public setter and a supported type (`Urgency` is `LarUrgency`, `DateReplyRequiredBy` is `DateTime?`, `CogCoordinatorActorId` is `string?`, `WorkItem.Modified` is `DateTime`). Adjust names to match if they differ.

- [ ] **Step 4: Let the person doing the task update fields in the demo.** In `DemoAuthorizationPolicy`, add `UpdateSubjectFields` to the first arm of the switch:

```csharp
            WorkflowOperation.CompleteTask
            or WorkflowOperation.CompleteWithSelectiveRejection
            or WorkflowOperation.UpdateTaskNotes
            or WorkflowOperation.AddAdHocTask
            or WorkflowOperation.StartSubWorkflow
            // The trigger runs as whoever completed the step, so "may they do this task"
            // is the question. A host wanting field-level rules reads context.Fields.
            or WorkflowOperation.UpdateSubjectFields
                => await DoingAsync(person, task, ct).ConfigureAwait(false),
```

- [ ] **Step 5: Build**

Run: `dotnet build TaskRouter.slnx -v q`
Expected: 0 errors, no new warnings.

- [ ] **Step 6: Commit**

```bash
git add samples/DemoDocuments.Server
git commit -m "Give the demo a status a document can be held at, and the fields its workflows may set"
```

---

### Task 7: The `workflow.updateFields` trigger

**Files:**
- Create: `src/TaskRouter.EntityFrameworkCore/Fields/UpdateFieldsTrigger.cs`
- Modify: `src/TaskRouter.EntityFrameworkCore/Triggers/BuiltInTriggers.cs` (`BuiltInTriggerKeys`)
- Modify: `src/TaskRouter.EntityFrameworkCore/ServiceCollectionExtensions.cs` (`WorkflowEngineBuilder`)
- Modify: `samples/DemoDocuments.Server/Program.cs` (~line 71)
- Modify: `tests/TaskRouter.Tests/TestHost.cs` (~line 156), `tests/TaskRouter.Tests/EndpointTestHost.cs` (~line 101)
- Test: `tests/TaskRouter.Tests/UpdateFieldsTriggerTests.cs`

- [ ] **Step 1: Add the end-to-end tests** to `UpdateFieldsTriggerTests` (keep Task 5's tests; add these usings: `DemoDocuments.Server.Workflow`, `TaskRouter.Core.Abstractions`, `TaskRouter.Core.Fields`, `TaskRouter.Core.Model`, `TaskRouter.EntityFrameworkCore.Triggers`):

```csharp
    // ───────────────────────────── The trigger ─────────────────────────────

    [TestMethod]
    public async Task Completing_a_step_puts_the_document_on_hold()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        Assert.AreEqual(DocumentStatus.OnHold, await StatusOfAsync(document.Id));

        var execution = await ExecutionAsync();
        Assert.AreEqual(TriggerExecutionStatus.Succeeded, execution.Status, execution.Error);
        Assert.AreEqual($"ChangeRequest {document.Id} Status: Scheduled → OnHold", execution.Detail);
    }

    [TestMethod]
    public async Task Several_fields_change_together_and_tokens_render()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document",
        [
            new("Status", "OnHold"),
            new("Comments", "Held by {actor}"),
            new("InternalDueDate", "{today+5d}")
        ]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();
        var stored = await _host.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);

        Assert.AreEqual(DocumentStatus.OnHold, stored.Status);
        Assert.AreEqual("Held by user-originator", stored.Comments);
        Assert.AreEqual(DateTime.UtcNow.Date.AddDays(5), stored.InternalDueDate);
    }

    [TestMethod]
    public async Task A_refused_transition_fails_the_completion_by_default()
    {
        var document = await NewChangeRequestAsync(DocumentStatus.Complete);
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "cannot move from Complete to OnHold");
        Assert.AreEqual(WorkflowTaskStatus.NotStarted, await TaskStatusAsync(taskId));
        Assert.AreEqual(DocumentStatus.Complete, await StatusOfAsync(document.Id));
    }

    [TestMethod]
    public async Task Under_log_and_continue_a_refusal_changes_nothing_and_the_step_completes()
    {
        var document = await NewChangeRequestAsync(DocumentStatus.Complete);
        var taskId = await StartWithTriggerAsync(document, "document",
            [new("Comments", "should not stick"), new("Status", "OnHold")],
            TriggerFailurePolicy.LogAndContinue);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();
        var stored = await _host.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);

        Assert.AreEqual(WorkflowTaskStatus.Completed, await TaskStatusAsync(taskId));
        Assert.IsNull(stored.Comments, "The first assignment must be undone with the second.");
        Assert.AreEqual(DocumentStatus.Complete, stored.Status);
        Assert.AreEqual(TriggerExecutionStatus.Failed, (await ExecutionAsync()).Status);
    }

    [TestMethod]
    public async Task A_value_already_set_is_left_alone_and_reported()
    {
        var document = await NewChangeRequestAsync(DocumentStatus.OnHold);
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        Assert.AreEqual("Nothing to change.", (await ExecutionAsync()).Detail);
    }

    [TestMethod]
    public async Task A_change_request_only_field_works_on_a_change_request()
    {
        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Urgency", nameof(LarUrgency.Priority))]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        _host.Db.ChangeTracker.Clear();
        Assert.AreEqual(LarUrgency.Priority,
            (await _host.Db.ChangeRequests.AsNoTracking().SingleAsync(c => c.Id == document.Id)).Urgency);
    }

    [TestMethod]
    public async Task A_field_this_subject_type_lacks_fails_with_a_sentence()
    {
        var drawing = new Drawing { Title = "Deck plan", DocNumber = $"DW-{Guid.NewGuid():N}"[..12] };
        _host.Db.Drawings.Add(drawing);
        await _host.Db.SaveChangesAsync();

        var taskId = await StartWithTriggerAsync(drawing, "document", [new("Urgency", "Priority")],
            subjectType: nameof(DemoDocumentType.Drawing));

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "Drawing has no field 'Urgency' on 'This document'");
    }

    [TestMethod]
    public async Task All_work_items_on_the_document_are_updated()
    {
        var document = await NewChangeRequestAsync();
        await AddWorkItemsAsync(document, "C100", "C200");
        var taskId = await StartWithTriggerAsync(document, "allWorkItems", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        var states = await _host.Db.WorkItems.AsNoTracking()
            .Where(w => w.DocumentId == document.Id).Select(w => w.WorkItemState).ToListAsync();

        Assert.HasCount(2, states);
        Assert.IsTrue(states.All(s => s == WorkItemStatus.InProgress));
    }

    [TestMethod]
    public async Task The_branch_work_item_is_the_one_for_the_tasks_section()
    {
        var document = await NewChangeRequestAsync();
        await AddWorkItemsAsync(document, "C100", "C200");
        var taskId = await StartWithTriggerAsync(document, "branchWorkItem", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        var task = await _host.Db.WorkflowTasks.SingleAsync(t => t.Id == taskId);
        task.AssignedBranchKey = "C200";
        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();

        (await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator")).Unwrap();

        var bySection = await _host.Db.WorkItems.AsNoTracking()
            .Where(w => w.DocumentId == document.Id)
            .Select(w => new { w.AssignedSection!.Code, w.WorkItemState })
            .ToDictionaryAsync(w => w.Code, w => w.WorkItemState);

        Assert.AreEqual(WorkItemStatus.InProgress, bySection["C200"]);
        Assert.AreEqual(WorkItemStatus.Estimated, bySection["C100"]);
    }

    [TestMethod]
    public async Task A_branch_target_with_no_branch_fails_saying_so()
    {
        var document = await NewChangeRequestAsync();
        await AddWorkItemsAsync(document, "C100");
        var taskId = await StartWithTriggerAsync(document, "branchWorkItem", [new("WorkItemState", nameof(WorkItemStatus.InProgress))]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "There is no work item for this task's section to update");
    }

    [TestMethod]
    public async Task A_denied_actor_cannot_update_fields()
    {
        await _host.DisposeAsync();
        _host = await TestHost.CreateAsync(withTriggers: true, policy: new DenyFieldsPolicy());

        var document = await NewChangeRequestAsync();
        var taskId = await StartWithTriggerAsync(document, "document", [new("Status", "OnHold")]);

        var result = await _host.Engine.CompleteTaskAsync(taskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(result.UnwrapError().Message, "fields are frozen");
        Assert.AreEqual(DocumentStatus.Scheduled, await StatusOfAsync(document.Id));
    }

    // ───────────────────────────── Fixtures ─────────────────────────────

    private async Task<int> StartWithTriggerAsync(
        DocumentBase document,
        string target,
        IReadOnlyList<FieldAssignment> assignments,
        TriggerFailurePolicy policy = TriggerFailurePolicy.FailOperation,
        string subjectType = nameof(DemoDocumentType.ChangeRequest))
    {
        var workflowId = await _host.Db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

        var subject = new WorkflowSubject(subjectType, document.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var run = (await _host.Engine.StartRunAsync(subject, workflowId, "user-originator")).Unwrap();
        var taskId = run.Tasks.Single().Id;

        var definitionId = await _host.Db.WorkflowTasks
            .Where(t => t.Id == taskId).Select(t => t.TaskDefinitionId).SingleAsync();

        _host.Db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = definitionId,
            TriggerKey = BuiltInTriggerKeys.UpdateFields,
            Event = WorkflowEventKind.TaskCompleted,
            FailurePolicy = policy,
            Configuration = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["target"] = target,
                ["assignments"] = FieldAssignments.Serialize(assignments)
            }),
            CreatorId = "system",
            ModifierId = "system"
        });

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
        return taskId;
    }

    private async Task<ChangeRequest> NewChangeRequestAsync(DocumentStatus status = DocumentStatus.Scheduled)
    {
        var document = new ChangeRequest
        {
            Title = "Pump room rewire",
            DocNumber = $"CR-{Guid.NewGuid():N}"[..12],
            Originator = "user-originator",
            Status = status
        };

        _host.Db.ChangeRequests.Add(document);
        await _host.Db.SaveChangesAsync();
        return document;
    }

    private async Task AddWorkItemsAsync(DocumentBase document, params string[] sectionCodes)
    {
        foreach (var code in sectionCodes)
        {
            var sectionId = await _host.Db.Sections.Where(s => s.Code == code).Select(s => s.Id).SingleAsync();
            _host.Db.WorkItems.Add(new WorkItem { DocumentId = document.Id, AssignedSectionId = sectionId });
        }

        await _host.Db.SaveChangesAsync();
        _host.Db.ChangeTracker.Clear();
    }

    private async Task<DocumentStatus> StatusOfAsync(int documentId)
    {
        _host.Db.ChangeTracker.Clear();
        return await _host.Db.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.Status).SingleAsync();
    }

    private async Task<WorkflowTaskStatus> TaskStatusAsync(int taskId) =>
        await _host.Db.WorkflowTasks.AsNoTracking().Where(t => t.Id == taskId).Select(t => t.Status).SingleAsync();

    private async Task<TriggerExecution> ExecutionAsync() =>
        await _host.Db.WorkflowTriggerExecutions.AsNoTracking()
            .Join(_host.Db.WorkflowTriggerDefinitions.Where(d => d.TriggerKey == BuiltInTriggerKeys.UpdateFields),
                e => e.TriggerDefinitionId, d => d.Id, (e, _) => e)
            .SingleAsync();

    private sealed class DenyFieldsPolicy : IWorkflowAuthorizationPolicy
    {
        public Task<WorkflowAuthorizationResult> EvaluateAsync(
            WorkflowOperation operation, WorkflowAuthorizationContext context, CancellationToken ct = default) =>
            Task.FromResult(operation == WorkflowOperation.UpdateSubjectFields
                ? WorkflowAuthorizationResult.Denied("fields are frozen")
                : WorkflowAuthorizationResult.Allowed);
    }
```

Remove Task 5's first `NewChangeRequestAsync` (this one replaces it). Before running, confirm against the codebase: the demo `WorkItemStatus` member names (the demo enum is `Estimated, InProgress, Complete`); the failed-completion task status (`NotStarted` is the entry task's status before completion — check `WorkflowTaskStatus`). Adjust names to what is there; do not change the assertions' meaning.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~UpdateFieldsTriggerTests"`
Expected: build error — `BuiltInTriggerKeys.UpdateFields` does not exist.

- [ ] **Step 3: Add the key.** In `BuiltInTriggerKeys`:

```csharp
    public const string Webhook = "workflow.webhook";
    public const string UpdateFields = "workflow.updateFields";
```

- [ ] **Step 4: Write `UpdateFieldsTrigger.cs`**

```csharp
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
/// trigger fails before any record is loaded. Only then does it resolve and write.</para>
/// </summary>
public sealed class UpdateFieldsTrigger(
    WorkflowFieldCatalog catalog,
    IWorkflowDbContext db,
    IWorkflowAuthorizationPolicy policy) : IWorkflowTrigger
{
    public string Key => BuiltInTriggerKeys.UpdateFields;

    public TriggerDescriptor Describe() => new(
        Key,
        "Update fields",
        "Changes fields on the document this workflow runs against, or on records related to it.",
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
        DefaultFailurePolicy: TriggerFailurePolicy.FailOperation);

    public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var subjectType = context.Subject.SubjectType;
        var targetKey = context.Config.GetString("target")
            ?? throw new InvalidOperationException("No target is configured.");

        if (!catalog.TryGetTarget(subjectType, targetKey, out var target))
        {
            throw new InvalidOperationException($"{subjectType} has no '{targetKey}' to update.");
        }

        var assignments = FieldAssignments.Parse(context.Config.GetString("assignments"));

        if (assignments.Count == 0)
        {
            throw new InvalidOperationException("No fields are configured.");
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

        await AuthorizeAsync(context, target, assignments, ct).ConfigureAwait(false);

        var services = context.Services
            ?? throw new InvalidOperationException("Field updates need the trigger's service provider.");

        var entities = await target.Resolve(new FieldResolveContext(context.Subject, context.Task, services), ct)
            .ConfigureAwait(false);

        if (target.Cardinality == FieldCardinality.One && entities.Count != 1)
        {
            throw new InvalidOperationException(entities.Count == 0
                ? $"There is no {target.Label.ToLowerInvariant()} to update."
                : $"'{target.Label}' found {entities.Count} records; it should find exactly one.");
        }

        var write = new FieldWriteContext(context.ActorId, services);
        var changes = new List<string>();

        // Not `await using`: the scope must be told why it is being abandoned, so that if the
        // undo itself fails its error carries the original refusal rather than replacing it.
        var scope = await FieldWriteScope.BeginAsync(db, ct).ConfigureAwait(false);

        try
        {
            foreach (var entity in entities)
            {
                var changed = false;

                foreach (var (field, value) in planned)
                {
                    if (!field.AppliesTo(entity))
                    {
                        throw new InvalidOperationException(
                            $"'{target.Label}' found a {entity.GetType().Name}, which has no '{field.Key}'. "
                            + "The host's resolver for this subject type returns the wrong type.");
                    }

                    var before = field.Read(entity);

                    if (Equals(before, value))
                    {
                        continue;
                    }

                    await field.Write(entity, value, write, ct).ConfigureAwait(false);
                    changed = true;

                    changes.Add(
                        $"{scope.Describe(entity)} {field.Label}: "
                        + $"{FieldValueConverter.Format(before) ?? "(empty)"} → {FieldValueConverter.Format(value) ?? "(empty)"}");
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

        context.Report.Detail = changes.Count == 0 ? "Nothing to change." : string.Join("; ", changes);
    }

    private async Task AuthorizeAsync(
        WorkflowTriggerContext context,
        TargetRegistration target,
        IReadOnlyList<FieldAssignment> assignments,
        CancellationToken ct)
    {
        // The deadline sweep is the system acting on its own schedule; see IWorkflowActions.
        if (context.ActorId == WorkflowActors.System)
        {
            return;
        }

        var result = await policy.EvaluateAsync(
            WorkflowOperation.UpdateSubjectFields,
            new WorkflowAuthorizationContext(context.ActorId, context.Task, context.Subject, null)
            {
                FieldTarget = target.Key,
                Fields = [.. assignments.Select(a => a.Field)]
            },
            ct).ConfigureAwait(false);

        if (!result.IsAllowed)
        {
            throw new WorkflowAuthorizationException(
                WorkflowOperation.UpdateSubjectFields, context.ActorId, result.Reason ?? "Not permitted.");
        }
    }
}
```

The test for the branch target asserts "There is no work item for this task's section to update" — that is `target.Label.ToLowerInvariant()` of "Work item for this task's section". Keep labels and message in step.

- [ ] **Step 5: Register it.** In `WorkflowEngineBuilder` (`ServiceCollectionExtensions.cs`), after `AddTrigger<T>()`, add (with `using TaskRouter.EntityFrameworkCore.Fields;`):

```csharp
    /// <summary>
    /// Declares the fields workflow authors may set, and offers them the
    /// <c>workflow.updateFields</c> trigger. Call once; declare every subject type here.
    ///
    /// <para>The catalog is built — and every wiring mistake in it reported — here, as the
    /// application starts, not when the first task completes.</para>
    /// </summary>
    public WorkflowEngineBuilder AddWorkflowFields(Action<WorkflowFieldsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (Services.Any(d => d.ServiceType == typeof(WorkflowFieldCatalog)))
        {
            throw new InvalidOperationException(
                "AddWorkflowFields has already been called. Declare every subject type's fields in one call.");
        }

        var fields = new WorkflowFieldsBuilder();
        configure(fields);

        Services.AddSingleton(fields.Build());
        return AddTrigger<UpdateFieldsTrigger>();
    }
```

- [ ] **Step 6: Wire the demo and both test hosts.** In `Program.cs`, after `.AddTrigger<DemoEscalationTrigger>()`:

```csharp
    .AddTrigger<DemoEscalationTrigger>()
    // The fields authors may set on a document and its work items. Declared once, here,
    // so the builder only ever offers fields this host has code behind.
    .AddWorkflowFields(DemoWorkflowFields.Configure)
```

In `TestHost.cs`, chain `.AddWorkflowFields(DemoWorkflowFields.Configure)` after `.AddTrigger<DemoEscalationTrigger>()` (move the semicolon). Do the same in `EndpointTestHost.cs` after its `.AddTrigger<DemoEscalationTrigger>()`.

- [ ] **Step 7: Run the tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~UpdateFieldsTriggerTests"`
Expected: all pass.

- [ ] **Step 8: Run the whole suite** — the new trigger is now in every DI host's registry.

Run: `dotnet test TaskRouter.slnx`
Expected: all pass. A test asserting an exact count of trigger descriptors will now see one more; update the expected count and say why in the assertion message.

- [ ] **Step 9: Commit**

```bash
git add src samples tests
git commit -m "Let a workflow step set fields on its document and the work items under it"
```

---

### Task 8: The builder client describes, searches and validates fields

**Files:**
- Modify: `src/TaskRouter.Core/Builder/IWorkflowBuilderClient.cs`
- Modify: `src/TaskRouter.EntityFrameworkCore/Builder/EfWorkflowBuilderClient.cs`
- Test: `tests/TaskRouter.Tests/SubjectFieldsBuilderTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Builder;
using TaskRouter.Core.Fields;
using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Builder;
using TaskRouter.EntityFrameworkCore.Fields;
using TaskRouter.EntityFrameworkCore.Triggers;

namespace TaskRouter.Tests;

/// <summary>
/// What the builder needs to author a field update: the targets and fields for the
/// workflow's subject type, options for actor and lookup fields, and validation that
/// refuses a field update the host could never run.
/// </summary>
[TestClass]
public class SubjectFieldsBuilderTests
{
    private TestHost _host = null!;
    private IServiceScope _scope = null!;
    private IWorkflowBuilderClient _client = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _host = await TestHost.CreateAsync(withTriggers: true);
        _scope = _host.Provider!.CreateScope();

        var services = _scope.ServiceProvider;

        // Built by hand, as BuilderClientTests does: TestHost registers no builder client.
        // Everything else comes from the container, so the registry includes
        // workflow.updateFields and the actor search reaches the demo's directory.
        _client = new EfWorkflowBuilderClient(
            services.GetRequiredService<IWorkflowDbContext>(),
            services.GetRequiredService<IWorkflowTriggerRegistry>(),
            [new RequiresReviewCondition()],
            new WorkflowBuilderOptions { SubjectTypes = [.. DemoWorkflowFields.DocumentSubjectTypes] },
            new TestEditorActorAccessor(),
            services.GetRequiredService<WorkflowFieldCatalog>(),
            services);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _scope.Dispose();
        await _host.DisposeAsync();
    }

    [TestMethod]
    public async Task A_change_request_offers_its_own_fields_and_a_drawing_does_not()
    {
        var changeRequest = await _client.GetSubjectFieldsAsync("ChangeRequest");
        var drawing = await _client.GetSubjectFieldsAsync("Drawing");

        CollectionAssert.AreEqual(
            new[] { "document", "branchWorkItem", "allWorkItems" },
            changeRequest.Select(t => t.Key).ToArray());

        CollectionAssert.Contains(changeRequest[0].Fields.Select(f => f.Key).ToList(), "Urgency");
        CollectionAssert.DoesNotContain(drawing[0].Fields.Select(f => f.Key).ToList(), "Urgency");
    }

    [TestMethod]
    public async Task An_actor_field_searches_the_directory()
    {
        var options = await _client.SearchFieldOptionsAsync("ChangeRequest", "document", "CogCoordinatorActorId", null);

        Assert.IsNotEmpty(options, "The demo directory has people; an actor field should offer them.");
    }

    [TestMethod]
    public async Task A_valid_field_update_passes_validation()
    {
        var errors = await ValidateWithAsync("ChangeRequest", "document",
            [new("Status", "OnHold"), new("Comments", "Held by {actor}"), new("InternalDueDate", "{today+5d}")]);

        Assert.IsFalse(errors.Any(e => e.Code.StartsWith("FIELDS_", StringComparison.Ordinal)),
            string.Join("; ", errors.Select(e => e.Message)));
    }

    [TestMethod]
    public async Task A_field_the_subject_type_lacks_is_refused()
    {
        var errors = await ValidateWithAsync("Drawing", "document", [new("Urgency", "Priority")]);

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_UNKNOWN_FIELD"), Codes(errors));
    }

    [TestMethod]
    public async Task An_unknown_target_is_refused()
    {
        var errors = await ValidateWithAsync("ChangeRequest", "theDocument", [new("Status", "OnHold")]);

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_UNKNOWN_TARGET"), Codes(errors));
    }

    [TestMethod]
    public async Task A_choice_that_is_not_a_member_is_refused()
    {
        var errors = await ValidateWithAsync("ChangeRequest", "document", [new("Status", "On Hold")]);

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_BAD_VALUE"), Codes(errors));
    }

    [TestMethod]
    public async Task A_token_in_a_choice_is_refused()
    {
        var errors = await ValidateWithAsync("ChangeRequest", "document", [new("Status", "{var:status}")]);

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_TOKEN_NOT_ALLOWED"), Codes(errors));
    }

    [TestMethod]
    public async Task A_field_update_needs_a_subject_type()
    {
        var errors = await ValidateWithAsync(null, "document", [new("Status", "OnHold")]);

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_NO_SUBJECT"), Codes(errors));
    }

    private async Task<IReadOnlyList<Core.Validation.ValidationError>> ValidateWithAsync(
        string? subjectType, string target, IReadOnlyList<FieldAssignment> assignments)
    {
        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinition!.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName && v.IsPublished)
            .Select(v => v.Id)
            .FirstAsync();

        var model = (await _client.GetWorkflowAsync(versionId))!;
        model.SubjectType = subjectType;

        model.Tasks[0].Triggers.Add(new TriggerEditModel
        {
            TriggerKey = BuiltInTriggerKeys.UpdateFields,
            Configuration = new()
            {
                ["target"] = target,
                ["assignments"] = FieldAssignments.Serialize(assignments)
            }
        });

        return await _client.ValidateAsync(model);
    }

    private static string Codes(IEnumerable<Core.Validation.ValidationError> errors) =>
        "Got: " + string.Join("; ", errors.Select(e => $"{e.Code} {e.Message}"));
}
```

Before running, confirm the namespaces of `EfWorkflowBuilderClient`, `WorkflowBuilderOptions` and `IWorkflowTriggerRegistry` against `BuilderClientTests`' usings, and confirm `WorkflowDefinitionVersion` navigates to its definition as `WorkflowDefinition` and that `ValidationError` lives in `TaskRouter.Core.Validation`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~SubjectFieldsBuilderTests"`
Expected: build error — `GetSubjectFieldsAsync` does not exist.

- [ ] **Step 3: Add the interface members** with defaults, so any other implementation keeps compiling. In `IWorkflowBuilderClient`, after `GetSubjectTypesAsync` (add `using TaskRouter.Core.Fields;`):

```csharp
    /// <summary>
    /// The targets and fields a field-update trigger may set on a workflow of this subject
    /// type. Empty when the host declares none.
    /// </summary>
    Task<IReadOnlyList<SubjectTargetInfo>> GetSubjectFieldsAsync(
        string subjectType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SubjectTargetInfo>>([]);

    /// <summary>Options for an actor or lookup field, narrowed by what the author has typed.</summary>
    Task<IReadOnlyList<FieldChoice>> SearchFieldOptionsAsync(
        string subjectType, string target, string field, string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<FieldChoice>>([]);
```

- [ ] **Step 4: Implement them in `EfWorkflowBuilderClient`.** Extend the primary constructor with two optional parameters at the end (existing hand-built instances in tests keep compiling):

```csharp
public sealed class EfWorkflowBuilderClient(
    IWorkflowDbContext db,
    IWorkflowTriggerRegistry triggers,
    IEnumerable<IRouteConditionEvaluator> conditions,
    WorkflowBuilderOptions options,
    IWorkflowEditorActorAccessor actors,
    WorkflowFieldCatalog? fields = null,
    IServiceProvider? services = null) : IWorkflowBuilderClient
```

After `GetSubjectTypesAsync`, add:

```csharp
    public Task<IReadOnlyList<SubjectTargetInfo>> GetSubjectFieldsAsync(
        string subjectType, CancellationToken ct = default) =>
        Task.FromResult(fields?.Describe(subjectType) ?? []);

    /// <summary>
    /// A lookup asks the host's options delegate. An actor field with no delegate of its
    /// own asks the directory — the same one the reassign picker uses — so a host gets
    /// actor pickers without writing anything.
    /// </summary>
    public async Task<IReadOnlyList<FieldChoice>> SearchFieldOptionsAsync(
        string subjectType, string target, string field, string? search, CancellationToken ct = default)
    {
        if (fields is null || services is null
            || !fields.TryGetTarget(subjectType, target, out var registration))
        {
            return [];
        }

        var match = registration.Fields.FirstOrDefault(f => f.Key == field);

        if (match?.Options is { } options)
        {
            return await options(new FieldOptionsContext(search, services), ct).ConfigureAwait(false);
        }

        if (match?.Kind != FieldKind.Actor)
        {
            return [];
        }

        var directory = services.GetService(typeof(IWorkflowActorResolver)) as IWorkflowActorResolver;

        if (directory is null)
        {
            return [];
        }

        var actors = await directory.GetActorsAsync(ct).ConfigureAwait(false);

        return [.. actors
            .Where(a => string.IsNullOrWhiteSpace(search)
                || a.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || a.ActorId.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .Select(a => new FieldChoice(a.ActorId, a.DisplayName))];
    }
```

In `ValidateAsync`, add the field checks after the validator line:

```csharp
        errors.AddRange(StructuralErrors(model));
        errors.AddRange(WorkflowDefinitionValidator.Validate(ToTransientVersion(model)));
        errors.AddRange(FieldErrors(model));
```

and add the method below `StructuralErrors` (usings: `TaskRouter.Core.Fields`, `TaskRouter.EntityFrameworkCore.Fields`):

```csharp
    /// <summary>
    /// A field update the host could never run: a target or field this subject type does
    /// not have, a value that cannot become the field's type, or a token where only a fixed
    /// value makes sense. Not structural — a draft may be saved half-configured — so these
    /// block publishing, not saving.
    ///
    /// <para>Lookup values are not checked: whether key 12 exists is a question about data,
    /// and is answered when the trigger runs.</para>
    /// </summary>
    private List<ValidationError> FieldErrors(WorkflowEditModel model)
    {
        var errors = new List<ValidationError>();

        foreach (var task in model.Tasks)
        {
            foreach (var trigger in task.Triggers.Where(t =>
                string.Equals(t.TriggerKey, BuiltInTriggerKeys.UpdateFields, StringComparison.OrdinalIgnoreCase)))
            {
                if (fields is null)
                {
                    errors.Add(new("FIELDS_NOT_CONFIGURED", $"'{task.Label}' updates fields, but this host declares none."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(model.SubjectType))
                {
                    errors.Add(new("FIELDS_NO_SUBJECT",
                        $"'{task.Label}' updates fields, so the workflow needs a subject type."));
                    continue;
                }

                var targetKey = trigger.Configuration.GetValueOrDefault("target");

                if (targetKey is null || !fields.TryGetTarget(model.SubjectType, targetKey, out var target))
                {
                    errors.Add(new("FIELDS_UNKNOWN_TARGET",
                        $"'{task.Label}': {model.SubjectType} has no '{targetKey}' to update."));
                    continue;
                }

                IReadOnlyList<FieldAssignment> assignments;

                try
                {
                    assignments = FieldAssignments.Parse(trigger.Configuration.GetValueOrDefault("assignments"));
                }
                catch (FormatException ex)
                {
                    errors.Add(new("FIELDS_BAD_CONFIG", $"'{task.Label}': {ex.Message}"));
                    continue;
                }

                foreach (var assignment in assignments)
                {
                    var field = target.Fields.FirstOrDefault(f => f.Key == assignment.Field);

                    if (field is null)
                    {
                        errors.Add(new("FIELDS_UNKNOWN_FIELD",
                            $"'{task.Label}': {model.SubjectType} has no field '{assignment.Field}' on '{target.Label}'."));
                        continue;
                    }

                    if (assignment.Value?.Contains('{', StringComparison.Ordinal) == true)
                    {
                        if (field.Kind is FieldKind.Choice or FieldKind.Boolean or FieldKind.Lookup)
                        {
                            errors.Add(new("FIELDS_TOKEN_NOT_ALLOWED",
                                $"'{task.Label}': {field.Label} takes a fixed value, not '{assignment.Value}'."));
                        }

                        continue;   // a token's value is only known when the trigger runs
                    }

                    if (field.Kind is FieldKind.Lookup)
                    {
                        continue;
                    }

                    if (!FieldValueConverter.TryConvert(assignment.Value, field.ValueType, out _, out var error))
                    {
                        errors.Add(new("FIELDS_BAD_VALUE", $"'{task.Label}': {field.Label}: {error}"));
                    }
                }
            }
        }

        return errors;
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~SubjectFieldsBuilderTests|FullyQualifiedName~BuilderClientTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src tests/TaskRouter.Tests/SubjectFieldsBuilderTests.cs
git commit -m "Tell the builder which fields a workflow may set, and refuse ones its host could not"
```

---

### Task 9: Over HTTP

**Files:**
- Modify: `src/TaskRouter.AspNetCore/BuilderEndpoints.cs` (after `/subject-types`, ~line 116)
- Modify: `src/TaskRouter.Blazor/Http/HttpWorkflowBuilderClient.cs` (after `GetSubjectTypesAsync`, ~line 105)
- Test: add to `tests/TaskRouter.Tests/HttpBuilderClientTests.cs`

- [ ] **Step 1: Write the failing tests** — append to `HttpBuilderClientTests`:

```csharp
    [TestMethod]
    public async Task Subject_fields_round_trip()
    {
        var targets = await _client.GetSubjectFieldsAsync("ChangeRequest");

        Assert.AreEqual("document", targets[0].Key);
        var status = targets[0].Fields.Single(f => f.Key == "Status");
        Assert.AreEqual(TaskRouter.Core.Fields.FieldKind.Choice, status.Kind,
            "Enums nested inside records are the first thing a careless serializer flattens.");
        Assert.IsNotEmpty(status.Choices);
    }

    [TestMethod]
    public async Task Field_options_round_trip_and_escape_the_search()
    {
        var options = await _client.SearchFieldOptionsAsync(
            "ChangeRequest", "document", "CogCoordinatorActorId", "a b&c");

        Assert.IsNotNull(options);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~HttpBuilderClientTests"`
Expected: `Subject_fields_round_trip` fails — the default interface implementation returns empty, so `targets[0]` throws.

- [ ] **Step 3: Map the routes** in `BuilderEndpoints.cs`, after `/subject-types`:

```csharp
        builder.MapGet("/fields/{subjectType}", async (
            string subjectType, [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.GetSubjectFieldsAsync(subjectType, ct).ConfigureAwait(false)));

        builder.MapGet("/fields/{subjectType}/{target}/{field}/options", async (
            string subjectType, string target, string field, string? q,
            [FromServices] IWorkflowBuilderClient client, CancellationToken ct) =>
            Results.Ok(await client.SearchFieldOptionsAsync(subjectType, target, field, q, ct).ConfigureAwait(false)));
```

- [ ] **Step 4: Call them** in `HttpWorkflowBuilderClient`, after `GetSubjectTypesAsync` (add `using TaskRouter.Core.Fields;`):

```csharp
    public async Task<IReadOnlyList<SubjectTargetInfo>> GetSubjectFieldsAsync(
        string subjectType, CancellationToken ct = default) =>
        await GetAsync<List<SubjectTargetInfo>>(
            $"{_root}/fields/{Uri.EscapeDataString(subjectType)}", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<FieldChoice>> SearchFieldOptionsAsync(
        string subjectType, string target, string field, string? search, CancellationToken ct = default) =>
        await GetAsync<List<FieldChoice>>(
            $"{_root}/fields/{Uri.EscapeDataString(subjectType)}/{Uri.EscapeDataString(target)}"
            + $"/{Uri.EscapeDataString(field)}/options?q={Uri.EscapeDataString(search ?? string.Empty)}", ct)
            .ConfigureAwait(false);
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~HttpBuilderClientTests|FullyQualifiedName~BuilderEndpointTests|FullyQualifiedName~EndpointRouteTests"`
Expected: all pass. If `EndpointRouteTests` enumerates every mapped route against an expected list, add the two new routes to it.

- [ ] **Step 6: Commit**

```bash
git add src tests/TaskRouter.Tests/HttpBuilderClientTests.cs
git commit -m "Serve a subject type's fields and their options to a builder over HTTP"
```

---

### Task 10: Editor state, testable without a renderer

**Files:**
- Create: `src/TaskRouter.Blazor/SubjectFieldsEditModel.cs`
- Test: `tests/TaskRouter.Tests/SubjectFieldsEditModelTests.cs`

The razor component in Task 11 is a thin skin over this class; all the logic that can be wrong lives here, where a unit test can reach it (the test project has no component renderer).

- [ ] **Step 1: Write the failing tests**

```csharp
using TaskRouter.Blazor;
using TaskRouter.Core.Fields;

namespace TaskRouter.Tests;

[TestClass]
public class SubjectFieldsEditModelTests
{
    private static readonly IReadOnlyList<SubjectTargetInfo> Targets =
    [
        new("document", "This document", FieldCardinality.One,
        [
            new("Status", "Status", FieldKind.Choice, false, [new("Scheduled", "Scheduled"), new("OnHold", "On Hold")]),
            new("Comments", "Comments", FieldKind.Text, true, [])
        ]),
        new("allWorkItems", "All work items", FieldCardinality.Many,
        [
            new("WorkItemState", "State", FieldKind.Choice, false, [])
        ])
    ];

    [TestMethod]
    public void It_reads_back_what_is_stored()
    {
        var model = SubjectFieldsEditModel.Load(Targets, new Dictionary<string, string?>
        {
            ["target"] = "document",
            ["assignments"] = FieldAssignments.Serialize([new("Status", "OnHold")])
        });

        Assert.AreEqual("document", model.TargetKey);
        Assert.HasCount(1, model.Rows);
        Assert.AreEqual("Status", model.Rows[0].Field!.Key);
        Assert.AreEqual("OnHold", model.Rows[0].Value);
    }

    [TestMethod]
    public void It_writes_the_configuration_the_trigger_reads()
    {
        var model = SubjectFieldsEditModel.Load(Targets, []);
        model.SelectTarget("document");
        model.AddRow();
        model.SetField(0, "Comments");
        model.Rows[0].Value = "Held by {actor}";

        var configuration = new Dictionary<string, string?>();
        model.WriteTo(configuration);

        Assert.AreEqual("document", configuration["target"]);
        CollectionAssert.AreEqual(
            new[] { new FieldAssignment("Comments", "Held by {actor}") },
            FieldAssignments.Parse(configuration["assignments"]).ToArray());
    }

    [TestMethod]
    public void Changing_target_clears_rows_that_no_longer_apply()
    {
        var model = SubjectFieldsEditModel.Load(Targets, new Dictionary<string, string?>
        {
            ["target"] = "document",
            ["assignments"] = FieldAssignments.Serialize([new("Status", "OnHold")])
        });

        model.SelectTarget("allWorkItems");

        Assert.IsEmpty(model.Rows);
    }

    [TestMethod]
    public void A_field_already_used_is_not_offered_again()
    {
        var model = SubjectFieldsEditModel.Load(Targets, []);
        model.SelectTarget("document");
        model.AddRow();
        model.SetField(0, "Status");
        model.AddRow();

        CollectionAssert.AreEqual(new[] { "Comments" }, model.AvailableFields(1).Select(f => f.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "Status", "Comments" }, model.AvailableFields(0).Select(f => f.Key).ToArray());
    }

    [TestMethod]
    public void A_stored_field_the_catalog_no_longer_has_is_kept_and_flagged()
    {
        var model = SubjectFieldsEditModel.Load(Targets, new Dictionary<string, string?>
        {
            ["target"] = "document",
            ["assignments"] = FieldAssignments.Serialize([new("Urgency", "Priority")])
        });

        Assert.IsNull(model.Rows[0].Field);
        Assert.AreEqual("Urgency", model.Rows[0].StoredKey);

        var configuration = new Dictionary<string, string?>();
        model.WriteTo(configuration);
        StringAssert.Contains(configuration["assignments"], "Urgency",
            "Opening and saving must not silently drop what validation should be reporting.");
    }

    [TestMethod]
    public void Changing_a_rows_field_resets_its_value()
    {
        var model = SubjectFieldsEditModel.Load(Targets, []);
        model.SelectTarget("document");
        model.AddRow();
        model.SetField(0, "Status");
        model.Rows[0].Value = "OnHold";

        model.SetField(0, "Comments");

        Assert.IsNull(model.Rows[0].Value);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~SubjectFieldsEditModelTests"`
Expected: build error — `SubjectFieldsEditModel` does not exist.

- [ ] **Step 3: Write `SubjectFieldsEditModel.cs`**

```csharp
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
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/TaskRouter.Tests --filter "FullyQualifiedName~SubjectFieldsEditModelTests"`
Expected: all pass. (`InternalsVisibleTo` for the Blazor project already exists.)

- [ ] **Step 5: Commit**

```bash
git add src/TaskRouter.Blazor/SubjectFieldsEditModel.cs tests/TaskRouter.Tests/SubjectFieldsEditModelTests.cs
git commit -m "Hold the field-update editor's state where a test can reach it"
```

---

### Task 11: The editor in the builder

**Files:**
- Create: `src/TaskRouter.Blazor/SubjectFieldsEditor.razor`
- Modify: `src/TaskRouter.Blazor/TriggerConfigForm.razor`
- Modify: `src/TaskRouter.Blazor/TriggerEditDialog.razor` (~line 60, parameters ~line 137)
- Modify: `src/TaskRouter.Blazor/WorkflowBuilder.razor` (~line 562)

No automated test: the test project has no component renderer. Task 13 verifies it in a browser.

- [ ] **Step 1: Write `SubjectFieldsEditor.razor`**

```razor
@using TaskRouter.Core.Builder
@using TaskRouter.Core.Fields
@inject IWorkflowBuilderClient Client

@*
    Edits a field-update trigger: one target, then field = value rows. The value control
    follows the field's kind, so an author picks "On Hold" from a list rather than typing
    an enum member name. Text and date fields also take tokens ({actor}, {today+5d}).
*@

@if (string.IsNullOrWhiteSpace(SubjectType))
{
    <MudAlert Severity="Severity.Warning" Dense="true">
        Choose what this workflow applies to (its subject type) before updating fields.
    </MudAlert>
}
else if (_model is null)
{
    <MudProgressLinear Indeterminate="true" />
}
else if (_model.Targets.Count == 0)
{
    <MudAlert Severity="Severity.Info" Dense="true">
        This application has no fields a workflow may set on a @SubjectType.
    </MudAlert>
}
else
{
    <MudStack Spacing="2">
        <MudSelect T="string" Label="Target" Value="_model.TargetKey" ValueChanged="SelectTargetAsync" Dense="true">
            @foreach (var target in _model.Targets)
            {
                <MudSelectItem Value="@target.Key">@target.Label</MudSelectItem>
            }
        </MudSelect>

        @if (_model.Target is not null)
        {
            @for (var i = 0; i < _model.Rows.Count; i++)
            {
                var index = i;
                var row = _model.Rows[index];

                <MudStack Row="true" AlignItems="AlignItems.Center" Spacing="2">
                    <MudSelect T="string" Label="Field" Value="row.StoredKey" Dense="true" Style="min-width: 12rem"
                               ValueChanged="@(key => SetFieldAsync(index, key))">
                        @if (row.Field is null && row.StoredKey is not null)
                        {
                            <MudSelectItem Value="@row.StoredKey">@row.StoredKey (no longer available)</MudSelectItem>
                        }
                        @foreach (var field in _model.AvailableFields(index))
                        {
                            <MudSelectItem Value="@field.Key">@field.Label</MudSelectItem>
                        }
                    </MudSelect>

                    <MudText>=</MudText>

                    @switch (row.Field?.Kind)
                    {
                        case FieldKind.Choice:
                            <MudSelect T="string" Label="Value" Value="row.Value" Dense="true" Clearable="@row.Field.IsNullable"
                                       ValueChanged="@(v => SetValueAsync(row, v))">
                                @foreach (var choice in row.Field.Choices)
                                {
                                    <MudSelectItem Value="@choice.Value">@choice.Label</MudSelectItem>
                                }
                            </MudSelect>
                            break;

                        case FieldKind.Boolean:
                            <MudSwitch T="bool" Value="@(bool.TryParse(row.Value, out var on) && on)"
                                       ValueChanged="@(v => SetValueAsync(row, v ? "true" : "false"))" />
                            break;

                        case FieldKind.Actor:
                        case FieldKind.Lookup:
                            <MudAutocomplete T="string" Label="Value" Value="row.Value" Dense="true"
                                             CoerceValue="@(row.Field.Kind == FieldKind.Actor)"
                                             SearchFunc="@((text, ct) => SearchAsync(row.Field.Key, text, ct))"
                                             ToStringFunc="@(v => Label(row.Field.Key, v))"
                                             ValueChanged="@(v => SetValueAsync(row, v))"
                                             HelperText="@(row.Field.Kind == FieldKind.Actor ? "Or {actor} or {task.assignee}" : null)" />
                            break;

                        case FieldKind.Date:
                            <MudTextField T="string" Label="Value" Value="row.Value" Dense="true"
                                          HelperText="2026-10-20, or {today}, {today+5d}, {now}"
                                          ValueChanged="@(v => SetValueAsync(row, v))" />
                            break;

                        case null:
                            <MudTextField T="string" Label="Value" Value="row.Value" Dense="true" Disabled="true" />
                            break;

                        default:
                            <MudTextField T="string" Label="Value" Value="row.Value" Dense="true"
                                          HelperText="May use {actor}, {today}, {var:name}"
                                          ValueChanged="@(v => SetValueAsync(row, v))" />
                            break;
                    }

                    <MudIconButton Icon="@Icons.Material.Filled.Close" Size="Size.Small"
                                   aria-label="Remove this field" OnClick="@(() => RemoveRowAsync(index))" />
                </MudStack>
            }

            <MudButton StartIcon="@Icons.Material.Filled.Add" Variant="Variant.Text" Size="Size.Small"
                       Disabled="@(_model.AvailableFields(_model.Rows.Count).Count == 0)"
                       OnClick="AddRowAsync">
                Add field
            </MudButton>
        }
    </MudStack>
}

@code {
    [Parameter] public string? SubjectType { get; set; }
    [Parameter] public Dictionary<string, string?> Configuration { get; set; } = [];
    [Parameter] public EventCallback<Dictionary<string, string?>> ConfigurationChanged { get; set; }

    private SubjectFieldsEditModel? _model;
    private string? _loadedFor;
    private readonly Dictionary<string, string> _labels = [];

    protected override async Task OnParametersSetAsync()
    {
        if (string.IsNullOrWhiteSpace(SubjectType) || SubjectType == _loadedFor)
        {
            return;
        }

        _loadedFor = SubjectType;
        _model = null;

        var targets = await Client.GetSubjectFieldsAsync(SubjectType);
        _model = SubjectFieldsEditModel.Load(targets, Configuration);
    }

    private async Task<IEnumerable<string>> SearchAsync(string field, string? text, CancellationToken ct)
    {
        var options = await Client.SearchFieldOptionsAsync(SubjectType!, _model!.TargetKey!, field, text, ct);

        foreach (var option in options)
        {
            _labels[$"{field}|{option.Value}"] = option.Label;
        }

        return options.Select(o => o.Value);
    }

    private string Label(string field, string? value) =>
        value is null ? string.Empty : _labels.GetValueOrDefault($"{field}|{value}", value);

    private Task SelectTargetAsync(string? key)
    {
        _model!.SelectTarget(key);
        return SaveAsync();
    }

    private Task SetFieldAsync(int index, string? key)
    {
        _model!.SetField(index, key);
        return SaveAsync();
    }

    private Task SetValueAsync(SubjectFieldsEditModel.Row row, string? value)
    {
        row.Value = value;
        return SaveAsync();
    }

    private Task AddRowAsync()
    {
        _model!.AddRow();
        return SaveAsync();
    }

    private Task RemoveRowAsync(int index)
    {
        _model!.RemoveRow(index);
        return SaveAsync();
    }

    private async Task SaveAsync()
    {
        _model!.WriteTo(Configuration);
        await ConfigurationChanged.InvokeAsync(Configuration);
    }
}
```

Before building, check the MudBlazor version's `MudAutocomplete.SearchFunc` signature (`Func<string, CancellationToken, Task<IEnumerable<T>>>` in MudBlazor 7+; the existing `OptionPicker.razor` uses it — copy its form if it differs).

- [ ] **Step 2: Render it from the form.** In `TriggerConfigForm.razor`, add a parameter next to the others:

```csharp
    /// <summary>The workflow's subject type — what a field-update trigger's fields depend on.</summary>
    [Parameter] public string? SubjectType { get; set; }
```

and two cases in the parameter `switch`, before the `default` text-field case:

```razor
                case TriggerParameterKind.FieldTarget:
                    @* One editor owns both the target and the assignments parameters. *@
                    <SubjectFieldsEditor SubjectType="@SubjectType"
                                         Configuration="Configuration"
                                         ConfigurationChanged="ConfigurationChanged" />
                    break;

                case TriggerParameterKind.FieldAssignments:
                    break;
```

- [ ] **Step 3: Pass the subject type down.** In `TriggerEditDialog.razor`, add `[Parameter] public string? SubjectType { get; set; }` beside `RoleOptions`, and pass it on:

```razor
            <TriggerConfigForm Descriptor="Selected"
                               Configuration="_edit.Configuration"
                               TaskOptions="TaskOptions"
                               RoleOptions="RoleOptions"
                               SubjectType="SubjectType" />
```

In `WorkflowBuilder.razor` where the dialog's parameters are built:

```csharp
        var parameters = new DialogParameters<TriggerEditDialog>
        {
            { x => x.Model, trigger },
            { x => x.Descriptors, _descriptors },
            { x => x.TaskOptions, TaskOptions },
            { x => x.RoleOptions, _roles },
            { x => x.SubjectType, _model.SubjectType }
        };
```

- [ ] **Step 4: Make the failure-policy default follow the descriptor.** Check how `TriggerEditDialog` initialises `FailurePolicy` and `DispatchMode` when a trigger is chosen (`grep -n "DefaultFailurePolicy\|DefaultDispatch" src/TaskRouter.Blazor/*.razor`). If choosing a trigger does not copy `Descriptor.DefaultFailurePolicy` onto the model, add that next to wherever `DefaultDispatch` is copied — otherwise every new field update is created as `LogAndContinue`, contrary to the spec. If neither is copied, copy both at the point the selected descriptor changes for a **new** trigger only.

- [ ] **Step 5: Build**

Run: `dotnet build TaskRouter.slnx -v q`
Expected: 0 errors, no new warnings.

- [ ] **Step 6: Commit**

```bash
git add src/TaskRouter.Blazor
git commit -m "Let an author pick a target, fields and values for a field update in the builder"
```

---

### Task 12: Documentation

**Files:**
- Modify: `README.md`
- Modify: `STATE.md`

- [ ] **Step 1: README.** Find the section on triggers (`grep -n "^##" README.md`) and add a subsection after the built-in triggers list:

````markdown
### Letting authors set fields — `workflow.updateFields`

Declare, once, which fields a workflow may set, and the builder offers an **Update fields**
trigger over exactly those:

```csharp
builder.Services.AddTaskRouterFor<AppDbContext>()
    .AddWorkflowFields(fields =>
    {
        // Every document type: shared fields.
        fields.ForSubjectTypes(["ChangeRequest", "Drawing"])
            .Target<DocumentBase>("document", "This document", FieldCardinality.One,
                ctx => ctx.Db<AppDbContext>().Documents.Where(d => d.Id == int.Parse(ctx.Subject.SubjectId)))
                .Field(d => d.Status, "Status", write: (doc, status, w, ct) => statusRules.MoveTo(doc, status))
                .Field(d => d.Comments, "Comments");

        // One type's own fields, added to the same target.
        fields.ForSubjectType("ChangeRequest")
            .Extend<ChangeRequest>("document")
                .Field(c => c.Urgency, "Urgency");
    });
```

- **Kinds** come from the property type (text, number, true/false, date, enum). Pass
  `FieldKind.Actor` for an actor id, or `FieldKind.Lookup` with an `options` delegate for a key
  into another table.
- **Give a field a `write` delegate** whenever host rules guard it. Throwing refuses the write;
  the message is what the user sees.
- **Values** may use `{actor}`, `{today}`, `{today+Nd}`, `{now}`, `{var:name}` and the task tokens.
- **All or nothing.** Every assignment on every record succeeds or none does — including writes
  your own code saved — and under *Log and continue* the step still completes.
- **Checked as the actor**, as `WorkflowOperation.UpdateSubjectFields`, with the target and
  field keys on the context.
- **Schema:** this release adds `TaskRouterTriggerExecutions.Detail` (nullable). Add a migration.
````

- [ ] **Step 2: STATE.md.** Add a dated entry at the top of the "Last updated" paragraph in the existing style: `2026-10-14` (or the actual date): field-update triggers on branch `field-triggers` — what shipped, the one schema change, and that TRDIS wiring is the next plan. Do not bump the version here; the repo bumps versions in their own commit at merge time.

- [ ] **Step 3: Commit**

```bash
git add README.md STATE.md
git commit -m "Document field-update triggers"
```

---

### Task 13: Verify end to end

- [ ] **Step 1: Full suite and build**

Run: `dotnet build TaskRouter.slnx -v q 2>&1 | grep -E "warning [A-Z]{2}[0-9]+" | grep -v NU190 ; dotnet test TaskRouter.slnx`
Expected: no code warnings printed; all tests pass, 551 + the new ones.

- [ ] **Step 2: Drive it in a browser.** Use the `superpowers:verification-before-completion` discipline: evidence, not assumption.

```bash
cd samples/DemoDocuments.Server && dotnet run --urls http://localhost:5199
```

1. Open `/workflows`, create a draft of the change-request workflow, set its subject type to `ChangeRequest`.
2. On the first task add a trigger, choose **Update fields**. Confirm the failure policy defaults to *fail the step*.
3. Target **This document**; add **Status = On Hold** (picked from a list showing labels) and **Comments = Held by {actor}**. Confirm **Urgency** is offered.
4. Switch the workflow's subject type to `Drawing`: the editor reloads, **Urgency** is gone; validation reports the stored row if it was Urgency.
5. Publish (back on `ChangeRequest`). Open `/documents/{id}` for a scheduled change request, complete the first task. The document shows **OnHold** and the comment.
6. Do the same on a change request whose status is **Complete**: completion is refused with "cannot move from Complete to OnHold" and nothing changed.

Record what was seen, with screenshots or a GIF, in the PR description.

- [ ] **Step 3: Finish the branch**

Use `superpowers:finishing-a-development-branch`.

---

## Follow-up plan (not this one): TRDIS wiring in `../Dev`

Written after this ships as a preview package. Contents, so nothing is lost:

- Take the new preview; add the `TriggerExecutionDetail` migration to TRDIS (watch the CRLF churn rule in `TASKROUTER-INTEGRATION.md`).
- `IDocumentService.TransitionToAsync(DocumentBase, DocumentStatus, string? modifierEdipi)` and `IWorkItemService.TransitionToAsync(WorkItem, WorkItemStatus, string? modifierEdipi)`: build the Stateless machine, pick the permitted trigger whose destination is the target (`GetPermittedTriggers` + `GetInfo()`), fire it; no-op when already there; refactor `FireTriggerAsync` to share the core.
- `TrdisWorkflowFields.cs` (base `document` target over every `TrdisDocumentType`, `branchWorkItem` by `AssignedSectionId == int.Parse(branchKey)`, `allWorkItems`), `TrdisWorkflowFields.Lar.cs` (Urgency, IsReplyRequired, DateReplyRequiredBy, DateReplyReceived, DateSent, CogCoordinatorId as Actor, RecipientId as Lookup over IndustrialActivity). `IsCancelled` deliberately absent.
- Stamping through `TrdisWorkflowVocabulary.ToEdipi`; keep the existing modifier for `WorkflowActors.System`.
- `TrdisAuthorizationPolicy`: decide who may `UpdateSubjectFields`.
- Tests in the `Dev` fixture: Misc Document to On Hold; Issued refuses and rolls back; LAR sets Urgency.
