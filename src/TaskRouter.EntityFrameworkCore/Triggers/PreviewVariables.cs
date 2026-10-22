using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore.Triggers;

internal sealed class PreviewVariables(
    Dictionary<string, string?> run,
    Dictionary<string, string?>? task = null) : IWorkflowVariables
{
    public PreviewVariables ForRun() => new(run);

    public Task<string?> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(task is not null && task.TryGetValue(name, out var local)
            ? local : run.GetValueOrDefault(name));

    public Task SetAsync(string name, string? value, VariableType type = VariableType.String, CancellationToken ct = default)
    {
        (task ?? run)[name] = value;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken ct = default)
    {
        var values = new Dictionary<string, string?>(run, StringComparer.OrdinalIgnoreCase);
        foreach (var value in task ?? []) values[value.Key] = value.Value;
        return Task.FromResult<IReadOnlyDictionary<string, string?>>(values);
    }
}
