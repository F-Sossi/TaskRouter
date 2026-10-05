using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Runs <see cref="WorkflowWiring.Inspect(IServiceProvider)"/> once as the application starts.
///
/// <para>A hosted service rather than something inside <c>AddTaskRouter()</c>, because the
/// check has to see the <em>finished</em> container: a host registers its seams after
/// calling <c>AddTaskRouter()</c>, so inspecting during registration would report problems
/// that are about to be fixed two lines later.</para>
///
/// <para>Registered by <c>ValidateWiringAtStartup()</c> — never automatically. A diagnostic
/// that a host did not ask for and cannot see the reason for is noise, and the engine must
/// stay usable in tests and tools that wire up a deliberate subset.</para>
/// </summary>
internal sealed class WorkflowWiringCheck(
    IServiceProvider provider,
    ILogger<WorkflowWiringCheck> logger,
    bool throwOnProblems) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var report = WorkflowWiring.Inspect(provider);

        foreach (var warning in report.Warnings)
        {
            logger.LogWarning("TaskRouter wiring: {Warning}", warning);
        }

        if (report.IsHealthy)
        {
            // Said out loud, once. "Did the check actually run?" is otherwise unanswerable
            // from the log, and an absent message reads the same as a passing one.
            logger.LogInformation(
                "TaskRouter wiring checked: no problems{Warnings}.",
                report.Warnings.Count > 0 ? $", {report.Warnings.Count} warning(s)" : string.Empty);

            return Task.CompletedTask;
        }

        foreach (var problem in report.Problems)
        {
            logger.LogError("TaskRouter wiring: {Problem}", problem);
        }

        if (throwOnProblems)
        {
            // The whole report, not the first line of it. Somebody reading a startup crash
            // should see every fix at once rather than restarting five times.
            throw new InvalidOperationException(report.ToString());
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
