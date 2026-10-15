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
