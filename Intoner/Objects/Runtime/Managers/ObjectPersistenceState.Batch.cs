using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Collections.Immutable;

namespace Intoner.Objects.Runtime;

/// <summary> owns staged persistent content while the caller holds the object state lock </summary>
internal interface IObjectPersistenceBatch : IDisposable
{
    /// <summary> writes each affected layout once, then publishes standalone objects and groups together </summary>
    /// <returns> success, storage failure after rollback, or recovery required if file rollback failed </returns>
    PersistentMutationStatus Commit();

    /// <summary> restores captured owner contents after a committed batch cannot finish its runtime changes </summary>
    PersistentMutationStatus RestoreCommitted();
}

internal sealed partial class ObjectPersistenceState
{
    private readonly Dictionary<Guid, SceneGroupSnapshot> _standaloneGroups = [];
    private PersistenceBatch? _batch;

    public IObjectPersistenceBatch BeginBatch()
    {
        if (_batch is not null)
        {
            throw new InvalidOperationException("object persistence batches cannot be nested");
        }

        IReadOnlyList<ObjectLayoutSnapshot> layouts = _layoutManager.GetLayouts();
        PersistenceBatch batch = new(this,
            _standaloneSnapshots.Values.Concat(layouts.SelectMany(static layout => layout.Objects)),
            _standaloneGroups.Values.Concat(layouts.SelectMany(static layout => layout.Groups)));
        _batch = batch;
        return batch;
    }

    public IReadOnlyList<SceneGroupSnapshot> GetPersistedGroups()
    {
        lock (_stateLock)
        {
            return _batch is not null
                ? _batch.Groups.Values.Where(group => !group.LayoutId.HasValue || _layoutManager.IsLayoutEnabled(group.LayoutId.Value)).ToArray()
                : _standaloneGroups.Values.Concat(_layoutManager.GetEnabledLayouts().SelectMany(static layout => layout.Groups)).ToArray();
        }
    }

    public IReadOnlyList<SceneGroupSnapshot> GetPersistedGroups(Guid? layoutId)
    {
        lock (_stateLock)
        {
            if (!IsCurrentOwner(layoutId))
            {
                return [];
            }

            if (_batch is not null)
            {
                return _batch.Groups.Values.Where(group => group.LayoutId == layoutId).ToArray();
            }

            if (!layoutId.HasValue)
            {
                return GetStandaloneGroups();
            }

            return _layoutManager.TryGetLayout(layoutId.Value, out ObjectLayoutSnapshot layout) ? layout.Groups : [];
        }
    }

    public IReadOnlyList<SceneGroupSnapshot> GetStandaloneGroups()
    {
        lock (_stateLock)
            return _batch is null ? _standaloneGroups.Values.ToArray()
                : _batch.Groups.Values.Where(static group => !group.LayoutId.HasValue).ToArray();
    }

    public IReadOnlyList<ObjectLayoutSnapshot> GetStoredLayouts()
    {
        lock (_stateLock)
        {
            IReadOnlyList<ObjectLayoutSnapshot> layouts = _layoutManager.GetLayouts();
            return _batch?.BuildLayoutSnapshots(layouts) ?? layouts;
        }
    }

    public bool TryApplyGroupChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        if (_batch is null)
        {
            throw new InvalidOperationException("group changes require an object persistence batch");
        }

        return _batch.ApplyGroups(changes);
    }

    private sealed class PersistenceBatch : IObjectPersistenceBatch
    {
        private readonly ObjectPersistenceState _owner;
        private readonly HashSet<Guid> _layoutIds = [];
        private bool _standaloneChanged;
        private bool _committed;
        private bool _commitStarted;
        private bool _disposed;
        private readonly ObjectSnapshot[] _previousStandaloneObjects;
        private readonly SceneGroupSnapshot[] _previousStandaloneGroups;
        private readonly List<ObjectLayoutSnapshot> _previousLayouts = [];

        public PersistenceBatch(ObjectPersistenceState owner, IEnumerable<ObjectSnapshot> objects, IEnumerable<SceneGroupSnapshot> groups)
        {
            _owner = owner;
            Objects = objects.ToDictionary(static snapshot => snapshot.Id);
            Groups = groups.ToDictionary(static group => group.Id);
            _previousStandaloneObjects = owner._standaloneSnapshots.Values.ToArray();
            _previousStandaloneGroups = owner._standaloneGroups.Values.ToArray();
        }

        public Dictionary<Guid, ObjectSnapshot> Objects { get; }
        public Dictionary<Guid, SceneGroupSnapshot> Groups { get; }

        public IReadOnlyList<ObjectLayoutSnapshot> BuildLayoutSnapshots(IReadOnlyList<ObjectLayoutSnapshot> layouts)
        {
            if (_layoutIds.Count == 0)
            {
                return layouts;
            }

            ILookup<Guid?, ObjectSnapshot> objects = Objects.Values
                .Where(snapshot => snapshot.LayoutId is Guid id && _layoutIds.Contains(id))
                .ToLookup(static snapshot => snapshot.LayoutId);
            ILookup<Guid?, SceneGroupSnapshot> groups = Groups.Values
                .Where(group => group.LayoutId is Guid id && _layoutIds.Contains(id))
                .ToLookup(static group => group.LayoutId);
            return layouts.Select(layout => _layoutIds.Contains(layout.Id)
                ? layout with { Objects = objects[layout.Id].ToImmutableArray(), Groups = groups[layout.Id].ToImmutableArray() }
                : layout).ToArray();
        }

        public PersistentMutationStatus Upsert(IReadOnlyList<ObjectSnapshot> snapshots)
        {
            HashSet<Guid> ids = [];
            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (snapshot.Id == Guid.Empty || !ids.Add(snapshot.Id) || !_owner.HasStoredOwner(snapshot.LayoutId))
                {
                    return PersistentMutationStatus.InvalidRequest;
                }
            }

            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (Objects.TryGetValue(snapshot.Id, out ObjectSnapshot? previous))
                {
                    Touch(previous.LayoutId);
                }
                Objects[snapshot.Id] = snapshot;
                Touch(snapshot.LayoutId);
            }
            return PersistentMutationStatus.Success;
        }

        public bool Remove(Guid id)
        {
            if (!Objects.Remove(id, out ObjectSnapshot? snapshot))
            {
                return false;
            }
            Touch(snapshot.LayoutId);
            return true;
        }

        public bool ApplyGroups(IReadOnlyList<SceneItemSnapshotChange> changes)
        {
            HashSet<Guid> ids = [];
            foreach (SceneItemSnapshotChange change in changes)
            {
                if (!change.TryGetSnapshots(out SceneGroupSnapshot? before, out SceneGroupSnapshot? after))
                {
                    return false;
                }
                Guid id = (after ?? before)!.Id;
                if (id == Guid.Empty || !ids.Add(id)
                    || (before is null ? Groups.ContainsKey(id) : Groups.GetValueOrDefault(id) != before))
                {
                    return false;
                }
                if (after is not null && !_owner.HasStoredOwner(after.LayoutId))
                {
                    return false;
                }
            }

            foreach (SceneItemSnapshotChange change in changes)
            {
                if (change.Before is SceneGroupSnapshot before)
                    Touch(before.LayoutId);
                if (change.After is SceneGroupSnapshot after)
                {
                    Groups[after.Id] = after;
                    Touch(after.LayoutId);
                }
                else
                {
                    Groups.Remove(change.Before!.Id);
                }
            }
            return true;
        }

        public PersistentMutationStatus Commit()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed)
                throw new InvalidOperationException("object persistence batch was already committed");

            ILookup<Guid?, ObjectSnapshot> objects = Objects.Values.ToLookup(static snapshot => snapshot.LayoutId);
            ILookup<Guid?, SceneGroupSnapshot> groups = Groups.Values.ToLookup(static group => group.LayoutId);
            List<ObjectLayoutContentUpdate> updates = new(_layoutIds.Count);
            foreach (Guid id in _layoutIds)
            {
                if (!_owner._layoutManager.TryGetLayout(id, out ObjectLayoutSnapshot layout))
                    return PersistentMutationStatus.NotFound;
                _previousLayouts.Add(layout);
                ObjectSnapshot[] ownerObjects = objects[id].ToArray();
                SceneGroupSnapshot[] ownerGroups = groups[id].ToArray();
                if (!SceneGroupUtility.TryValidateOwner(id, ownerObjects, ownerGroups, out _))
                    return PersistentMutationStatus.InvalidRequest;
                updates.Add(new ObjectLayoutContentUpdate(id, ownerObjects, layout.Folders) { Groups = ownerGroups });
            }

            ObjectSnapshot[] standaloneObjects = objects[null].ToArray();
            SceneGroupSnapshot[] standaloneGroups = groups[null].ToArray();
            if (!SceneGroupUtility.TryValidateOwner(null, standaloneObjects, standaloneGroups, out _))
                return PersistentMutationStatus.InvalidRequest;
            _commitStarted = true;
            PersistentMutationStatus status = _owner._layoutManager.ReplaceLayoutContents(updates);
            if (status != PersistentMutationStatus.Success)
                return status;

            if (_standaloneChanged)
            {
                _owner.ReplaceStandaloneContent(standaloneObjects, standaloneGroups);
            }
            _committed = true;
            return PersistentMutationStatus.Success;
        }

        public PersistentMutationStatus RestoreCommitted()
        {
            if (!_commitStarted)
                return PersistentMutationStatus.Success;
            try
            {
                return _owner._layoutManager.RestoreLayouts(_previousLayouts) == PersistentMutationStatus.Success
                    ? PersistentMutationStatus.Success : PersistentMutationStatus.RecoveryRequired;
            }
            finally
            {
                _owner.ReplaceStandaloneContent(_previousStandaloneObjects, _previousStandaloneGroups);
                _committed = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner._batch = null;
        }

        private void Touch(Guid? layoutId)
        {
            if (layoutId is Guid id)
                _layoutIds.Add(id);
            else
                _standaloneChanged = true;
        }
    }
}
