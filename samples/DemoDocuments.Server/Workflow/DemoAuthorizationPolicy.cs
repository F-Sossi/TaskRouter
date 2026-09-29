using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;

using TaskRouter.Core.Abstractions;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// The demo's authorization rules, and the worked example of the
/// <see cref="IWorkflowAuthorizationPolicy"/> seam.
///
/// It splits <b>doing</b> the work from <b>managing</b> it. Completing a task, editing its
/// notes and hanging work off it are for the person who has it; cancelling, reassigning,
/// forking and unwinding it are for the person above them. That split is why the seam
/// takes an operation rather than answering one question — a policy that ignored its first
/// argument would suggest the enum was unnecessary.
///
/// The org walk is the one <see cref="DemoEscalationTrigger"/> already uses, in the same
/// direction: <c>Person.SectionId</c> → <c>Section.Code</c> (which is the engine's opaque
/// branch key) → <c>Section.SectionLeadActorId</c> → <c>Division.DivisionHeadActorId</c>. the original system would
/// answer the same questions against its own model; nothing here is engine knowledge.
/// </summary>
public sealed class DemoAuthorizationPolicy(DemoDbContext db) : IWorkflowAuthorizationPolicy
{
    public async Task<WorkflowAuthorizationResult> EvaluateAsync(
        WorkflowOperation operation,
        WorkflowAuthorizationContext context,
        CancellationToken ct = default)
    {
        var person = await db.People
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ActorId == context.ActorId, ct)
            .ConfigureAwait(false);

        if (person is null)
        {
            return WorkflowAuthorizationResult.Denied("you are not a known person");
        }

        // Starting a run is open to anybody on the books. The demo has no notion of who
        // may originate work, and inventing one would teach a rule the original system does not have.
        if (operation == WorkflowOperation.StartRun)
        {
            return WorkflowAuthorizationResult.Allowed;
        }

        var task = context.Task;

        if (task is null)
        {
            return WorkflowAuthorizationResult.Denied("no task to authorize against");
        }

        return operation switch
        {
            WorkflowOperation.CompleteTask
            or WorkflowOperation.CompleteWithSelectiveRejection
            or WorkflowOperation.UpdateTaskNotes
            or WorkflowOperation.AddAdHocTask
            or WorkflowOperation.StartSubWorkflow
                => await DoingAsync(person, task, ct).ConfigureAwait(false),

            WorkflowOperation.CancelTask
            or WorkflowOperation.ReassignTask
            or WorkflowOperation.ForkTask
            or WorkflowOperation.AddBranchToFork
            or WorkflowOperation.CancelSubWorkflow
                => await ManagingAsync(person, task, ct).ConfigureAwait(false),

            // A new operation the engine gained and this policy has not considered.
            // Denied on purpose: an unreviewed operation should fail visibly here rather
            // than be permitted by a default arm nobody remembers writing.
            _ => WorkflowAuthorizationResult.Denied(
                $"this host has no rule for {operation}"),
        };
    }

    /// <summary>
    /// The assignee, or anyone in the section when nobody has claimed it.
    ///
    /// "Unclaimed" is the same shape as the inbox's unclaimed half — a branch key that
    /// matches, and no actor — deliberately, so the demo cannot show somebody work in
    /// their inbox that it then refuses to let them do.
    /// </summary>
    private async Task<WorkflowAuthorizationResult> DoingAsync(
        Person person, WorkflowTaskSnapshot task, CancellationToken ct)
    {
        if (task.AssignedToActorId == person.ActorId)
        {
            return WorkflowAuthorizationResult.Allowed;
        }

        if (task.AssignedToActorId is null
            && task.AssignedBranchKey is not null
            && task.AssignedBranchKey == await SectionCodeAsync(person, ct).ConfigureAwait(false))
        {
            return WorkflowAuthorizationResult.Allowed;
        }

        return WorkflowAuthorizationResult.Denied(
            "this task belongs to somebody else, or to another section");
    }

    /// <summary>The section's Section Lead, or the division head above it.</summary>
    private async Task<WorkflowAuthorizationResult> ManagingAsync(
        Person person, WorkflowTaskSnapshot task, CancellationToken ct)
    {
        if (task.AssignedBranchKey is null)
        {
            return WorkflowAuthorizationResult.Denied(
                "this task is in no section, so it has no supervisor");
        }

        var section = await db.Sections
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Code == task.AssignedBranchKey, ct)
            .ConfigureAwait(false);

        if (section is null)
        {
            return WorkflowAuthorizationResult.Denied(
                $"section '{task.AssignedBranchKey}' is not one of ours");
        }

        if (section.SectionLeadActorId == person.ActorId)
        {
            return WorkflowAuthorizationResult.Allowed;
        }

        var divisionHead = await db.Divisions
            .AsNoTracking()
            .Where(b => b.Id == section.DivisionId)
            .Select(b => b.DivisionHeadActorId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return divisionHead == person.ActorId
            ? WorkflowAuthorizationResult.Allowed
            : WorkflowAuthorizationResult.Denied(
                "only the section's Section Lead or the division head above it may do this");
    }

    private async Task<string?> SectionCodeAsync(Person person, CancellationToken ct) =>
        await db.Sections
            .AsNoTracking()
            .Where(s => s.Id == person.SectionId)
            .Select(s => s.Code)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
