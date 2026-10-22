using System.Collections;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace TaskRouter.EntityFrameworkCore;

/// <summary>
/// What a DbContext's change tracker held at one moment, so it can be put back after a
/// rollback.
///
/// <para>A database rollback restores rows, not the tracker. Without this, a completion
/// that failed after saving left its task tracked as Completed while the row said open, and
/// the next operation on the same context refused it as "already completed". The context is
/// shared with the host, so <c>ChangeTracker.Clear()</c> is not an option: it would throw
/// away the host's own pending changes along with the engine's.</para>
///
/// <para>Restores each entry's state, current and original values, which properties were
/// modified, temporary keys, and navigation membership. Entities first tracked after the
/// checkpoint are detached.</para>
///
/// <para>Taking one has to be cheap, because every engine operation takes one and a
/// long-lived context (one per Blazor Server circuit) can track thousands of rows. So only
/// entries with pending changes -- Added, Modified, Deleted, usually a handful -- are copied
/// up front. An Unchanged entry is copied only if it changes: the first time it leaves
/// Unchanged, its original values are still exactly what it held at the checkpoint, and that
/// is when they are taken. Restoring is the expensive part, and it only happens on failure.</para>
///
/// <para>Dispose it once the transaction has ended, to stop listening for changes.</para>
/// </summary>
internal sealed class WorkflowTrackingCheckpoint : IDisposable
{
    private readonly DbContext _context;

    /// <summary>Every entity tracked at the checkpoint, with its type and the state it had.</summary>
    private readonly Dictionary<object, (IEntityType Type, EntityState State)> _tracked =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Full copies: pending entries at once, Unchanged ones when they first change.</summary>
    private readonly Dictionary<object, EntrySnapshot> _snapshots = new(ReferenceEqualityComparer.Instance);

    public WorkflowTrackingCheckpoint(DbContext context)
    {
        _context = context;

        // Entries() detects changes first, so an edit the host made but EF has not yet
        // noticed shows up as Modified and is copied now, not mistaken for Unchanged.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            _tracked[entry.Entity] = (entry.Metadata, entry.State);

            if (entry.State != EntityState.Unchanged)
            {
                _snapshots[entry.Entity] = Capture(entry, entry.State, fromOriginal: false);
            }
        }

        context.ChangeTracker.StateChanged += OnStateChanged;
    }

    public void Dispose() => _context.ChangeTracker.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, EntityStateChangedEventArgs e)
    {
        if (e.OldState == EntityState.Unchanged && !_snapshots.ContainsKey(e.Entry.Entity))
        {
            // Only entities tracked at the checkpoint matter; anything newer is detached on
            // restore. An entity that was Unchanged then has original values equal to its
            // checkpoint values until a save accepts new ones -- and a save cannot do that
            // without first moving it out of Unchanged, which is this event.
            if (_tracked.TryGetValue(e.Entry.Entity, out var at) && at.State == EntityState.Unchanged)
            {
                _snapshots[e.Entry.Entity] = Capture(e.Entry, EntityState.Unchanged, fromOriginal: true);
            }
        }
    }

    private static EntrySnapshot Capture(EntityEntry entry, EntityState state, bool fromOriginal)
    {
        var original = Snapshot(entry.OriginalValues);

        return new EntrySnapshot(
            state,
            fromOriginal ? original : Snapshot(entry.CurrentValues),
            original,
            fromOriginal ? [] : entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToHashSet(),
            fromOriginal ? [] : entry.Properties.Where(p => p.IsTemporary).Select(p => p.Metadata.Name).ToHashSet(),
            fromOriginal
                ? []
                : entry.Properties.Where(p => p.IsTemporary && p.Metadata.PropertyInfo is not null)
                    .ToDictionary(p => p.Metadata.Name, p => p.Metadata.PropertyInfo!.GetValue(entry.Entity)));
    }

    /// <summary>
    /// A copy that cannot be changed through the entity: mutable values (byte arrays, owned
    /// collections) are snapshotted by their comparer, not shared by reference.
    /// </summary>
    private static PropertyValues Snapshot(PropertyValues values)
    {
        var snapshot = values.Clone();

        foreach (var property in values.Properties)
        {
            snapshot[property] = property.GetValueComparer()?.Snapshot(values[property]) ?? values[property];
        }

        return snapshot;
    }

    public void Restore()
    {
        // Picks up edits made since the last detection, so an Unchanged entity the failed
        // work modified without saving is copied (by OnStateChanged) before it is reset.
        _context.ChangeTracker.DetectChanges();

        var kept = new HashSet<object>(_tracked.Keys, ReferenceEqualityComparer.Instance);
        var loaded = new Dictionary<object, List<string>>(ReferenceEqualityComparer.Instance);

        // Unhook anything created after the checkpoint from the entities being kept, before
        // the tracker forgets which navigations exist. Re-attaching below lets EF's fix-up
        // rebuild every relationship between the kept entities from their keys.
        foreach (var entry in _context.ChangeTracker.Entries().Where(e => kept.Contains(e.Entity)).ToList())
        {
            loaded[entry.Entity] = [.. entry.Navigations.Where(n => n.IsLoaded).Select(n => n.Metadata.Name)];

            foreach (var navigation in entry.Navigations)
            {
                DropNewcomers(entry.Entity, navigation, kept);
            }
        }

        var autoDetect = _context.ChangeTracker.AutoDetectChangesEnabled;

        // Off while the entities are put back, or the tracker would "detect" the restored
        // values as fresh edits half-way through.
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        _context.ChangeTracker.StateChanged -= OnStateChanged;

        try
        {
            _context.ChangeTracker.Clear();

            // First the CLR objects: the host may hold references to them, so they are
            // restored in place rather than replaced. An entity never copied did not change.
            foreach (var (entity, snapshot) in _snapshots)
            {
                foreach (var property in _tracked[entity].Type.GetProperties())
                {
                    if (property.PropertyInfo is { } propertyInfo)
                    {
                        propertyInfo.SetValue(entity, snapshot.Current[property.Name]);
                    }
                    else if (property.FieldInfo is { } fieldInfo)
                    {
                        fieldInfo.SetValue(entity, snapshot.Current[property.Name]);
                    }
                }
            }

            // Then re-attach each one in the state it had, with the same original values and
            // modified flags, so the next SaveChanges writes exactly what it would have.
            foreach (var (entity, (_, state)) in _tracked)
            {
                var entry = _context.Entry(entity);

                if (!_snapshots.TryGetValue(entity, out var snapshot))
                {
                    entry.State = state;
                    RestoreLoaded(entry, loaded);
                    continue;
                }

                entry.CurrentValues.SetValues(snapshot.Current);
                entry.State = snapshot.State == EntityState.Added ? EntityState.Added : EntityState.Unchanged;
                entry.OriginalValues.SetValues(snapshot.Original);

                foreach (var property in entry.Properties)
                {
                    property.IsTemporary = snapshot.Temporary.Contains(property.Metadata.Name);

                    if (snapshot.State != EntityState.Added)
                    {
                        property.IsModified = snapshot.Modified.Contains(property.Metadata.Name);
                    }
                }

                if (snapshot.State == EntityState.Deleted)
                {
                    entry.State = EntityState.Deleted;
                }

                RestoreLoaded(entry, loaded);
            }

            // Last, temporary keys: attaching assigns fresh ones, and an Added entity must
            // keep the value the host may already have used to link others to it.
            foreach (var (entity, snapshot) in _snapshots)
            {
                foreach (var temporary in snapshot.TemporaryClrValues)
                {
                    _tracked[entity].Type.FindProperty(temporary.Key)!.PropertyInfo!
                        .SetValue(entity, temporary.Value);
                }
            }
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }
    }

    private static void RestoreLoaded(EntityEntry entry, Dictionary<object, List<string>> loaded)
    {
        foreach (var name in loaded.GetValueOrDefault(entry.Entity) ?? [])
        {
            entry.Navigation(name).IsLoaded = true;
        }
    }

    /// <summary>Removes references to entities that did not exist at the checkpoint.</summary>
    private static void DropNewcomers(object entity, NavigationEntry navigation, HashSet<object> kept)
    {
        var metadata = navigation.Metadata;

        if (metadata.IsCollection)
        {
            if (navigation.CurrentValue is not IEnumerable current)
            {
                return;
            }

            var accessor = metadata.GetCollectionAccessor()!;

            foreach (var member in current.Cast<object>().Where(m => !kept.Contains(m)).ToArray())
            {
                accessor.Remove(entity, member);
            }
        }
        else if (navigation.CurrentValue is { } target && !kept.Contains(target))
        {
            if (metadata.PropertyInfo is { } propertyInfo)
            {
                propertyInfo.SetValue(entity, null);
            }
            else if (metadata.FieldInfo is { } fieldInfo)
            {
                fieldInfo.SetValue(entity, null);
            }
        }
    }

    private sealed record EntrySnapshot(
        EntityState State, PropertyValues Current, PropertyValues Original,
        HashSet<string> Modified, HashSet<string> Temporary, Dictionary<string, object?> TemporaryClrValues);
}
