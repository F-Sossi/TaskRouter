using TaskRouter.Core.Model;

namespace TaskRouter.Core.Validation;

public sealed record ValidationError(string Code, string Message, int? TaskDefinitionId = null);

/// <summary>
/// Validates a workflow version's task graph before publish.
///
/// Differences from the original validator this is derived from:
///  - Entry and terminal are configuration (EntryTaskDefinitionId, IsTerminal), not
///    hardcoded EnterRecord/CloseDocument enum members.
///  - Archived task definitions are excluded consistently. The original engine filtered archived
///    tasks in only one of its checks, so an archived task could raise DEAD_END /
///    UNREACHABLE and make an otherwise valid workflow unsaveable.
///  - Task types may repeat, because routes target definition ids. Duplicate *types*
///    are legal; duplicate *edges* are not.
///  - Cycles are detected, so the validator cannot accept a graph the runtime rejects.
/// </summary>
public static class WorkflowDefinitionValidator
{
    public static List<ValidationError> Validate(WorkflowDefinitionVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var errors = new List<ValidationError>();
        var tasks = version.Tasks.Where(t => !t.IsArchived).ToList();

        if (tasks.Count == 0)
        {
            errors.Add(new("WF_EMPTY", "Workflow must contain at least one task."));
            return errors;
        }

        var byId = tasks.ToDictionary(t => t.Id);

        ValidateEntry(version, byId, errors);
        ValidateTerminals(tasks, errors);

        var edges = BuildEdges(tasks, byId, errors);

        ValidateOutcomeCoverage(tasks, errors);
        ValidateForkRules(tasks, errors);

        if (version.EntryTaskDefinitionId is { } entryId && byId.ContainsKey(entryId))
        {
            ValidateReachability(tasks, entryId, edges, errors);
            ValidateTerminalReachable(tasks, edges, errors);
        }

        // Attachments. Automatic + version-wide is the one combination the engine cannot
        // interpret sensibly: it would spawn an instance on every task in the run.
        foreach (var attachment in version.SubWorkflowAttachments.Where(a => !a.IsArchived))
        {
            if (attachment.IsAutomatic && attachment.TaskDefinitionId is null)
            {
                errors.Add(new("attachment.automatic-needs-task",
                    "An automatic sub-workflow must be attached to a specific task. " +
                    "Attached to any task, it would start on every task in the run."));
            }

            if (attachment.TaskDefinitionId is { } taskId
                && version.Tasks.All(t => t.Id != taskId))
            {
                errors.Add(new("attachment.unknown-task",
                    $"Sub-workflow attachment names task {taskId}, which is not in this version.",
                    taskId));
            }

            // Only checkable when the caller loaded the navigation. The validator is a
            // pure function over an entity graph and cannot go to the database, so a
            // caller that did not Include it gets no opinion rather than a false alarm.
            if (attachment.SubWorkflowDefinition is { IsSubWorkflow: false } target)
            {
                errors.Add(new("attachment.not-a-sub-workflow",
                    $"'{target.Name}' is not flagged as a sub-workflow, so it cannot be attached."));
            }
        }

        var duplicates = version.SubWorkflowAttachments
            .Where(a => !a.IsArchived)
            .GroupBy(a => (a.TaskDefinitionId, a.SubWorkflowDefinitionId))
            .Where(g => g.Count() > 1);

        foreach (var duplicate in duplicates)
        {
            errors.Add(new("attachment.duplicate",
                $"Sub-workflow definition {duplicate.Key.SubWorkflowDefinitionId} is attached twice " +
                "at the same scope. Use AllowMultiple for concurrent instances instead."));
        }

        return errors;
    }

    private static void ValidateEntry(
        WorkflowDefinitionVersion version,
        Dictionary<int, WorkflowTaskDefinition> byId,
        List<ValidationError> errors)
    {
        if (version.EntryTaskDefinitionId is null)
        {
            errors.Add(new("ENTRY_MISSING", "Workflow must declare an entry task."));
        }
        else if (!byId.ContainsKey(version.EntryTaskDefinitionId.Value))
        {
            errors.Add(new("ENTRY_UNKNOWN",
                $"Entry task definition {version.EntryTaskDefinitionId} is not part of this workflow."));
        }
        else if (byId[version.EntryTaskDefinitionId.Value].IsAdHoc)
        {
            errors.Add(new("ENTRY_ADHOC", "Entry task cannot be ad-hoc.",
                version.EntryTaskDefinitionId));
        }
    }

    private static void ValidateTerminals(List<WorkflowTaskDefinition> tasks, List<ValidationError> errors)
    {
        if (!tasks.Any(t => t.IsTerminal))
        {
            errors.Add(new("TERMINAL_MISSING", "Workflow must declare at least one terminal task."));
        }

        foreach (var t in tasks.Where(t => t.IsTerminal && t.OutgoingRoutes.Any(r => !r.IsArchived)))
        {
            errors.Add(new("TERMINAL_HAS_EDGES",
                $"Terminal task '{Label(t)}' must not have outgoing routes.", t.Id));
        }
    }

    private static Dictionary<int, List<int>> BuildEdges(
        List<WorkflowTaskDefinition> tasks,
        Dictionary<int, WorkflowTaskDefinition> byId,
        List<ValidationError> errors)
    {
        var edges = new Dictionary<int, List<int>>();

        foreach (var t in tasks)
        {
            var outs = new List<int>();
            edges[t.Id] = outs;

            var routes = t.OutgoingRoutes.Where(r => !r.IsArchived).ToList();

            if (t.IsAdHoc && routes.Count > 0 && !t.IsTerminal)
            {
                // Ad-hoc tasks MAY route now — that is what makes sub-workflows possible.
                // They simply do not participate in mainline reachability.
            }

            // "Default" means the fallback when no conditional route matches, so it is
            // scoped to an outcome: one default per outcome, not one per task.
            var noOutcome = routes.Where(r => r.Outcome is null).ToList();

            if (noOutcome.Count > 0)
            {
                // Only reachable from an unsaved graph -- the foreign key refuses it in the
                // database. It means a route naming an outcome its task does not declare,
                // which used to be writable and simply never fired.
                errors.Add(new("ROUTE_NO_OUTCOME",
                    $"Task '{Label(t)}' has {noOutcome.Count} route(s) whose outcome it does "
                    + "not declare.", t.Id));
            }

            var multiDefault = routes
                .Where(r => r.Outcome is not null && r.IsDefault)
                .GroupBy(r => r.Outcome!.OutcomeKey)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (multiDefault.Count > 0)
            {
                errors.Add(new("ROUTE_MULTI_DEFAULT",
                    $"Task '{Label(t)}' has more than one default route for outcome(s): " +
                    $"{string.Join(", ", multiDefault)}.", t.Id));
            }

            var duplicates = routes
                .Where(r => r.Outcome is not null)
                .GroupBy(r => (r.Outcome!.OutcomeKey, r.ConditionKey, r.NextTaskDefinitionId))
                .Where(g => g.Count() > 1);

            if (duplicates.Any())
            {
                errors.Add(new("ROUTE_DUPLICATE",
                    $"Task '{Label(t)}' has duplicate routes for the same outcome, condition and target.", t.Id));
            }

            foreach (var r in routes)
            {
                if (!byId.ContainsKey(r.NextTaskDefinitionId))
                {
                    errors.Add(new("ROUTE_TO_UNKNOWN",
                        $"Task '{Label(t)}' routes to a task definition that is not in this workflow.", t.Id));
                    continue;
                }

                if (r.NextTaskDefinitionId == t.Id)
                {
                    errors.Add(new("SELF_LOOP", $"Task '{Label(t)}' routes to itself.", t.Id));
                    continue;
                }

                outs.Add(r.NextTaskDefinitionId);
            }

            if (!t.IsTerminal && !t.IsAdHoc && outs.Count == 0)
            {
                errors.Add(new("DEAD_END",
                    $"Task '{Label(t)}' has no outgoing routes and is not terminal.", t.Id));
            }
        }

        return edges;
    }

    private static void ValidateOutcomeCoverage(List<WorkflowTaskDefinition> tasks, List<ValidationError> errors)
    {
        foreach (var t in tasks.Where(t => !t.IsTerminal))
        {
            var outcomes = t.ValidOutcomes
                .Where(o => !o.IsArchived)
                .Select(o => o.OutcomeKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (outcomes.Count == 0)
            {
                continue;
            }

            var routes = t.OutgoingRoutes.Where(r => !r.IsArchived).ToList();

            // An ad-hoc task with no routes at all is a legitimate leaf — that is how a
            // single ad-hoc task (or the last step of an ad-hoc chain) behaves. One with
            // *some* routes is partially wired, which is far more likely a mistake.
            if (t.IsAdHoc && routes.Count == 0)
            {
                continue;
            }

            var routed = routes
                .Where(r => r.Outcome is not null)
                .Select(r => r.Outcome!.OutcomeKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = outcomes.Except(routed).ToList();

            if (missing.Count > 0)
            {
                errors.Add(new("OUTCOME_UNROUTED",
                    $"Task '{Label(t)}' has no route for outcome(s): {string.Join(", ", missing)}.", t.Id));
            }
        }
    }

    private static void ValidateForkRules(List<WorkflowTaskDefinition> tasks, List<ValidationError> errors)
    {
        foreach (var t in tasks.Where(t => t.IsForkable && t.IsConvergencePoint))
        {
            errors.Add(new("FORK_CONVERGENCE_DUAL",
                $"Task '{Label(t)}' cannot be both forkable and a convergence point.", t.Id));
        }

        if (tasks.Any(t => t.IsForkable) && !tasks.Any(t => t.IsConvergencePoint))
        {
            errors.Add(new("FORK_NO_CONVERGENCE",
                "Workflow has forkable task(s) but no convergence point."));
        }
    }

    private static void ValidateReachability(
        List<WorkflowTaskDefinition> tasks,
        int entryId,
        Dictionary<int, List<int>> edges,
        List<ValidationError> errors)
    {
        var reachable = Bfs(entryId, edges);

        var unreachable = tasks
            .Where(t => !t.IsAdHoc && t.Id != entryId && !reachable.Contains(t.Id))
            .ToList();

        foreach (var t in unreachable)
        {
            errors.Add(new("UNREACHABLE", $"Task '{Label(t)}' is unreachable from the entry task.", t.Id));
        }
    }

    private static void ValidateTerminalReachable(
        List<WorkflowTaskDefinition> tasks,
        Dictionary<int, List<int>> edges,
        List<ValidationError> errors)
    {
        var reverse = new Dictionary<int, List<int>>();
        foreach (var (from, tos) in edges)
        {
            foreach (var to in tos)
            {
                if (!reverse.TryGetValue(to, out var list))
                {
                    list = [];
                    reverse[to] = list;
                }

                list.Add(from);
            }
        }

        var canReach = new HashSet<int>();
        foreach (var terminal in tasks.Where(t => t.IsTerminal))
        {
            foreach (var id in Bfs(terminal.Id, reverse))
            {
                canReach.Add(id);
            }
        }

        foreach (var t in tasks.Where(t => !t.IsAdHoc && !t.IsTerminal && !canReach.Contains(t.Id)))
        {
            errors.Add(new("NO_PATH_TO_TERMINAL",
                $"Task '{Label(t)}' has no path to a terminal task.", t.Id));
        }
    }

    private static HashSet<int> Bfs(int start, Dictionary<int, List<int>> edges)
    {
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!edges.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var n in next)
            {
                if (seen.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }

        return seen;
    }

    private static string Label(WorkflowTaskDefinition t) =>
        t.DisplayName ?? t.TaskType?.DisplayName ?? $"#{t.Id}";
}
