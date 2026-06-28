using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Stores persisted local object state and resolves composed persisted snapshots.
/// </summary>
internal interface IObjectPersistenceState
{
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
    void UpsertPersistedSnapshot(ObjectSnapshot snapshot);

    /// <summary>
    /// Removes one persisted snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to remove.</param>
    void RemovePersistedSnapshot(ObjectSnapshot snapshot);

    /// <summary>
    /// Replaces one persisted snapshot with another, moving between standalone and layout storage when needed.
    /// </summary>
    /// <param name="previousSnapshot">The previous persisted snapshot.</param>
    /// <param name="nextSnapshot">The replacement snapshot.</param>
    void ReplacePersistedSnapshot(ObjectSnapshot previousSnapshot, ObjectSnapshot nextSnapshot);

    /// <summary>
    /// Clears all standalone persisted objects.
    /// </summary>
    void ClearStandaloneSnapshots();

}

internal sealed class ObjectPersistenceState : IObjectPersistenceState
{
    private readonly Lock                 _stateLock;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectKindService   _objectKindService;
    private readonly IObjectFolderService _objectFolderService;

    private readonly Dictionary<Guid, ObjectSnapshot> _standaloneSnapshots = [];
    private readonly Dictionary<ObjectKind, int> _kindCounters = [];

    public ObjectPersistenceState(
        ObjectStateLock stateLock,
        IObjectLayoutManager layoutManager,
        IObjectKindService objectKindService,
        IObjectFolderService objectFolderService)
    {
        _stateLock = stateLock.Value;
        _layoutManager = layoutManager;
        _objectKindService = objectKindService;
        _objectFolderService = objectFolderService;
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

    public bool TryGetSceneSnapshot(Guid id, IReadOnlyList<ObjectTemporaryLayoutSnapshot> temporaryLayouts, out ObjectSnapshot snapshot)
    {
        if (TryGetPersistedSnapshot(id, out snapshot))
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
                || defaultLayout.Folders.Count > 0
                || defaultLayout.FolderColors.Count > 0);
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

    public void UpsertPersistedSnapshot(ObjectSnapshot snapshot)
    {
        if (snapshot.LayoutId.HasValue)
        {
            UpsertLayoutSnapshot(snapshot);
            return;
        }

        lock (_stateLock)
        {
            _standaloneSnapshots[snapshot.Id] = snapshot;
        }
    }

    public void RemovePersistedSnapshot(ObjectSnapshot snapshot)
    {
        if (snapshot.LayoutId.HasValue)
        {
            RemoveLayoutSnapshot(snapshot.LayoutId.Value, snapshot.Id);
            return;
        }

        lock (_stateLock)
        {
            _standaloneSnapshots.Remove(snapshot.Id);
        }
    }

    public void ReplacePersistedSnapshot(ObjectSnapshot previousSnapshot, ObjectSnapshot nextSnapshot)
    {
        if (previousSnapshot.LayoutId == nextSnapshot.LayoutId)
        {
            UpsertPersistedSnapshot(nextSnapshot);
            return;
        }

        RemovePersistedSnapshot(previousSnapshot);
        UpsertPersistedSnapshot(nextSnapshot);
    }

    public void ClearStandaloneSnapshots()
    {
        lock (_stateLock)
        {
            _standaloneSnapshots.Clear();
        }
        _objectFolderService.ClearStandaloneState();
    }

    private void UpsertLayoutSnapshot(ObjectSnapshot snapshot)
    {
        if (!snapshot.LayoutId.HasValue
            || !_layoutManager.TryGetLayout(snapshot.LayoutId.Value, out var layout))
        {
            return;
        }

        var nextObjects = layout.Objects
            .Where(entry => entry.Id != snapshot.Id)
            .Append(snapshot)
            .OrderBy(static entry => entry.CreatedAtUtc)
            .ToList();
        _layoutManager.TryReplaceLayoutObjects(snapshot.LayoutId.Value, nextObjects);
    }

    private void RemoveLayoutSnapshot(Guid layoutId, Guid objectId)
    {
        if (!_layoutManager.TryGetLayout(layoutId, out var layout))
        {
            return;
        }

        var nextObjects = layout.Objects
            .Where(entry => entry.Id != objectId)
            .OrderBy(static entry => entry.CreatedAtUtc)
            .ToList();
        _layoutManager.TryReplaceLayoutObjects(layoutId, nextObjects);
    }

    private static List<ObjectSnapshot> OrderDistinctSnapshots(IEnumerable<ObjectSnapshot> snapshots)
        => snapshots
            .GroupBy(static snapshot => snapshot.Id)
            .Select(static group => group.Last())
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToList();
}

