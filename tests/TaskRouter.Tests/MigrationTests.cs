using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Model;

namespace TaskRouter.Tests;

/// <summary>
/// Guards the migration story.
///
/// The engine has no database of its own: <c>ConfigureTaskRouter()</c> puts its
/// tables on the host's context, so they are part of the host's schema and the host owns
/// the migrations. That is what lets a task completion and a domain update share one
/// transaction, and it means a change to an engine entity is a change to every
/// consumer's schema.
///
/// The other tests build their databases with <c>EnsureCreated</c>, which is fast but
/// reads the model rather than the migrations — so a migration could drift from the model
/// and every one of them would still pass. These two close that gap.
/// </summary>
[TestClass]
public class MigrationTests
{
    [TestMethod]
    public void The_model_and_the_migrations_have_not_drifted()
    {
        var options = new DbContextOptionsBuilder<DemoDbContext>()
            .UseSqlServer($"{TestHost.BaseConnectionString};Database=WorkflowMigrationCheck")
            .Options;

        using var db = new DemoDbContext(options);

        // No connection is made: this compares the model against the last migration's
        // snapshot. If it fails, someone changed an entity and did not add a migration.
        //
        //   dotnet ef migrations add <Name> \
        //     --project samples/DemoDocuments.Server --context DemoDbContext \
        //     --output-dir Data/Migrations
        Assert.IsFalse(db.Database.HasPendingModelChanges(),
            "the model has changed since the last migration; add one");
    }

    [TestMethod]
    public async Task A_database_built_from_the_migrations_runs_a_workflow()
    {
        // EnsureCreated would prove nothing here — the point is that the migration
        // scripts themselves produce a schema the engine can work against.
        var name = $"WorkflowMigrated_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<DemoDbContext>()
            .UseSqlServer($"{TestHost.BaseConnectionString};Database={name}",
                sql => sql.EnableRetryOnFailure())
            .Options;

        await using var db = new DemoDbContext(options);

        try
        {
            await db.Database.MigrateAsync();

            var applied = await db.Database.GetAppliedMigrationsAsync();
            Assert.IsNotEmpty(applied);
            Assert.IsEmpty(await db.Database.GetPendingMigrationsAsync());

            // Exercise the schema rather than just inspecting it: the filtered unique
            // index that guards convergence, the owned WorkflowSubject columns and the
            // enum-ordinal-sensitive index filter only show up when something runs.
            await DemoWorkflowSeeder.SeedAsync(db);

            var engine = new TaskRouter.EntityFrameworkCore.WorkflowEngine(
                db,
                new DemoAssignmentResolver(
                    db, Microsoft.Extensions.Logging.Abstractions.NullLogger<DemoAssignmentResolver>.Instance),
                [new RequiresReviewCondition()],
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskRouter.EntityFrameworkCore.WorkflowEngine>.Instance);

            var definitionId = await db.WorkflowDefinitions.Where(d => !d.IsSubWorkflow).Select(d => d.Id).FirstAsync();

            var run = (await engine.StartRunAsync(
                new WorkflowSubject("ChangeRequest", "1"), definitionId, "user-originator")).Unwrap();

            var entryTaskId = run.Tasks.Single().Id;

            (await engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator")).Unwrap();

            Assert.AreEqual(2, await db.WorkflowTasks.CountAsync(t => t.WorkflowRunId == run.Id));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
