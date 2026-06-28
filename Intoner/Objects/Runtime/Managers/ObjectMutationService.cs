using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Applies local and temporary object mutations against persisted state and the active scene state.
/// </summary>
internal interface IObjectMutationService : IObjectSnapshotChangeApplier
{
    /// <summary>
    /// Creates a new local object in front of the player.
    /// </summary>
    /// <param name="kind">The object kind to create.</param>
    /// <param name="overrides">Optional initial placement overrides.</param>
    /// <returns>The new object id when creation succeeded.</returns>
    Guid? CreateObjectAtPlayer(ObjectKind kind, ObjectPlacementOverrides? overrides = null);

    /// <summary>
    /// Creates a new local object in front of the player and returns the applied snapshot.
    /// </summary>
    /// <param name="kind">The object kind to create.</param>
    /// <param name="snapshot">The created persisted snapshot when creation succeeded.</param>
    /// <param name="overrides">Optional initial placement overrides.</param>
    /// <returns>The new object id when creation succeeded.</returns>
    Guid? CreateObjectAtPlayer(ObjectKind kind, out ObjectSnapshot snapshot, ObjectPlacementOverrides? overrides = null);

    /// <summary>
    /// Imports an existing object snapshot into local persisted state.
    /// </summary>
    /// <param name="snapshot">The object snapshot to import.</param>
    /// <returns>The new imported object id when import succeeded.</returns>
    Guid? ImportObjectSnapshot(ObjectSnapshot snapshot);

    /// <summary>
    /// Imports an existing object snapshot into local persisted state and returns the applied snapshot.
    /// </summary>
    /// <param name="snapshot">The object snapshot to import.</param>
    /// <param name="importedSnapshot">The imported persisted snapshot when import succeeded.</param>
    /// <returns>The new imported object id when import succeeded.</returns>
    Guid? ImportObjectSnapshot(ObjectSnapshot snapshot, out ObjectSnapshot importedSnapshot);

    /// <summary>
    /// Creates a local object from the given snapshot.
    /// </summary>
    /// <param name="snapshot">The object snapshot to create.</param>
    /// <returns>The new object id when creation succeeded.</returns>
    Guid? CreateObjectSnapshot(ObjectSnapshot snapshot);

    /// <summary>
    /// Duplicates one persisted local object.
    /// </summary>
    /// <param name="id">The object id to duplicate.</param>
    /// <param name="duplicateId">The new duplicate object id when duplication succeeded.</param>
    /// <returns>true when the object existed and was duplicated.</returns>
    bool TryDuplicate(Guid id, out Guid duplicateId);

    /// <summary>
    /// Duplicates one persisted local object and returns the duplicate snapshot.
    /// </summary>
    /// <param name="id">The object id to duplicate.</param>
    /// <param name="duplicateId">The new duplicate object id when duplication succeeded.</param>
    /// <param name="duplicateSnapshot">The duplicate persisted snapshot when duplication succeeded.</param>
    /// <returns>true when the object existed and was duplicated.</returns>
    bool TryDuplicate(Guid id, out Guid duplicateId, out ObjectSnapshot duplicateSnapshot);

    /// <summary>
    /// Moves one persisted local object to the player placement position.
    /// </summary>
    /// <param name="id">The object id to move.</param>
    /// <returns>true when the object existed and the move succeeded.</returns>
    bool TryMoveToPlayer(Guid id);

    /// <summary>
    /// Moves one persisted local object to the player placement position and returns the applied snapshot.
    /// </summary>
    /// <param name="id">The object id to move.</param>
    /// <param name="snapshot">The applied persisted snapshot when the move succeeded.</param>
    /// <returns>true when the object existed and the move succeeded.</returns>
    bool TryMoveToPlayer(Guid id, out ObjectSnapshot snapshot);

    /// <summary>
    /// Applies a partial update to one persisted local object.
    /// </summary>
    /// <param name="id">The object id to patch.</param>
    /// <param name="patch">The partial object patch to apply.</param>
    /// <returns>true when the object existed and was updated.</returns>
    bool TryPatch(Guid id, ObjectSnapshotPatch patch);

    /// <summary>
    /// Replaces one persisted local object snapshot.
    /// </summary>
    /// <param name="snapshot">The replacement object snapshot.</param>
    /// <returns>true when the object existed and was updated.</returns>
    bool TryUpdate(ObjectSnapshot snapshot);

    /// <summary>
    /// Replaces one persisted local object snapshot and returns the applied snapshot.
    /// </summary>
    /// <param name="snapshot">The replacement object snapshot.</param>
    /// <param name="appliedSnapshot">The applied persisted snapshot when the update succeeded.</param>
    /// <returns>true when the object existed and was updated.</returns>
    bool TryUpdate(ObjectSnapshot snapshot, out ObjectSnapshot appliedSnapshot);

    /// <summary>
    /// Replaces multiple persisted local object snapshots as one checked batch and returns the applied snapshots.
    /// </summary>
    /// <param name="snapshots">The replacement object snapshots.</param>
    /// <param name="appliedSnapshots">The applied persisted snapshots in request order when the batch succeeded.</param>
    /// <returns>true when every object in the batch was updated successfully.</returns>
    bool TryUpdateMany(IReadOnlyList<ObjectSnapshot> snapshots, out IReadOnlyList<ObjectSnapshot> appliedSnapshots);

    /// <summary>
    /// Creates multiple scene objects and returns the applied snapshots.
    /// </summary>
    /// <param name="snapshots">The snapshots to create.</param>
    /// <param name="createdSnapshots">The created snapshots in request order when creation succeeded.</param>
    /// <param name="applyDefaultLayout">Whether the current default layout should be applied automatically.</param>
    /// <param name="persistSnapshots">Whether the created snapshots should be stored in persistent state.</param>
    /// <param name="sourceOverride">Optional explicit scene source metadata.</param>
    /// <returns>true when every object in the batch was created successfully.</returns>
    bool TryCreateMany(IReadOnlyList<ObjectSnapshot> snapshots, out IReadOnlyList<ObjectSnapshot> createdSnapshots, bool applyDefaultLayout = true, bool persistSnapshots = true, ObjectSceneSource? sourceOverride = null);

    /// <summary>
    /// Duplicates multiple persisted local objects and returns the duplicate snapshots.
    /// </summary>
    /// <param name="ids">The object ids to duplicate.</param>
    /// <param name="duplicateSnapshots">The duplicate snapshots in request order when duplication succeeded.</param>
    /// <returns>true when every object in the batch was duplicated successfully.</returns>
    bool TryDuplicateMany(IReadOnlyList<Guid> ids, out IReadOnlyList<ObjectSnapshot> duplicateSnapshots);

    /// <summary>
    /// Removes multiple persisted local objects and returns the removed snapshots.
    /// </summary>
    /// <param name="ids">The object ids to remove.</param>
    /// <param name="removedSnapshots">The removed snapshots in request order when removal succeeded.</param>
    /// <returns>true when every object in the batch was removed successfully.</returns>
    bool TryRemoveMany(IReadOnlyList<Guid> ids, out IReadOnlyList<ObjectSnapshot> removedSnapshots);

    /// <summary>
    /// Removes one persisted local object.
    /// </summary>
    /// <param name="id">The object id to remove.</param>
    /// <returns>true when the object existed and was removed.</returns>
    bool Remove(Guid id);

    /// <summary>
    /// Creates one scene object from the given snapshot and stores it in the active scene.
    /// </summary>
    /// <param name="snapshot">The snapshot to create.</param>
    /// <param name="id">The created object id when creation succeeded.</param>
    /// <param name="applyDefaultLayout">Whether the current default layout should be applied automatically.</param>
    /// <param name="persistSnapshot">Whether the created snapshot should be stored in persistent state.</param>
    /// <param name="sourceOverride">Optional explicit scene source metadata.</param>
    /// <returns>true when the scene object was created.</returns>
    bool TryCreateObject(ObjectSnapshot snapshot, out Guid id, bool applyDefaultLayout = true, bool persistSnapshot = true, ObjectSceneSource? sourceOverride = null);

    /// <summary>
    /// Creates one scene object from the given snapshot, stores it in the active scene, and returns the applied snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot to create.</param>
    /// <param name="id">The created object id when creation succeeded.</param>
    /// <param name="createdSnapshot">The created snapshot after default layout, creation context, and sanitization.</param>
    /// <param name="applyDefaultLayout">Whether the current default layout should be applied automatically.</param>
    /// <param name="persistSnapshot">Whether the created snapshot should be stored in persistent state.</param>
    /// <param name="sourceOverride">Optional explicit scene source metadata.</param>
    /// <returns>true when the scene object was created.</returns>
    bool TryCreateObject(ObjectSnapshot snapshot, out Guid id, out ObjectSnapshot createdSnapshot, bool applyDefaultLayout = true, bool persistSnapshot = true, ObjectSceneSource? sourceOverride = null);

    /// <summary>
    /// Removes one active scene entry and disposes the scene object.
    /// </summary>
    /// <param name="entry">The scene entry to remove.</param>
    void RemoveActiveEntry(ObjectSceneEntry entry);

    /// <summary>
    /// Disposes a sequence of runtime entries.
    /// </summary>
    /// <param name="entries">The entries to destroy.</param>
    void DestroyEntries(IEnumerable<ObjectSceneEntry> entries);

    /// <summary>
    /// Disposes entries that are being replaced by desired scene snapshots.
    /// </summary>
    /// <param name="entries">the entries to destroy after replacement usage has already been tracked.</param>
    void DestroyReplacedEntries(IEnumerable<ObjectSceneEntry> entries);

    /// <summary>
    /// Refreshes tracked object collection usage for one scene snapshot replacement.
    /// </summary>
    /// <param name="previousSnapshot">the previous active snapshot when one existed.</param>
    /// <param name="nextSnapshot">the next active snapshot when one exists now.</param>
    void RefreshCollectionUsage(ObjectSnapshot? previousSnapshot, ObjectSnapshot? nextSnapshot);

    /// <summary>
    /// Clears all active scene entries.
    /// </summary>
    /// <param name="removePersistedState">Whether persisted snapshots should also be removed for the active entries.</param>
    void ClearAllActiveEntries(bool removePersistedState);

    /// <summary>
    /// Recreates one active scene entry with a replacement snapshot.
    /// </summary>
    /// <param name="entry">The existing scene entry.</param>
    /// <param name="snapshot">The replacement snapshot.</param>
    /// <param name="persistSnapshot">Whether the replacement snapshot should be persisted.</param>
    /// <param name="sourceOverride">Optional explicit scene source metadata.</param>
    /// <returns>true when the entry was recreated.</returns>
    bool TryRecreateEntry(ObjectSceneEntry entry, ObjectSnapshot snapshot, bool persistSnapshot, ObjectSceneSource? sourceOverride = null);

    /// <summary>
    /// Applies one temporary object upsert to the current active scene.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="snapshot">The remapped temporary snapshot.</param>
    /// <param name="currentLocation">The current active location scope.</param>
    /// <returns>The result of the temporary scene mutation.</returns>
    ObjectTemporaryMutationStatus TryApplyTemporaryObjectUpsert(string sourceKey, ObjectSnapshot snapshot, ObjectLocationScope currentLocation);

    /// <summary>
    /// Applies one temporary object removal to the current active scene.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="objectId">The remapped local temporary object id.</param>
    /// <returns>The result of the temporary scene mutation.</returns>
    ObjectTemporaryMutationStatus TryApplyTemporaryObjectRemoval(string sourceKey, Guid objectId);
}

internal sealed class ObjectMutationService : IObjectMutationService
{
    private enum BatchChangeKind
    {
        None,
        Create,
        Update,
        Remove,
    }

    private enum ActiveEntryUpdateStatus
    {
        Applied,
        RecreateFailed,
        Rejected,
    }

    private readonly record struct PreparedObjectUpdate(ObjectSceneEntry? Entry, ObjectSnapshot PreviousSnapshot, ObjectSnapshot NextSnapshot)
    {
        public Guid ObjectId => NextSnapshot.Id;
        public bool HasChange => PreviousSnapshot != NextSnapshot;
        public bool IsPersistedOnly => Entry is null;
    }

    private interface IObjectMutationBatchStep
    {
        IReadOnlyList<ObjectSnapshot> ResultSnapshots { get; }

        bool TryApply(ObjectMutationService service);

        void Rollback(ObjectMutationService service);
    }

    private sealed class ObjectMutationBatch(IReadOnlyList<IObjectMutationBatchStep> steps)
    {
        private readonly IObjectMutationBatchStep[] _steps = [.. steps];
        private readonly List<ObjectSnapshot> _resultSnapshots = [];

        public IReadOnlyList<ObjectSnapshot> ResultSnapshots => _resultSnapshots;

        public bool TryExecute(ObjectMutationService service)
        {
            _resultSnapshots.Clear();
            var appliedSteps = new List<IObjectMutationBatchStep>(_steps.Length);
            foreach (var step in _steps)
            {
                if (step.TryApply(service))
                {
                    appliedSteps.Add(step);
                    _resultSnapshots.AddRange(step.ResultSnapshots);
                    continue;
                }

                for (var i = appliedSteps.Count - 1; i >= 0; --i)
                {
                    appliedSteps[i].Rollback(service);
                }

                _resultSnapshots.Clear();
                return false;
            }

            return true;
        }
    }

    private sealed class CreateBatchStep(
        IReadOnlyList<ObjectSnapshot> snapshots,
        bool applyDefaultLayout,
        bool persistSnapshots,
        ObjectSceneSource? sourceOverride) : IObjectMutationBatchStep
    {
        private readonly List<ObjectSnapshot> _resultSnapshots = new(snapshots.Count);

        public IReadOnlyList<ObjectSnapshot> ResultSnapshots => _resultSnapshots;

        public bool TryApply(ObjectMutationService service)
        {
            _resultSnapshots.Clear();
            foreach (var snapshot in snapshots)
            {
                if (!service.TryCreateObject(snapshot, out _, out var createdSnapshot, applyDefaultLayout, persistSnapshots, sourceOverride))
                {
                    Rollback(service);
                    return false;
                }

                _resultSnapshots.Add(createdSnapshot);
            }

            return true;
        }

        public void Rollback(ObjectMutationService service)
        {
            if (_resultSnapshots.Count == 0)
            {
                return;
            }

            service.RollbackCreatedSnapshots(_resultSnapshots);
            _resultSnapshots.Clear();
        }
    }

    private sealed class UpdateBatchStep(IReadOnlyList<PreparedObjectUpdate> updates) : IObjectMutationBatchStep
    {
        private readonly PreparedObjectUpdate[] _updates = [.. updates];
        private readonly List<ObjectSnapshot> _resultSnapshots = new(updates.Count);
        private readonly List<PreparedObjectUpdate> _appliedUpdates = new(updates.Count);

        public IReadOnlyList<ObjectSnapshot> ResultSnapshots => _resultSnapshots;

        public bool TryApply(ObjectMutationService service)
        {
            _resultSnapshots.Clear();
            _appliedUpdates.Clear();
            foreach (var update in _updates)
            {
                if (!service.TryApplyPreparedObjectUpdate(update, out var appliedSnapshot))
                {
                    Rollback(service);
                    return false;
                }

                _resultSnapshots.Add(appliedSnapshot);
                if (update.HasChange)
                {
                    _appliedUpdates.Add(update);
                }
            }

            return true;
        }

        public void Rollback(ObjectMutationService service)
        {
            if (_appliedUpdates.Count == 0)
            {
                _resultSnapshots.Clear();
                return;
            }

            service.RestorePreparedObjectUpdates(_appliedUpdates);
            _appliedUpdates.Clear();
            _resultSnapshots.Clear();
        }
    }

    private sealed class RemoveBatchStep(IReadOnlyList<ObjectSnapshot> snapshots) : IObjectMutationBatchStep
    {
        private readonly ObjectSnapshot[] _snapshots = [.. snapshots];
        private readonly List<ObjectSnapshot> _resultSnapshots = new(snapshots.Count);

        public IReadOnlyList<ObjectSnapshot> ResultSnapshots => _resultSnapshots;

        public bool TryApply(ObjectMutationService service)
        {
            _resultSnapshots.Clear();
            foreach (var snapshot in _snapshots)
            {
                if (!service.Remove(snapshot.Id))
                {
                    Rollback(service);
                    return false;
                }

                _resultSnapshots.Add(snapshot);
            }

            return true;
        }

        public void Rollback(ObjectMutationService service)
        {
            if (_resultSnapshots.Count == 0)
            {
                return;
            }

            service.RestoreRemovedSnapshots(_resultSnapshots);
            _resultSnapshots.Clear();
        }
    }

    private readonly ILogger<ObjectMutationService> _logger;
    private readonly IObjectPersistenceState _persistenceState;
    private readonly IObjectSceneState _sceneState;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly IObjectPlacementResolver _placementResolver;
    private readonly IObjectKindService _objectKindService;
    private readonly IObjectHousingModePolicy _housingModePolicy;
    private readonly Lazy<ISceneObjectFactory> _sceneObjectFactory;
    private readonly IObjectRuntimeLocationService _locationService;
    private readonly IObjectCollectionManager _objectCollectionManager;
    private readonly IObjectResolvedCollectionStore _collectionStore;

    public ObjectMutationService(
        ILogger<ObjectMutationService> logger,
        IObjectPersistenceState persistenceState,
        IObjectSceneState sceneState,
        IObjectRevisionTracker revisionTracker,
        IObjectPlacementResolver placementResolver,
        IObjectKindService objectKindService,
        IObjectHousingModePolicy housingModePolicy,
        Func<ISceneObjectFactory> sceneObjectFactoryFactory,
        IObjectRuntimeLocationService locationService,
        IObjectCollectionManager objectCollectionManager,
        IObjectResolvedCollectionStore collectionStore)
    {
        _logger = logger;
        _persistenceState = persistenceState;
        _sceneState = sceneState;
        _revisionTracker = revisionTracker;
        _placementResolver = placementResolver;
        _objectKindService = objectKindService;
        _housingModePolicy = housingModePolicy;
        _sceneObjectFactory = new Lazy<ISceneObjectFactory>(sceneObjectFactoryFactory);
        _locationService = locationService;
        _objectCollectionManager = objectCollectionManager;
        _collectionStore = collectionStore;
    }

    public Guid? CreateObjectAtPlayer(ObjectKind kind, ObjectPlacementOverrides? overrides = null)
        => CreateObjectAtPlayer(kind, out _, overrides);

    public Guid? CreateObjectAtPlayer(ObjectKind kind, out ObjectSnapshot snapshot, ObjectPlacementOverrides? overrides = null)
    {
        snapshot = default!;
        if (!_objectKindService.CanCreate(kind))
        {
            return null;
        }

        var placementResolved = _placementResolver.TryResolveFromPlayer(out var transform);
        var nextSnapshot = ApplyObjectPlacementOverrides(
            _objectKindService.CreateDefaultSnapshot(
                kind,
                placementResolved ? transform : new ObjectTransform(),
                _persistenceState.NextName(kind)),
            overrides);

        return TryCreateObject(nextSnapshot, out var id, out snapshot)
            ? id
            : null;
    }

    public Guid? ImportObjectSnapshot(ObjectSnapshot snapshot)
        => ImportObjectSnapshot(snapshot, out _);

    public Guid? ImportObjectSnapshot(ObjectSnapshot snapshot, out ObjectSnapshot importedSnapshot)
    {
        importedSnapshot = snapshot with
        {
            Id = Guid.NewGuid(),
            LayoutId = null,
            CreatedAtUtc = DateTime.UtcNow,
        };

        if (string.IsNullOrWhiteSpace(importedSnapshot.Name))
        {
            importedSnapshot = importedSnapshot with { Name = _persistenceState.NextName(snapshot.Kind) };
        }

        importedSnapshot = ApplyCreationContext(importedSnapshot, refresh: true);
        return TryCreateObject(importedSnapshot, out var id, out importedSnapshot)
            ? id
            : null;
    }

    public Guid? CreateObjectSnapshot(ObjectSnapshot snapshot)
    {
        if (!_objectKindService.CanCreate(snapshot.Kind))
        {
            return null;
        }

        return TryCreateObject(snapshot, out var id)
            ? id
            : null;
    }

    public bool TryDuplicate(Guid id, out Guid duplicateId)
        => TryDuplicate(id, out duplicateId, out _);

    public bool TryDuplicate(Guid id, out Guid duplicateId, out ObjectSnapshot duplicateSnapshot)
    {
        duplicateId = Guid.Empty;
        duplicateSnapshot = default!;

        if (!TryBuildDuplicateSnapshot(id, out ObjectSnapshot duplicate))
        {
            return false;
        }

        if (!TryCreateObject(duplicate, out duplicateId, out duplicateSnapshot))
        {
            return false;
        }
        return true;
    }

    public bool TryMoveToPlayer(Guid id)
        => TryMoveToPlayer(id, out _);

    public bool TryMoveToPlayer(Guid id, out ObjectSnapshot snapshot)
    {
        snapshot = default!;
        if (!_sceneState.TryGetObjectSnapshot(id, out var currentSnapshot)
            || !_placementResolver.TryResolveFromPlayer(out var placement))
        {
            return false;
        }

        return TryUpdate(currentSnapshot with
        {
            Transform = currentSnapshot.Transform with
            {
                Position = placement.Position,
                RotationDegrees = placement.RotationDegrees,
            },
        }, out snapshot);
    }

    public bool TryPatch(Guid id, ObjectSnapshotPatch patch)
    {
        if (!TryResolveLocalPersistentSnapshot(id, out var snapshot))
        {
            return false;
        }

        if (!patch.HasChanges)
        {
            return true;
        }

        return TryUpdate(ObjectSnapshotUtility.ApplyPatch(snapshot, patch));
    }

    public bool TryUpdate(ObjectSnapshot snapshot)
        => TryUpdate(snapshot, out _);

    public bool TryUpdate(ObjectSnapshot snapshot, out ObjectSnapshot appliedSnapshot)
    {
        appliedSnapshot = default!;
        if (!TryPrepareObjectUpdate(snapshot, out var preparedUpdate))
        {
            return false;
        }

        return TryApplyPreparedObjectUpdate(preparedUpdate, out appliedSnapshot);
    }

    public bool TryUpdateMany(IReadOnlyList<ObjectSnapshot> snapshots, out IReadOnlyList<ObjectSnapshot> appliedSnapshots)
    {
        appliedSnapshots = [];
        if (snapshots.Count == 0)
        {
            return true;
        }

        if (!TryPrepareObjectUpdates(snapshots, out var preparedUpdates)
            || !TryExecuteBatch(new UpdateBatchStep(preparedUpdates), out appliedSnapshots))
        {
            return false;
        }

        return true;
    }

    public bool TryCreateMany(IReadOnlyList<ObjectSnapshot> snapshots, out IReadOnlyList<ObjectSnapshot> createdSnapshots, bool applyDefaultLayout = true, bool persistSnapshots = true, ObjectSceneSource? sourceOverride = null)
    {
        createdSnapshots = [];
        if (snapshots.Count == 0)
        {
            return true;
        }

        if (HasDuplicateObjectIds(snapshots))
        {
            return false;
        }

        if (!TryExecuteBatch(
                new CreateBatchStep(snapshots, applyDefaultLayout, persistSnapshots, sourceOverride),
                out createdSnapshots))
        {
            return false;
        }

        return true;
    }

    public bool TryDuplicateMany(IReadOnlyList<Guid> ids, out IReadOnlyList<ObjectSnapshot> duplicateSnapshots)
    {
        duplicateSnapshots = [];
        if (ids.Count == 0)
        {
            return true;
        }

        return TryBuildDuplicateSnapshots(ids, out var requestedSnapshots)
            && TryCreateMany(requestedSnapshots, out duplicateSnapshots);
    }

    public bool TryRemoveMany(IReadOnlyList<Guid> ids, out IReadOnlyList<ObjectSnapshot> removedSnapshots)
    {
        removedSnapshots = [];
        if (ids.Count == 0)
        {
            return true;
        }

        if (!TryResolveRemovableSnapshots(ids, out var removableSnapshots)
            || !TryExecuteBatch(new RemoveBatchStep(removableSnapshots), out removedSnapshots))
        {
            return false;
        }

        return true;
    }

    public bool TryApplySnapshotChanges(IReadOnlyList<ObjectSnapshotChange> changes)
    {
        if (changes.Count == 0)
        {
            return true;
        }

        if (!TryBuildSnapshotChangeBatch(changes, out var batch))
        {
            return false;
        }

        return batch.TryExecute(this);
    }

    public bool Remove(Guid id)
    {
        if (_sceneState.TryGetEntry(id, out var entry))
        {
            if (entry.Source.IsRuntimeOnly)
            {
                return false;
            }

            RemoveActiveEntry(entry);
            _persistenceState.RemovePersistedSnapshot(entry.Snapshot);
            _sceneState.ClearRuntimeFailure(id);
            _revisionTracker.Increment(persistentChanged: true);
            return true;
        }

        if (!_persistenceState.TryGetPersistedSnapshot(id, out var persistedSnapshot))
        {
            return false;
        }

        _persistenceState.RemovePersistedSnapshot(persistedSnapshot);
        RefreshCollectionUsage(persistedSnapshot, null);
        _sceneState.ClearRuntimeFailure(id);
        _revisionTracker.Increment(persistentChanged: true);
        return true;
    }

    public bool TryCreateObject(ObjectSnapshot snapshot, out Guid id, bool applyDefaultLayout = true, bool persistSnapshot = true, ObjectSceneSource? sourceOverride = null)
        => TryCreateObject(snapshot, out id, out _, applyDefaultLayout, persistSnapshot, sourceOverride);

    public bool TryCreateObject(ObjectSnapshot snapshot, out Guid id, out ObjectSnapshot createdSnapshot, bool applyDefaultLayout = true, bool persistSnapshot = true, ObjectSceneSource? sourceOverride = null)
    {
        createdSnapshot = default!;
        if (applyDefaultLayout && (!sourceOverride.HasValue || !sourceOverride.Value.IsRuntimeOnly))
        {
            snapshot = _persistenceState.ApplyDefaultLayout(snapshot);
        }

        snapshot = ApplyCreationContext(snapshot);
        if (!_objectKindService.TrySanitizeSnapshot(snapshot, out var sanitizedSnapshot))
        {
            _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.InvalidObject);
            id = Guid.Empty;
            return false;
        }

        var source = sourceOverride ?? _persistenceState.ResolveSceneSource(sanitizedSnapshot);
        var persistSceneSnapshot = persistSnapshot && source.IsLocalPersistent;
        if (source.UsesUserHousingPolicy
            && !_housingModePolicy.TryValidateCreate(sanitizedSnapshot, GetHousingPolicySceneSnapshots(), out var housingModeError))
        {
            _logger.LogDebug("object create rejected by housing mode: {Reason}", housingModeError);
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.HousingModeRejected);
            id = Guid.Empty;
            return false;
        }

        _objectCollectionManager.EnsureCollectionMaterialized(sanitizedSnapshot.CollectionId, [sanitizedSnapshot]);

        if (!SceneObjectFactory.TryCreateSceneObject(sanitizedSnapshot, out var sceneObject, out string failureCode))
        {
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, failureCode);
            id = Guid.Empty;
            return false;
        }

        _sceneState.UpsertEntry(new ObjectSceneEntry
        {
            SceneObject = sceneObject,
            Source = source,
            ResourceCollection = GetResourceCollectionState(sanitizedSnapshot),
        });
        RefreshCollectionUsage(null, sanitizedSnapshot);

        if (persistSceneSnapshot)
        {
            _persistenceState.UpsertPersistedSnapshot(sanitizedSnapshot);
        }

        _revisionTracker.Increment(persistentChanged: persistSceneSnapshot);
        id = sanitizedSnapshot.Id;
        createdSnapshot = sanitizedSnapshot;
        return true;
    }

    public void RemoveActiveEntry(ObjectSceneEntry entry)
        => RemoveActiveEntry(entry, releaseCollectionUsage: true);

    public void DestroyEntries(IEnumerable<ObjectSceneEntry> entries)
        => DestroyEntries(entries, releaseCollectionUsage: true);

    public void DestroyReplacedEntries(IEnumerable<ObjectSceneEntry> entries)
        => DestroyEntries(entries, releaseCollectionUsage: false);

    private void RemoveActiveEntry(ObjectSceneEntry entry, bool releaseCollectionUsage)
    {
        _sceneState.TryRemoveEntry(entry.Snapshot.Id, out _);
        DestroyEntry(entry, releaseCollectionUsage);
    }

    private void DestroyEntries(IEnumerable<ObjectSceneEntry> entries, bool releaseCollectionUsage)
    {
        foreach (var entry in entries)
        {
            DestroyEntry(entry, releaseCollectionUsage);
        }
    }

    public void ClearAllActiveEntries(bool removePersistedState)
    {
        var entries = _sceneState.RemoveAllEntries();
        if (entries.Count == 0)
        {
            return;
        }

        _logger.LogTrace("clearing {Count} active objects", entries.Count);
        DestroyEntries(entries);

        if (!removePersistedState)
        {
            return;
        }

        foreach (var entry in entries)
        {
            _persistenceState.RemovePersistedSnapshot(entry.Snapshot);
        }
    }

    public bool TryRecreateEntry(ObjectSceneEntry entry, ObjectSnapshot snapshot, bool persistSnapshot, ObjectSceneSource? sourceOverride = null)
    {
        RefreshCollectionUsage(entry.Snapshot, snapshot);
        RemoveActiveEntry(entry, releaseCollectionUsage: false);
        if (!_objectKindService.CanCreate(snapshot.Kind))
        {
            RefreshCollectionUsage(snapshot, null);
            _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.ServiceMissing);
            return false;
        }

        if (TryCreateObject(
            snapshot,
            out _,
            applyDefaultLayout: false,
            persistSnapshot: persistSnapshot,
            sourceOverride: sourceOverride ?? entry.Source))
        {
            return true;
        }

        RefreshCollectionUsage(snapshot, null);
        return false;
    }

    public ObjectTemporaryMutationStatus TryApplyTemporaryObjectUpsert(string sourceKey, ObjectSnapshot snapshot, ObjectLocationScope currentLocation)
    {
        var shouldBeActive = currentLocation.IsValid && ObjectSnapshotUtility.MatchesLocation(snapshot, currentLocation);
        var source = ObjectSceneSource.CreateTemporaryLayout(sourceKey);

        if (!_sceneState.TryGetEntry(snapshot.Id, out var entry))
        {
            if (!shouldBeActive)
            {
                _sceneState.ClearRuntimeFailure(snapshot.Id);
                return ObjectTemporaryMutationStatus.Success;
            }

            if (!_objectKindService.CanCreate(snapshot.Kind))
            {
                _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.ServiceMissing);
                return ResolveTemporaryRuntimeFailureStatus(snapshot.Id);
            }

            return TryCreateTemporaryEntry(snapshot, source);
        }

        if (!entry.Source.IsRuntimeOnly
            || !string.Equals(entry.Source.SourceKey, sourceKey, StringComparison.Ordinal))
        {
            _sceneState.MarkNeedsRefresh();
            return ObjectTemporaryMutationStatus.SourceMismatch;
        }

        if (!shouldBeActive)
        {
            RemoveActiveEntry(entry);
            _sceneState.ClearRuntimeFailure(snapshot.Id);
            return ObjectTemporaryMutationStatus.Success;
        }

        if (entry.SceneObject.Kind != snapshot.Kind)
        {
            RemoveActiveEntry(entry);
            return TryCreateTemporaryEntry(snapshot, source);
        }

        if (entry.Snapshot == snapshot)
        {
            return ObjectTemporaryMutationStatus.Success;
        }

        switch (ApplyActiveEntryUpdate(entry, entry.Snapshot, snapshot, persistSnapshot: false, source, out _))
        {
            case ActiveEntryUpdateStatus.Applied:
                _sceneState.ClearRuntimeFailure(snapshot.Id);
                return ObjectTemporaryMutationStatus.Success;

            case ActiveEntryUpdateStatus.RecreateFailed:
                return ResolveTemporaryRuntimeFailureStatus(snapshot.Id);

            default:
                RemoveActiveEntry(entry);
                _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.UpdateRejected);
                return ResolveTemporaryRuntimeFailureStatus(snapshot.Id);
        }
    }

    public ObjectTemporaryMutationStatus TryApplyTemporaryObjectRemoval(string sourceKey, Guid objectId)
    {
        if (!_sceneState.TryGetEntry(objectId, out var entry))
        {
            _sceneState.ClearRuntimeFailure(objectId);
            return ObjectTemporaryMutationStatus.Success;
        }

        if (!entry.Source.IsRuntimeOnly
            || !string.Equals(entry.Source.SourceKey, sourceKey, StringComparison.Ordinal))
        {
            _sceneState.MarkNeedsRefresh();
            return ObjectTemporaryMutationStatus.SourceMismatch;
        }

        RemoveActiveEntry(entry);
        _sceneState.ClearRuntimeFailure(objectId);
        return ObjectTemporaryMutationStatus.Success;
    }

    private ObjectSnapshot ApplyCreationContext(ObjectSnapshot snapshot, bool refresh = false)
        => !refresh && snapshot.CreatedIn.IsValid
            ? snapshot
            : snapshot with { CreatedIn = _locationService.GetCurrentCreationContext() };

    private bool TryBuildDuplicateSnapshot(Guid id, out ObjectSnapshot duplicateSnapshot)
    {
        duplicateSnapshot = default!;
        if (!TryResolveLocalPersistentSnapshot(id, out var snapshot))
        {
            return false;
        }

        duplicateSnapshot = ApplyCreationContext(snapshot with
        {
            Id = Guid.NewGuid(),
            Name = _persistenceState.NextName(snapshot.Kind),
            CreatedAtUtc = DateTime.UtcNow,
            Transform = snapshot.Transform with
            {
                Position = snapshot.Transform.Position + new Vector3(0.5f, 0f, 0.5f),
            },
        }, refresh: true);
        return true;
    }

    private bool TryResolveLocalPersistentSnapshot(Guid id, out ObjectSnapshot snapshot)
    {
        if (_sceneState.TryGetEntry(id, out var entry))
        {
            if (entry.Source.IsRuntimeOnly)
            {
                snapshot = default!;
                return false;
            }

            snapshot = entry.Snapshot;
            return true;
        }

        return _persistenceState.TryGetPersistedSnapshot(id, out snapshot);
    }

    private bool TryPrepareObjectUpdate(ObjectSnapshot snapshot, out PreparedObjectUpdate preparedUpdate)
    {
        if (_sceneState.TryGetEntry(snapshot.Id, out var entry))
        {
            if (entry.Source.IsRuntimeOnly
                || !TrySanitizeUpdatedSnapshot(snapshot, entry.Snapshot, out var nextSnapshot)
                || !_housingModePolicy.TryValidateSnapshot(nextSnapshot, out _))
            {
                preparedUpdate = default;
                return false;
            }

            preparedUpdate = new PreparedObjectUpdate(entry, entry.Snapshot, nextSnapshot);
            return true;
        }

        if (!_persistenceState.TryGetPersistedSnapshot(snapshot.Id, out var persistedSnapshot)
            || !TrySanitizeUpdatedSnapshot(snapshot, persistedSnapshot, out var persistedNextSnapshot)
            || !_housingModePolicy.TryValidateSnapshot(persistedNextSnapshot, out _))
        {
            preparedUpdate = default;
            return false;
        }

        preparedUpdate = new PreparedObjectUpdate(null, persistedSnapshot, persistedNextSnapshot);
        return true;
    }

    private bool TryApplyPreparedObjectUpdate(PreparedObjectUpdate preparedUpdate, out ObjectSnapshot appliedSnapshot)
    {
        if (preparedUpdate.IsPersistedOnly)
        {
            if (!preparedUpdate.HasChange)
            {
                appliedSnapshot = preparedUpdate.PreviousSnapshot;
                return true;
            }

            _persistenceState.UpsertPersistedSnapshot(preparedUpdate.NextSnapshot);
            _revisionTracker.Increment(persistentChanged: true);
            appliedSnapshot = preparedUpdate.NextSnapshot;
            return true;
        }

        if (!preparedUpdate.HasChange)
        {
            appliedSnapshot = preparedUpdate.NextSnapshot;
            return true;
        }

        return ApplyActiveEntryUpdate(
                preparedUpdate.Entry!,
                preparedUpdate.PreviousSnapshot,
                preparedUpdate.NextSnapshot,
                persistSnapshot: true,
                sourceOverride: null,
                out appliedSnapshot)
            == ActiveEntryUpdateStatus.Applied;
    }

    private bool TryExecuteBatch(IObjectMutationBatchStep step, out IReadOnlyList<ObjectSnapshot> resultSnapshots)
    {
        var batch = new ObjectMutationBatch([step]);
        if (!batch.TryExecute(this))
        {
            resultSnapshots = [];
            return false;
        }

        resultSnapshots = batch.ResultSnapshots;
        return true;
    }

    private IReadOnlyList<ObjectSnapshot> GetHousingPolicySceneSnapshots()
    {
        Dictionary<Guid, ObjectSnapshot> snapshots = [];
        foreach (ObjectSnapshot snapshot in _persistenceState.GetPersistedSnapshots())
        {
            snapshots[snapshot.Id] = snapshot;
        }

        foreach (ObjectSceneEntry entry in _sceneState.GetEntriesSnapshot())
        {
            if (!entry.Source.UsesUserHousingPolicy)
            {
                continue;
            }

            snapshots[entry.Snapshot.Id] = entry.Snapshot;
        }

        return snapshots.Values.ToList();
    }

    private bool TryPrepareObjectUpdates(IReadOnlyList<ObjectSnapshot> snapshots, out List<PreparedObjectUpdate> preparedUpdates)
    {
        preparedUpdates = new List<PreparedObjectUpdate>(snapshots.Count);
        var seenIds = new HashSet<Guid>();
        foreach (var snapshot in snapshots)
        {
            if (!seenIds.Add(snapshot.Id)
                || !TryPrepareObjectUpdate(snapshot, out var preparedUpdate))
            {
                preparedUpdates = [];
                return false;
            }

            preparedUpdates.Add(preparedUpdate);
        }

        return true;
    }

    private bool TryBuildDuplicateSnapshots(IReadOnlyList<Guid> ids, out List<ObjectSnapshot> duplicateSnapshots)
    {
        duplicateSnapshots = new List<ObjectSnapshot>(ids.Count);
        var seenIds = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (!seenIds.Add(id)
                || !TryBuildDuplicateSnapshot(id, out var duplicateSnapshot))
            {
                duplicateSnapshots = [];
                return false;
            }

            duplicateSnapshots.Add(duplicateSnapshot);
        }

        return true;
    }

    private bool TryResolveRemovableSnapshots(IReadOnlyList<Guid> ids, out List<ObjectSnapshot> removableSnapshots)
    {
        removableSnapshots = new List<ObjectSnapshot>(ids.Count);
        var seenIds = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (!seenIds.Add(id)
                || !TryResolveLocalPersistentSnapshot(id, out var removableSnapshot))
            {
                removableSnapshots = [];
                return false;
            }

            removableSnapshots.Add(removableSnapshot);
        }

        return true;
    }

    private bool TrySanitizeUpdatedSnapshot(ObjectSnapshot snapshot, ObjectSnapshot currentSnapshot, out ObjectSnapshot sanitizedSnapshot)
        => _objectKindService.TrySanitizeSnapshot(
            snapshot with
            {
                Id = currentSnapshot.Id,
                Kind = currentSnapshot.Kind,
                LayoutId = currentSnapshot.LayoutId,
                CreatedAtUtc = currentSnapshot.CreatedAtUtc,
                CreatedIn = currentSnapshot.CreatedIn,
            },
            out sanitizedSnapshot);

    private ActiveEntryUpdateStatus ApplyActiveEntryUpdate(
        ObjectSceneEntry entry,
        ObjectSnapshot previousSnapshot,
        ObjectSnapshot nextSnapshot,
        bool persistSnapshot,
        ObjectSceneSource? sourceOverride,
        out ObjectSnapshot appliedSnapshot)
    {
        ObjectCollectionMaterializationState collectionState = _objectCollectionManager.EnsureCollectionMaterialized(nextSnapshot.CollectionId, [nextSnapshot]);
        if (ShouldStorePendingCollectionAssignment(collectionState, previousSnapshot, nextSnapshot))
        {
            return ApplyPendingCollectionAssignment(
                entry,
                previousSnapshot,
                nextSnapshot,
                persistSnapshot,
                sourceOverride,
                out appliedSnapshot);
        }

        switch (entry.SceneObject.TryUpdate(nextSnapshot))
        {
            case SceneObjectUpdateResult.Applied:
                UpsertActiveEntryMetadata(entry, sourceOverride ?? entry.Source);
                return CompleteActiveEntryUpdate(
                    entry,
                    previousSnapshot,
                    persistSnapshot,
                    out appliedSnapshot);

            case SceneObjectUpdateResult.RequiresRecreate:
                if (!TryRecreateEntry(entry, nextSnapshot, persistSnapshot, sourceOverride))
                {
                    appliedSnapshot = default!;
                    return ActiveEntryUpdateStatus.RecreateFailed;
                }

                appliedSnapshot = nextSnapshot;
                return ActiveEntryUpdateStatus.Applied;

            default:
                appliedSnapshot = default!;
                return ActiveEntryUpdateStatus.Rejected;
        }
    }

    private bool ShouldStorePendingCollectionAssignment(
        ObjectCollectionMaterializationState collectionState,
        ObjectSnapshot previousSnapshot,
        ObjectSnapshot nextSnapshot)
        => collectionState == ObjectCollectionMaterializationState.Pending
            && ObjectSnapshotUtility.IsCollectionOnlyChange(previousSnapshot, nextSnapshot)
            && !_collectionStore.TryGetCollectionResourceRevision(
                nextSnapshot.CollectionId,
                ObjectSnapshotUtility.GetRootResourcePath(nextSnapshot),
                out _);

    private ActiveEntryUpdateStatus ApplyPendingCollectionAssignment(
        ObjectSceneEntry entry,
        ObjectSnapshot previousSnapshot,
        ObjectSnapshot nextSnapshot,
        bool persistSnapshot,
        ObjectSceneSource? sourceOverride,
        out ObjectSnapshot appliedSnapshot)
    {
        if (entry.SceneObject.TryUpdateCollectionAssignment(nextSnapshot) != SceneObjectUpdateResult.Applied)
        {
            appliedSnapshot = default!;
            return ActiveEntryUpdateStatus.Rejected;
        }

        UpsertActiveEntryMetadata(
            entry,
            sourceOverride ?? entry.Source,
            SceneResourceCollectionState.Pending);

        return CompleteActiveEntryUpdate(
            entry,
            previousSnapshot,
            persistSnapshot,
            out appliedSnapshot);
    }

    private ActiveEntryUpdateStatus CompleteActiveEntryUpdate(
        ObjectSceneEntry entry,
        ObjectSnapshot previousSnapshot,
        bool persistSnapshot,
        out ObjectSnapshot appliedSnapshot)
    {
        if (persistSnapshot)
        {
            _persistenceState.ReplacePersistedSnapshot(previousSnapshot, entry.Snapshot);
            _revisionTracker.Increment(persistentChanged: true);
        }

        RefreshCollectionUsage(previousSnapshot, entry.Snapshot);
        appliedSnapshot = entry.Snapshot;
        return ActiveEntryUpdateStatus.Applied;
    }

    private void UpsertActiveEntryMetadata(
        ObjectSceneEntry entry,
        ObjectSceneSource source,
        SceneResourceCollectionState? resourceCollection = null)
    {
        _sceneState.UpsertEntry(new ObjectSceneEntry
        {
            SceneObject = entry.SceneObject,
            Source = source,
            ResourceCollection = resourceCollection
                ?? GetResourceCollectionState(entry.SceneObject.Snapshot),
        });
    }

    private SceneResourceCollectionState GetResourceCollectionState(ObjectSnapshot snapshot)
        => SceneResourceCollectionState.Current(_collectionStore.GetCollectionRevision(
            snapshot.CollectionId,
            ObjectSnapshotUtility.GetRootResourcePath(snapshot)));

    private void RestorePreparedObjectUpdates(IReadOnlyList<PreparedObjectUpdate> appliedUpdates)
    {
        if (appliedUpdates.Count == 0)
        {
            return;
        }

        for (var i = appliedUpdates.Count - 1; i >= 0; --i)
        {
            if (TryUpdate(appliedUpdates[i].PreviousSnapshot, out _))
            {
                continue;
            }

            _logger.LogError(
                "could not restore object {ObjectId} after batch update failure",
                appliedUpdates[i].ObjectId);
        }
    }

    private void RollbackCreatedSnapshots(IReadOnlyList<ObjectSnapshot> createdSnapshots)
    {
        for (var i = createdSnapshots.Count - 1; i >= 0; --i)
        {
            if (Remove(createdSnapshots[i].Id))
            {
                continue;
            }

            _logger.LogError(
                "could not remove object {ObjectId} while rolling back created batch state",
                createdSnapshots[i].Id);
        }
    }

    private void RestoreRemovedSnapshots(IReadOnlyList<ObjectSnapshot> removedSnapshots)
    {
        for (var i = 0; i < removedSnapshots.Count; ++i)
        {
            if (TryCreateObject(removedSnapshots[i], out _, applyDefaultLayout: false, persistSnapshot: true))
            {
                continue;
            }

            _logger.LogError(
                "could not recreate object {ObjectId} while rolling back removed batch state",
                removedSnapshots[i].Id);
        }
    }

    public void RefreshCollectionUsage(ObjectSnapshot? previousSnapshot, ObjectSnapshot? nextSnapshot)
        => _objectCollectionManager.RefreshCollectionUsage(previousSnapshot, nextSnapshot);

    private void DestroyEntry(ObjectSceneEntry entry, bool releaseCollectionUsage = true)
    {
        entry.SceneObject.Dispose();
        if (releaseCollectionUsage)
        {
            RefreshCollectionUsage(entry.Snapshot, null);
        }
    }

    private bool TryBuildSnapshotChangeBatch(IReadOnlyList<ObjectSnapshotChange> changes, out ObjectMutationBatch batch)
    {
        var steps = new List<IObjectMutationBatchStep>();
        var pendingCreates = new List<ObjectSnapshot>();
        var pendingUpdates = new List<PreparedObjectUpdate>();
        var pendingRemoves = new List<ObjectSnapshot>();
        var seenIds = new HashSet<Guid>();

        void FlushCreates()
        {
            if (pendingCreates.Count == 0)
            {
                return;
            }

            steps.Add(new CreateBatchStep([.. pendingCreates], applyDefaultLayout: false, persistSnapshots: true, sourceOverride: null));
            pendingCreates.Clear();
        }

        void FlushUpdates()
        {
            if (pendingUpdates.Count == 0)
            {
                return;
            }

            steps.Add(new UpdateBatchStep([.. pendingUpdates]));
            pendingUpdates.Clear();
        }

        void FlushRemoves()
        {
            if (pendingRemoves.Count == 0)
            {
                return;
            }

            steps.Add(new RemoveBatchStep([.. pendingRemoves]));
            pendingRemoves.Clear();
        }

        void Flush(BatchChangeKind pendingKind)
        {
            switch (pendingKind)
            {
                case BatchChangeKind.Create:
                    FlushCreates();
                    break;
                case BatchChangeKind.Update:
                    FlushUpdates();
                    break;
                case BatchChangeKind.Remove:
                    FlushRemoves();
                    break;
            }
        }

        var currentKind = BatchChangeKind.None;
        foreach (var change in changes)
        {
            if (!change.HasChange)
            {
                continue;
            }

            var nextKind = change switch
            {
                { Before: null, After: not null } => BatchChangeKind.Create,
                { Before: not null, After: null } => BatchChangeKind.Remove,
                { Before: not null, After: not null } => BatchChangeKind.Update,
                _ => BatchChangeKind.None,
            };

            if (nextKind == BatchChangeKind.None)
            {
                continue;
            }

            if (!seenIds.Add(change.ObjectId))
            {
                batch = default!;
                return false;
            }

            if (currentKind != nextKind)
            {
                Flush(currentKind);
                currentKind = nextKind;
            }

            switch (nextKind)
            {
                case BatchChangeKind.Create:
                    pendingCreates.Add(change.After!);
                    break;
                case BatchChangeKind.Update:
                    if (!TryPrepareObjectUpdate(change.After!, out var preparedUpdate))
                    {
                        batch = default!;
                        return false;
                    }

                    pendingUpdates.Add(preparedUpdate);
                    break;
                case BatchChangeKind.Remove:
                    pendingRemoves.Add(change.Before!);
                    break;
            }
        }

        Flush(currentKind);
        batch = new ObjectMutationBatch(steps);
        return true;
    }

    private static bool HasDuplicateObjectIds(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        var seenIds = new HashSet<Guid>();
        foreach (var snapshot in snapshots)
        {
            if (!seenIds.Add(snapshot.Id))
            {
                return true;
            }
        }

        return false;
    }

    private static ObjectSnapshot ApplyObjectPlacementOverrides(ObjectSnapshot snapshot, ObjectPlacementOverrides? overrides)
    {
        if (overrides is null)
        {
            return snapshot;
        }

        var nextSnapshot = snapshot;
        if (overrides.Visible.HasValue)
        {
            nextSnapshot = nextSnapshot with { Visible = overrides.Visible.Value };
        }

        if (overrides.FolderPath is not null)
        {
            nextSnapshot = nextSnapshot with { FolderPath = overrides.FolderPath };
        }

        if (overrides.Scale.HasValue)
        {
            nextSnapshot = nextSnapshot with
            {
                Transform = nextSnapshot.Transform with { Scale = overrides.Scale.Value },
            };
        }

        if (overrides.Model is null)
        {
            return nextSnapshot;
        }

        return nextSnapshot.Kind switch
        {
            ObjectKind.Light when overrides.Model is LightModel lightModel => nextSnapshot with { Model = lightModel },
            ObjectKind.BgObject when overrides.Model is BgObjectModel bgObjectModel => nextSnapshot with { Model = bgObjectModel },
            ObjectKind.Furniture when overrides.Model is FurnitureModel furnitureModel => nextSnapshot with { Model = furnitureModel },
            ObjectKind.Vfx when overrides.Model is VfxModel vfxModel => nextSnapshot with { Model = vfxModel },
            _ => nextSnapshot,
        };
    }

    private ObjectTemporaryMutationStatus TryCreateTemporaryEntry(ObjectSnapshot snapshot, ObjectSceneSource source)
    {
        if (!_objectKindService.CanCreate(snapshot.Kind))
        {
            _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.ServiceMissing);
            return ResolveTemporaryRuntimeFailureStatus(snapshot.Id);
        }

        if (!TryCreateObject(
                snapshot,
                out _,
                applyDefaultLayout: false,
                persistSnapshot: false,
                sourceOverride: source))
        {
            return ResolveTemporaryRuntimeFailureStatus(snapshot.Id);
        }

        return ObjectTemporaryMutationStatus.Success;
    }

    private ObjectTemporaryMutationStatus ResolveTemporaryRuntimeFailureStatus(Guid id)
    {
        if (!HasRetryableRuntimeFailure(id))
        {
            return ObjectTemporaryMutationStatus.Success;
        }

        _sceneState.MarkNeedsRefresh();
        return ObjectTemporaryMutationStatus.RuntimeApplyFailed;
    }

    private bool HasRetryableRuntimeFailure(Guid id)
        => !_sceneState.TryGetRuntimeFailureCode(id, out string? failureCode)
        || ObjectRuntimeFailureCodes.ShouldRetrySceneLoad(failureCode);

    private ISceneObjectFactory SceneObjectFactory
        => _sceneObjectFactory.Value;
}

