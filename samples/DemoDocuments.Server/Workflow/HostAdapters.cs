using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace DemoDocuments.Server.Workflow;

/// <summary>Role keys this host understands. Opaque strings to the engine.</summary>
public static class DemoRoles
{
    public const string SectionLead = "section-lead";
    public const string DivisionHead = "division-manager";
    public const string Originator = "originator";
}

/// <summary>
/// Resolves who a task goes to. This is the direct replacement for the the original system
/// DetermineTaskAssignmentAsync / AssignToSectionLeadAsync / AssignToDivisionHeadAsync block
/// in DocumentTaskService.Complete.cs — same logic, moved out of the engine.
/// </summary>
public class DemoAssignmentResolver(DemoDbContext db, ILogger<DemoAssignmentResolver> logger)
    : IWorkflowAssignmentResolver
{
    public async Task<WorkflowAssignment> ResolveAsync(
        string? roleKey,
        WorkflowAssignment current,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (string.IsNullOrWhiteSpace(roleKey))
        {
            return current;
        }

        var branchKey = current.BranchKey;
        if (string.IsNullOrWhiteSpace(branchKey))
        {
            logger.LogDebug("No branch key on task {TaskId}; keeping current assignment.", task?.Id);
            return current;
        }

        var section = await db.Sections
            .Include(s => s.Division)
            .SingleOrDefaultAsync(s => s.Code == branchKey, ct)
            .ConfigureAwait(false);

        if (section is null)
        {
            logger.LogWarning("Section '{BranchKey}' not found; keeping current assignment.", branchKey);
            return current;
        }

        return roleKey switch
        {
            DemoRoles.SectionLead => string.IsNullOrWhiteSpace(section.SectionLeadActorId)
                ? current
                : new WorkflowAssignment(section.SectionLeadActorId, section.Code),

            DemoRoles.DivisionHead => string.IsNullOrWhiteSpace(section.Division?.DivisionHeadActorId)
                ? current
                : new WorkflowAssignment(section.Division!.DivisionHeadActorId, section.Code),

            DemoRoles.Originator => current,

            _ => throw new InvalidOperationException($"Unknown assignment role '{roleKey}'.")
        };
    }
}

/// <summary>
/// The demo's directory: who its people are, what its sections are, and which section a
/// person is in. The engine stores only opaque ids and asks here for everything else.
///
/// <para>Absorbs what used to be <c>DemoActorResolver</c> and <c>DemoOrg</c>. They were
/// separate because nothing in the library consumed the first and the second was a private
/// helper of the demo's own clients; now that the library's runner and inbox ask these
/// questions through one seam, one class answers them.</para>
/// </summary>
public class DemoDirectory(DemoDbContext db) : IWorkflowActorResolver
{
    public async Task<string?> GetDisplayNameAsync(string actorId, CancellationToken ct = default) =>
        await db.People
            .Where(p => p.ActorId == actorId)
            .Select(p => p.FullName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<ActorOption>> GetActorsAsync(CancellationToken ct = default) =>
        await db.People
            .AsNoTracking()
            .OrderBy(p => p.FullName)
            .Select(p => new ActorOption(p.ActorId, p.FullName))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Every section. Deliberately unfiltered — the engine drops the ones a fork has
    /// already branched on, and filtering here as well would duplicate a rule this host
    /// would then have to keep in step.
    /// </summary>
    public async Task<IReadOnlyList<BranchOption>> GetBranchOptionsAsync(CancellationToken ct = default) =>
        await db.Sections
            .AsNoTracking()
            .OrderBy(s => s.Code)
            .Select(s => new BranchOption(s.Code, $"{s.Code} — {s.Name}"))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// A demo person sits in exactly one section, so this is that section or nothing. A host
    /// whose people cover several returns several — that is the whole reason this is a list.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetOrgUnitsForAsync(
        string actorId, CancellationToken ct = default)
    {
        var assignment = await GetAssignmentForAsync(actorId, ct).ConfigureAwait(false);

        return assignment.BranchKey is null ? [] : [assignment.BranchKey];
    }

    public async Task<WorkflowAssignment> GetAssignmentForAsync(
        string? actorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return WorkflowAssignment.Unassigned;
        }

        // Person has no navigation to Section, so this is a correlated subquery rather
        // than a join: an unknown person and a person with no section both come back
        // null, which is the same answer either way.
        var sectionCode = await db.People
            .AsNoTracking()
            .Where(p => p.ActorId == actorId)
            .Select(p => db.Sections
                .Where(s => s.Id == p.SectionId)
                .Select(s => s.Code)
                .FirstOrDefault())
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // A null section is a real state, not an error: the assignment resolver already
        // treats a missing branch key as "keep the current assignment", and inventing a
        // second failure style here would be worse.
        return new WorkflowAssignment(actorId, sectionCode);
    }
}

/// <summary>
/// Where computed progress lands. This is the host half of what the original system's
/// UpdateWorkItemProgressTrigger did; the engine now computes the percentage and this
/// only decides where it goes.
/// </summary>
public class DemoProgressSink(DemoDbContext db, ILogger<DemoProgressSink> logger) : IWorkflowProgressSink
{
    public async Task ReportAsync(
        WorkflowSubject subject,
        string? branchKey,
        double percentComplete,
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!int.TryParse(subject.SubjectId, out var documentId))
        {
            return;
        }

        // The engine's opaque branch key is this host's section code.
        var section = branchKey is null
            ? null
            : await db.Sections.SingleOrDefaultAsync(s => s.Code == branchKey, ct).ConfigureAwait(false);

        var items = await db.WorkItems
            .Where(w => w.DocumentId == documentId
                     && !w.IsArchived
                     && (section == null || w.AssignedSectionId == section.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var item in items)
        {
            item.CompletionPercentage = percentComplete;
            item.Modified = DateTime.UtcNow;
        }

        // The demo's WorkItem carries no modifier column, so the actor is logged rather
        // than stored. A host whose rows are audited stores it -- that is what the
        // parameter is for, and expect WorkflowActors.System from the deadline sweep
        // rather than one of your own users.
        logger.LogInformation(
            "{ActorId} moved {Count} work item(s) on {Subject} to {Percent}%",
            actorId, items.Count, subject, percentComplete);
    }
}

/// <summary>
/// Collects notifications instead of sending them, so the demo (and tests) can see
/// what the engine produced. A real host would send mail or raise a UI event here.
/// </summary>
public class DemoNotificationSink(ILogger<DemoNotificationSink> logger) : IWorkflowNotificationSink
{
    // Instance state, not static: tests run in parallel and each needs its own view.
    private readonly List<WorkflowNotification> _sent = [];

    public IReadOnlyList<WorkflowNotification> Delivered
    {
        get { lock (_sent) { return _sent.ToList(); } }
    }

    public Task SendAsync(WorkflowNotification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        lock (_sent) { _sent.Add(notification); }

        logger.LogInformation("Notification to {Recipient}: {Subject}",
            notification.RecipientActorId, notification.Subject_);

        return Task.CompletedTask;
    }
}

/// <summary>
/// A route condition. Replaces the original system's TaskRoutingService dictionary, which matched
/// substrings inside free-text task notes; this reads a workflow variable instead.
/// </summary>
public class RequiresReviewCondition : IRouteConditionEvaluator
{
    public string ConditionKey => "requires-review";

    public bool Evaluate(WorkflowTaskSnapshot task, IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        return variables.TryGetValue("requiresReview", out var v)
            && bool.TryParse(v, out var flag)
            && flag;
    }
}
