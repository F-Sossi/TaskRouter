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
