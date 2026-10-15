using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Triggers;

/// <summary>Variable access scoped to one run, handed to triggers and conditions.</summary>
public sealed class WorkflowVariables(IWorkflowDbContext db, int runId, int? taskId = null)
    : IWorkflowVariables
{
    /// <summary>
    /// The same run's variables at run scope: what this instance writes is task-scoped when
    /// it was given a task, and a later step's task-scoped view only sees its own task's
    /// values and the run's.
    /// </summary>
    internal WorkflowVariables ForRun() => taskId is null ? this : new(db, runId);

    /// <summary>
    /// Variables the context is tracking, as they stand now. Inline triggers do not save
    /// between one another, so a value set by an earlier trigger on the same event is only
    /// here, not in the database (Local never includes deleted entries) — and a tracked change to a stored value is newer than the row.
    /// </summary>
    private List<WorkflowVariable> Tracked() =>
        db.WorkflowVariables.Local
            .Where(v => v.WorkflowRunId == runId && (v.TaskId == null || v.TaskId == taskId))
            .ToList();

    public async Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        var tracked = Tracked();

        foreach (var scope in new[] { taskId, null })
        {
            var hit = tracked.FirstOrDefault(v =>
                v.TaskId == scope && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

            if (hit is not null)
            {
                return hit.Value;
            }

            var stored = await db.WorkflowVariables
                .Where(v => v.WorkflowRunId == runId && v.TaskId == scope && v.Name == name)
                .Select(v => new { v.Value })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            // Existence, not a non-null value: a task-scoped variable set to null still shadows
            // the run-scoped one, as it does in GetAllAsync.
            if (stored is not null)
            {
                return stored.Value;
            }
        }

        return null;
    }

    public async Task SetAsync(
        string name,
        string? value,
        VariableType type = VariableType.String,
        CancellationToken ct = default)
    {
        // Tracked first: a variable an earlier trigger on this event added has not been saved,
        // so the database cannot see it, and adding again would violate the unique index.
        var existing = db.WorkflowVariables.Local.FirstOrDefault(
                v => v.WorkflowRunId == runId && v.TaskId == taskId
                    && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? await db.WorkflowVariables
                .SingleOrDefaultAsync(
                    v => v.WorkflowRunId == runId && v.TaskId == taskId && v.Name == name, ct)
                .ConfigureAwait(false);

        if (existing is null)
        {
            db.WorkflowVariables.Add(new WorkflowVariable
            {
                WorkflowRunId = runId,
                TaskId = taskId,
                Name = name,
                Value = value,
                Type = type
            });

            return;
        }

        existing.Value = value;
        existing.Type = type;
        existing.Modified = DateTime.UtcNow;
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await db.WorkflowVariables
            .Where(v => v.WorkflowRunId == runId && (v.TaskId == null || v.TaskId == taskId))
            .Select(v => new { v.Name, v.Value, v.TaskId })
            .ToListAsync(ct).ConfigureAwait(false);

        // Task-scoped values shadow run-scoped ones of the same name.
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var all = rows.Select(r => (r.Name, r.Value, r.TaskId))
            .Concat(Tracked().Select(v => (v.Name, v.Value, v.TaskId)));

        // Tracked values come last within a scope, so they win over the stored row they replace.
        foreach (var row in all.OrderBy(r => r.TaskId.HasValue))
        {
            result[row.Name] = row.Value;
        }

        return result;
    }
}

/// <summary>
/// The engine operations a trigger may invoke. Deliberately small and enumerated —
/// a trigger never receives the DbContext.
/// </summary>
public sealed class WorkflowActions(
    IWorkflowEngine engine,
    IWorkflowDbContext db,
    int runId,
    string actorId) : IWorkflowActions
{
    public async Task AssignTaskAsync(
        int taskId, WorkflowAssignment assignment, CancellationToken ct = default)
    {
        var result = await engine.ReassignTaskAsync(
            taskId, assignment, actorId, "Assigned by trigger.", ct).ConfigureAwait(false);

        if (result.IsError)
        {
            throw result.UnwrapError();
        }
    }

    public async Task CancelTaskAsync(int taskId, string? note, CancellationToken ct = default)
    {
        var result = await engine.CancelTaskAsync(taskId, actorId, note, ct).ConfigureAwait(false);

        if (result.IsError)
        {
            throw result.UnwrapError();
        }
    }

    /// <summary>
    /// Records a host-defined domain event as a run variable. Triggers subscribed to
    /// WorkflowEventKind.Custom with a matching name can react to it.
    /// </summary>
    public async Task RaiseEventAsync(
        string customEventName, string? payloadJson, CancellationToken ct = default)
    {
        var variables = new WorkflowVariables(db, runId);
        await variables
            .SetAsync($"event:{customEventName}", payloadJson, VariableType.Json, ct)
            .ConfigureAwait(false);
    }
}
