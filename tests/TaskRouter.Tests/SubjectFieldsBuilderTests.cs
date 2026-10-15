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
