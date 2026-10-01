using System.Globalization;
using System.Text;

namespace TaskRouter.Core.Builder;

/// <summary>
/// Renders a workflow as Mermaid flowchart source.
///
/// Kept here rather than in the Blazor components for three reasons: it is pure string
/// building with no UI in it, it can be unit tested without a browser, and a host that
/// wants a diagram somewhere other than the builder — a document page, an email, a
/// generated document — can call it directly.
/// </summary>
public static class WorkflowMermaid
{
    /// <summary>
    /// Builds a top-down flowchart. Shape carries the task's role and edge style
    /// carries the route's, so the diagram says what the chips in the task list say:
    ///
    ///   stadium   entry task          double square   terminal task
    ///   hexagon   forkable            trapezoid       convergence point
    ///   dashed    ad-hoc              dotted edge     rework route
    /// </summary>
    public static string ToFlowchart(WorkflowEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var tasks = WorkflowLayout.Order(model);

        if (tasks.Count == 0)
        {
            // Mermaid rejects an empty graph, and a diagram saying so beats an error.
            return "flowchart TD\n    empty[\"No tasks yet\"]\n";
        }

        // Node ids are positional rather than derived from the label: labels are free
        // text and would need escaping into a valid identifier anyway.
        var ids = tasks
            .Select((task, index) => (task.LocalId, Id: "t" + index.ToString(CultureInfo.InvariantCulture)))
            .ToDictionary(x => x.LocalId, x => x.Id);

        var sb = new StringBuilder();
        sb.AppendLine("flowchart TD");

        foreach (var task in tasks)
        {
            var id = ids[task.LocalId];
            var label = Escape(task.Label);

            var isEntry = model.EntryTaskLocalId == task.LocalId;

            var node = true switch
            {
                _ when isEntry => $"{id}([\"{label}\"])",
                _ when task.IsTerminal => $"{id}[[\"{label}\"]]",
                _ when task.IsConvergencePoint => $"{id}[/\"{label}\"/]",
                _ when task.IsForkable => $"{id}{{{{\"{label}\"}}}}",
                _ => $"{id}[\"{label}\"]"
            };

            sb.Append("    ").AppendLine(node);

            // Colour as well as shape, and the same meaning in both. Shape alone asks a
            // reader to know that a hexagon forks and a parallelogram converges; colour
            // tells them which nodes are alike before they have learned the vocabulary.
            //
            // Ad-hoc wins where a task is both, because "this step is optional" changes how
            // to read the whole path through it.
            var kind = true switch
            {
                _ when task.IsAdHoc => "adhoc",
                _ when isEntry => "entry",
                _ when task.IsTerminal => "terminal",
                _ when task.IsConvergencePoint => "convergence",
                _ when task.IsForkable => "forkable",
                _ => "step"
            };

            sb.Append("    ").Append(id).Append(":::").AppendLine(kind);
        }

        foreach (var task in tasks)
        {
            var from = ids[task.LocalId];

            foreach (var route in task.Routes.OrderBy(r => r.Order))
            {
                if (route.NextTaskLocalId is not { } next || !ids.TryGetValue(next, out var to))
                {
                    continue;   // a dangling route; the validator reports it separately
                }

                var label = route.OutcomeKey;

                if (!string.IsNullOrWhiteSpace(route.ConditionKey))
                {
                    label += $" [{route.ConditionKey}]";
                }

                // Dotted for rework, so a cycle reads as "goes back" rather than as
                // just another edge.
                var arrow = route.IsReworkRoute ? "-. " : "-- ";
                var tail = route.IsReworkRoute ? " .-> " : " --> ";

                sb.Append("    ").Append(from).Append(arrow)
                  .Append('"').Append(Escape(label)).Append('"')
                  .Append(tail).AppendLine(to);
            }
        }

        // Explicit fills rather than leaning on Mermaid's own theme, because the diagram
        // has to stay legible on a light page and a dark one, and the source is generated
        // here where the theme is not known. Mid-tone fills with light labels read on both.
        sb.AppendLine("    classDef step fill:#37474f,stroke:#78909c,color:#eceff1;");
        sb.AppendLine("    classDef entry fill:#2e7d32,stroke:#66bb6a,color:#ffffff;");
        sb.AppendLine("    classDef terminal fill:#4527a0,stroke:#9575cd,color:#ffffff;");
        sb.AppendLine("    classDef convergence fill:#ef6c00,stroke:#ffb74d,color:#ffffff;");
        sb.AppendLine("    classDef forkable fill:#1565c0,stroke:#64b5f6,color:#ffffff;");
        sb.AppendLine("    classDef adhoc fill:#37474f,stroke:#b0bec5,color:#eceff1,stroke-dasharray: 5 5;");

        return sb.ToString();
    }

    /// <summary>
    /// Mermaid parses inside quoted labels, so the characters that would end the label
    /// or start markup have to become entities.
    /// </summary>
    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value)
            ? "(unnamed)"
            : value.Replace("&", "#amp;", StringComparison.Ordinal)
                   .Replace("\"", "#quot;", StringComparison.Ordinal)
                   .Replace("<", "#lt;", StringComparison.Ordinal)
                   .Replace(">", "#gt;", StringComparison.Ordinal)
                   .Replace("\r", " ", StringComparison.Ordinal)
                   .Replace("\n", " ", StringComparison.Ordinal);
}
