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
        var model = SubjectFieldsEditModel.Load(Targets, new Dictionary<string, string?>());
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
        var model = SubjectFieldsEditModel.Load(Targets, new Dictionary<string, string?>());
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
        var model = SubjectFieldsEditModel.Load(Targets, new Dictionary<string, string?>());
        model.SelectTarget("document");
        model.AddRow();
        model.SetField(0, "Status");
        model.Rows[0].Value = "OnHold";

        model.SetField(0, "Comments");

        Assert.IsNull(model.Rows[0].Value);
    }

    [TestMethod]
    public void A_row_never_given_a_field_is_not_written()
    {
        var model = SubjectFieldsEditModel.Load(Targets, new Dictionary<string, string?>());
        model.SelectTarget("document");
        model.AddRow();
        model.AddRow();
        model.SetField(1, "Status");
        model.Rows[1].Value = "OnHold";

        var configuration = new Dictionary<string, string?>();
        model.WriteTo(configuration);

        CollectionAssert.AreEqual(
            new[] { new FieldAssignment("Status", "OnHold") },
            FieldAssignments.Parse(configuration["assignments"]).ToArray());
    }
}
