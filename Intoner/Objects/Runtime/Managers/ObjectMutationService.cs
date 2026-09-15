using Intoner.Objects.Collections;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Applies local and temporary object mutations against persisted state and the active scene state.
/// </summary>
internal interface IObjectMutationService
{
    /// <summary>
    /// Creates a new local object in front of the player and returns the applied snapshot.
    /// </summary>
    /// <param name="kind">The object kind to create.</param>
    /// <param name="snapshot">The created persisted snapshot when creation succeeded.</param>
    /// <param name="overrides">Optional initial placement overrides.</param>
    /// <returns>The new object id when creation succeeded.</returns>
    Guid? CreateObjectAtPlayer(ObjectKind kind, out ObjectSnapshot snapshot, ObjectPlacementOverrides? overrides = null);

    /// <summary>
    /// Creates a local object from the given snapshot.
    /// </summary>
    /// <param name="snapshot">The object snapshot to create.</param>
    /// <param name="expectedRevision">The persistent scene revision that must still be current.</param>
    /// <returns>The mutation result and applied snapshot.</returns>
    PersistentMutationResult CreateObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision);

    /// <summary>
    /// Restores a local object from the given snapshot without applying current layout defaults.
    /// </summary>
    /// <param name="snapshot">The object snapshot to restore.</param>
    /// <param name="expectedRevision">The persistent scene revision that must still be current.</param>
    /// <returns>The mutation result and applied snapshot.</returns>
    PersistentMutationResult RestoreObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision);

    /// <summary>Updates one persistent object and reports its commit state.</summary>
    /// <param name="snapshot">The replacement object snapshot.</param>
    /// <param name="expectedRevision">The persistent scene revision that must still be current.</param>
    /// <returns>The mutation result and applied snapshot.</returns>
    PersistentMutationResult UpdateObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision);

    /// <summary>Patches one persistent object and reports its commit state.</summary>
    /// <param name="id">The object id to patch.</param>
    /// <param name="patch">The partial object update.</param>
    /// <param name="expectedRevision">The persistent scene revision that must still be current.</param>
    /// <returns>The mutation result and applied snapshot.</returns>
    PersistentMutationResult PatchObjectSnapshot(Guid id, ObjectSnapshotPatch patch, long expectedRevision);

    /// <summary>Removes one persistent object and reports its commit state.</summary>
    /// <param name="id">The object id to remove.</param>
    /// <param name="expectedRevision">The persistent scene revision that must still be current.</param>
    /// <returns>The durable mutation result and removed snapshot.</returns>
    PersistentMutationResult RemoveObjectSnapshot(Guid id, long expectedRevision);

    /// <summary>Duplicates one persistent object and reports its durable commit state.</summary>
    /// <param name="id">The object id to duplicate.</param>
    /// <param name="expectedRevision">The persistent scene revision that must still be current.</param>
    /// <returns>The durable mutation result and duplicate snapshot.</returns>
    PersistentMutationResult DuplicateObjectSnapshot(Guid id, long expectedRevision);
}

/// <summary> manages active object entries while the object scene is reconciled </summary>
internal interface IObjectSceneMutationService
{
    /// <summary> prepares object duplicates without changing persistent or active state </summary>
    bool TryCreateDuplicates(
        IReadOnlyList<ObjectSnapshot> snapshots,
        out IReadOnlyList<ObjectSnapshot> duplicateSnapshots);

    /// <summary>
    /// applies checked object transitions, including world changes, while preserving other location fields
    /// </summary>
    /// <param name="changes">The object snapshot transitions to apply.</param>
    /// <returns>The applied, rejected, or recovery required batch status.</returns>
    SceneMutationStatus ApplySnapshotChanges(IReadOnlyList<SceneItemSnapshotChange> changes);

    /// <summary> previews transform and surface attachment changes without persistence or replacement </summary>
    /// <param name="before"> the expected active snapshot </param>
    /// <param name="after"> the requested transform or furniture attachment change </param>
    /// <param name="applied"> the sanitized snapshot on success </param>
    /// <returns> applied on success, rejected with the prior runtime preserved, or recovery required </returns>
    SceneMutationStatus PreviewChange(ObjectSnapshot before, ObjectSnapshot after, out ObjectSnapshot applied);

    /// <summary> creates one active object entry from the given snapshot </summary>
    bool TryCreateObject(
        ObjectSnapshot snapshot,
        out Guid id,
        bool applyDefaultLayout = true,
        bool persistSnapshot = true,
        ObjectSceneSource? sourceOverride = null);

    /// <summary> disposes active entries removed from the desired scene </summary>
    void DestroyEntries(IEnumerable<ObjectSceneEntry> entries);

    /// <summary> replaces an active runtime only after its replacement was created </summary>
    /// <param name="snapshot"> The desired object snapshot. </param>
    /// <param name="source"> The desired scene source. </param>
    /// <returns> true when replacement and ownership updates succeeded. </returns>
    bool TryReplaceObject(ObjectSnapshot snapshot, ObjectSceneSource source);

    /// <summary> clears every active object entry and optionally its persisted state </summary>
    void ClearAllActiveEntries(bool removePersistedState);
}

internal sealed class ObjectMutationService : IObjectMutationService, IObjectSceneMutationService
{
    private enum BatchChangeKind
    {
        None,
        Create,
        Update,
        Remove,
    }

    private enum ObjectUpdateStatus
    {
        Applied,
        Deferred,
        RecreateFailed,
        Rejected,
        StorageFailed,
        RecoveryRequired,
        AppliedWithRuntimeFailure,
    }

    private enum ObjectUpdateMode
    {
        Persist,
        PrepareBatch,
        RuntimeOnly,
    }

    private static PersistentMutationStatus ToPersistentMutationStatus(ObjectUpdateStatus status)
        => status switch
        {
            ObjectUpdateStatus.Applied or ObjectUpdateStatus.Deferred => PersistentMutationStatus.Success,
            ObjectUpdateStatus.StorageFailed => PersistentMutationStatus.StorageFailed,
            ObjectUpdateStatus.RecoveryRequired => PersistentMutationStatus.RecoveryRequired,
            _ => PersistentMutationStatus.RuntimeApplyFailed,
        };

    private static SceneMutationStatus ToSceneMutationStatus(PersistentMutationStatus status)
        => status switch
        {
            PersistentMutationStatus.Success => SceneMutationStatus.Applied,
            PersistentMutationStatus.RuntimeApplyFailed => SceneMutationStatus.AppliedWithRuntimeFailure,
            _ => SceneMutationStatus.RecoveryRequired,
        };

    private static SceneMutationStatus ToSceneMutationStatus(ObjectUpdateStatus status)
        => ToSceneMutationStatus(ToPersistentMutationStatus(status));

    private static SceneMutationStatus ResolveFailure(
        PersistentMutationStatus status,
        SceneMutationStatus rollbackStatus)
        => status == PersistentMutationStatus.RecoveryRequired
           || !rollbackStatus.IsFullyApplied()
            ? SceneMutationStatus.RecoveryRequired
            : SceneMutationStatus.Rejected;

    private readonly record struct PreparedObjectUpdate(ObjectSceneEntry? Entry, ObjectSnapshot PreviousSnapshot, ObjectSnapshot NextSnapshot)
    {
        public Guid ObjectId => NextSnapshot.Id;
        public bool HasChange => PreviousSnapshot != NextSnapshot;
        public bool IsPersistedOnly => Entry is null;
        public bool ChangesWorld => PreviousSnapshot.CreatedIn.WorldId != NextSnapshot.CreatedIn.WorldId;
    }

    private interface IObjectMutationBatchStep
    {
        SceneMutationStatus Apply(ObjectMutationService service);

        SceneMutationStatus Rollback(ObjectMutationService service);
    }

    private sealed class ObjectMutationBatch(IReadOnlyList<IObjectMutationBatchStep> steps)
    {
        private readonly IObjectMutationBatchStep[] _steps = [.. steps];

        public SceneMutationStatus Execute(ObjectMutationService service)
        {
            var appliedSteps = new List<IObjectMutationBatchStep>(_steps.Length);
            SceneMutationStatus result = SceneMutationStatus.Applied;
            foreach (var step in _steps)
            {
                SceneMutationStatus stepStatus = step.Apply(service);
                if (stepStatus.IsApplied())
                {
                    appliedSteps.Add(step);
                    result = result.MergeApplied(stepStatus);
                    continue;
                }

                bool recoveryRequired = stepStatus == SceneMutationStatus.RecoveryRequired;
                for (var i = appliedSteps.Count - 1; i >= 0; --i)
                {
                    recoveryRequired |= !appliedSteps[i].Rollback(service).IsFullyApplied();
                }

                return recoveryRequired
                    ? SceneMutationStatus.RecoveryRequired
                    : SceneMutationStatus.Rejected;
            }

            return result;
        }
    }

    private sealed class CreateBatchStep(
        IReadOnlyList<ObjectSnapshot> snapshots,
        bool applyDefaultLayout,
        bool persistSnapshots,
        ObjectSceneSource? sourceOverride) : IObjectMutationBatchStep
    {
        private readonly List<ObjectSnapshot> _resultSnapshots = new(snapshots.Count);

        public SceneMutationStatus Apply(ObjectMutationService service)
        {
            _resultSnapshots.Clear();
            SceneMutationStatus result = SceneMutationStatus.Applied;
            foreach (var snapshot in snapshots)
            {
                bool accepted = service.TryCreateObjectCore(
                    snapshot,
                    out _,
                    out ObjectSnapshot createdSnapshot,
                    applyDefaultLayout,
                    persistSnapshots,
                    sourceOverride,
                    out PersistentMutationStatus status);
                if (!accepted)
                {
                    SceneMutationStatus rollbackStatus = Rollback(service);
                    return ResolveFailure(status, rollbackStatus);
                }

                _resultSnapshots.Add(createdSnapshot);
                result = result.MergeApplied(ToSceneMutationStatus(status));
            }

            return result;
        }

        public SceneMutationStatus Rollback(ObjectMutationService service)
        {
            if (_resultSnapshots.Count == 0)
            {
                return SceneMutationStatus.Applied;
            }

            SceneMutationStatus status = service.RollbackCreatedSnapshots(_resultSnapshots);
            _resultSnapshots.Clear();
            return status;
        }
    }

    private sealed class UpdateBatchStep(IReadOnlyList<PreparedObjectUpdate> updates) : IObjectMutationBatchStep
    {
        private readonly List<PreparedObjectUpdate> _appliedUpdates = new(updates.Count);
        private bool _persistenceApplied;

        public SceneMutationStatus Apply(ObjectMutationService service)
        {
            _appliedUpdates.Clear();
            _persistenceApplied = false;
            List<ObjectSnapshot> snapshots = new(updates.Count);
            List<PreparedObjectUpdate>? deferredUpdates = null;
            SceneMutationStatus result = SceneMutationStatus.Applied;
            foreach (PreparedObjectUpdate update in updates)
            {
                ObjectUpdateStatus status = service.ApplyPreparedObjectUpdate(update, out ObjectSnapshot appliedSnapshot, ObjectUpdateMode.PrepareBatch);
                if (status == ObjectUpdateStatus.Deferred)
                {
                    (deferredUpdates ??= []).Add(update);
                }
                else if (status is not (ObjectUpdateStatus.Applied or ObjectUpdateStatus.AppliedWithRuntimeFailure))
                {
                    return Fail(service, ToPersistentMutationStatus(status));
                }

                result = result.MergeApplied(ToSceneMutationStatus(status));
                if (update.HasChange)
                {
                    _appliedUpdates.Add(update);
                    snapshots.Add(appliedSnapshot);
                }
            }

            if (snapshots.Count == 0)
            {
                return result;
            }

            PersistentMutationStatus storageStatus = Persist(service, snapshots);
            if (storageStatus != PersistentMutationStatus.Success)
            {
                return Fail(service, storageStatus);
            }

            _persistenceApplied = true;
            if (deferredUpdates is not null)
            {
                foreach (PreparedObjectUpdate update in deferredUpdates.OrderBy(static update => update.ChangesWorld))
                {
                    ObjectUpdateStatus status = service.ApplyPreparedObjectUpdate(update, out _, ObjectUpdateMode.RuntimeOnly);
                    if (status is not (ObjectUpdateStatus.Applied or ObjectUpdateStatus.AppliedWithRuntimeFailure))
                    {
                        return Fail(service, ToPersistentMutationStatus(status));
                    }

                    result = result.MergeApplied(ToSceneMutationStatus(status));
                }
            }

            return result;
        }

        public SceneMutationStatus Rollback(ObjectMutationService service)
        {
            if (_appliedUpdates.Count == 0)
            {
                return SceneMutationStatus.Applied;
            }

            SceneMutationStatus status = service.RestorePreparedObjectUpdates(_appliedUpdates);
            if (_persistenceApplied && Persist(service, _appliedUpdates.Select(static update => update.PreviousSnapshot).ToArray())
                != PersistentMutationStatus.Success)
            {
                service._logger.LogError("could not restore persisted objects after batch update failure");
                service._sceneState.MarkNeedsRefresh();
                status = SceneMutationStatus.RecoveryRequired;
            }

            _persistenceApplied = false;
            _appliedUpdates.Clear();
            return status;
        }

        private SceneMutationStatus Fail(ObjectMutationService service, PersistentMutationStatus status)
        {
            SceneMutationStatus result = ResolveFailure(status, Rollback(service));
            if (result == SceneMutationStatus.RecoveryRequired)
            {
                service._sceneState.MarkNeedsRefresh();
            }

            return result;
        }

        private static PersistentMutationStatus Persist(ObjectMutationService service, IReadOnlyList<ObjectSnapshot> snapshots)
        {
            PersistentMutationStatus status = service._persistenceState.UpsertPersistedSnapshots(snapshots);
            if (status == PersistentMutationStatus.Success)
            {
                service._revisionTracker.Increment(persistentChanged: true);
            }

            return status;
        }
    }

    private sealed class RemoveBatchStep(IReadOnlyList<ObjectSnapshot> snapshots) : IObjectMutationBatchStep
    {
        private readonly ObjectSnapshot[] _snapshots = [.. snapshots];
        private readonly List<ObjectSnapshot> _resultSnapshots = new(snapshots.Count);

        public SceneMutationStatus Apply(ObjectMutationService service)
        {
            _resultSnapshots.Clear();
            SceneMutationStatus result = SceneMutationStatus.Applied;
            foreach (var snapshot in _snapshots)
            {
                bool accepted = service.RemoveCore(snapshot.Id, out PersistentMutationStatus status);
                if (!accepted)
                {
                    SceneMutationStatus rollbackStatus = Rollback(service);
                    return ResolveFailure(status, rollbackStatus);
                }

                _resultSnapshots.Add(snapshot);
                result = result.MergeApplied(ToSceneMutationStatus(status));
            }

            return result;
        }

        public SceneMutationStatus Rollback(ObjectMutationService service)
        {
            if (_resultSnapshots.Count == 0)
            {
                return SceneMutationStatus.Applied;
            }

            SceneMutationStatus status = service.RestoreRemovedSnapshots(_resultSnapshots);
            _resultSnapshots.Clear();
            return status;
        }
    }

    private readonly ILogger<ObjectMutationService> _logger;
    private readonly Lock _stateLock;
    private readonly IObjectPersistenceState _persistenceState;
    private readonly IObjectIdentityService _objectIdentityService;
    private readonly IObjectSceneState _sceneState;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly IScenePlacementService _placementResolver;
    private readonly IObjectKindService _objectKindService;
    private readonly IObjectHousingModePolicy _housingModePolicy;
    private readonly Lazy<IObjectRuntimeFactory> _runtimeFactory;
    private readonly ISceneLocationService _locationService;
    private readonly IObjectCollectionManager _objectCollectionManager;
    private readonly IObjectResolvedCollectionStore _collectionStore;

    public ObjectMutationService(
        ILogger<ObjectMutationService> logger,
        ObjectStateLock stateLock,
        IObjectPersistenceState persistenceState,
        IObjectIdentityService objectIdentityService,
        IObjectSceneState sceneState,
        IObjectRevisionTracker revisionTracker,
        IScenePlacementService placementResolver,
        IObjectKindService objectKindService,
        IObjectHousingModePolicy housingModePolicy,
        Func<IObjectRuntimeFactory> runtimeFactoryFactory,
        ISceneLocationService locationService,
        IObjectCollectionManager objectCollectionManager,
        IObjectResolvedCollectionStore collectionStore)
    {
        _logger = logger;
        _stateLock = stateLock.Value;
        _persistenceState = persistenceState;
        _objectIdentityService = objectIdentityService;
        _sceneState = sceneState;
        _revisionTracker = revisionTracker;
        _placementResolver = placementResolver;
        _objectKindService = objectKindService;
        _housingModePolicy = housingModePolicy;
        _runtimeFactory = new Lazy<IObjectRuntimeFactory>(runtimeFactoryFactory);
        _locationService = locationService;
        _objectCollectionManager = objectCollectionManager;
        _collectionStore = collectionStore;
    }

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
                placementResolved ? transform : new SceneTransform(),
                _persistenceState.NextName(kind)),
            overrides);

        return TryCreateObject(nextSnapshot, out var id, out snapshot)
            ? id
            : null;
    }

    public PersistentMutationResult CreateObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => CreateObjectSnapshotCore(snapshot));

    private PersistentMutationResult CreateObjectSnapshotCore(ObjectSnapshot snapshot)
    {
        if (!_objectKindService.CanCreate(snapshot.Kind))
        {
            return PersistentMutationResult.Failed(
                PersistentMutationStatus.InvalidRequest,
                "object kind cannot be created");
        }

        return TryCreateObjectCore(
            snapshot,
            out Guid id,
            out _,
            applyDefaultLayout: true,
            persistSnapshot: true,
            sourceOverride: null,
            out PersistentMutationStatus failureStatus)
            ? PersistentMutationResult.Success(id)
            : PersistentMutationResult.Failed(failureStatus, "object could not be created");
    }

    public PersistentMutationResult RestoreObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => RestoreObjectSnapshotCore(snapshot));

    private PersistentMutationResult RestoreObjectSnapshotCore(ObjectSnapshot snapshot)
    {
        if (!_objectKindService.CanCreate(snapshot.Kind))
        {
            return PersistentMutationResult.Failed(
                PersistentMutationStatus.InvalidRequest,
                "object kind cannot be created");
        }

        return TryCreateObjectCore(
            snapshot,
            out Guid id,
            out _,
            applyDefaultLayout: false,
            persistSnapshot: true,
            sourceOverride: null,
            out PersistentMutationStatus failureStatus)
            ? PersistentMutationResult.Success(id)
            : PersistentMutationResult.Failed(failureStatus, "object could not be imported");
    }

    public PersistentMutationResult UpdateObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => UpdateObjectSnapshotCore(snapshot));

    private PersistentMutationResult UpdateObjectSnapshotCore(ObjectSnapshot snapshot)
    {
        bool accepted = TryUpdateCore(snapshot, out _, out PersistentMutationStatus status);
        return CreatePersistentMutationResult(
            accepted,
            status,
            snapshot.Id,
            "object could not be updated");
    }

    public PersistentMutationResult PatchObjectSnapshot(Guid id, ObjectSnapshotPatch patch, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => PatchObjectSnapshotCore(id, patch));

    private PersistentMutationResult PatchObjectSnapshotCore(Guid id, ObjectSnapshotPatch patch)
    {
        if (!TryResolveLocalPersistentSnapshot(id, out ObjectSnapshot snapshot))
        {
            return PersistentMutationResult.Failed(PersistentMutationStatus.NotFound, "object was not found");
        }

        if (!patch.HasChanges)
        {
            return PersistentMutationResult.Success(id);
        }

        return UpdateObjectSnapshotCore(ObjectSnapshotUtility.ApplyPatch(snapshot, patch));
    }

    public PersistentMutationResult RemoveObjectSnapshot(Guid id, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => RemoveObjectSnapshotCore(id));

    private PersistentMutationResult RemoveObjectSnapshotCore(Guid id)
    {
        bool accepted = RemoveCore(id, out PersistentMutationStatus status);
        return CreatePersistentMutationResult(
            accepted,
            status,
            id,
            "object could not be removed");
    }

    public PersistentMutationResult DuplicateObjectSnapshot(Guid id, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => DuplicateObjectSnapshotCore(id));

    private PersistentMutationResult DuplicateObjectSnapshotCore(Guid id)
    {
        if (!TryBuildDuplicateSnapshot(id, out ObjectSnapshot duplicate))
        {
            return PersistentMutationResult.Failed(PersistentMutationStatus.NotFound, "object was not found");
        }

        return TryCreateObjectCore(
            duplicate,
            out Guid duplicateId,
            out _,
            applyDefaultLayout: true,
            persistSnapshot: true,
            sourceOverride: null,
            out PersistentMutationStatus failureStatus)
            ? PersistentMutationResult.Success(duplicateId)
            : PersistentMutationResult.Failed(failureStatus, "object could not be duplicated");
    }

    private PersistentMutationResult ExecutePersistentMutation(
        long expectedRevision,
        Func<PersistentMutationResult> mutation)
    {
        lock (_stateLock)
        {
            if (expectedRevision <= 0)
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.InvalidRequest,
                    "expected revision must be positive");
            }

            if (expectedRevision != _revisionTracker.GetPersistentSceneRevision())
            {
                return PersistentMutationResult.Failed(
                    PersistentMutationStatus.Conflict,
                    "persistent scene revision has changed");
            }

            PersistentMutationResult result = mutation();
            if (!result.IsAccepted)
            {
                return result;
            }

            ObjectRevisionSnapshot revisions = _revisionTracker.GetSnapshot();
            return result.WithRevisions(
                revisions.SceneRevision,
                revisions.PersistentSceneRevision,
                revisions.SavedLayoutsRevision);
        }
    }

    private static PersistentMutationResult CreatePersistentMutationResult(
        bool accepted,
        PersistentMutationStatus status,
        Guid entityId,
        string failureMessage)
    {
        if (!accepted)
        {
            return PersistentMutationResult.Failed(status, failureMessage);
        }

        return status == PersistentMutationStatus.Success
            ? PersistentMutationResult.Success(entityId)
            : PersistentMutationResult.AcceptedRuntimeFailure(
                entityId,
                "persistent state was accepted but runtime reconciliation is pending");
    }

    private bool TryUpdateCore(
        ObjectSnapshot snapshot,
        out ObjectSnapshot appliedSnapshot,
        out PersistentMutationStatus failureStatus,
        bool allowWorldChange = false,
        ObjectUpdateMode mode = ObjectUpdateMode.Persist)
    {
        appliedSnapshot = default!;
        if (!TryPrepareObjectUpdate(snapshot, out var preparedUpdate, allowWorldChange))
        {
            failureStatus = PersistentMutationStatus.NotFound;
            return false;
        }

        ObjectUpdateStatus status = ApplyPreparedObjectUpdate(preparedUpdate, out appliedSnapshot, mode);
        failureStatus = ToPersistentMutationStatus(status);
        return status is ObjectUpdateStatus.Applied or ObjectUpdateStatus.AppliedWithRuntimeFailure;
    }

    public bool TryCreateDuplicates(
        IReadOnlyList<ObjectSnapshot> snapshots,
        out IReadOnlyList<ObjectSnapshot> duplicateSnapshots)
    {
        lock (_stateLock)
        {
            duplicateSnapshots = [];
            if (!TryBuildDuplicateSnapshots(snapshots, out List<ObjectSnapshot> requestedSnapshots))
            {
                return false;
            }

            duplicateSnapshots = requestedSnapshots;
            return true;
        }
    }

    public SceneMutationStatus ApplySnapshotChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        lock (_stateLock)
        {
            if (changes.Count == 0)
            {
                return SceneMutationStatus.Applied;
            }

            return !TryBuildSnapshotChangeBatch(changes, out var batch)
                ? SceneMutationStatus.Rejected
                : batch.Execute(this);
        }
    }

    public SceneMutationStatus PreviewChange(ObjectSnapshot before, ObjectSnapshot after, out ObjectSnapshot applied)
    {
        lock (_stateLock)
        {
            applied = before;
            ObjectData model = after.Model;
            if (model is FurnitureModel furniture && before.Model is FurnitureModel previousFurniture)
            {
                model = furniture with { AttachmentParentId = previousFurniture.AttachmentParentId };
            }

            if (after with { Transform = before.Transform, Model = model } != before
                || !_sceneState.TryGetEntry(before.Id, out ObjectSceneEntry entry)
                || entry.Source.IsRuntimeOnly)
            {
                return SceneMutationStatus.Rejected;
            }

            if (entry.Snapshot == after)
            {
                applied = after;
                return SceneMutationStatus.Applied;
            }

            if (entry.Snapshot != before
                || !TrySanitizeUpdatedSnapshot(after, before, out ObjectSnapshot sanitized, allowWorldChange: false)
                || !_housingModePolicy.TryValidateSnapshot(sanitized, out _))
            {
                return SceneMutationStatus.Rejected;
            }

            try
            {
                if (entry.Runtime.TryUpdate(sanitized) == ObjectRuntimeUpdateResult.Applied)
                {
                    applied = entry.Snapshot;
                    _revisionTracker.Increment();
                    return SceneMutationStatus.Applied;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "object edit preview failed for {ObjectId}", before.Id);
            }

            try
            {
                if (entry.Runtime.TryUpdate(before) == ObjectRuntimeUpdateResult.Applied)
                {
                    _revisionTracker.Increment();
                    return SceneMutationStatus.Rejected;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "object edit preview restoration failed for {ObjectId}", before.Id);
            }

            _sceneState.MarkNeedsRefresh();
            _revisionTracker.Increment();
            return SceneMutationStatus.RecoveryRequired;
        }
    }

    private bool RemoveCore(Guid id, out PersistentMutationStatus failureStatus)
    {
        if (_sceneState.TryGetEntry(id, out var entry))
        {
            if (entry.Source.IsRuntimeOnly)
            {
                failureStatus = PersistentMutationStatus.NotFound;
                return false;
            }

            if (!_persistenceState.TryRemovePersistedSnapshot(entry.Snapshot))
            {
                failureStatus = PersistentMutationStatus.StorageFailed;
                return false;
            }

            _revisionTracker.Increment(persistentChanged: true);
            bool runtimeApplied = TryRemoveActiveEntry(entry, releaseCollectionUsage: true);
            _sceneState.ClearRuntimeFailure(id);
            failureStatus = runtimeApplied
                ? PersistentMutationStatus.Success
                : PersistentMutationStatus.RuntimeApplyFailed;
            return true;
        }

        if (!_persistenceState.TryGetPersistedSnapshot(id, out var persistedSnapshot))
        {
            failureStatus = PersistentMutationStatus.NotFound;
            return false;
        }

        if (!_persistenceState.TryRemovePersistedSnapshot(persistedSnapshot))
        {
            failureStatus = PersistentMutationStatus.StorageFailed;
            return false;
        }

        _revisionTracker.Increment(persistentChanged: true);
        bool persistedRuntimeApplied = TryRefreshCollectionUsage(persistedSnapshot, null);
        _sceneState.ClearRuntimeFailure(id);
        failureStatus = persistedRuntimeApplied
            ? PersistentMutationStatus.Success
            : PersistentMutationStatus.RuntimeApplyFailed;
        return true;
    }

    public bool TryCreateObject(ObjectSnapshot snapshot, out Guid id, bool applyDefaultLayout = true, bool persistSnapshot = true, ObjectSceneSource? sourceOverride = null)
    {
        lock (_stateLock)
        {
            return TryCreateObjectCore(snapshot, out id, out _, applyDefaultLayout, persistSnapshot, sourceOverride, out _);
        }
    }

    private bool TryCreateObject(ObjectSnapshot snapshot, out Guid id, out ObjectSnapshot createdSnapshot, bool applyDefaultLayout = true, bool persistSnapshot = true, ObjectSceneSource? sourceOverride = null)
    {
        lock (_stateLock)
        {
            return TryCreateObjectCore(snapshot, out id, out createdSnapshot, applyDefaultLayout, persistSnapshot, sourceOverride, out _);
        }
    }

    private bool TryCreateObjectCore(
        ObjectSnapshot snapshot,
        out Guid id,
        out ObjectSnapshot createdSnapshot,
        bool applyDefaultLayout,
        bool persistSnapshot,
        ObjectSceneSource? sourceOverride,
        out PersistentMutationStatus failureStatus)
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
            failureStatus = PersistentMutationStatus.InvalidRequest;
            return false;
        }

        if (_sceneState.TryGetEntry(sanitizedSnapshot.Id, out _))
        {
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.DuplicateObject);
            id = Guid.Empty;
            failureStatus = PersistentMutationStatus.Conflict;
            return false;
        }

        var source = sourceOverride ?? _persistenceState.ResolveSceneSource(sanitizedSnapshot);
        var persistSceneSnapshot = persistSnapshot && source.IsLocalPersistent;
        if (persistSceneSnapshot
            && !_objectIdentityService.TryValidatePersistentObject(sanitizedSnapshot.Id, out _))
        {
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.DuplicateObject);
            id = Guid.Empty;
            failureStatus = PersistentMutationStatus.Conflict;
            return false;
        }

        if (source.UsesUserHousingPolicy
            && !_housingModePolicy.TryValidateCreate(sanitizedSnapshot, GetHousingPolicySceneSnapshots(), out var housingModeError))
        {
            _logger.LogDebug("object create rejected by housing mode: {Reason}", housingModeError);
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.HousingModeRejected);
            id = Guid.Empty;
            failureStatus = PersistentMutationStatus.InvalidRequest;
            return false;
        }

        _objectCollectionManager.EnsureCollectionMaterialized(sanitizedSnapshot.CollectionId, [sanitizedSnapshot]);

        SceneResourceCollectionState resourceCollection = GetResourceCollectionState(sanitizedSnapshot);
        if (!_runtimeFactory.Value.TryCreate(sanitizedSnapshot, out var runtime, out string failureCode))
        {
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, failureCode);
            id = Guid.Empty;
            failureStatus = PersistentMutationStatus.RuntimeApplyFailed;
            return false;
        }

        ObjectSceneEntry createdEntry = new()
        {
            Runtime = runtime,
            Source = source,
            ResourceCollection = resourceCollection,
        };
        _sceneState.UpsertEntry(createdEntry);
        if (!TryRefreshCollectionUsage(null, sanitizedSnapshot))
        {
            bool recovered = TryRemoveActiveEntry(createdEntry, releaseCollectionUsage: true);
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.CollectionUsageFailed);
            id = Guid.Empty;
            failureStatus = recovered
                ? PersistentMutationStatus.RuntimeApplyFailed
                : PersistentMutationStatus.RecoveryRequired;
            return false;
        }

        if (persistSceneSnapshot && !_persistenceState.TryUpsertPersistedSnapshot(sanitizedSnapshot))
        {
            bool recovered = TryRemoveActiveEntry(createdEntry, releaseCollectionUsage: true);

            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.PersistenceFailed);
            id = Guid.Empty;
            failureStatus = recovered
                ? PersistentMutationStatus.StorageFailed
                : PersistentMutationStatus.RecoveryRequired;
            return false;
        }

        _revisionTracker.Increment(persistentChanged: persistSceneSnapshot);
        id = sanitizedSnapshot.Id;
        createdSnapshot = sanitizedSnapshot;
        failureStatus = PersistentMutationStatus.Success;
        return true;
    }

    public bool TryReplaceObject(ObjectSnapshot snapshot, ObjectSceneSource source)
    {
        lock (_stateLock)
        {
            if (!_sceneState.TryGetEntry(snapshot.Id, out ObjectSceneEntry entry))
            {
                return false;
            }

            if (!_objectKindService.TrySanitizeSnapshot(snapshot, out ObjectSnapshot sanitizedSnapshot))
            {
                _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.InvalidObject);
                return false;
            }

            if (source.UsesUserHousingPolicy
                && !_housingModePolicy.TryValidateCreate(sanitizedSnapshot, GetHousingPolicySceneSnapshots(), out _))
            {
                _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.HousingModeRejected);
                return false;
            }

            return TryReplaceEntryRuntime(entry, entry.Snapshot, sanitizedSnapshot,
                persistSnapshot: false, sourceOverride: source, updateCollectionUsage: true, out _)
                == ObjectUpdateStatus.Applied;
        }
    }

    private bool TryRemoveActiveEntry(ObjectSceneEntry entry, bool releaseCollectionUsage)
    {
        _sceneState.TryRemoveEntry(entry.Snapshot.Id, out _);
        return TryDestroyEntry(entry, releaseCollectionUsage);
    }

    public void DestroyEntries(IEnumerable<ObjectSceneEntry> entries)
    {
        foreach (var entry in entries)
        {
            _ = TryDestroyEntry(entry);
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
            _ = _persistenceState.TryRemovePersistedSnapshot(entry.Snapshot);
        }
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

    private bool TryPrepareObjectUpdate(ObjectSnapshot snapshot, out PreparedObjectUpdate preparedUpdate, bool allowWorldChange = false)
    {
        if (_sceneState.TryGetEntry(snapshot.Id, out var entry))
        {
            if (entry.Source.IsRuntimeOnly
                || !TrySanitizeUpdatedSnapshot(snapshot, entry.Snapshot, out var nextSnapshot, allowWorldChange)
                || !_housingModePolicy.TryValidateSnapshot(nextSnapshot, out _))
            {
                preparedUpdate = default;
                return false;
            }

            preparedUpdate = new PreparedObjectUpdate(entry, entry.Snapshot, nextSnapshot);
            return true;
        }

        if (!_persistenceState.TryGetPersistedSnapshot(snapshot.Id, out var persistedSnapshot)
            || !TrySanitizeUpdatedSnapshot(snapshot, persistedSnapshot, out var persistedNextSnapshot, allowWorldChange)
            || !_housingModePolicy.TryValidateSnapshot(persistedNextSnapshot, out _))
        {
            preparedUpdate = default;
            return false;
        }

        preparedUpdate = new PreparedObjectUpdate(null, persistedSnapshot, persistedNextSnapshot);
        return true;
    }

    private ObjectUpdateStatus ApplyPreparedObjectUpdate(
        PreparedObjectUpdate preparedUpdate,
        out ObjectSnapshot appliedSnapshot,
        ObjectUpdateMode mode)
    {
        appliedSnapshot = preparedUpdate.NextSnapshot;
        if (!preparedUpdate.HasChange)
        {
            return ObjectUpdateStatus.Applied;
        }

        if (preparedUpdate.ChangesWorld && mode == ObjectUpdateMode.PrepareBatch)
        {
            return ObjectUpdateStatus.Deferred;
        }

        if (preparedUpdate.IsPersistedOnly || preparedUpdate.ChangesWorld)
        {
            if (mode == ObjectUpdateMode.Persist)
            {
                if (!_persistenceState.TryReplacePersistedSnapshot(preparedUpdate.PreviousSnapshot, preparedUpdate.NextSnapshot))
                {
                    appliedSnapshot = default!;
                    return ObjectUpdateStatus.StorageFailed;
                }

                _revisionTracker.Increment(persistentChanged: true);
            }

            if (preparedUpdate.ChangesWorld)
            {
                bool removed = preparedUpdate.Entry is null || TryRemoveActiveEntry(preparedUpdate.Entry, releaseCollectionUsage: true);
                _sceneState.ClearRuntimeFailure(preparedUpdate.ObjectId);
                _sceneState.MarkNeedsRefresh();
                return removed ? ObjectUpdateStatus.Applied : ObjectUpdateStatus.AppliedWithRuntimeFailure;
            }

            return ObjectUpdateStatus.Applied;
        }

        ObjectUpdateStatus status = ApplyActiveEntryUpdate(
            preparedUpdate.Entry!,
            preparedUpdate.PreviousSnapshot,
            preparedUpdate.NextSnapshot,
            mode,
            sourceOverride: null,
            out appliedSnapshot);
        if (status == ObjectUpdateStatus.RecoveryRequired)
        {
            _revisionTracker.Increment();
        }

        return status;
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

    private bool TryBuildDuplicateSnapshots(
        IReadOnlyList<ObjectSnapshot> snapshots,
        out List<ObjectSnapshot> duplicateSnapshots)
    {
        duplicateSnapshots = new List<ObjectSnapshot>(snapshots.Count);
        var seenIds = new HashSet<Guid>();
        foreach (Guid id in snapshots.Select(static snapshot => snapshot.Id))
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

    private bool TrySanitizeUpdatedSnapshot(ObjectSnapshot snapshot, ObjectSnapshot currentSnapshot, out ObjectSnapshot sanitizedSnapshot, bool allowWorldChange)
    {
        SceneCreationContext location = currentSnapshot.CreatedIn;
        if (allowWorldChange && snapshot.CreatedIn.WorldId != location.WorldId)
        {
            if (!location.Scope.IsValid || !_locationService.TryResolveWorld(snapshot.CreatedIn.WorldId, out SceneWorldInfo? world))
            {
                sanitizedSnapshot = default!;
                return false;
            }

            // keep captured labels intact when history restores an earlier world
            location = location with { WorldId = world.Id, WorldName = snapshot.CreatedIn.WorldName };
        }

        return _objectKindService.TrySanitizeSnapshot(
            snapshot with
            {
                Id = currentSnapshot.Id,
                Kind = currentSnapshot.Kind,
                LayoutId = currentSnapshot.LayoutId,
                CreatedAtUtc = currentSnapshot.CreatedAtUtc,
                CreatedIn = location,
            },
            out sanitizedSnapshot);
    }

    private ObjectUpdateStatus ApplyActiveEntryUpdate(
        ObjectSceneEntry entry,
        ObjectSnapshot previousSnapshot,
        ObjectSnapshot nextSnapshot,
        ObjectUpdateMode mode,
        ObjectSceneSource? sourceOverride,
        out ObjectSnapshot appliedSnapshot)
    {
        bool persistSnapshot = mode == ObjectUpdateMode.Persist;
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

        SceneResourceCollectionState resourceCollection = GetResourceCollectionState(nextSnapshot);
        switch (entry.Runtime.TryUpdate(nextSnapshot))
        {
            case ObjectRuntimeUpdateResult.Applied:
                UpsertActiveEntryMetadata(entry, sourceOverride ?? entry.Source, resourceCollection);
                return CompleteActiveEntryUpdate(
                    entry,
                    previousSnapshot,
                    persistSnapshot,
                    out appliedSnapshot);

            case ObjectRuntimeUpdateResult.RequiresRecreate:
                if (mode == ObjectUpdateMode.PrepareBatch)
                {
                    appliedSnapshot = nextSnapshot;
                    return ObjectUpdateStatus.Deferred;
                }

                return TryReplaceEntryRuntime(
                    entry,
                    previousSnapshot,
                    nextSnapshot,
                    persistSnapshot,
                    sourceOverride,
                    updateCollectionUsage: true,
                    out appliedSnapshot);

            default:
                appliedSnapshot = default!;
                return ObjectUpdateStatus.Rejected;
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

    private ObjectUpdateStatus ApplyPendingCollectionAssignment(
        ObjectSceneEntry entry,
        ObjectSnapshot previousSnapshot,
        ObjectSnapshot nextSnapshot,
        bool persistSnapshot,
        ObjectSceneSource? sourceOverride,
        out ObjectSnapshot appliedSnapshot)
    {
        if (entry.Runtime.TryUpdateCollectionAssignment(nextSnapshot) != ObjectRuntimeUpdateResult.Applied)
        {
            appliedSnapshot = default!;
            return ObjectUpdateStatus.Rejected;
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

    private ObjectUpdateStatus CompleteActiveEntryUpdate(
        ObjectSceneEntry entry,
        ObjectSnapshot previousSnapshot,
        bool persistSnapshot,
        out ObjectSnapshot appliedSnapshot)
    {
        if (persistSnapshot)
        {
            if (!_persistenceState.TryReplacePersistedSnapshot(previousSnapshot, entry.Snapshot))
            {
                appliedSnapshot = default!;
                return TryRestoreEntryRuntime(entry, previousSnapshot)
                    ? ObjectUpdateStatus.StorageFailed
                    : ObjectUpdateStatus.RecoveryRequired;
            }

            _revisionTracker.Increment(persistentChanged: true);
        }

        bool runtimeApplied = TryRefreshCollectionUsage(previousSnapshot, entry.Snapshot);
        appliedSnapshot = entry.Snapshot;
        return runtimeApplied
            ? ObjectUpdateStatus.Applied
            : ObjectUpdateStatus.AppliedWithRuntimeFailure;
    }

    private ObjectUpdateStatus TryReplaceEntryRuntime(
        ObjectSceneEntry entry,
        ObjectSnapshot previousSnapshot,
        ObjectSnapshot nextSnapshot,
        bool persistSnapshot,
        ObjectSceneSource? sourceOverride,
        bool updateCollectionUsage,
        out ObjectSnapshot appliedSnapshot)
    {
        appliedSnapshot = default!;
        nint previousAddress = entry.Runtime.Address;
        using IDisposable? scope = _logger.BeginScope("object={ObjectId}; operation=runtime replacement", nextSnapshot.Id);
        _logger.LogDebug("object runtime replacement started; object={ObjectId}; oldAddress=0x{OldAddress:X}; oldCollection={OldCollectionId}; newCollection={NewCollectionId}; oldRevision={OldRevision}",
            nextSnapshot.Id, (ulong)previousAddress, previousSnapshot.CollectionId, nextSnapshot.CollectionId, entry.ResourceCollection.Revision);
        if (!_objectKindService.CanCreate(nextSnapshot.Kind))
        {
            _logger.LogWarning("object runtime replacement rejected; object={ObjectId}; retainedAddress=0x{Address:X}; failure={FailureCode}",
                nextSnapshot.Id, (ulong)previousAddress, ObjectRuntimeFailureCodes.ServiceMissing);
            _sceneState.SetRuntimeFailure(nextSnapshot.Id, ObjectRuntimeFailureCodes.ServiceMissing);
            return ObjectUpdateStatus.RecreateFailed;
        }

        _objectCollectionManager.EnsureCollectionMaterialized(nextSnapshot.CollectionId, [nextSnapshot]);
        SceneResourceCollectionState resourceCollection = GetResourceCollectionState(nextSnapshot);
        if (!_runtimeFactory.Value.TryCreate(nextSnapshot, out IObjectRuntime replacement, out string failureCode))
        {
            _logger.LogDebug("object runtime replacement rejected; object={ObjectId}; retainedAddress=0x{Address:X}; failure={FailureCode}",
                nextSnapshot.Id, (ulong)previousAddress, failureCode);
            _sceneState.SetRuntimeFailure(nextSnapshot.Id, failureCode);
            return ObjectUpdateStatus.RecreateFailed;
        }

        nint replacementAddress = replacement.Address;
        _logger.LogDebug("object runtime replacement created; object={ObjectId}; oldAddress=0x{OldAddress:X}; newAddress=0x{NewAddress:X}; revision={Revision}",
            nextSnapshot.Id, (ulong)previousAddress, (ulong)replacementAddress, resourceCollection.Revision);
        if (persistSnapshot && !_persistenceState.TryReplacePersistedSnapshot(previousSnapshot, nextSnapshot))
        {
            bool disposed = TryDisposeRuntime(replacement, nextSnapshot.Id);
            _logger.LogWarning("object runtime replacement rolled back; reason=storage failed; object={ObjectId}; retainedAddress=0x{OldAddress:X}; rejectedAddress=0x{NewAddress:X}; cleanupSucceeded={CleanupSucceeded}",
                nextSnapshot.Id, (ulong)previousAddress, (ulong)replacementAddress, disposed);
            return disposed
                ? ObjectUpdateStatus.StorageFailed
                : ObjectUpdateStatus.RecoveryRequired;
        }

        if (persistSnapshot)
        {
            _revisionTracker.Increment(persistentChanged: true);
        }

        _sceneState.UpsertEntry(new ObjectSceneEntry
        {
            Runtime = replacement,
            Source = sourceOverride ?? entry.Source,
            ResourceCollection = resourceCollection,
        });
        bool runtimeApplied = TryDestroyEntry(entry, releaseCollectionUsage: false);
        if (updateCollectionUsage)
        {
            runtimeApplied &= TryRefreshCollectionUsage(previousSnapshot, nextSnapshot);
        }

        _sceneState.ClearRuntimeFailure(nextSnapshot.Id);
        appliedSnapshot = nextSnapshot;
        _logger.Log(runtimeApplied ? LogLevel.Debug : LogLevel.Warning,
            "object runtime replacement completed; object={ObjectId}; oldAddress=0x{OldAddress:X}; newAddress=0x{NewAddress:X}; cleanupAndUsageSucceeded={Succeeded}",
            nextSnapshot.Id, (ulong)previousAddress, (ulong)replacementAddress, runtimeApplied);
        return runtimeApplied
            ? ObjectUpdateStatus.Applied
            : ObjectUpdateStatus.AppliedWithRuntimeFailure;
    }

    private bool TryRestoreEntryRuntime(ObjectSceneEntry entry, ObjectSnapshot previousSnapshot)
    {
        SceneResourceCollectionState resourceCollection = GetResourceCollectionState(previousSnapshot);
        switch (entry.Runtime.TryUpdate(previousSnapshot))
        {
            case ObjectRuntimeUpdateResult.Applied:
                UpsertActiveEntryMetadata(entry, entry.Source, resourceCollection);
                return true;
            case ObjectRuntimeUpdateResult.RequiresRecreate:
                return TryReplaceEntryRuntime(
                        entry,
                        entry.Snapshot,
                        previousSnapshot,
                        persistSnapshot: false,
                        sourceOverride: entry.Source,
                        updateCollectionUsage: false,
                        out _)
                    == ObjectUpdateStatus.Applied;
            default:
                _sceneState.MarkNeedsRefresh();
                return false;
        }
    }

    private void UpsertActiveEntryMetadata(
        ObjectSceneEntry entry,
        ObjectSceneSource source,
        SceneResourceCollectionState resourceCollection)
    {
        _sceneState.UpsertEntry(new ObjectSceneEntry
        {
            Runtime = entry.Runtime,
            Source = source,
            ResourceCollection = resourceCollection,
        });
    }

    private SceneResourceCollectionState GetResourceCollectionState(ObjectSnapshot snapshot)
        => SceneResourceCollectionState.Current(_collectionStore.GetCollectionRevision(
            snapshot.CollectionId,
            ObjectSnapshotUtility.GetRootResourcePath(snapshot)));

    private SceneMutationStatus RestorePreparedObjectUpdates(IReadOnlyList<PreparedObjectUpdate> appliedUpdates)
    {
        if (appliedUpdates.Count == 0)
        {
            return SceneMutationStatus.Applied;
        }

        SceneMutationStatus result = SceneMutationStatus.Applied;
        bool recovered = true;
        for (var i = appliedUpdates.Count - 1; i >= 0; --i)
        {
            bool accepted = TryUpdateCore(
                appliedUpdates[i].PreviousSnapshot,
                out _,
                out PersistentMutationStatus status,
                allowWorldChange: true,
                mode: ObjectUpdateMode.RuntimeOnly);
            if (accepted)
            {
                result = result.MergeApplied(ToSceneMutationStatus(status));
                continue;
            }

            recovered = false;
            _logger.LogError(
                "could not restore object {ObjectId} after batch update failure",
                appliedUpdates[i].ObjectId);
        }

        if (recovered)
        {
            return result;
        }

        _sceneState.MarkNeedsRefresh();
        return SceneMutationStatus.RecoveryRequired;
    }

    private SceneMutationStatus RollbackCreatedSnapshots(IReadOnlyList<ObjectSnapshot> createdSnapshots)
    {
        SceneMutationStatus result = SceneMutationStatus.Applied;
        bool recovered = true;
        for (var i = createdSnapshots.Count - 1; i >= 0; --i)
        {
            bool accepted = RemoveCore(
                createdSnapshots[i].Id,
                out PersistentMutationStatus status);
            if (accepted)
            {
                result = result.MergeApplied(ToSceneMutationStatus(status));
                continue;
            }

            recovered = false;
            _logger.LogError(
                "could not remove object {ObjectId} while rolling back created batch state",
                createdSnapshots[i].Id);
        }

        if (recovered)
        {
            return result;
        }

        _sceneState.MarkNeedsRefresh();
        return SceneMutationStatus.RecoveryRequired;
    }

    private SceneMutationStatus RestoreRemovedSnapshots(IReadOnlyList<ObjectSnapshot> removedSnapshots)
    {
        SceneMutationStatus result = SceneMutationStatus.Applied;
        bool recovered = true;
        for (var i = 0; i < removedSnapshots.Count; ++i)
        {
            bool accepted = TryCreateObjectCore(
                removedSnapshots[i],
                out _,
                out _,
                applyDefaultLayout: false,
                persistSnapshot: true,
                sourceOverride: null,
                out PersistentMutationStatus status);
            if (accepted)
            {
                result = result.MergeApplied(ToSceneMutationStatus(status));
                continue;
            }

            recovered = false;
            _logger.LogError(
                "could not recreate object {ObjectId} while rolling back removed batch state",
                removedSnapshots[i].Id);
        }

        if (recovered)
        {
            return result;
        }

        _sceneState.MarkNeedsRefresh();
        return SceneMutationStatus.RecoveryRequired;
    }

    private bool TryDestroyEntry(ObjectSceneEntry entry, bool releaseCollectionUsage = true)
    {
        bool success = TryDisposeRuntime(entry.Runtime, entry.Snapshot.Id);

        if (releaseCollectionUsage)
        {
            success &= TryRefreshCollectionUsage(entry.Snapshot, null);
        }

        return success;
    }

    private bool TryDisposeRuntime(IObjectRuntime runtime, Guid objectId)
    {
        try
        {
            runtime.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to destroy object runtime {ObjectId}", objectId);
            _sceneState.MarkNeedsRefresh();
            return false;
        }
    }

    private bool TryRefreshCollectionUsage(ObjectSnapshot? previousSnapshot, ObjectSnapshot? nextSnapshot)
    {
        try
        {
            _objectCollectionManager.RefreshCollectionUsage(previousSnapshot, nextSnapshot);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "failed to refresh object collection usage");
            _sceneState.MarkNeedsRefresh();
            return false;
        }
    }

    private bool TryBuildSnapshotChangeBatch(IReadOnlyList<SceneItemSnapshotChange> changes, out ObjectMutationBatch batch)
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
        foreach (SceneItemSnapshotChange change in changes)
        {
            if (!change.HasChange)
            {
                continue;
            }

            if (!change.TryGetSnapshots(out ObjectSnapshot? before, out ObjectSnapshot? after))
            {
                batch = default!;
                return false;
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

            Guid objectId = after?.Id ?? before?.Id ?? Guid.Empty;
            if (objectId == Guid.Empty || !seenIds.Add(objectId))
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
                    pendingCreates.Add(after!);
                    break;
                case BatchChangeKind.Update:
                    if (!TryPrepareObjectUpdate(after!, out var preparedUpdate, allowWorldChange: true))
                    {
                        batch = default!;
                        return false;
                    }

                    pendingUpdates.Add(preparedUpdate);
                    break;
                case BatchChangeKind.Remove:
                    pendingRemoves.Add(before!);
                    break;
            }
        }

        Flush(currentKind);
        batch = new ObjectMutationBatch(steps);
        return true;
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

}
