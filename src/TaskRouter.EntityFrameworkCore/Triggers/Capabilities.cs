using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Triggers;

/// <summary>Variable access scoped to one run, handed to triggers and conditions.</summary>
public sealed class WorkflowVariables(IWorkflowDbContext db, int runId, int? taskId = null)
    : IWorkflowVariables
{
    public async Task<string?> GetAsync(string name, CancellationToken ct = default) =>
        await db.WorkflowVariables
            .Where(v => v.WorkflowRunId == runId && v.TaskId == taskId && v.Name == name)
            .Select(v => v.Value)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
        ?? await db.WorkflowVariables
            .Where(v => v.WorkflowRunId == runId && v.TaskId == null && v.Name == name)
            .Select(v => v.Value)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    public async Task SetAsync(
        string name,
        string? value,
        VariableType type = VariableType.String,
        CancellationToken ct = default)
    {
        var existing = await db.WorkflowVariables
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

        foreach (var row in rows.OrderBy(r => r.TaskId.HasValue))
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
