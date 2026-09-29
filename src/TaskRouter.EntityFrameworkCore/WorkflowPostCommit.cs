namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Work to run once the engine transaction has committed, and only if it did.
///
/// Anything that reaches outside the database has to wait for the commit. Signalling a
/// background worker that an outbox row exists is the case this was built for: signal
/// during the transaction and the worker wakes, finds nothing — the row is not committed
/// yet — and goes back to sleep, so a "prompt" trigger waits for the next poll instead.
/// Signal after the commit and it finds the row.
///
/// Scoped, so the actions belong to one unit of work and are discarded with it.
/// </summary>
public interface IWorkflowPostCommitActions
{
    void Add(Func<CancellationToken, Task> action);

    /// <summary>Runs and clears the queued actions.</summary>
    Task RunAsync(CancellationToken ct = default);
}

public sealed class WorkflowPostCommitActions : IWorkflowPostCommitActions
{
    private readonly List<Func<CancellationToken, Task>> _actions = [];

    public void Add(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions.Add(action);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_actions.Count == 0)
        {
            return;
        }

        // Taken and cleared first: the work has committed, so a retry of the outer
        // operation must not run these a second time.
        var pending = _actions.ToList();
        _actions.Clear();

        foreach (var action in pending)
        {
            await action(ct).ConfigureAwait(false);
        }
    }
}
