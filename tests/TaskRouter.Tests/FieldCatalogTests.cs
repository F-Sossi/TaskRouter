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
    public void A_nested_member_chain_is_not_a_field()
    {
        var target = new WorkflowFieldsBuilder().ForSubjectType("Drawing")
            .Target<DocumentBase>("document", "This document", FieldCardinality.One, NoDocuments);

        Assert.ThrowsExactly<ArgumentException>(() => target.Field(d => d.Group!.Name, "Group name"));
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
