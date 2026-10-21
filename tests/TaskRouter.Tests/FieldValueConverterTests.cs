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
        Assert.IsTrue((bool)flag!);

        Assert.IsTrue(FieldValueConverter.TryConvert("2.5", typeof(double), out var hours, out _));
        Assert.AreEqual(2.5, hours);

        Assert.IsTrue(FieldValueConverter.TryConvert("12", typeof(int?), out var id, out _));
        Assert.AreEqual(12, id);
    }

    [TestMethod]
    public void Unparseable_text_says_what_was_wrong()
    {
        Assert.IsFalse(FieldValueConverter.TryConvert("next week", typeof(DateTime), out _, out var error));
        Assert.AreEqual("'next week' is not a date (yyyy-MM-dd).", error);
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
    public void An_empty_string_formats_as_empty_like_null()
    {
        // Null means empty, and the change summary shows it as "(empty)". An empty string
        // must too, or a summary would end in a bare arrow.
        Assert.IsNull(FieldValueConverter.Format(string.Empty));
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

    private enum Level
    {
        Tier1 = 1,
        Tier2 = 2
    }

    [TestMethod]
    public void An_enum_name_must_match_exactly_with_no_flag_lists_padding_or_numbers()
    {
        Assert.IsFalse(FieldValueConverter.TryConvert("Hold, Done", typeof(Phase), out _, out _));
        Assert.IsFalse(FieldValueConverter.TryConvert(" Hold", typeof(Phase), out _, out _));
        Assert.IsFalse(FieldValueConverter.TryConvert("4", typeof(Phase), out _, out _));
        Assert.IsFalse(FieldValueConverter.TryConvert("-1", typeof(Phase), out _, out _));
        Assert.IsFalse(FieldValueConverter.TryConvert("hold", typeof(Phase), out _, out _));
    }

    [TestMethod]
    public void An_enum_member_with_a_digit_in_its_name_is_accepted()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("Tier2", typeof(Level), out var value, out _));
        Assert.AreEqual(Level.Tier2, value);
    }

    [TestMethod]
    public void Dates_must_be_iso()
    {
        Assert.IsFalse(FieldValueConverter.TryConvert("10/20/2026", typeof(DateTime), out _, out var error));
        Assert.AreEqual("'10/20/2026' is not a date (yyyy-MM-dd).", error);
    }

    [TestMethod]
    public void The_now_token_round_trips_as_utc()
    {
        var now = DateTime.UtcNow;

        Assert.IsTrue(FieldValueConverter.TryConvert(now.ToString("O"), typeof(DateTime), out var value, out _));

        var parsed = (DateTime)value!;
        Assert.AreEqual(now, parsed);
        Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
    }

    [TestMethod]
    public void A_date_with_an_offset_becomes_utc()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("2026-10-20T10:00:00+02:00", typeof(DateTime), out var value, out _));

        var parsed = (DateTime)value!;
        Assert.AreEqual(new DateTime(2026, 10, 20, 8, 0, 0), parsed);
        Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
    }

    [TestMethod]
    public void A_plain_date_is_midnight_and_unspecified()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("2026-10-20", typeof(DateTime), out var value, out _));

        var parsed = (DateTime)value!;
        Assert.AreEqual(new DateTime(2026, 10, 20), parsed);
        Assert.AreEqual(DateTimeKind.Unspecified, parsed.Kind);
    }

    [TestMethod]
    public void An_unsupported_type_is_refused_whatever_the_input()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => FieldValueConverter.TryConvert(null, typeof(Guid?), out _, out _));
        Assert.ThrowsExactly<NotSupportedException>(() => FieldValueConverter.TryConvert("x", typeof(Guid), out _, out _));
    }

    [TestMethod]
    public void Whitespace_clears_a_nullable_non_text_field_and_is_text_for_a_string()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("  ", typeof(int?), out var cleared, out _));
        Assert.IsNull(cleared);

        Assert.IsFalse(FieldValueConverter.TryConvert("  ", typeof(int), out _, out var error));
        Assert.AreEqual("A value is required.", error);

        Assert.IsTrue(FieldValueConverter.TryConvert("  ", typeof(string), out var text, out _));
        Assert.AreEqual("  ", text);
    }

    [TestMethod]
    public void A_double_must_be_finite()
    {
        Assert.IsFalse(FieldValueConverter.TryConvert("NaN", typeof(double), out _, out var error));
        Assert.AreEqual("'NaN' is not a number.", error);
        Assert.IsFalse(FieldValueConverter.TryConvert("Infinity", typeof(double), out _, out _));
        Assert.IsFalse(FieldValueConverter.TryConvert("-Infinity", typeof(double), out _, out _));
    }

    [TestMethod]
    public void Long_decimal_and_bool_failures_parse_and_report()
    {
        Assert.IsTrue(FieldValueConverter.TryConvert("9000000000", typeof(long), out var big, out _));
        Assert.AreEqual(9000000000L, big);

        Assert.IsTrue(FieldValueConverter.TryConvert("12.50", typeof(decimal), out var money, out _));
        Assert.AreEqual(12.50m, money);

        Assert.IsFalse(FieldValueConverter.TryConvert("maybe", typeof(bool), out _, out var error));
        Assert.AreEqual("'maybe' is not true or false.", error);
    }

    [TestMethod]
    public void Choices_of_a_non_enum_is_refused()
    {
        Assert.ThrowsExactly<ArgumentException>(() => FieldValueConverter.ChoicesOf(typeof(int?)));
    }

    [TestMethod]
    public void Booleans_and_times_format_for_the_change_summary()
    {
        Assert.AreEqual("true", FieldValueConverter.Format(true));
        Assert.AreEqual("false", FieldValueConverter.Format(false));

        var moment = new DateTime(2026, 10, 20, 10, 30, 0, DateTimeKind.Utc);
        Assert.AreEqual(moment.ToString("O", System.Globalization.CultureInfo.InvariantCulture), FieldValueConverter.Format(moment));
    }

    [TestMethod]
    public void Assignments_with_a_null_element_or_no_field_are_a_format_error()
    {
        Assert.ThrowsExactly<FormatException>(() => FieldAssignments.Parse("[null]"));
        Assert.ThrowsExactly<FormatException>(() => FieldAssignments.Parse("[{}]"));
        Assert.ThrowsExactly<FormatException>(() => FieldAssignments.Parse("[{\"field\":\" \"}]"));
    }
}
