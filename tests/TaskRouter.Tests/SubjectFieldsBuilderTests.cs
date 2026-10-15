using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
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

    [TestMethod]
    public async Task The_registered_builder_client_receives_the_catalog_and_the_service_provider()
    {
        await using var host = await EndpointTestHost.CreateAsync();
        using var scope = host.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IWorkflowBuilderClient>();

        var targets = await client.GetSubjectFieldsAsync("ChangeRequest");
        var options = await client.SearchFieldOptionsAsync("ChangeRequest", "document", "CogCoordinatorActorId", null);

        Assert.IsNotEmpty(targets, "The catalog should reach the DI-registered client.");
        Assert.IsNotEmpty(options, "The service provider should reach the DI-registered client.");
    }

    private Task<IReadOnlyList<Core.Validation.ValidationError>> ValidateWithAsync(
        string? subjectType, string target, IReadOnlyList<FieldAssignment> assignments) =>
        ValidateConfigAsync(_client, subjectType, new()
        {
            ["target"] = target,
            ["assignments"] = FieldAssignments.Serialize(assignments)
        });

    private async Task<IReadOnlyList<Core.Validation.ValidationError>> ValidateConfigAsync(
        IWorkflowBuilderClient client, string? subjectType, Dictionary<string, string?> configuration)
    {
        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinition!.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName && v.IsPublished)
            .Select(v => v.Id)
            .FirstAsync();

        var model = (await client.GetWorkflowAsync(versionId))!;
        model.SubjectType = subjectType;

        model.Tasks[0].Triggers.Add(new TriggerEditModel
        {
            TriggerKey = BuiltInTriggerKeys.UpdateFields,
            Configuration = configuration
        });

        return await client.ValidateAsync(model);
    }

    private sealed class Probe
    {
        public bool Flag { get; set; }
        public int? OwnerId { get; set; }
        public string? Name { get; set; }
        public string? ReviewerId { get; set; }
    }

    /// <summary>A client over a catalog of its own, for the cases the demo's fields cannot express.</summary>
    private EfWorkflowBuilderClient ClientWith(
        Func<FieldOptionsContext, CancellationToken, Task<IReadOnlyList<FieldChoice>>> owners, bool withCatalog = true)
    {
        var fields = new WorkflowFieldsBuilder();
        fields.ForSubjectType("Probe")
            .Target<Probe>("probe", "The probe", FieldCardinality.One, _ => Enumerable.Empty<Probe>().AsQueryable())
                .Field(p => p.Flag, "Flag")
                .Field(p => p.OwnerId, "Owner", FieldKind.Lookup, options: owners)
                .Field(p => p.Name, "Name")
                .Field(p => p.ReviewerId, "Reviewer", FieldKind.Actor, options: owners);

        var services = _scope.ServiceProvider;

        return new EfWorkflowBuilderClient(
            services.GetRequiredService<IWorkflowDbContext>(),
            services.GetRequiredService<IWorkflowTriggerRegistry>(),
            [new RequiresReviewCondition()],
            new WorkflowBuilderOptions(),
            new TestEditorActorAccessor(),
            withCatalog ? fields.Build() : null,
            services);
    }

    private static Task<IReadOnlyList<FieldChoice>> NoOwners(FieldOptionsContext c, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FieldChoice>>([]);

    private static Dictionary<string, string?> Config(string? target, string? assignments) =>
        new() { ["target"] = target, ["assignments"] = assignments };

    [TestMethod]
    public async Task A_host_with_no_catalog_refuses_a_field_update()
    {
        var errors = await ValidateConfigAsync(ClientWith(NoOwners, withCatalog: false), "Probe",
            Config("probe", FieldAssignments.Serialize([new("Name", "x")])));

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_NOT_CONFIGURED"), Codes(errors));
    }

    [TestMethod]
    public async Task Assignments_that_are_not_json_are_refused()
    {
        var errors = await ValidateWithAsync2("{not json");

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_BAD_CONFIG"), Codes(errors));
    }

    [TestMethod]
    public async Task A_blank_target_is_refused_as_missing_not_unknown()
    {
        var errors = await ValidateConfigAsync(_client, "ChangeRequest",
            Config("  ", FieldAssignments.Serialize([new("Status", "OnHold")])));

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_NO_TARGET"), Codes(errors));
        Assert.IsFalse(errors.Any(e => e.Code == "FIELDS_UNKNOWN_TARGET"), Codes(errors));
    }

    [TestMethod]
    public async Task A_field_update_that_sets_nothing_is_refused()
    {
        var errors = await ValidateWithAsync2("[]");

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_NO_FIELDS"), Codes(errors));
    }

    [TestMethod]
    public async Task A_token_in_a_boolean_is_refused()
    {
        var errors = await ValidateWithAsync2(FieldAssignments.Serialize([new("Flag", "{var:x}")]));

        Assert.IsTrue(errors.Any(e => e.Code == "FIELDS_TOKEN_NOT_ALLOWED"), Codes(errors));
    }

    [TestMethod]
    public async Task A_lookup_literal_is_not_checked_here()
    {
        var errors = await ValidateWithAsync2(FieldAssignments.Serialize([new("OwnerId", "12")]));

        Assert.IsFalse(errors.Any(e => e.Code.StartsWith("FIELDS_", StringComparison.Ordinal)), Codes(errors));
    }

    [TestMethod]
    public async Task Configuration_keys_are_read_case_insensitively()
    {
        var errors = await ValidateConfigAsync(_client, "ChangeRequest", new()
        {
            ["Target"] = "document",
            ["Assignments"] = FieldAssignments.Serialize([new("Status", "OnHold")])
        });

        Assert.IsFalse(errors.Any(e => e.Code.StartsWith("FIELDS_", StringComparison.Ordinal)), Codes(errors));
    }

    [TestMethod]
    public async Task A_field_error_names_the_task()
    {
        var errors = await ValidateWithAsync("ChangeRequest", "theDocument", [new("Status", "OnHold")]);
        var error = errors.Single(e => e.Code == "FIELDS_UNKNOWN_TARGET");

        var versionId = await _host.Db.WorkflowDefinitionVersions
            .Where(v => v.WorkflowDefinition!.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName && v.IsPublished)
            .Select(v => v.Id)
            .FirstAsync();
        var label = (await _client.GetWorkflowAsync(versionId))!.Tasks[0].Label;

        Assert.AreEqual(
            $"'{label}': 'theDocument' is not something a ChangeRequest workflow can update.", error.Message);
    }

    [TestMethod]
    public async Task A_lookup_uses_its_options_delegate_and_is_capped_at_twenty()
    {
        string? seen = null;
        var client = ClientWith((c, ct) =>
        {
            seen = c.Search;
            return Task.FromResult<IReadOnlyList<FieldChoice>>(
                [.. Enumerable.Range(1, 50).Select(i => new FieldChoice(i.ToString(), $"Owner {i}"))]);
        });

        var options = await client.SearchFieldOptionsAsync("Probe", "probe", "OwnerId", "  own ");

        Assert.HasCount(20, options);
        Assert.AreEqual("own", seen, "The search is trimmed before the host sees it.");
    }

    [TestMethod]
    public async Task A_whitespace_search_reaches_the_delegate_as_null()
    {
        var seen = "unset";
        var client = ClientWith((c, ct) => { seen = c.Search; return NoOwners(c, ct); });

        await client.SearchFieldOptionsAsync("Probe", "probe", "OwnerId", "   ");

        Assert.IsNull(seen);
    }

    [TestMethod]
    public async Task A_throwing_options_delegate_offers_nothing_rather_than_failing()
    {
        var client = ClientWith((c, ct) => throw new InvalidOperationException("the lookup table is down"));

        var options = await client.SearchFieldOptionsAsync("Probe", "probe", "OwnerId", null);

        Assert.IsEmpty(options);
    }

    [TestMethod]
    public async Task An_actor_field_with_an_options_delegate_asks_it_rather_than_the_directory()
    {
        var client = ClientWith((c, ct) =>
            Task.FromResult<IReadOnlyList<FieldChoice>>([new FieldChoice("reviewer-1", "The only reviewer")]));

        var options = await client.SearchFieldOptionsAsync("Probe", "probe", "ReviewerId", null);

        Assert.HasCount(1, options);
        Assert.AreEqual("reviewer-1", options[0].Value);
    }

    [TestMethod]
    public async Task An_actor_search_narrows_to_matching_people()
    {
        var all = await _client.SearchFieldOptionsAsync("ChangeRequest", "document", "CogCoordinatorActorId", null);
        var narrowed = await _client.SearchFieldOptionsAsync("ChangeRequest", "document", "CogCoordinatorActorId", "Casey");

        Assert.IsGreaterThan(1, all.Count);
        Assert.HasCount(1, narrowed);
        StringAssert.Contains(narrowed[0].Label, "Casey");
    }

    private Task<IReadOnlyList<Core.Validation.ValidationError>> ValidateWithAsync2(string assignmentsJson) =>
        ValidateConfigAsync(ClientWith(NoOwners), "Probe", Config("probe", assignmentsJson));

    private static string Codes(IEnumerable<Core.Validation.ValidationError> errors) =>
        "Got: " + string.Join("; ", errors.Select(e => $"{e.Code} {e.Message}"));
}
