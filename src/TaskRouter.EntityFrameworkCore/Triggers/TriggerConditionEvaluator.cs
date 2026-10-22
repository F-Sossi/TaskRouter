using System.Globalization;

using TaskRouter.Core.Abstractions;

namespace TaskRouter.EntityFrameworkCore.Triggers;

/// <summary>
/// Evaluates trigger conditions over a fixed, tiny grammar:
///
///     &lt;operand&gt; &lt;op&gt; &lt;operand&gt; [ (&amp;&amp; | ||) ... ]
///
/// Operands are either a quoted literal, a bare literal, or one of a closed set of
/// paths: <c>task.status</c>, <c>task.outcome</c>, <c>task.type</c>,
/// <c>task.branch</c>, <c>task.assignee</c>, <c>run.status</c>, or <c>var.NAME</c>.
/// Operators are <c>==</c>, <c>!=</c>, <c>contains</c>, <c>startsWith</c>.
///
/// Kept deliberately small. A general expression language here would be both a
/// security problem and an unbounded support burden — if a rule cannot be expressed,
/// the answer is a custom trigger, not a bigger grammar.
///
/// Precedence: <c>&amp;&amp;</c> binds tighter than <c>||</c>. No parentheses.
/// </summary>
internal static class TriggerConditionEvaluator
{
    private static readonly string[] Operators = ["==", "!=", "contains", "startsWith"];

    /// <summary>
    /// The paths an operand may name. Anything else with a dot in it is refused rather than
    /// read as a literal, because a mistyped path would otherwise compare as text and
    /// silently never match.
    /// </summary>
    private static readonly HashSet<string> KnownPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "task.status", "task.outcome", "task.type", "task.branch", "task.assignee", "task.id",
        "run.status", "run.id", "event", "subject.type", "subject.id"
    };

    private sealed record Comparison(string Left, string Operator, string Right);

    /// <summary>
    /// Throws <see cref="FormatException"/> if <paramref name="expression"/> would not
    /// evaluate. The builder calls this at publish, so a malformed condition is refused
    /// there instead of being skipped with a log line at run time. It is the same parse
    /// <see cref="Evaluate"/> runs, so the two cannot disagree about what is valid.
    /// </summary>
    public static void Validate(string expression) => Parse(expression);

    public static bool Evaluate(string expression, WorkflowTriggerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // OR over AND-groups.
        return Parse(expression).Any(group => group.All(c => Compare(c, context)));
    }

    private static List<List<Comparison>> Parse(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var orGroups = expression.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (orGroups.Length == 0)
        {
            throw new FormatException($"Empty condition: '{expression}'.");
        }

        var parsed = new List<List<Comparison>>();

        foreach (var group in orGroups)
        {
            var terms = group.Split("&&", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (terms.Length == 0)
            {
                throw new FormatException($"Empty condition group in '{expression}'.");
            }

            parsed.Add([.. terms.Select(ParseComparison)]);
        }

        return parsed;
    }

    private static Comparison ParseComparison(string term)
    {
        foreach (var op in Operators)
        {
            var index = term.IndexOf(op, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var left = term[..index].Trim();
            var right = term[(index + op.Length)..].Trim();

            if (left.Length == 0 || right.Length == 0)
            {
                throw new FormatException($"Malformed comparison: '{term}'.");
            }

            CheckOperand(left);
            CheckOperand(right);

            return new Comparison(left, op, right);
        }

        throw new FormatException(
            $"No supported operator in '{term}'. Expected one of: {string.Join(", ", Operators)}.");
    }

    private static void CheckOperand(string operand)
    {
        if (IsQuoted(operand) || IsVariable(operand))
        {
            return;
        }

        if (operand.Contains('.', StringComparison.Ordinal) && !KnownPaths.Contains(operand))
        {
            throw new FormatException($"Unknown condition path '{operand}'.");
        }
    }

    private static bool IsQuoted(string operand) =>
        operand.Length >= 2
        && ((operand[0] == '\'' && operand[^1] == '\'')
         || (operand[0] == '"' && operand[^1] == '"'));

    private static bool IsVariable(string operand) =>
        operand.Length > 4 && operand.StartsWith("var.", StringComparison.OrdinalIgnoreCase);

    private static bool Compare(Comparison comparison, WorkflowTriggerContext context)
    {
        var leftValue = Resolve(comparison.Left, context);
        var rightValue = Resolve(comparison.Right, context);

        return comparison.Operator.ToUpperInvariant() switch
        {
            "==" => string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase),
            "CONTAINS" => leftValue is not null && rightValue is not null
                && leftValue.Contains(rightValue, StringComparison.OrdinalIgnoreCase),
            "STARTSWITH" => leftValue is not null && rightValue is not null
                && leftValue.StartsWith(rightValue, StringComparison.OrdinalIgnoreCase),
            _ => throw new FormatException($"Unsupported operator '{comparison.Operator}'.")
        };
    }

    /// <summary>An operand's value. Parse has already refused anything unresolvable.</summary>
    private static string? Resolve(string operand, WorkflowTriggerContext context)
    {
        if (IsQuoted(operand))
        {
            return operand[1..^1];
        }

        if (IsVariable(operand))
        {
            var name = operand[4..];
            var variables = context.Variables.GetAllAsync().GetAwaiter().GetResult();
            return variables.TryGetValue(name, out var value) ? value : null;
        }

        return operand.ToUpperInvariant() switch
        {
            "TASK.STATUS" => context.Task.Status.ToString(),
            "TASK.OUTCOME" => context.Task.OutcomeKey,
            "TASK.TYPE" => context.Task.TaskTypeKey,
            "TASK.BRANCH" => context.Task.AssignedBranchKey,
            "TASK.ASSIGNEE" => context.Task.AssignedToActorId,
            "TASK.ID" => context.Task.Id.ToString(CultureInfo.InvariantCulture),
            "RUN.STATUS" => context.Run.Status.ToString(),
            "RUN.ID" => context.Run.Id.ToString(CultureInfo.InvariantCulture),
            "EVENT" => context.Event.ToString(),
            "SUBJECT.TYPE" => context.Subject.SubjectType,
            "SUBJECT.ID" => context.Subject.SubjectId,

            // Anything else is a bare literal.
            _ => operand
        };
    }
}
