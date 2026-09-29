namespace TaskRouter.Core.Builder;

/// <summary>
/// Orders a workflow's tasks so a reader meets them roughly in the order a run would.
///
/// Insertion order is what the database hands back, and it says nothing useful: a task
/// added last may be the second thing that happens. Sorting by the graph instead makes
/// both the task list and the diagram legible.
///
/// The rule is that a task appears after everything that can lead to it — a longest-path
/// layering, not a breadth-first one. The difference shows up on any workflow with an
/// early exit: in the demo ChangeRequest chain, Enter Record can reject straight to Close Document,
/// so breadth-first puts Close Document second, ahead of the PM Review that normally
/// precedes it. Longest-path pushes it to the end where a reader expects it.
///
/// Two things stop this from being a plain topological sort:
///
///  - Workflows contain cycles by design. A rework route sends a rejected convergence
///    task back to an earlier task, and a topological sort is undefined on a cyclic
///    graph. Rework routes are therefore excluded when computing depth; they are drawn,
///    but they do not order anything.
///  - A graph can still be cyclic after that, if someone builds a loop out of ordinary
///    routes. Anything the layering cannot place is appended in discovery order rather
///    than dropped, so a bad graph stays visible and fixable.
/// </summary>
public static class WorkflowLayout
{
    /// <summary>
    /// Returns the tasks in display order: the mainline from the entry task first, then
    /// each ad-hoc chain, then anything unreachable.
    /// </summary>
    public static IReadOnlyList<TaskEditModel> Order(WorkflowEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var ordered = new List<TaskEditModel>(model.Tasks.Count);
        var placed = new HashSet<Guid>();

        // The mainline: everything reachable from the entry task.
        if (model.EntryTaskLocalId is { } entryId && model.Find(entryId) is { } entry)
        {
            ordered.AddRange(LayerSegment(model, Reachable(model, entry, placed)));
        }

        // Ad-hoc chains. Reachable only by someone adding them to a live run, so they
        // have no inbound route and would otherwise fall into the leftovers.
        foreach (var root in model.Tasks.Where(t => t.IsAdHoc && !placed.Contains(t.LocalId) && !HasInbound(model, t)))
        {
            ordered.AddRange(LayerSegment(model, Reachable(model, root, placed)));
        }

        ordered.AddRange(model.Tasks.Where(t => !placed.Contains(t.LocalId)));

        return ordered;
    }

    /// <summary>Reorders the model's own list in place. Route targets are held by
    /// local id, so reordering the list cannot change the workflow's meaning.</summary>
    public static void ApplyOrder(WorkflowEditModel model)
    {
        var ordered = Order(model);

        model.Tasks.Clear();
        model.Tasks.AddRange(ordered);
    }

    /// <summary>
    /// Everything reachable from a root, in discovery order, skipping tasks another
    /// segment already claimed. Discovery order is the tie-break within a layer, which
    /// is what makes the result stable and route order meaningful.
    /// </summary>
    private static List<TaskEditModel> Reachable(
        WorkflowEditModel model, TaskEditModel root, HashSet<Guid> placed)
    {
        var segment = new List<TaskEditModel>();
        var queue = new Queue<TaskEditModel>();

        if (!placed.Add(root.LocalId))
        {
            return segment;
        }

        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var task = queue.Dequeue();
            segment.Add(task);

            foreach (var next in Successors(model, task))
            {
                if (placed.Add(next.LocalId))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return segment;
    }

    /// <summary>
    /// Kahn's algorithm, carrying a longest-path depth, then sorted by depth with
    /// discovery order as the tie-break.
    /// </summary>
    private static List<TaskEditModel> LayerSegment(WorkflowEditModel model, List<TaskEditModel> segment)
    {
        if (segment.Count == 0)
        {
            return segment;
        }

        var discovery = segment
            .Select((task, index) => (task.LocalId, index))
            .ToDictionary(x => x.LocalId, x => x.index);

        var inSegment = discovery.Keys.ToHashSet();

        var indegree = segment.ToDictionary(t => t.LocalId, _ => 0);
        var depth = segment.ToDictionary(t => t.LocalId, _ => 0);

        foreach (var task in segment)
        {
            foreach (var next in Successors(model, task).Where(n => inSegment.Contains(n.LocalId)))
            {
                indegree[next.LocalId]++;
            }
        }

        var ready = new List<TaskEditModel>(segment.Where(t => indegree[t.LocalId] == 0));
        var settled = new List<TaskEditModel>(segment.Count);

        while (ready.Count > 0)
        {
            // Lowest discovery index first, so siblings keep the order their routes
            // were declared in.
            ready.Sort((a, b) => discovery[a.LocalId].CompareTo(discovery[b.LocalId]));

            var task = ready[0];
            ready.RemoveAt(0);
            settled.Add(task);

            foreach (var next in Successors(model, task).Where(n => inSegment.Contains(n.LocalId)))
            {
                depth[next.LocalId] = Math.Max(depth[next.LocalId], depth[task.LocalId] + 1);

                if (--indegree[next.LocalId] == 0)
                {
                    ready.Add(next);
                }
            }
        }

        var result = settled
            .OrderBy(t => depth[t.LocalId])
            .ThenBy(t => discovery[t.LocalId])
            .ToList();

        // A residual cycle leaves tasks unsettled. Keep them, at the end.
        var settledIds = settled.Select(t => t.LocalId).ToHashSet();
        result.AddRange(segment.Where(t => !settledIds.Contains(t.LocalId)));

        return result;
    }

    /// <summary>
    /// Route targets, in declared order. Rework routes are excluded: they point
    /// backwards, and following them for ordering would drag an early task down to sit
    /// after the task that rejects it.
    /// </summary>
    private static IEnumerable<TaskEditModel> Successors(WorkflowEditModel model, TaskEditModel task) =>
        task.Routes
            .Where(r => !r.IsReworkRoute)
            .OrderBy(r => r.Order)
            .Select(r => r.NextTaskLocalId is { } next ? model.Find(next) : null)
            .Where(t => t is not null)!;

    private static bool HasInbound(WorkflowEditModel model, TaskEditModel task) =>
        model.Tasks.Any(t => t.LocalId != task.LocalId
                          && t.Routes.Any(r => r.NextTaskLocalId == task.LocalId));
}
