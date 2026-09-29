using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;

using TaskRouter.Core.Model;
using TaskRouter.EntityFrameworkCore;

namespace TaskRouter.Tests;

/// <summary>
/// Publishing a workflow by hand takes four saves in the right order, and the ordering
/// is undiscoverable: <see cref="WorkflowDefinitionVersion.EntryTaskDefinitionId"/> can
/// only be set once the task definition it points at has an id, while the publish flags
/// are naturally set when the version is created — so a version written the obvious way
/// spends a moment flagged published while structurally incomplete.
///
/// The integration spike hit this seeding the smallest possible workflow. These tests
/// pin the convenience that removes the need for a caller to know any of it.
/// </summary>
[TestClass]
public class PublishingTests
{
    private const string Actor = "publisher";

    private static async Task<(WorkflowDefinition Definition, WorkflowDefinitionVersion Version,
        WorkflowTaskDefinition Entry)> DraftAsync(
        DemoDbContext db, int version = 1, bool terminal = true)
    {
        var now = DateTime.UtcNow;

        var type = new TaskTypeDefinition
        {
            Key = $"publish-test-{Guid.NewGuid():N}",
            DisplayName = "Publish Test",
            CreatorId = Actor,
            ModifierId = Actor,
            Created = now,
            Modified = now
        };
        db.WorkflowTaskTypes.Add(type);

        var definition = new WorkflowDefinition
        {
            Name = $"Publish Test {Guid.NewGuid():N}",
            CreatorId = Actor,
            ModifierId = Actor,
            Created = now,
            Modified = now
        };
        db.WorkflowDefinitions.Add(definition);

        await db.SaveChangesAsync();

        return await DraftOnAsync(db, definition, version, terminal);
    }

    /// <summary>A second draft version of a definition that already has one.</summary>
    private static Task<(WorkflowDefinition, WorkflowDefinitionVersion, WorkflowTaskDefinition)>
        DraftOnAsync(DemoDbContext db, WorkflowDefinition definition, int version, bool terminal = true)
    {
        var now = DateTime.UtcNow;
        var type = db.WorkflowTaskTypes.Local.First(t => t.Key.StartsWith("publish-test-", StringComparison.Ordinal));

        // Deliberately *not* flagged published: that is the helper's job, and doing it
        // here is the mistake the helper exists to prevent.
        var draft = new WorkflowDefinitionVersion
        {
            WorkflowDefinitionId = definition.Id,
            Version = version,
            SubjectType = "PublishTest",
            CreatorId = Actor,
            ModifierId = Actor,
            Created = now,
            Modified = now
        };
        db.WorkflowDefinitionVersions.Add(draft);

        var entry = new WorkflowTaskDefinition
        {
            WorkflowDefinitionVersionId = 0,
            WorkflowDefinitionVersion = draft,
            TaskTypeDefinitionId = type.Id,
            DisplayName = "Only Task",
            IsTerminal = terminal,
            CreatorId = Actor,
            ModifierId = Actor,
            Created = now,
            Modified = now
        };
        draft.Tasks.Add(entry);
        db.WorkflowTaskDefinitions.Add(entry);

        return Task.FromResult((definition, draft, entry));
    }

    [TestMethod]
    public async Task It_publishes_a_version_and_records_its_entry_task_in_one_call()
    {
        await using var host = await TestHost.CreateAsync();
        var (_, version, entry) = await DraftAsync(host.Db);

        var errors = await host.Db.PublishWithEntryTaskAsync(version, entry);

        Assert.IsEmpty(errors);

        var saved = await host.Db.WorkflowDefinitionVersions
            .AsNoTracking().SingleAsync(v => v.Id == version.Id);

        Assert.IsTrue(saved.IsPublished);
        Assert.IsTrue(saved.IsLatest);
        Assert.IsNotNull(saved.PublishedAt);
        Assert.AreEqual(entry.Id, saved.EntryTaskDefinitionId,
            "The caller never had to know the entry task's id had to be back-filled.");
    }

    [TestMethod]
    public async Task A_published_version_can_start_a_run()
    {
        await using var host = await TestHost.CreateAsync();
        var (definition, version, entry) = await DraftAsync(host.Db);

        await host.Db.PublishWithEntryTaskAsync(version, entry);

        var run = await host.Engine.StartRunAsync(
            new WorkflowSubject("PublishTest", "1"), definition.Id, Actor);

        Assert.IsTrue(run.IsOk, run.IsError ? run.UnwrapError().Message : null);

        var task = await host.Db.WorkflowTasks.AsNoTracking()
            .SingleAsync(t => t.WorkflowRunId == run.Unwrap().Id);

        Assert.AreEqual(entry.Id, task.TaskDefinitionId);
    }

    [TestMethod]
    public async Task Publishing_a_new_version_demotes_the_previous_one()
    {
        await using var host = await TestHost.CreateAsync();
        var (definition, v1, entry1) = await DraftAsync(host.Db);
        await host.Db.PublishWithEntryTaskAsync(v1, entry1);

        var (_, v2, entry2) = await DraftOnAsync(host.Db, definition, version: 2);
        await host.Db.PublishWithEntryTaskAsync(v2, entry2);

        var versions = await host.Db.WorkflowDefinitionVersions.AsNoTracking()
            .Where(v => v.WorkflowDefinitionId == definition.Id)
            .OrderBy(v => v.Version)
            .ToListAsync();

        Assert.HasCount(2, versions);
        Assert.IsTrue(versions[0].IsPublished, "A superseded version stays published; runs pin to it.");
        Assert.IsFalse(versions[0].IsLatest);
        Assert.IsTrue(versions[1].IsLatest);

        // The engine selects the latest published version with SingleOrDefaultAsync, so
        // two latest versions is not a cosmetic problem — it throws.
        var run = await host.Engine.StartRunAsync(
            new WorkflowSubject("PublishTest", "2"), definition.Id, Actor);

        Assert.IsTrue(run.IsOk, run.IsError ? run.UnwrapError().Message : null);
        Assert.AreEqual(v2.Id, run.Unwrap().WorkflowDefinitionVersionId);
    }

    [TestMethod]
    public async Task A_version_that_fails_validation_is_not_published()
    {
        await using var host = await TestHost.CreateAsync();

        // One non-terminal task with no outgoing routes: a dead end.
        var (_, version, entry) = await DraftAsync(host.Db, terminal: false);

        var errors = await host.Db.PublishWithEntryTaskAsync(version, entry);

        Assert.IsNotEmpty(errors);

        var saved = await host.Db.WorkflowDefinitionVersions
            .AsNoTracking().SingleAsync(v => v.Id == version.Id);

        Assert.IsFalse(saved.IsPublished,
            "Failing validation must leave the version a draft, not a published fragment.");
        Assert.IsFalse(saved.IsLatest);
        Assert.IsNull(saved.PublishedAt);
    }

    [TestMethod]
    public async Task An_entry_task_from_another_version_is_refused()
    {
        await using var host = await TestHost.CreateAsync();
        var (_, version, _) = await DraftAsync(host.Db);
        var (_, other, otherEntry) = await DraftAsync(host.Db);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => host.Db.PublishWithEntryTaskAsync(version, otherEntry));

        Assert.AreNotEqual(version.Id, other.Id);
    }
}
