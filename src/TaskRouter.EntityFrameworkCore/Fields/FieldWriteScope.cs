using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace TaskRouter.EntityFrameworkCore.Fields;

/// <summary>
/// Makes a field update all-or-nothing, even when the failure policy lets the workflow
/// carry on.
///
/// <para>Begins by saving the engine's own pending changes, so that rolling back to the
/// savepoint cannot take those with it. Then watches the change tracker: on abandonment it
/// rolls the database back to the savepoint and returns every entity that changed in
/// between to its stored state — reloading the ones that existed, detaching the ones that
/// were new — so the engine's next save does not quietly write them again.</para>
///
/// <para>Without an ambient transaction there is no savepoint, so writes a host already saved
/// stay saved; the tracker is still reverted. The engine always runs triggers inside one.</para>
///
/// <para>The savepoint nests safely with EF's own: inside a user transaction SaveChanges
/// takes an automatic savepoint of its own (under a different name) within ours, and
/// rolling back to ours discards everything after it, EF's included.</para>
/// </summary>
internal sealed class FieldWriteScope : IAsyncDisposable
{
    private readonly DbContext _db;
    private readonly IDbContextTransaction? _transaction;
    private readonly string _savepoint;
    private readonly Dictionary<object, bool> _touched = new(ReferenceEqualityComparer.Instance);
    private bool _completed;
    private bool _abandoned;

    private FieldWriteScope(DbContext db, IDbContextTransaction? transaction, string savepoint)
    {
        _db = db;
        _transaction = transaction;
        _savepoint = savepoint;
    }

    /// <summary>
    /// Saves the engine's pending work, takes a savepoint, and starts watching the change
    /// tracker.
    ///
    /// <para>The flush is a real SaveChanges, so the host's SaveChanges interceptors run one
    /// extra time, and a failure in the engine's own pending work surfaces here, as this
    /// trigger's failure, rather than at the engine's final save.</para>
    /// </summary>
    public static async Task<FieldWriteScope> BeginAsync(IWorkflowDbContext workflowDb, CancellationToken ct)
    {
        var db = workflowDb as DbContext
            ?? throw new InvalidOperationException("Field updates need IWorkflowDbContext to be an EF Core DbContext.");

        var transaction = db.Database.CurrentTransaction;

        // Checked before the flush, so a refusal writes nothing.
        if (transaction is not null && !transaction.SupportsSavepoints)
        {
            throw new InvalidOperationException(
                "Field updates need database savepoints, which this connection does not support. "
                + "SQL Server disables them when MultipleActiveResultSets=True; turn MARS off for the workflow's DbContext.");
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var savepoint = "taskrouter_fields_" + Guid.NewGuid().ToString("N")[..8];

        if (transaction is not null)
        {
            await transaction.CreateSavepointAsync(savepoint, ct).ConfigureAwait(false);
        }

        var scope = new FieldWriteScope(db, transaction, savepoint);
        db.ChangeTracker.Tracked += scope.OnTracked;
        db.ChangeTracker.StateChanged += scope.OnStateChanged;
        return scope;
    }

    /// <summary>A record's identity for the change summary: its CLR type and key values.</summary>
    public string Describe(object entity)
    {
        var entry = _db.Entry(entity);
        var key = entry.Metadata.FindPrimaryKey();

        var id = key is null
            ? "?"
            : string.Join(",", key.Properties.Select(p => entry.Property(p.Name).CurrentValue));

        return $"{entry.Metadata.ClrType.Name} {id}";
    }

    /// <summary>
    /// Saves inside the savepoint — so a database refusal (a constraint, say) is still this
    /// update's failure and still undone — then keeps everything.
    /// </summary>
    public async Task SaveAndCompleteAsync(CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // SQL Server cannot release a savepoint; EF's SQL Server transaction makes this a
        // no-op there. Providers that can release one do, so the call stays.
        if (_transaction is not null)
        {
            await _transaction.ReleaseSavepointAsync(_savepoint, ct).ConfigureAwait(false);
        }

        _completed = true;
    }

    /// <summary>
    /// Undoes everything the scope saw: rolls the database back to the savepoint and returns
    /// the tracked entities to their stored state. Returns normally when the undo succeeds,
    /// so the caller can rethrow <paramref name="cause"/> itself.
    ///
    /// <para>If the undo itself fails, the scope's writes may be half-reverted, and letting
    /// the workflow carry on would commit them. So the whole transaction is rolled back,
    /// which makes the caller's final save and commit fail even under LogAndContinue, and a
    /// <see cref="FieldUpdateUndoFailedException"/> carrying both errors is thrown.</para>
    ///
    /// <para>Consequences of that composite worth knowing: it is not a DbUpdateException, so
    /// the execution strategy will not retry a deadlock (1205) that hit the host's write; an
    /// OperationCanceledException cause is wrapped in it too, rather than surfacing as
    /// cancellation; and when the caller started the transaction itself, the caller's own
    /// commit will fail.</para>
    ///
    /// <para>Deliberately takes no CancellationToken: the cause may itself be cancellation,
    /// and an undo cut short by it would leave exactly the partial state this prevents.</para>
    /// </summary>
    internal async Task AbandonAsync(Exception? cause)
    {
        if (_completed || _abandoned)
        {
            return;
        }

        _abandoned = true;

        try
        {
            await UndoAsync().ConfigureAwait(false);
        }
        catch (Exception undoError)
        {
            if (_transaction is not null)
            {
                try
                {
                    // Rolls back the DbTransaction underneath EF rather than through
                    // IDbContextTransaction: EF's own rollback would also clear
                    // Database.CurrentTransaction, and the caller's final SaveChanges would
                    // then run in a fresh transaction of its own and commit whatever the
                    // tracker still holds. Left in place, the dead transaction makes that
                    // save and the commit fail instead.
                    await _transaction.GetDbTransaction().RollbackAsync().ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Best effort: the undo error is what gets reported.
                catch (Exception)
                {
                    // The rollback itself failed. Closing the connection makes the server
                    // roll back whatever transaction it still holds, so the message below
                    // stays true; the caller's next save then fails on the closed transaction.
                    try
                    {
                        await _db.Database.GetDbConnection().CloseAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
#pragma warning restore CA1031
            }

            throw new FieldUpdateUndoFailedException(
                $"{cause?.Message ?? "The field update was abandoned"}; undoing it also failed ({undoError.Message}), "
                + "so the whole operation was rolled back",
                cause is null ? undoError : new AggregateException(cause, undoError));
        }
    }

    /// <summary>
    /// Undoes the scope's writes unless it completed or was already abandoned. Safe to call
    /// more than once. Like <see cref="AbandonAsync"/>, passes no CancellationToken to the
    /// undo.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_completed || _abandoned)
        {
            Unsubscribe();
            return;
        }

        await AbandonAsync(null).ConfigureAwait(false);
    }

    private async Task UndoAsync()
    {
        try
        {
            // A snapshot-tracked entity only reports a property write when DetectChanges
            // runs: SaveChanges and Entry() run it, a plain assignment does not. A write the
            // host made but never saved would otherwise raise no StateChanged event, never
            // reach _touched, and be written by the engine's next save. Detect now, while
            // still subscribed, so every pending modification is recorded before we revert.
            _db.ChangeTracker.DetectChanges();
        }
        finally
        {
            // Unsubscribe before reverting: the reloads and detaches below raise events of
            // their own, and _touched must not change while it is being enumerated. In a
            // finally, so a throwing DetectChanges cannot leak the handlers.
            Unsubscribe();
        }

        if (_transaction is not null)
        {
            await _transaction.RollbackToSavepointAsync(_savepoint).ConfigureAwait(false);
        }

        // New entities first: detaching is in memory and cannot fail.
        foreach (var (entity, wasNew) in _touched)
        {
            if (wasNew)
            {
                DetachNew(_db.Entry(entity));
            }
        }

        List<Exception>? errors = null;

        foreach (var (entity, wasNew) in _touched)
        {
            if (wasNew)
            {
                continue;
            }

            var entry = _db.Entry(entity);

            try
            {
                await RevertExistingAsync(entry).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Fail safe: an entity we could not reload must not stay tracked with the
                // scope's values, or the engine's next save would write them.
                entry.State = EntityState.Detached;
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
        {
            throw errors.Count == 1 ? errors[0] : new AggregateException(errors);
        }
    }

    /// <summary>
    /// Detaches a new entity and takes it out of the navigations that point at it.
    ///
    /// <para>EF only fixes navigations up when an Added entity is detached. One the host
    /// already saved is Unchanged by now, so detaching it would leave it sitting in, say,
    /// <c>document.WorkItems</c>, where the engine's next DetectChanges would find it and
    /// write it again.</para>
    /// </summary>
    private static void DetachNew(EntityEntry entry)
    {
        foreach (var navigation in entry.Navigations)
        {
            if (navigation.Metadata.Inverse is not { } inverse || navigation.CurrentValue is not { } current)
            {
                continue;
            }

            var related = navigation.Metadata.IsCollection
                ? ((System.Collections.IEnumerable)current).Cast<object>().ToList()
                : [current];

            foreach (var other in related)
            {
                if (inverse.IsCollection)
                {
                    inverse.GetCollectionAccessor()?.Remove(other, entry.Entity);
                }
                else
                {
                    var back = entry.Context.Entry(other).Navigation(inverse.Name);
                    if (ReferenceEquals(back.CurrentValue, entry.Entity))
                    {
                        back.CurrentValue = null;
                    }
                }
            }
        }

        // A collection with no back-reference (Basket.Fruits with no Fruit.Basket) is not
        // among the entity's own navigations, so look for it from the principal side.
        foreach (var foreignKey in entry.Metadata.GetForeignKeys())
        {
            if (foreignKey.PrincipalToDependent is not { } navigation || foreignKey.DependentToPrincipal is not null)
            {
                continue;
            }

            var principals = entry.Context.ChangeTracker.Entries()
                .Where(e => foreignKey.PrincipalEntityType.IsAssignableFrom(e.Metadata))
                .ToList();

            foreach (var principal in principals)
            {
                if (navigation.IsCollection)
                {
                    navigation.GetCollectionAccessor()?.Remove(principal.Entity, entry.Entity);
                }
                else
                {
                    var reference = principal.Navigation(navigation.Name);
                    if (ReferenceEquals(reference.CurrentValue, entry.Entity))
                    {
                        reference.CurrentValue = null;
                    }
                }
            }
        }

        entry.State = EntityState.Detached;
    }

    private static async Task RevertExistingAsync(EntityEntry entry)
    {
        if (entry.State != EntityState.Detached)
        {
            // Detaches the entity if its row no longer exists, which is right too.
            await entry.ReloadAsync().ConfigureAwait(false);
            return;
        }

        // An existing entity the host deleted and saved inside the scope is Detached by now,
        // and the rollback has restored its row. Track it again with its stored values.
        try
        {
            entry.State = EntityState.Unchanged;
        }
        catch (InvalidOperationException)
        {
            // Another instance with the same key is tracked; that one stands, this stays detached.
            return;
        }

        await entry.ReloadAsync().ConfigureAwait(false);
    }

    private void Unsubscribe()
    {
        _db.ChangeTracker.Tracked -= OnTracked;
        _db.ChangeTracker.StateChanged -= OnStateChanged;
    }

    // FromQuery entities are recorded too, as existing ones: a value read after a host save
    // inside the scope reflects that save, and is stale once the rollback undoes it, so those
    // entities are reloaded along with the ones that changed.
    private void OnTracked(object? sender, EntityTrackedEventArgs e) =>
        _touched.TryAdd(e.Entry.Entity, !e.FromQuery);

    private void OnStateChanged(object? sender, EntityStateChangedEventArgs e) =>
        _touched.TryAdd(e.Entry.Entity, false);
}
