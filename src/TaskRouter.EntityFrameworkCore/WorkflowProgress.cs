using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Completion percentage for a run, or for one branch of a fork.
///
/// The original engine's review finding M1: its calculation divided a *branch's* completed tasks by the
/// *whole workflow's* defined task count. A section that had finished everything it
/// would ever be asked to do still reported a fraction of 100%. The fix is simply that
/// numerator and denominator must describe the same scope.
/// </summary>
public static class WorkflowProgress
{
    /// <summary>
    /// Progress for the given scope. Pass a branch key for per-branch progress, or
    /// null for the whole run.
    /// </summary>
    public static double Calculate(WorkflowRunSnapshot run, string? branchKey)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Fork origins are superseded rather than done, and cancelled work is not
        // outstanding either — neither should count in the denominator.
        var scoped = run.Tasks
            .Where(t => !t.IsForkOrigin
                     && t.Status != WorkflowTaskStatus.Cancelled
                     && t.Status != WorkflowTaskStatus.Forked)
            .Where(t => branchKey is null || t.AssignedBranchKey == branchKey)
            .ToList();

        if (scoped.Count == 0)
        {
            return 0d;
        }

        // Where a task definition has been re-run (selective rejection re-creates a
        // branch's task), only the latest instance counts, so a superseded completion
        // does not keep inflating the total.
        var latestPerDefinition = scoped
            .GroupBy(t => t.TaskDefinitionId)
            .Select(g => g.OrderByDescending(t => t.Id).First())
            .ToList();

        var completed = latestPerDefinition.Count(t => t.Status == WorkflowTaskStatus.Completed);

        return Math.Round(
            Math.Clamp((double)completed / latestPerDefinition.Count * 100d, 0d, 100d),
            0,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>Distinct branch keys taking part in a run.</summary>
    public static IReadOnlyList<string> BranchKeys(WorkflowRunSnapshot run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.Tasks
            .Where(t => !t.IsForkOrigin && t.AssignedBranchKey is not null)
            .Select(t => t.AssignedBranchKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
