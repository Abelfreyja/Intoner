using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Stores persisted local object state and resolves composed persisted snapshots.
/// </summary>
internal interface IObjectPersistenceState
{
    /// <summary>
    /// Stages one scene mutation batch until all runtime steps are ready to commit.
    /// </summary>
    IObjectPersistenceBatch BeginBatch();

    /// <summary>
    /// Gets all saved owners with staged objects and groups when a batch is active.
    /// </summary>
    IReadOnlyList<ObjectLayoutSnapshot> GetStoredLayouts();

    /// <summary>
    /// Gets standalone group metadata, including staged changes.
    /// </summary>
    IReadOnlyList<SceneGroupSnapshot> GetStandaloneGroups();

    /// <summary>
    /// Gets group metadata for standalone storage and enabled layouts.
    /// </summary>
    IReadOnlyList<SceneGroupSnapshot> GetPersistedGroups();

    /// <summary>
    /// Gets group metadata belonging to one enabled layout or standalone storage.
    /// </summary>
    /// <param name="layoutId">Enabled layout id, or null for standalone groups.</param>
    /// <returns>The owner's groups, or an empty list for a disabled or missing layout.</returns>
    IReadOnlyList<SceneGroupSnapshot> GetPersistedGroups(Guid? layoutId);

    /// <summary>
    /// Stages checked group transitions in the current scene mutation batch.
    /// </summary>
    bool TryApplyGroupChanges(IReadOnlyList<Intoner.Scene.SceneItemSnapshotChange> changes);

    /// <summary> gets the published persistent scene revision </summary>
    long Revision { get; }

    /// <summary>
    /// Gets the combined content version for standalone and all saved owners.
    /// </summary>
    long StoredRevision { get; }

    /// <summary>
    /// Checks whether standalone storage or a saved layout exists.
    /// </summary>
    bool HasStoredOwner(Guid? layoutId);

    /// <summary>
    /// Checks whether an owner contributes to the current persisted scene.
    /// </summary>
    bool IsCurrentOwner(Guid? layoutId);

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
    /// Gets persisted objects belonging to one enabled layout or standalone storage.
    /// </summary>
    /// <param name="layoutId">Enabled layout id, or null for standalone objects.</param>
    /// <returns>The owner's snapshots, or an empty list for a disabled or missing layout.</returns>
    IReadOnlyList<ObjectSnapshot> GetPersistedSnapshots(Guid? layoutId);

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
    /// <param name="snapshot">The resolved standalone or enabled layout snapshot when found.</param>
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
    /// Tries to resolve the current creation layout.
    /// </summary>
    /// <param name="layout">The enabled layout selected for new objects.</param>
    /// <returns>true when new objects belong to a saved layout.</returns>
    bool TryGetCreationLayout(out ObjectLayoutSnapshot layout);

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
    /// Gets a detached set of object names for a stored layout or unassigned objects.
    /// </summary>
    HashSet<string> GetObjectNames(Guid? layoutId);

    /// <summary>
    /// Applies the creation target to a new standalone snapshot without changing an existing owner.
    /// </summary>
    /// <param name="snapshot">The snapshot to assign.</param>
    /// <returns>The snapshot after creation layout assignment.</returns>
    ObjectSnapshot ApplyCreationLayout(ObjectSnapshot snapshot);

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
    /// Replaces a batch and moves ownership in the same layout file transaction.
    /// </summary>
    /// <param name="previousSnapshots">Previous snapshots in the same order as their replacements.</param>
    /// <param name="nextSnapshots">Distinct replacements retaining the previous object identities.</param>
    /// <returns>Success, rejection without published changes, or recovery required when file rollback fails.</returns>
    PersistentMutationStatus ReplacePersistedSnapshots(IReadOnlyList<ObjectSnapshot> previousSnapshots, IReadOnlyList<ObjectSnapshot> nextSnapshots);

    /// <summary>
    /// Removes one persisted snapshot from its current stored owner.
    /// </summary>
    /// <param name="id">The identity to remove, this is independent of runtime ownership!</param>
    /// <returns>true when the snapshot was removed.</returns>
    bool TryRemovePersistedSnapshot(Guid id);

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
    /// Validates and replaces standalone objects and groups together without changing folder state.
    /// </summary>
    /// <param name="snapshots">The replacement standalone object snapshots.</param>
    /// <param name="groups">The complete replacement standalone group metadata.</param>
    void ReplaceStandaloneContent(IReadOnlyList<ObjectSnapshot> snapshots, IReadOnlyList<SceneGroupSnapshot> groups);

}

internal sealed partial class ObjectPersistenceState : IObjectPersistenceState
{
    private readonly Lock _stateLock;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectFolderService _objectFolderService;
    private readonly IObjectRevisionTracker _revisionTracker;

    private readonly Dictionary<Guid, ObjectSnapshot> _standaloneSnapshots = [];

    public ObjectPersistenceState(
        ObjectStateLock stateLock,
        IObjectLayoutManager layoutManager,
        IObjectFolderService objectFolderService,
        IObjectRevisionTracker revisionTracker)
    {
        _stateLock = stateLock.Value;
        _layoutManager = layoutManager;
        _objectFolderService = objectFolderService;
        _revisionTracker = revisionTracker;
    }

    public long Revision
        => _revisionTracker.GetPersistentSceneRevision();

    public long StoredRevision
        => unchecked(Revision + _revisionTracker.GetSavedLayoutsRevision());

    public ObjectPersistentWorkspaceSnapshot CaptureWorkspace(DateTime capturedAtUtc)
    {
        lock (_stateLock)
        {
            ObjectFolderSceneState folderState = _objectFolderService.CaptureSceneState();
            return new ObjectPersistentWorkspaceSnapshot
            {
                StandaloneObjects = GetStandaloneSnapshots(),
                StandaloneGroups = GetStandaloneGroups(),
                StandaloneFolders = folderState.StandaloneFolders,
                Layouts = _layoutManager.GetEnabledLayouts(),
                CreationLayoutId = _layoutManager.GetCreationLayoutId(),
                Revision = Revision,
                CapturedAtUtc = capturedAtUtc,
            };
        }
    }

    public IReadOnlyList<ObjectSnapshot> GetStandaloneSnapshots()
    {
        lock (_stateLock)
        {
            return OrderDistinctSnapshots(_batch is null
                ? _standaloneSnapshots.Values
                : _batch.Objects.Values.Where(static snapshot => !snapshot.LayoutId.HasValue));
        }
    }

    public IReadOnlyList<ObjectSnapshot> GetPersistedSnapshots()
    {
        lock (_stateLock)
        {
            return OrderDistinctSnapshots(EnumerateCurrentPersistedSnapshots());
        }
    }

    public IReadOnlyList<ObjectSnapshot> GetPersistedSnapshots(Guid? layoutId)
    {
        lock (_stateLock)
        {
            if (_batch is not null)
            {
                return !layoutId.HasValue || _layoutManager.IsLayoutEnabled(layoutId.Value)
                    ? OrderDistinctSnapshots(_batch.Objects.Values.Where(snapshot => snapshot.LayoutId == layoutId))
                    : [];
            }
            if (!layoutId.HasValue)
            {
                return GetStandaloneSnapshots();
            }

            return _layoutManager.IsLayoutEnabled(layoutId.Value)
                && _layoutManager.TryGetLayout(layoutId.Value, out ObjectLayoutSnapshot layout)
                    ? layout.Objects
                    : [];
        }
    }

    public IReadOnlyList<ObjectSnapshot> GetSceneSnapshots(IReadOnlyList<ObjectTemporaryLayoutSnapshot> temporaryLayouts)
    {
        lock (_stateLock)
        {
            return OrderDistinctSnapshots(EnumerateCurrentPersistedSnapshots()
                .Concat(temporaryLayouts.SelectMany(static layout => layout.Objects)));
        }
    }

    private IEnumerable<ObjectSnapshot> EnumerateCurrentPersistedSnapshots()
        => _batch is not null
            ? _batch.Objects.Values.Where(snapshot => !snapshot.LayoutId.HasValue
                || _layoutManager.IsLayoutEnabled(snapshot.LayoutId.Value))
            : _standaloneSnapshots.Values.Concat(_layoutManager.GetEnabledLayouts().SelectMany(static layout => layout.Objects));

    public bool TryGetPersistedSnapshot(Guid id, out ObjectSnapshot snapshot)
        => TryGetPersistedSnapshot(id, includeDisabledLayouts: true, out snapshot);

    public bool TryGetCurrentPersistedSnapshot(Guid id, out ObjectSnapshot snapshot)
        => TryGetPersistedSnapshot(id, includeDisabledLayouts: false, out snapshot);

    private bool TryGetPersistedSnapshot(Guid id, bool includeDisabledLayouts, out ObjectSnapshot snapshot)
    {
        lock (_stateLock)
        {
            if (_batch is not null)
            {
                return _batch.Objects.TryGetValue(id, out snapshot!)
                    && (includeDisabledLayouts || !snapshot.LayoutId.HasValue || _layoutManager.IsLayoutEnabled(snapshot.LayoutId.Value));
            }

            if (_standaloneSnapshots.TryGetValue(id, out snapshot!))
            {
                return true;
            }

            IReadOnlyList<ObjectLayoutSnapshot> layouts = includeDisabledLayouts
                ? _layoutManager.GetLayouts()
                : _layoutManager.GetEnabledLayouts();
            foreach (ObjectLayoutSnapshot layout in layouts)
            {
                ObjectSnapshot? layoutSnapshot = layout.Objects.FirstOrDefault(entry => entry.Id == id);

                if (layoutSnapshot is not null)
                {
                    snapshot = layoutSnapshot;
                    return true;
                }
            }

            snapshot = default!;
            return false;
        }
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

    public bool TryGetCreationLayout(out ObjectLayoutSnapshot layout)
    {
        Guid? creationLayoutId = _layoutManager.GetCreationLayoutId();
        if (creationLayoutId.HasValue
            && _layoutManager.TryGetLayout(creationLayoutId.Value, out layout))
        {
            return true;
        }

        layout = null!;
        return false;
    }

    public bool HasPersistedObjects()
        => HasStandaloneContent() || _layoutManager.HasAnyObjects();

    private bool HasStandaloneContent()
    {
        lock (_stateLock)
            return HasStandaloneSnapshots() || (_batch is null
                ? _standaloneGroups.Count > 0
                : _batch.Groups.Values.Any(static group => !group.LayoutId.HasValue));
    }

    public bool HasStandaloneSnapshots()
    {
        lock (_stateLock)
        {
            return _batch is null ? _standaloneSnapshots.Count > 0
                : _batch.Objects.Values.Any(static snapshot => !snapshot.LayoutId.HasValue);
        }
    }

    public HashSet<string> GetObjectNames(Guid? layoutId)
    {
        lock (_stateLock)
        {
            IEnumerable<ObjectSnapshot> snapshots;

            if (_batch is not null)
            {
                snapshots = _batch.Objects.Values.Where(snapshot => snapshot.LayoutId == layoutId);
            }
            else if (layoutId is Guid id)
            {
                snapshots = _layoutManager.TryGetLayout(id, out ObjectLayoutSnapshot layout) ? layout.Objects : [];
            }
            else
            {
                snapshots = _standaloneSnapshots.Values;
            }

            return snapshots.Select(static snapshot => TextUtility.TrimOrEmpty(snapshot.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public ObjectSnapshot ApplyCreationLayout(ObjectSnapshot snapshot)
    {
        if (snapshot.LayoutId.HasValue)
        {
            return snapshot;
        }

        Guid? creationLayoutId = _layoutManager.GetCreationLayoutId();
        return creationLayoutId.HasValue
            ? snapshot with { LayoutId = creationLayoutId }
            : snapshot;
    }

    public bool HasStoredOwner(Guid? layoutId)
        => !layoutId.HasValue || _layoutManager.TryGetLayout(layoutId.Value, out _);

    public bool IsCurrentOwner(Guid? layoutId)
        => !layoutId.HasValue || _layoutManager.IsLayoutEnabled(layoutId.Value);

    public ObjectSceneSource ResolveSceneSource(ObjectSnapshot snapshot)
    {
        if (snapshot.LayoutId.HasValue && _layoutManager.IsLayoutEnabled(snapshot.LayoutId.Value))
        {
            return ObjectSceneSource.CreatePersistentLayout(snapshot.LayoutId.Value);
        }

        return ObjectSceneSource.CreateStandalone(snapshot.LayoutId);
    }

    public bool TryUpsertPersistedSnapshot(ObjectSnapshot snapshot)
        => UpsertPersistedSnapshots([snapshot]) == PersistentMutationStatus.Success;

    public PersistentMutationStatus UpsertPersistedSnapshots(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        lock (_stateLock)
        {
            if (_batch is not null)
            {
                return _batch.Upsert(snapshots);
            }
            List<ObjectSnapshot> layoutSnapshots = [];
            HashSet<Guid> objectIds = [];
            foreach (ObjectSnapshot snapshot in snapshots)
            {
                if (!objectIds.Add(snapshot.Id) || !IsValidObjectMembership(snapshot))
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

    private bool IsValidObjectMembership(ObjectSnapshot snapshot)
    {
        if (snapshot.Id == Guid.Empty)
            return false;

        SceneGroupSnapshot? group = null;
        if (snapshot.LayoutId is Guid layoutId)
        {
            if (!_layoutManager.TryGetLayout(layoutId, out ObjectLayoutSnapshot layout))
                return !snapshot.GroupId.HasValue;
            foreach (SceneGroupSnapshot candidate in layout.Groups)
            {
                if (candidate.Id == snapshot.Id)
                    return false;
                if (candidate.Id == snapshot.GroupId)
                    group = candidate;
            }
        }
        else
        {
            if (_standaloneGroups.ContainsKey(snapshot.Id))
                return false;
            if (snapshot.GroupId is Guid groupId)
                _standaloneGroups.TryGetValue(groupId, out group);
        }

        return !snapshot.GroupId.HasValue || group is not null && SceneGroupUtility.IsValidMember(snapshot, group);
    }

    public bool TryRemovePersistedSnapshot(Guid id)
    {
        lock (_stateLock)
        {
            if (_batch is not null)
                return _batch.Remove(id);
            if (!TryGetPersistedSnapshot(id, out ObjectSnapshot stored))
                return false;

            if (stored.LayoutId is not Guid layoutId)
            {
                return _standaloneSnapshots.Remove(stored.Id);
            }

            return _layoutManager.TryGetLayout(layoutId, out ObjectLayoutSnapshot layout)
                && _layoutManager.TryReplaceLayoutObjects(layoutId,
                    layout.Objects.Where(snapshot => snapshot.Id != stored.Id).ToArray());
        }
    }

    public bool TryReplacePersistedSnapshot(ObjectSnapshot previousSnapshot, ObjectSnapshot nextSnapshot)
        => ReplacePersistedSnapshots([previousSnapshot], [nextSnapshot]) == PersistentMutationStatus.Success;

    public PersistentMutationStatus ReplacePersistedSnapshots(
        IReadOnlyList<ObjectSnapshot> previousSnapshots,
        IReadOnlyList<ObjectSnapshot> nextSnapshots)
    {
        lock (_stateLock)
        {
            if (previousSnapshots.Count != nextSnapshots.Count)
            {
                return PersistentMutationStatus.InvalidRequest;
            }

            if (_batch is not null)
            {
                for (int index = 0; index < nextSnapshots.Count; ++index)
                {
                    if (previousSnapshots[index].Id != nextSnapshots[index].Id)
                    {
                        return PersistentMutationStatus.InvalidRequest;
                    }
                }
                return _batch.Upsert(nextSnapshots);
            }

            HashSet<Guid> objectIds = [];
            HashSet<Guid> layoutIds = [];
            bool movesOwnership = false;
            for (int index = 0; index < nextSnapshots.Count; ++index)
            {
                ObjectSnapshot before = previousSnapshots[index];
                ObjectSnapshot after = nextSnapshots[index];
                if (before.Id != after.Id || !objectIds.Add(after.Id) || !IsValidObjectMembership(after))
                {
                    return PersistentMutationStatus.InvalidRequest;
                }
                movesOwnership |= before.LayoutId != after.LayoutId;
                if (before.LayoutId is Guid previousId)
                {
                    layoutIds.Add(previousId);
                }
                if (after.LayoutId is Guid nextId)
                {
                    layoutIds.Add(nextId);
                }
            }

            if (!movesOwnership)
            {
                return UpsertPersistedSnapshots(nextSnapshots);
            }

            ILookup<Guid?, ObjectSnapshot> objectsByLayout = nextSnapshots.ToLookup(static snapshot => snapshot.LayoutId);
            List<ObjectLayoutContentUpdate> updates = new(layoutIds.Count);
            foreach (Guid layoutId in layoutIds)
            {
                if (!_layoutManager.TryGetLayout(layoutId, out ObjectLayoutSnapshot layout))
                {
                    return PersistentMutationStatus.NotFound;
                }
                List<ObjectSnapshot> objects = layout.Objects.Where(snapshot => !objectIds.Contains(snapshot.Id)).ToList();
                objects.AddRange(objectsByLayout[layoutId]);
                updates.Add(new ObjectLayoutContentUpdate(layoutId, objects, layout.Folders) { Groups = layout.Groups });
            }

            PersistentMutationStatus status = _layoutManager.ReplaceLayoutContents(updates);
            if (status != PersistentMutationStatus.Success)
            {
                return status;
            }
            foreach (ObjectSnapshot snapshot in nextSnapshots)
            {
                _standaloneSnapshots.Remove(snapshot.Id);
                if (!snapshot.LayoutId.HasValue)
                {
                    _standaloneSnapshots[snapshot.Id] = snapshot;
                }
            }
            return PersistentMutationStatus.Success;
        }
    }

    public void ClearStandaloneSnapshots()
    {
        lock (_stateLock)
        {
            _standaloneSnapshots.Clear();
            _standaloneGroups.Clear();
        }
        _objectFolderService.ClearStandaloneState();
    }

    public void ReplaceStandaloneContent(IReadOnlyList<ObjectSnapshot> snapshots, IReadOnlyList<SceneGroupSnapshot> groups)
    {
        lock (_stateLock)
        {
            ObjectSnapshot[] objects = snapshots.Select(static snapshot => snapshot with { LayoutId = null }).ToArray();
            SceneGroupSnapshot[] standaloneGroups = groups.Select(static group => group with { LayoutId = null }).ToArray();
            if (!SceneGroupUtility.TryValidateOwner(null, objects, standaloneGroups, out string error))
                throw new ArgumentException(error, nameof(snapshots));

            _standaloneSnapshots.Clear();
            _standaloneGroups.Clear();
            foreach (ObjectSnapshot snapshot in objects)
                _standaloneSnapshots.Add(snapshot.Id, snapshot);
            foreach (SceneGroupSnapshot group in standaloneGroups)
                _standaloneGroups.Add(group.Id, group);
        }
    }

    private static List<ObjectSnapshot> OrderDistinctSnapshots(IEnumerable<ObjectSnapshot> snapshots)
        => snapshots
            .DistinctBy(static snapshot => snapshot.Id)
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToList();
}
