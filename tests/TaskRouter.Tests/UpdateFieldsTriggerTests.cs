using System.Diagnostics;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using DemoDocuments.Server.Data;
using DemoDocuments.Server.Domain;
using DemoDocuments.Server.Workflow;

using TaskRouter.Core.Abstractions;
using TaskRouter.Core.Model;

using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Fields;

namespace TaskRouter.Tests;

[TestClass]
public class UpdateFieldsTriggerTests
{
    private TestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await TestHost.CreateAsync(
        withTriggers: true,
        configure: s => s.AddScoped<IWorkflowTrigger, UndoFailingTrigger>());

    [TestCleanup]
    public async Task Cleanup() => await _host.DisposeAsync();

    // ───────────────────────────── The undo scope ─────────────────────────────

    [TestMethod]
    public async Task An_abandoned_scope_undoes_saved_and_unsaved_writes_but_keeps_earlier_work()
    {
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            // Pending engine work from before the trigger: must survive.
            document.Description = "engine wrote this";

            await using (var scope = await FieldWriteScope.BeginAsync(db, ct))
            {
                document.Comments = "saved by the host";
                await db.SaveChangesAsync(ct);

                document.Revision = "B";                                   // unsaved
                db.Sections.Add(new Section { Code = "TMP", Name = "Temp" }); // new
                // no SaveAndCompleteAsync: abandoned
            }

            Assert.AreEqual(EntityState.Unchanged, db.Entry(document).State);
            Assert.IsNull(document.Comments);
            Assert.AreEqual("-", document.Revision);
            Assert.AreEqual("engine wrote this", document.Description);
            Assert.IsFalse(db.ChangeTracker.Entries<Section>().Any(e => e.Entity.Code == "TMP"));
        });

        db.ChangeTracker.Clear();

        var stored = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.AreEqual("engine wrote this", stored.Description);
        Assert.IsNull(stored.Comments);
        Assert.AreEqual("-", stored.Revision);
        Assert.IsFalse(await db.Sections.AnyAsync(s => s.Code == "TMP"));
    }

    [TestMethod]
    public async Task An_abandoned_scope_reverts_a_modification_that_was_never_saved_or_detected()
    {
        // The only change inside the scope is a plain property write: no SaveChanges, no
        // Entry() call, so nothing has run DetectChanges and no StateChanged event has
        // fired yet. The scope must still find and revert it.
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            await using (var scope = await FieldWriteScope.BeginAsync(db, ct))
            {
                document.Revision = "C";
            }

            Assert.AreEqual("-", document.Revision);
            Assert.AreEqual(EntityState.Unchanged, db.Entry(document).State);
        });

        db.ChangeTracker.Clear();
        Assert.AreEqual("-", (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id)).Revision);
    }

    [TestMethod]
    public async Task An_abandoned_scope_detaches_a_new_entity_the_host_already_saved()
    {
        var db = _host.Db;

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            var section = new Section { Code = "SAVED", Name = "Saved then undone" };

            await using (var scope = await FieldWriteScope.BeginAsync(db, ct))
            {
                db.Sections.Add(section);
                await db.SaveChangesAsync(ct);
            }

            Assert.AreEqual(EntityState.Detached, db.Entry(section).State);
        });

        db.ChangeTracker.Clear();
        Assert.IsFalse(await db.Sections.AnyAsync(s => s.Code == "SAVED"));
    }

    [TestMethod]
    public async Task Reverting_an_entity_whose_row_is_gone_detaches_it()
    {
        // The row is deleted before the scope begins, so the rollback does not bring it
        // back; reverting the tracked entity must detach it rather than throw.
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            await db.Documents.Where(d => d.Id == document.Id).ExecuteDeleteAsync(ct);

            await using (var scope = await FieldWriteScope.BeginAsync(db, ct))
            {
                document.Comments = "never lands";
            }

            Assert.AreEqual(EntityState.Detached, db.Entry(document).State);
        });

        db.ChangeTracker.Clear();
        Assert.IsFalse(await db.Documents.AnyAsync(d => d.Id == document.Id));
    }

    [TestMethod]
    public async Task A_completed_scope_keeps_its_writes()
    {
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            await using var scope = await FieldWriteScope.BeginAsync(db, ct);
            document.Comments = "kept";
            db.Sections.Add(new Section { Code = "KEPT", Name = "Kept" });
            await scope.SaveAndCompleteAsync(ct);
        });

        db.ChangeTracker.Clear();
        Assert.AreEqual("kept", (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id)).Comments);
        Assert.IsTrue(await db.Sections.AnyAsync(s => s.Code == "KEPT"));
    }

    [TestMethod]
    public async Task An_abandoned_scope_lets_the_original_error_through_with_its_writes_undone()
    {
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            document.Description = "engine wrote this";

            // As under LogAndContinue: the trigger's error is caught and the workflow carries on.
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            {
                var scope = await FieldWriteScope.BeginAsync(db, ct);
                try
                {
                    document.Comments = "saved by the host";
                    await db.SaveChangesAsync(ct);
                    Refuse();
                    await scope.SaveAndCompleteAsync(ct);
                }
                catch (Exception ex)
                {
                    await scope.AbandonAsync(ex);
                    throw;
                }
                finally
                {
                    await scope.DisposeAsync();
                }
            });

            Assert.AreEqual("refused", error.Message);
            Assert.IsNull(document.Comments);
            Assert.AreEqual(EntityState.Unchanged, db.Entry(document).State);
        });

        db.ChangeTracker.Clear();
        var stored = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.AreEqual("engine wrote this", stored.Description);
        Assert.IsNull(stored.Comments);

        static void Refuse() => throw new InvalidOperationException("refused");
    }

    [TestMethod]
    public async Task A_database_refusal_on_save_is_undone_and_earlier_work_kept()
    {
        var db = _host.Db;
        var document = await NewChangeRequestAsync();
        var existing = await NewSectionAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            document.Description = "engine wrote this";

            var scope = await FieldWriteScope.BeginAsync(db, ct);
            try
            {
                document.Comments = "never lands";
                db.Sections.Add(new Section { Code = existing.Code, Name = "Duplicate" }); // unique index on Code
                var refusal = await Assert.ThrowsExactlyAsync<DbUpdateException>(() => scope.SaveAndCompleteAsync(ct));
                await scope.AbandonAsync(refusal);
            }
            finally
            {
                await scope.DisposeAsync();
            }

            Assert.IsFalse(db.ChangeTracker.HasChanges());
            Assert.IsNull(document.Comments);
        });

        db.ChangeTracker.Clear();
        var stored = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.AreEqual("engine wrote this", stored.Description);
        Assert.IsNull(stored.Comments);
        Assert.AreEqual(1, await db.Sections.CountAsync(s => s.Code == existing.Code));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "unsaved")]
    [DataRow(true, DisplayName = "saved by the host")]
    public async Task A_new_entity_added_through_a_collection_is_undone(bool hostSaves)
    {
        var db = _host.Db;
        var document = await NewChangeRequestAsync();
        var section = await NewSectionAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            await using (var scope = await FieldWriteScope.BeginAsync(db, ct))
            {
                document.WorkItems.Add(new WorkItem { DocumentId = document.Id, AssignedSectionId = section.Id });
                if (hostSaves)
                {
                    await db.SaveChangesAsync(ct);
                }
            }

            Assert.IsEmpty(document.WorkItems);
            await db.SaveChangesAsync(ct);
        });

        db.ChangeTracker.Clear();
        Assert.IsFalse(await db.WorkItems.AnyAsync(w => w.DocumentId == document.Id));
    }

    [TestMethod]
    public async Task An_existing_entity_deleted_and_saved_inside_the_scope_comes_back_tracked()
    {
        var db = _host.Db;
        var section = await NewSectionAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            await using (var scope = await FieldWriteScope.BeginAsync(db, ct))
            {
                db.Sections.Remove(section);
                await db.SaveChangesAsync(ct);
            }

            Assert.AreEqual(EntityState.Unchanged, db.Entry(section).State);
        });

        db.ChangeTracker.Clear();
        Assert.IsTrue(await db.Sections.AnyAsync(s => s.Id == section.Id));
    }

    [TestMethod]
    public async Task An_undo_that_fails_rolls_back_everything_and_reports_both_errors()
    {
        // XACT_ABORT makes the server roll back the whole transaction on the error, taking
        // the savepoint with it, so rolling back to the savepoint must fail.
        await using var db = NonPooledDb();
        var document = await NewChangeRequestAsync(db);

        // This caller deliberately carries on past the undo failure, which real callers must
        // not do. Its write is kept out here only incidentally: the final save fails because
        // EF tries to take its automatic savepoint in a transaction the server no longer has
        // (error 628). The real guard is the exception propagating; see the dispatcher test.
        var final = await Assert.ThrowsExactlyAsync<SqlException>(() => WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            document.Description = "engine wrote this";

            var scope = await FieldWriteScope.BeginAsync(db, ct);
            FieldUpdateUndoFailedException? failure = null;
            try
            {
                try
                {
                    document.Comments = "saved by the host";
                    await db.SaveChangesAsync(ct);
                    await db.Database.ExecuteSqlRawAsync(AbortTransactionSql, ct);
                }
                catch (Exception ex)
                {
                    failure = await Assert.ThrowsExactlyAsync<FieldUpdateUndoFailedException>(() => scope.AbandonAsync(ex));
                    StringAssert.StartsWith(failure.Message, ex.Message);
                    StringAssert.Contains(failure.Message, "so the whole operation was rolled back.");
                    Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
                }
            }
            finally
            {
                await scope.DisposeAsync();
                await scope.DisposeAsync(); // idempotent
            }

            Assert.IsNotNull(failure);

            // Carrying on regardless and writing more.
            document.Revision = "Z";
        }));

        StringAssert.Contains(final.Message, "Cannot issue SAVE TRANSACTION when there is no active transaction");

        db.ChangeTracker.Clear();
        var stored = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.IsNull(stored.Description);
        Assert.IsNull(stored.Comments);
        Assert.AreEqual("-", stored.Revision);
    }

    [TestMethod]
    public async Task An_undo_failure_rethrown_out_of_the_transaction_keeps_its_own_message()
    {
        // As under FailOperation: the composite leaves the work delegate, and disposing the
        // dead transaction on the way out must not replace it.
        await using var db = NonPooledDb();
        var document = await NewChangeRequestAsync(db);
        string? causeMessage = null;

        var failure = await Assert.ThrowsExactlyAsync<FieldUpdateUndoFailedException>(() => WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            var scope = await FieldWriteScope.BeginAsync(db, ct);
            try
            {
                document.Comments = "saved by the host";
                await db.SaveChangesAsync(ct);
                await db.Database.ExecuteSqlRawAsync(AbortTransactionSql, ct);
                await scope.SaveAndCompleteAsync(ct);
            }
            catch (Exception ex)
            {
                causeMessage = ex.Message;
                await scope.AbandonAsync(ex);
                throw;
            }
            finally
            {
                await scope.DisposeAsync();
            }
        }));

        Assert.IsNotNull(causeMessage);
        StringAssert.StartsWith(failure.Message, causeMessage);

        db.ChangeTracker.Clear();
        Assert.IsNull((await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id)).Comments);
    }

    [TestMethod]
    public async Task A_connection_without_savepoints_is_refused_before_anything_is_saved()
    {
        await using var db = new DemoDbContext(new DbContextOptionsBuilder<DemoDbContext>()
            .UseSqlServer(HostConnectionString("MultipleActiveResultSets=True"), sql => sql.EnableRetryOnFailure())
            .Options);
        var document = await NewChangeRequestAsync(db);

        await Assert.ThrowsExactlyAsync<UnreachableException>(() => WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            document.Description = "pending engine work";

            var refusal = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => FieldWriteScope.BeginAsync(db, ct));
            StringAssert.StartsWith(refusal.Message, "Field updates need database savepoints");

            // Not flushed: still pending in the tracker.
            Assert.AreEqual(EntityState.Modified, db.Entry(document).State);

            throw new UnreachableException("stop here so the transaction rolls back");
        }));

        db.ChangeTracker.Clear();
        Assert.IsNull((await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id)).Description);
    }

    [TestMethod]
    public async Task A_failed_undo_stops_the_operation_even_under_LogAndContinue()
    {
        // Runs on the host's pooled connection. The XACT_ABORT setting it leaves behind can
        // only reach later connections to this test's own database, which is dropped at cleanup.
        var db = _host.Db;
        var workflowId = await db.WorkflowDefinitions
            .Where(w => w.Name == DemoWorkflowSeeder.ChangeRequestWorkflowName)
            .Select(w => w.Id)
            .SingleAsync();

        var subject = new WorkflowSubject(nameof(DemoDocumentType.ChangeRequest), Guid.NewGuid().ToString());
        var run = (await _host.Engine.StartRunAsync(subject, workflowId, "user-originator")).Unwrap();
        var entryTaskId = run.Tasks.Single().Id;
        var entryDefinitionId = await db.WorkflowTasks
            .Where(t => t.Id == entryTaskId).Select(t => t.TaskDefinitionId).SingleAsync();

        db.WorkflowTriggerDefinitions.Add(new TriggerDefinition
        {
            TaskDefinitionId = entryDefinitionId,
            TriggerKey = UndoFailingTrigger.TriggerKey,
            Event = WorkflowEventKind.TaskCompleted,
            DispatchMode = TriggerDispatchMode.InTransaction,
            FailurePolicy = TriggerFailurePolicy.LogAndContinue,
            CreatorId = "system",
            ModifierId = "system"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await _host.Engine.CompleteTaskAsync(entryTaskId, "approved", "user-originator");

        Assert.IsTrue(result.IsError);
        var error = result.UnwrapError();
        Assert.IsInstanceOfType<FieldUpdateUndoFailedException>(error, error.ToString());
        StringAssert.Contains(error.Message, "so the whole operation was rolled back.");

        db.ChangeTracker.Clear();
        var status = await db.WorkflowTasks.AsNoTracking()
            .Where(t => t.Id == entryTaskId).Select(t => t.Status).SingleAsync();
        Assert.AreNotEqual(WorkflowTaskStatus.Completed, status);
        Assert.IsFalse(await db.Sections.AnyAsync(s => s.Code == UndoFailingTrigger.SectionCode));
    }

    [TestMethod]
    [DataRow(false, DisplayName = "unsaved")]
    [DataRow(true, DisplayName = "saved by the host")]
    public async Task A_new_child_in_a_one_sided_collection_is_undone(bool hostSaves)
    {
        // The demo model has no collection without a back-reference, so this uses a
        // test-only context that adds one.
        var options = new DbContextOptionsBuilder<DemoDbContext>()
            .UseSqlServer(
                $"{TestHost.BaseConnectionString};Database=WorkflowTest_{Guid.NewGuid():N}",
                sql => sql.EnableRetryOnFailure())
            .Options;
        await using var db = new OneSidedDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();

            var basket = new Basket();
            db.Baskets.Add(basket);
            await db.SaveChangesAsync();

            await WorkflowTransaction.ExecuteAsync(db, async ct =>
            {
                await using (var scope = await FieldWriteScope.BeginAsync(db, ct))
                {
                    basket.Fruits.Add(new Fruit());
                    if (hostSaves)
                    {
                        await db.SaveChangesAsync(ct);
                    }
                }

                Assert.IsEmpty(basket.Fruits);
                await db.SaveChangesAsync(ct);
            });

            db.ChangeTracker.Clear();
            Assert.IsFalse(await db.Fruits.AnyAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    // ───────────────────────────── Fixtures ─────────────────────────────

    private async Task<Section> NewSectionAsync()
    {
        var section = new Section { Code = $"S-{Guid.NewGuid():N}"[..10], Name = "Fixture section" };
        _host.Db.Sections.Add(section);
        await _host.Db.SaveChangesAsync();
        return section;
    }

    private const string AbortTransactionSql = "SET XACT_ABORT ON; SELECT 1/0;";

    private string HostConnectionString(string extra) =>
        $"{TestHost.BaseConnectionString};Database={_host.Db.Database.GetDbConnection().Database};{extra}";

    /// <summary>
    /// A context on the host's database that does not pool its connection, so the
    /// XACT_ABORT setting the undo-failure tests leave behind dies with the connection.
    /// </summary>
    private DemoDbContext NonPooledDb() =>
        new(new DbContextOptionsBuilder<DemoDbContext>()
            .UseSqlServer(HostConnectionString("Pooling=False"), sql => sql.EnableRetryOnFailure())
            .Options);

    private Task<ChangeRequest> NewChangeRequestAsync() => NewChangeRequestAsync(_host.Db);

    private static async Task<ChangeRequest> NewChangeRequestAsync(DemoDbContext db)
    {
        var document = new ChangeRequest
        {
            Title = "Pump room rewire",
            DocNumber = $"CR-{Guid.NewGuid():N}"[..12],
            Originator = "user-originator"
        };

        db.ChangeRequests.Add(document);
        await db.SaveChangesAsync();
        return document;
    }

    /// <summary>Opens a field scope, saves a host write, then dooms the transaction.</summary>
    private sealed class UndoFailingTrigger(IWorkflowDbContext workflowDb) : IWorkflowTrigger
    {
        public const string TriggerKey = "test.undo-fails";
        public const string SectionCode = "UNDO-FAIL";

        public string Key => TriggerKey;

        public TriggerDescriptor Describe() =>
            new(Key, "Undo fails", "Writes, then dooms the transaction.", Enum.GetValues<WorkflowEventKind>(), []);

        public async Task ExecuteAsync(WorkflowTriggerContext context, CancellationToken ct = default)
        {
            var db = (DemoDbContext)workflowDb;
            var scope = await FieldWriteScope.BeginAsync(db, ct);
            try
            {
                db.Sections.Add(new Section { Code = SectionCode, Name = "Never lands" });
                await db.SaveChangesAsync(ct);
                await db.Database.ExecuteSqlRawAsync(AbortTransactionSql, ct);
                await scope.SaveAndCompleteAsync(ct);
            }
            catch (Exception ex)
            {
                await scope.AbandonAsync(ex);
                throw;
            }
            finally
            {
                await scope.DisposeAsync();
            }
        }
    }

    private sealed class OneSidedDbContext(DbContextOptions<DemoDbContext> options) : DemoDbContext(options)
    {
        public DbSet<Basket> Baskets => Set<Basket>();
        public DbSet<Fruit> Fruits => Set<Fruit>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Basket>().HasMany(b => b.Fruits).WithOne().HasForeignKey(f => f.BasketId);
        }
    }

    private sealed class Basket
    {
        public int Id { get; set; }
        public List<Fruit> Fruits { get; } = [];
    }

    private sealed class Fruit
    {
        public int Id { get; set; }
        public int BasketId { get; set; }
    }
}
