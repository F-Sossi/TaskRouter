using Microsoft.EntityFrameworkCore;

using TaskRouter.Core.Model;
using TaskRouter.Core.Validation;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// Publishing a hand-built workflow, without the caller having to know the ordering it
/// requires.
///
/// Two constraints collide. <see cref="WorkflowDefinitionVersion.EntryTaskDefinitionId"/>
/// can only be set once the task definition it names has an id, which means after a save;
/// the publish flags, on the other hand, are naturally set when the version object is
/// constructed, which is before. Write it the obvious way and the version is briefly
/// flagged published while structurally incomplete — and if validation then fails, it
/// stays that way.
///
/// This flips the order: ids first, validate, and set the flags only in the final save.
/// A version that fails validation is left as a draft, which is what it is.
/// </summary>
public static class WorkflowPublishingExtensions
{
    /// <summary>
    /// Publishes <paramref name="version"/> with <paramref name="entryTask"/> as its entry
    /// point, and demotes whichever version of the same definition was latest before.
    ///
    /// Returns the validation errors, or an empty list if it published. Errors mean
    /// nothing was published: the version is saved as a draft, and the call can be
    /// repeated once the graph is fixed.
    ///
    /// Both objects may be freshly added and unsaved — the entry task's id is back-filled
    /// here. <paramref name="entryTask"/> must be one of <paramref name="version"/>'s own
    /// <see cref="WorkflowDefinitionVersion.Tasks"/>.
    /// </summary>
    public static async Task<IReadOnlyList<ValidationError>> PublishWithEntryTaskAsync(
        this IWorkflowDbContext db,
        WorkflowDefinitionVersion version,
        WorkflowTaskDefinition entryTask,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(entryTask);

        if (!version.Tasks.Contains(entryTask))
        {
            throw new ArgumentException(
                "The entry task must be one of the version's own tasks. A task belonging to "
                + "another version would leave the workflow with an unreachable graph.",
                nameof(entryTask));
        }

        // Mints ids and fixes up the foreign keys the validator reads off the routes.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        version.EntryTaskDefinitionId = entryTask.Id;

        var errors = WorkflowDefinitionValidator.Validate(version);

        if (errors.Count == 0)
        {
            // The engine selects the latest published version with SingleOrDefaultAsync,
            // so leaving two of them latest is not untidiness — it throws on the next run.
            var superseded = await db.WorkflowDefinitionVersions
                .Where(v => v.WorkflowDefinitionId == version.WorkflowDefinitionId
                         && v.Id != version.Id
                         && v.IsLatest)
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var old in superseded)
            {
                // Still published: runs pin to a version, and the ones already on it
                // must keep resolving their routes.
                old.IsLatest = false;
            }

            version.IsPublished = true;
            version.IsLatest = true;
            version.PublishedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return errors;
    }
}
