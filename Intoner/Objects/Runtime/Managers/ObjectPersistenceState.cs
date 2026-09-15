using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Stores persisted local object state and resolves composed persisted snapshots.
/// </summary>
internal interface IObjectPersistenceState
{
    /// <summary> gets the published persistent scene revision </summary>
    long Revision { get; }

    /// <summary> captures current persistent objects, folder owners, and their revision under the scene state lock </summary>
    /// <param name="capturedAtUtc"> the capture timestamp in UTC </param>
    /// <returns> the current persistent workspace, excluding unloaded and temporary layouts </returns>
    ObjectPersistentWorkspaceSnapshot CaptureWorkspace(DateTime capturedAtUtc);

    /// <summary>
    /// Gets persisted standalone objects that are not part of a layout.
    /// </summary>
    /// <returns>The standalone object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetStandaloneSnapshots();

    /// <summary>
    /// Gets all persisted local object snapshots.
    /// </summary>
    /// <returns>The persisted local object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetPersistedSnapshots();

    /// <summary>
    /// Gets the full composed object scene.
    /// </summary>
    /// <param name="temporaryLayouts">The temporary layouts currently loaded into the scene.</param>
    /// <returns>The composed scene object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetSceneSnapshots(IReadOnlyList<ObjectTemporaryLayoutSnapshot> temporaryLayouts);

    /// <summary>
    /// Tries to resolve one persisted local object snapshot.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="snapshot">The resolved persisted snapshot when found.</param>
    /// <returns>True when the object exists in persisted local state.</returns>
    bool TryGetPersistedSnapshot(Guid id, out ObjectSnapshot snapshot);

    /// <summary>
    /// Tries to resolve one snapshot from the current persistent scene.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="snapshot">The resolved standalone or default layout snapshot when found.</param>
    /// <returns>true when the current persistent scene contains the object.</returns>
    bool TryGetCurrentPersistedSnapshot(Guid id, out ObjectSnapshot snapshot);

    /// <summary>
    /// Tries to resolve one scene object snapshot from the composed scene.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="temporaryLayouts">The temporary layouts currently loaded into the scene.</param>
    /// <param name="snapshot">The resolved scene snapshot when found.</param>
    /// <returns>true when the scene contains that object.</returns>
    bool TryGetSceneSnapshot(Guid id, IReadOnlyList<ObjectTemporaryLayoutSnapshot> temporaryLayouts, out ObjectSnapshot snapshot);

    /// <summary>
    /// Tries to resolve the current default layout.
    /// </summary>
    /// <param name="layout">The resolved default layout when one is selected.</param>
    /// <returns>true when a default layout exists.</returns>
    bool TryGetDefaultLayout(out ObjectLayoutSnapshot layout);

    /// <summary>
    /// Checks whether any persisted local object or default layout state exists.
    /// </summary>
    /// <returns>true when any persistent scene state exists.</returns>
    bool HasPersistentSceneState();

    /// <summary>
    /// Checks whether any persisted local objects or layouts exist.
    /// </summary>
    /// <returns>true when any persisted local object state exists.</returns>
    bool HasPersistedObjects();

    /// <summary>
    /// Checks whether any standalone local objects exist.
    /// </summary>
    /// <returns>true when standalone objects are stored.</returns>
    bool HasStandaloneSnapshots();

    /// <summary>
    /// Generates the next default display name for the given object kind.
    /// </summary>
    /// <param name="kind">The object kind to name.</param>
    /// <returns>The generated object name.</returns>
    string NextName(ObjectKind kind);

    /// <summary>
    /// Applies the current default layout id to a standalone snapshot when appropriate.
    /// </summary>
    /// <param name="snapshot">The snapshot to assign.</param>
    /// <returns>The snapshot after default layout assignment.</returns>
    ObjectSnapshot ApplyDefaultLayout(ObjectSnapshot snapshot);

    /// <summary>
    /// Resolves the scene source metadata for the given snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to classify.</param>
    /// <returns>The resolved scene source.</returns>
    ObjectSceneSource ResolveSceneSource(ObjectSnapshot snapshot);

    /// <summary>
    /// Stores or replaces one persisted snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to store.</param>
    /// <returns>true when the snapshot was stored.</returns>
    bool TryUpsertPersistedSnapshot(ObjectSnapshot snapshot);

    /// <summary> stores a batch with one write per layout, publishing standalone state only after all files succeed </summary>
    /// <param name="snapshots"> distinct snapshots whose standalone or layout ownership has not changed </param>
    /// <returns> success or a failure without published changes; recovery required means file rollback failed </returns>
    PersistentMutationStatus UpsertPersistedSnapshots(IReadOnlyList<ObjectSnapshot> snapshots);

    /// <summary>
    /// Removes one persisted snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to remove.</param>
    /// <returns>true when the snapshot was removed.</returns>
    bool TryRemovePersistedSnapshot(ObjectSnapshot snapshot);

    /// <summary>
    /// Replaces one persisted snapshot with another, moving between standalone and layout storage when needed.
    /// </summary>
    /// <param name="previousSnapshot">The previous persisted snapshot.</param>
    /// <param name="nextSnapshot">The replacement snapshot.</param>
    /// <returns>true when the replacement was stored.</returns>
    bool TryReplacePersistedSnapshot(ObjectSnapshot previousSnapshot, ObjectSnapshot nextSnapshot);

    /// <summary>
    /// Clears all standalone persisted objects.
    /// </summary>
    void ClearStandaloneSnapshots();

    /// <summary>
    /// Replaces all standalone persisted objects without changing folder state.
    /// </summary>
    /// <param name="snapshots">The replacement standalone object snapshots.</param>
    void ReplaceStandaloneSnapshots(IReadOnlyList<ObjectSnapshot> snapshots);

}

internal sealed class ObjectPersistenceState : IObjectPersistenceState
{
    private readonly Lock _stateLock;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectKindService _objectKindService;
    private readonly IObjectFolderService _objectFolderService;
    private readonly IObjectRevisionTracker _revisionTracker;

    private readonly Dictionary<Guid, ObjectSnapshot> _standaloneSnapshots = [];
    private readonly Dictionary<ObjectKind, int> _kindCounters = [];

    public ObjectPersistenceState(
        ObjectStateLock stateLock,
        IObjectLayoutManager layoutManager,
        IObjectKindService objectKindService,
        IObjectFolderService objectFolderService,
        IObjectRevisionTracker revisionTracker)
    {
        _stateLock = stateLock.Value;
        _layoutManager = layoutManager;
        _objectKindService = objectKindService;
        _objectFolderService = objectFolderService;
        _revisionTracker = revisionTracker;
    }

    public long Revision
        => _revisionTracker.GetPersistentSceneRevision();

    public ObjectPersistentWorkspaceSnapshot CaptureWorkspace(DateTime capturedAtUtc)
    {
        lock (_stateLock)
        {
            ObjectFolderSceneState folderState = _objectFolderService.CaptureSceneState();
            return new ObjectPersistentWorkspaceSnapshot
            {
                Objects = GetPersistedSnapshots(),
                StandaloneFolders = folderState.StandaloneFolders,
                DefaultLayoutFolders = folderState.DefaultLayoutFolders,
                DefaultLayoutId = folderState.DefaultLayoutId,
                Name = TryGetDefaultLayout(out ObjectLayoutSnapshot layout) ? layout.Name : "Standalone objects",
                Revision = Revision,
                CapturedAtUtc = capturedAtUtc,
            };
        }
    }

    public IReadOnlyList<ObjectSnapshot> GetStandaloneSnapshots()
    {
        lock (_stateLock)
        {
            return OrderDistinctSnapshots(_standaloneSnapshots.Values);
        }
    }

    public IReadOnlyList<ObjectSnapshot> GetPersistedSnapshots()
    {
        List<ObjectSnapshot> objectSnapshots;
        lock (_stateLock)
        {
            objectSnapshots = [.. _standaloneSnapshots.Values];
        }

        if (TryGetDefaultLayout(out var defaultLayout))
        {
            objectSnapshots.AddRange(defaultLayout.Objects);
        }

        return OrderDistinctSnapshots(objectSnapshots);
    }

    public IReadOnlyList<ObjectSnapshot> GetSceneSnapshots(IReadOnlyList<ObjectTemporaryLayoutSnapshot> temporaryLayouts)
    {
        var objectSnapshots = GetPersistedSnapshots().ToList();
        objectSnapshots.AddRange(temporaryLayouts.SelectMany(static layout => layout.Objects));
        return OrderDistinctSnapshots(objectSnapshots);
    }

    public bool TryGetPersistedSnapshot(Guid id, out ObjectSnapshot snapshot)
    {
        lock (_stateLock)
        {
            if (_standaloneSnapshots.TryGetValue(id, out snapshot!))
            {
                return true;
            }
        }

        foreach (var layout in _layoutManager.GetLayouts())
        {
            var layoutSnapshot = layout.Objects.FirstOrDefault(entry => entry.Id == id);
            if (layoutSnapshot is not null)
            {
                snapshot = layoutSnapshot;
                return true;
            }
        }

        snapshot = default!;
        return false;
    }

    public bool TryGetCurrentPersistedSnapshot(Guid id, out ObjectSnapshot snapshot)
    {
        lock (_stateLock)
        {
            if (_standaloneSnapshots.TryGetValue(id, out snapshot!))
            {
                return true;
            }
        }

        if (TryGetDefaultLayout(out var defaultLayout))
        {
            ObjectSnapshot? layoutSnapshot = defaultLayout.Objects.FirstOrDefault(entry => entry.Id == id);
            if (layoutSnapshot is not null)
            {
                snapshot = layoutSnapshot;
                return true;
            }
        }

        snapshot = default!;
        return false;
    }

    public bool TryGetSceneSnapshot(Guid id, IReadOnlyList<ObjectTemporaryLayoutSnapshot> temporaryLayouts, out ObjectSnapshot snapshot)
    {
        if (TryGetCurrentPersistedSnapshot(id, out snapshot))
        {
            return true;
        }

        foreach (var temporaryLayout in temporaryLayouts)
        {
            var temporaryLayoutSnapshot = temporaryLayout.Objects.FirstOrDefault(entry => entry.Id == id);
            if (temporaryLayoutSnapshot is not null)
            {
                snapshot = temporaryLayoutSnapshot;
                return true;
            }
        }

        snapshot = default!;
        return false;
    }

    public bool TryGetDefaultLayout(out ObjectLayoutSnapshot layout)
    {
        var defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        if (defaultLayoutId.HasValue
            && _layoutManager.TryGetLayout(defaultLayoutId.Value, out layout))
        {
            return true;
        }

        layout = null!;
        return false;
    }

    public bool HasPersistentSceneState()
    {
        if (HasStandaloneSnapshots())
        {
            return true;
        }

        if (_objectFolderService.HasStandaloneState())
        {
            return true;
        }

        return TryGetDefaultLayout(out var defaultLayout)
            && (defaultLayout.Objects.Count > 0
                || defaultLayout.Folders.Count > 0);
    }

    public bool HasPersistedObjects()
        => HasStandaloneSnapshots() || _layoutManager.HasAnyObjects();

    public bool HasStandaloneSnapshots()
    {
        lock (_stateLock)
        {
            return _standaloneSnapshots.Count > 0;
        }
    }

    public string NextName(ObjectKind kind)
    {
        lock (_stateLock)
        {
            _kindCounters.TryGetValue(kind, out var currentValue);
            currentValue++;
            _kindCounters[kind] = currentValue;
            return $"{_objectKindService.GetDisplayName(kind)} {currentValue:00}";
        }
    }

    public ObjectSnapshot ApplyDefaultLayout(ObjectSnapshot snapshot)
    {
        if (snapshot.LayoutId.HasValue)
        {
            return snapshot;
        }

        var defaultLayoutId = _layoutManager.GetDefaultLayoutId();
        return defaultLayoutId.HasValue
            ? snapshot with { LayoutId = defaultLayoutId }
            : snapshot;
    }

    public ObjectSceneSource ResolveSceneSource(ObjectSnapshot snapshot)
    {
        if (snapshot.LayoutId.HasValue && _layoutManager.GetDefaultLayoutId() == snapshot.LayoutId)
        {
            return ObjectSceneSource.CreateDefaultLayout(snapshot.LayoutId.Value);
        }

        return ObjectSceneSource.CreateStandalone(snapshot.LayoutId);
    }

    public bool TryUpsertPersistedSnapshot(ObjectSnapshot snapshot)
        => UpsertPersistedSnapshots([snapshot]) == PersistentMutationStatus.Success;

    public PersistentMutationStatus UpsertPersistedSnapshots(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        lock (_stateLock)
        {
            List<ObjectSnapshot> layoutSnapshots = [];
            HashSet<Guid> objectIds = [];
            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (!objectIds.Add(snapshot.Id))
                {
                    return PersistentMutationStatus.InvalidRequest;
                }

                if (snapshot.LayoutId.HasValue)
                {
                    layoutSnapshots.Add(snapshot);
                }
            }

            if (layoutSnapshots.Count > 0)
            {
                PersistentMutationStatus status = _layoutManager.UpsertLayoutObjects(layoutSnapshots);
                if (status != PersistentMutationStatus.Success)
                {
                    return status;
                }
            }

            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (!snapshot.LayoutId.HasValue)
                {
                    _standaloneSnapshots[snapshot.Id] = snapshot;
                }
            }

            return PersistentMutationStatus.Success;
        }
    }

    public bool TryRemovePersistedSnapshot(ObjectSnapshot snapshot)
    {
        if (snapshot.LayoutId.HasValue)
        {
            return TryRemoveLayoutSnapshot(snapshot.LayoutId.Value, snapshot.Id);
        }

        lock (_stateLock)
        {
            _standaloneSnapshots.Remove(snapshot.Id);
        }

        return true;
    }

    public bool TryReplacePersistedSnapshot(ObjectSnapshot previousSnapshot, ObjectSnapshot nextSnapshot)
    {
        if (previousSnapshot.LayoutId != nextSnapshot.LayoutId)
        {
            return false;
        }

        return TryUpsertPersistedSnapshot(nextSnapshot);
    }

    public void ClearStandaloneSnapshots()
    {
        lock (_stateLock)
        {
            _standaloneSnapshots.Clear();
        }
        _objectFolderService.ClearStandaloneState();
    }

    public void ReplaceStandaloneSnapshots(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        lock (_stateLock)
        {
            _standaloneSnapshots.Clear();
            foreach (ObjectSnapshot snapshot in snapshots)
            {
                _standaloneSnapshots[snapshot.Id] = snapshot with { LayoutId = null };
            }
        }
    }

    private bool TryRemoveLayoutSnapshot(Guid layoutId, Guid objectId)
    {
        if (!_layoutManager.TryGetLayout(layoutId, out var layout))
        {
            return false;
        }

        var nextObjects = layout.Objects
            .Where(entry => entry.Id != objectId)
            .OrderBy(static entry => entry.CreatedAtUtc)
            .ToList();
        return _layoutManager.TryReplaceLayoutObjects(layoutId, nextObjects);
    }

    private static List<ObjectSnapshot> OrderDistinctSnapshots(IEnumerable<ObjectSnapshot> snapshots)
        => snapshots
            .GroupBy(static snapshot => snapshot.Id)
            .Select(static group => group.First())
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToList();
}

