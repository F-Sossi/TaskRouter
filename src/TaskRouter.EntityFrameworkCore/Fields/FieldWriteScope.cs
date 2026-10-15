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

    private FieldWriteScope(DbContext db, IDbContextTransaction? transaction, string savepoint)
    {
        _db = db;
        _transaction = transaction;
        _savepoint = savepoint;
    }

    public static async Task<FieldWriteScope> BeginAsync(IWorkflowDbContext workflowDb, CancellationToken ct)
    {
        var db = workflowDb as DbContext
            ?? throw new InvalidOperationException("Field updates need IWorkflowDbContext to be an EF Core DbContext.");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var transaction = db.Database.CurrentTransaction;
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

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            // A snapshot-tracked entity only reports a property write when DetectChanges
            // runs: SaveChanges and Entry() run it, a plain assignment does not. A write the
            // host made but never saved would otherwise raise no StateChanged event, never
            // reach _touched, and be written by the engine's next save. Detect now, while
            // still subscribed, so every pending modification is recorded before we revert.
            _db.ChangeTracker.DetectChanges();
        }

        // Unsubscribe before reverting: the reloads and detaches below raise events of
        // their own, and _touched must not change while it is being enumerated.
        _db.ChangeTracker.Tracked -= OnTracked;
        _db.ChangeTracker.StateChanged -= OnStateChanged;

        if (_completed)
        {
            return;
        }

        if (_transaction is not null)
        {
            await _transaction.RollbackToSavepointAsync(_savepoint).ConfigureAwait(false);
        }

        foreach (var (entity, wasNew) in _touched)
        {
            var entry = _db.Entry(entity);

            if (wasNew)
            {
                entry.State = EntityState.Detached;
            }
            else if (entry.State != EntityState.Detached)
            {
                // Detaches the entity if its row no longer exists, which is right too.
                await entry.ReloadAsync().ConfigureAwait(false);
            }

            // An existing entity the host deleted and saved inside the scope is Detached by
            // now. The rollback restores its row; it simply stays untracked, so the engine's
            // next save cannot touch it.
        }
    }

    private void OnTracked(object? sender, EntityTrackedEventArgs e) =>
        _touched.TryAdd(e.Entry.Entity, !e.FromQuery);

    private void OnStateChanged(object? sender, EntityStateChangedEventArgs e) =>
        _touched.TryAdd(e.Entry.Entity, false);
}
