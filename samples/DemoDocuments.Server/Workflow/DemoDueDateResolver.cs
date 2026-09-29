using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// Where a deadline comes from in this host: the document the run is about.
///
/// The fourth worked example of a host seam, and the smallest — which is the point. The
/// engine stores a date it cannot derive, because a deadline belongs to whatever the run
/// is about and only the host knows that a run on <c>ChangeRequest:42</c> inherits that ChangeRequest's date.
///
/// <c>InternalDueDate ?? DueDate</c>: internal first, because an internal deadline is the
/// one the organisation actually works to. The external date is what was promised to
/// somebody else, and is usually later.
///
/// <para>Deliberately not per-task. Every task in a run gets the document's date; a
/// per-step allowance ("this review gets 5 days of it") would be the engine inventing a
/// deadline the organisation already has an answer for.</para>
/// </summary>
public sealed class DemoDueDateResolver(DemoDbContext db) : IWorkflowDueDateResolver
{
    public async Task<DateTime?> ResolveAsync(
        WorkflowSubject subject,
        WorkflowTaskSnapshot task,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // Keyed on the id's shape, not on SubjectType — the same limitation
        // DemoInboxClient documents, and for the same reason. Correct while every subject
        // this host creates is a document; the first non-document subject type with an
        // integer key would inherit an unrelated document's deadline. Filter on
        // subject.SubjectType when a second type appears.
        if (!int.TryParse(subject.SubjectId, out var documentId))
        {
            return null;
        }

        var dates = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.InternalDueDate, d.DueDate })
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return dates?.InternalDueDate ?? dates?.DueDate;
    }
}
