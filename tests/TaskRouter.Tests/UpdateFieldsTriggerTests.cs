using Microsoft.EntityFrameworkCore;

using DemoDocuments.Server.Domain;

using TaskRouter.EntityFrameworkCore;
using TaskRouter.EntityFrameworkCore.Fields;

namespace TaskRouter.Tests;

[TestClass]
public class UpdateFieldsTriggerTests
{
    private TestHost _host = null!;

    [TestInitialize]
    public async Task Setup() => _host = await TestHost.CreateAsync(withTriggers: true);

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
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await Assert.ThrowsAsync<Exception>(() => WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            document.Description = "engine wrote this";

            var scope = await FieldWriteScope.BeginAsync(db, ct);
            InvalidOperationException? failure = null;
            try
            {
                try
                {
                    document.Comments = "saved by the host";
                    await db.SaveChangesAsync(ct);
                    await db.Database.ExecuteSqlRawAsync("SET XACT_ABORT ON; SELECT 1/0;", ct);
                }
                catch (Exception ex)
                {
                    failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => scope.AbandonAsync(ex));
                    StringAssert.StartsWith(failure.Message, ex.Message);
                    StringAssert.Contains(failure.Message, "so the whole operation was rolled back");
                    Assert.IsInstanceOfType<AggregateException>(failure.InnerException);
                }
            }
            finally
            {
                await scope.DisposeAsync();
                await scope.DisposeAsync(); // idempotent
            }

            Assert.IsNotNull(failure);

            // As under LogAndContinue: the workflow carries on and writes more. None of it may land.
            document.Revision = "Z";
        }));

        db.ChangeTracker.Clear();
        var stored = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.IsNull(stored.Description);
        Assert.IsNull(stored.Comments);
        Assert.AreEqual("-", stored.Revision);
    }

    [TestMethod]
    public async Task Describe_names_the_entity_type_and_key()
    {
        var db = _host.Db;
        var document = await NewChangeRequestAsync();

        await WorkflowTransaction.ExecuteAsync(db, async ct =>
        {
            await using var scope = await FieldWriteScope.BeginAsync(db, ct);
            Assert.AreEqual($"ChangeRequest {document.Id}", scope.Describe(document));
        });
    }

    // ───────────────────────────── Fixtures ─────────────────────────────

    private async Task<Section> NewSectionAsync()
    {
        var section = new Section { Code = $"S-{Guid.NewGuid():N}"[..10], Name = "Fixture section" };
        _host.Db.Sections.Add(section);
        await _host.Db.SaveChangesAsync();
        return section;
    }

    private async Task<ChangeRequest> NewChangeRequestAsync()
    {
        var document = new ChangeRequest
        {
            Title = "Pump room rewire",
            DocNumber = $"CR-{Guid.NewGuid():N}"[..12],
            Originator = "user-originator"
        };

        _host.Db.ChangeRequests.Add(document);
        await _host.Db.SaveChangesAsync();
        return document;
    }
}
