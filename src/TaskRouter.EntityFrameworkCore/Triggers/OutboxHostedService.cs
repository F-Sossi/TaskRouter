using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using TaskRouter.Core.Abstractions;

namespace TaskRouter.EntityFrameworkCore.Triggers;

/// <summary>Options for the background drain.</summary>
public sealed class WorkflowOutboxOptions
{
    /// <summary>How often to look for due messages when nothing has signalled.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Messages claimed per pass.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// How long to wait after a failed pass before trying again. Deliberately longer
    /// than the poll interval: a pass fails when the database is unreachable, and
    /// hammering it every ten seconds helps nobody.
    /// </summary>
    public TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Drains the outbox on a timer, and immediately whenever a trigger is dispatched in
/// <see cref="TaskRouter.Core.Model.TriggerDispatchMode.Background"/> mode.
///
/// Without this nothing ever runs an after-commit trigger: the engine writes the outbox
/// row inside its transaction and returns, and the row sits there until something asks
/// for it. The timer is the floor; the signal is what makes Background prompt rather
/// than "within ten seconds".
/// </summary>
public sealed class WorkflowOutboxHostedService(
    IServiceScopeFactory scopeFactory,
    WorkflowOutboxOptions options,
    ILogger<WorkflowOutboxHostedService> logger,
    IWorkflowOutboxSignal? signal = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Workflow outbox drain started; polling every {Interval}.", options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.PollInterval;

            try
            {
                // Keep going while work keeps appearing, so a burst is not metered out
                // one batch per poll.
                int processed;

                do
                {
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<IWorkflowOutboxProcessor>();

                    processed = await processor
                        .ProcessPendingAsync(options.BatchSize, stoppingToken)
                        .ConfigureAwait(false);
                }
                while (processed >= options.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed pass must not kill the service: the messages are still in the
                // outbox and the next pass will lease them again.
                logger.LogError(ex, "Outbox drain failed; retrying in {Backoff}.", options.ErrorBackoff);
                delay = options.ErrorBackoff;
            }

            try
            {
                if (signal is null)
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    // Wakes early when a Background trigger is enqueued.
                    await signal.WaitAsync(delay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Workflow outbox drain stopped.");
    }
}

/// <summary>
/// Lets a dispatch wake the drain instead of waiting for the next poll.
/// </summary>
public interface IWorkflowOutboxSignal
{
    /// <summary>Marks that there is work now.</summary>
    void Signal();

    /// <summary>Waits for a signal, or for the timeout, whichever comes first.</summary>
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// In-process signal. A single-slot channel rather than a counter: several signals
/// before the drain wakes should produce one pass, not several, because the pass takes
/// everything that is due anyway.
/// </summary>
public sealed class WorkflowOutboxSignal : IWorkflowOutboxSignal
{
    private readonly Channel<byte> _channel =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public void Signal() => _channel.Writer.TryWrite(0);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        try
        {
            await _channel.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The timeout, not a shutdown: a normal poll.
        }
    }
}

/// <summary>
/// The default <see cref="IWorkflowJobQueue"/>: writes the outbox row as usual and wakes
/// the drain, rather than running the trigger somewhere the engine cannot see.
///
/// This is what makes <c>Background</c> differ from <c>AfterCommit</c> — both are
/// durable and retried, but a background trigger is picked up as soon as the transaction
/// commits instead of at the next poll. A host wanting real out-of-process execution
/// (Hangfire, a queue) implements <see cref="IWorkflowJobQueue"/> itself.
/// </summary>
public sealed class SignallingJobQueue(IWorkflowOutboxSignal signal) : IWorkflowJobQueue
{
    public Task EnqueueAsync(WorkflowJob job, CancellationToken ct = default)
    {
        signal.Signal();
        return Task.CompletedTask;
    }
}
