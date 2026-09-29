using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaskRouter.EntityFrameworkCore.Triggers;

/// <summary>Options for the deadline sweep — reminders and escalations share it.</summary>
public sealed class WorkflowDeadlineOptions
{
    /// <summary>
    /// How often to sweep. Five minutes rather than the outbox's ten seconds because
    /// there is nothing to signal here: a reminder becomes due by the clock advancing,
    /// and nothing in-process knows when that happened. Lead times are measured in days,
    /// so five minutes of latency is invisible.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Candidate tasks examined per sweep. A pass runs two sweeps, so it examines up to
    /// twice this many rows and makes up to twice this many due-date resolutions.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// How long to wait after a failed pass before trying again. Longer than the poll
    /// interval, as the outbox's is: a pass fails when the database is unreachable, and
    /// hammering it helps nobody.
    /// </summary>
    public TimeSpan ErrorBackoff { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Drives <see cref="IWorkflowDeadlineProcessor"/> on a timer.
///
/// Deliberately thin, and deliberately unlike <see cref="WorkflowOutboxHostedService"/>
/// in one respect: there is no signal to wake it early. An outbox row appears because
/// something in this process just wrote it; a reminder becomes due because time passed,
/// which nothing can signal.
/// </summary>
public sealed class WorkflowDeadlineHostedService(
    IServiceScopeFactory scopeFactory,
    WorkflowDeadlineOptions options,
    ILogger<WorkflowDeadlineHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Workflow deadline sweep started; polling every {Interval}.", options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.PollInterval;

            try
            {
                // Keep going while a full batch keeps coming back, so a backlog is not
                // metered out one batch per poll. Same shape as the outbox drain.
                //
                // The condition is on events *fired*, not candidates examined, so a pass
                // that inspects a full batch and fires none stops here rather than
                // spinning. What it does do is over-continue: the count is the sum of
                // both sweeps against a threshold meant for one, so 50 reminders and 50
                // escalations reach the batch size and loop again even though neither
                // half was exhausted. Harmless — the extra pass finds nothing left to
                // claim, fires nothing, and exits — and cheaper than the second counter
                // it would take to tell the two halves apart.
                int fired;

                do
                {
                    using var scope = scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider
                        .GetRequiredService<IWorkflowDeadlineProcessor>();

                    fired = await processor
                        .ProcessDueAsync(options.BatchSize, stoppingToken)
                        .ConfigureAwait(false);
                }
                while (fired >= options.BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed pass must not kill the service: nothing was claimed, so the
                // next pass sees the same candidates.
                logger.LogError(ex, "Deadline sweep failed; retrying in {Backoff}.", options.ErrorBackoff);
                delay = options.ErrorBackoff;
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Workflow deadline sweep stopped.");
    }
}
