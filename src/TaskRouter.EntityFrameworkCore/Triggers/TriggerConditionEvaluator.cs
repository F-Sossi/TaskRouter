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
    public static bool Evaluate(string expression, WorkflowTriggerContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(context);

        // OR over AND-groups.
        var orGroups = expression.Split("||", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (orGroups.Length == 0)
        {
            throw new FormatException($"Empty condition: '{expression}'.");
        }

        foreach (var group in orGroups)
        {
            var terms = group.Split("&&", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (terms.Length == 0)
            {
                throw new FormatException($"Empty condition group in '{expression}'.");
            }

            if (terms.All(t => EvaluateComparison(t, context)))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] Operators = ["==", "!=", "contains", "startsWith"];

    private static bool EvaluateComparison(string term, WorkflowTriggerContext context)
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

            var leftValue = Resolve(left, context);
            var rightValue = Resolve(right, context);

            return op.ToUpperInvariant() switch
            {
                "==" => string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase),
                "!=" => !string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase),
                "CONTAINS" => leftValue is not null && rightValue is not null
                    && leftValue.Contains(rightValue, StringComparison.OrdinalIgnoreCase),
                "STARTSWITH" => leftValue is not null && rightValue is not null
                    && leftValue.StartsWith(rightValue, StringComparison.OrdinalIgnoreCase),
                _ => throw new FormatException($"Unsupported operator '{op}'.")
            };
        }

        throw new FormatException(
            $"No supported operator in '{term}'. Expected one of: {string.Join(", ", Operators)}.");
    }

    private static string? Resolve(string operand, WorkflowTriggerContext context)
    {
        if (operand.Length >= 2
            && ((operand[0] == '\'' && operand[^1] == '\'')
             || (operand[0] == '"' && operand[^1] == '"')))
        {
            return operand[1..^1];
        }

        if (operand.StartsWith("var.", StringComparison.OrdinalIgnoreCase))
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

            // Anything else is a bare literal. Unknown *paths* would be a silent
            // mis-evaluation, so reject anything that looks like one.
            _ => operand.Contains('.', StringComparison.Ordinal)
                ? throw new FormatException($"Unknown condition path '{operand}'.")
                : operand
        };
    }
}
