using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

namespace DemoDocuments.Server.Workflow;

/// <summary>
/// What this host's subjects are called and where they live: a document number, its title,
/// and the page it lives on.
///
/// <para>This is the whole reason the inbox needs a host at all. The engine knows a task
/// belongs to a run about <c>ChangeRequest:42</c>; only this host knows that is
/// "CR-2026-0042, Pump room rewire" at <c>documents/42</c>.</para>
/// </summary>
public sealed class DemoSubjectResolver(DemoDbContext db) : IWorkflowSubjectResolver
{
    public async Task<IReadOnlyDictionary<WorkflowSubject, SubjectDescriptor>> ResolveAsync(
        IReadOnlyList<WorkflowSubject> subjects, CancellationToken ct = default)
    {
        // One query for every document mentioned, rather than one per row. Subject ids are
        // strings because the engine cannot assume a key type; this host's are ints, so
        // anything unparseable is simply a subject that is not one of our documents.
        //
        // SubjectType is not checked, only the id's shape. Correct today because every
        // subject this host creates is a document, but the first non-document subject type
        // with an integer key would resolve to a document of that id and link to the wrong
        // page. Filter on s.SubjectType when a second type appears.
        var wanted = subjects
            .Select(s => int.TryParse(s.SubjectId, out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct()
            .ToList();

        if (wanted.Count == 0)
        {
            return new Dictionary<WorkflowSubject, SubjectDescriptor>();
        }

        var documents = await db.Documents
            .AsNoTracking()
            .Where(d => wanted.Contains(d.Id))
            .Select(d => new { d.Id, d.DocNumber, d.Title })
            .ToDictionaryAsync(d => d.Id, ct)
            .ConfigureAwait(false);

        var resolved = new Dictionary<WorkflowSubject, SubjectDescriptor>();

        foreach (var subject in subjects)
        {
            // A subject with no document is simply left out. The caller falls back to the
            // raw key and renders the row unclickable, which keeps an orphaned task
            // visible -- it is a defect worth seeing, not worth hiding.
            if (int.TryParse(subject.SubjectId, out var id)
                && documents.TryGetValue(id, out var doc))
            {
                resolved[subject] = new SubjectDescriptor(
                    Label: doc.DocNumber,
                    Subtitle: doc.Title,
                    Url: $"documents/{doc.Id}");
            }
        }

        return resolved;
    }
}
