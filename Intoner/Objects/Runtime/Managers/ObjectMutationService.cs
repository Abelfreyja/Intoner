using Intoner.Logging;
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
    /// <summary> prepares object and group duplicates with remapped identities without changing persistent or active state </summary>
    bool TryCreateDuplicates(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicateSnapshots);

    /// <summary>
    /// applies checked object transitions, including world changes, while preserving other location fields
    /// </summary>
    /// <param name="changes">The object snapshot transitions to apply.</param>
    /// <returns>The applied, rejected, or recovery required batch status.</returns>
    SceneMutationStatus ApplySnapshotChanges(IReadOnlyList<SceneItemSnapshotChange> changes);

    /// <summary> stores checked object and group transitions without creating objects </summary>
    SceneMutationStatus ApplyStoredSnapshotChanges(IReadOnlyList<SceneItemSnapshotChange> changes);

    /// <summary> previews transform and surface attachment changes without persistence or replacement </summary>
    /// <param name="before"> the expected active snapshot </param>
    /// <param name="after"> the requested transform or furniture attachment change </param>
    /// <param name="applied"> the sanitized snapshot on success </param>
    /// <returns> applied on success, rejected with the prior runtime preserved, or recovery required </returns>
    SceneMutationStatus PreviewChange(ObjectSnapshot before, ObjectSnapshot after, out ObjectSnapshot applied);

    /// <summary> creates one active object entry from the given snapshot </summary>
    bool TryCreateActiveObject(ObjectSnapshot snapshot, ObjectSceneSource source);

    /// <summary> disposes active entries removed from the desired scene </summary>
    void DestroyEntries(IEnumerable<ObjectSceneEntry> entries);

    /// <summary> updates an active runtime from saved scene state without writing persistence </summary>
    /// <param name="snapshot"> the saved snapshot to apply </param>
    /// <param name="source"> the desired scene owner </param>
    /// <returns> true when the update or required replacement succeeded </returns>
    bool TryUpdateActiveObject(ObjectSnapshot snapshot, ObjectSceneSource source);

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

    private static SceneMutationStatus ToRejectedMutationStatus(PersistentMutationStatus status)
        => status == PersistentMutationStatus.RecoveryRequired
            ? SceneMutationStatus.RecoveryRequired
            : SceneMutationStatus.Rejected;

    private readonly record struct PreparedObjectUpdate(ObjectSceneEntry? Entry, ObjectSnapshot PreviousSnapshot, ObjectSnapshot NextSnapshot)
    {
        public Guid ObjectId => NextSnapshot.Id;
        public bool HasChange => PreviousSnapshot != NextSnapshot;
        public bool IsPersistedOnly => Entry is null;
        public bool ChangesLocation => !PreviousSnapshot.CreatedIn.HasSameLocation(NextSnapshot.CreatedIn);
        public bool ChangesLayout => PreviousSnapshot.LayoutId != NextSnapshot.LayoutId;
    }

    /// <summary> applies one staged mutation and lets the enclosing batch own rollback and completion </summary>
    private interface IObjectMutationBatchStep
    {
        /// <summary> applies changes while retaining enough state to restore partial work on failure </summary>
        SceneMutationStatus Apply(ObjectMutationService service);

        /// <summary> restores this step, including work performed before an unsuccessful apply </summary>
        SceneMutationStatus Rollback(ObjectMutationService service);

        /// <summary> finishes runtime changes that require committed persistence </summary>
        SceneMutationStatus Complete(ObjectMutationService service)
            => SceneMutationStatus.Applied;
    }

    private sealed class ObjectMutationBatch(IReadOnlyList<IObjectMutationBatchStep> steps)
    {
        private readonly IObjectMutationBatchStep[] _steps = [.. steps];
        private bool _publishPersistentRevision;

        public SceneMutationStatus Execute(ObjectMutationService service)
        {
            if (_steps.Length == 0)
                return SceneMutationStatus.Applied;

            service._deferPersistentRevision = true;
            try
            {
                return ExecuteCore(service);
            }
            finally
            {
                service._deferPersistentRevision = false;

                if (_publishPersistentRevision)
                    service._revisionTracker.Increment(persistentChanged: true);
            }
        }

        private SceneMutationStatus ExecuteCore(ObjectMutationService service)
        {
            using IObjectPersistenceBatch persistence = service._persistenceState.BeginBatch();
            List<IObjectMutationBatchStep> appliedSteps = new(_steps.Length);
            SceneMutationStatus result = SceneMutationStatus.Applied;
            bool restorePersistence = false;
            try
            {
                foreach (IObjectMutationBatchStep step in _steps)
                {
                    appliedSteps.Add(step);
                    SceneMutationStatus stepStatus = step.Apply(service);
                    if (!stepStatus.IsApplied())
                        return Recover(stepStatus);
                    result = result.MergeApplied(stepStatus);
                }

                if (!service._objectIdentityService.TryValidatePersistentState(out _))
                    return Recover(SceneMutationStatus.Rejected);
                restorePersistence = true;
                PersistentMutationStatus committed = persistence.Commit();
                if (committed != PersistentMutationStatus.Success)
                {
                    restorePersistence = false;
                    return Recover(committed == PersistentMutationStatus.RecoveryRequired
                        ? SceneMutationStatus.RecoveryRequired : SceneMutationStatus.Rejected);
                }

                _publishPersistentRevision = true;
                foreach (IObjectMutationBatchStep step in appliedSteps)
                {
                    SceneMutationStatus completion = step.Complete(service);

                    if (!completion.IsApplied())
                        return Recover(completion);

                    result = result.MergeApplied(completion);
                }

                return result;
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(() => service._logger.LogError(exception, "object mutation batch failed"));
                return Recover(SceneMutationStatus.RecoveryRequired);
            }

            SceneMutationStatus Recover(SceneMutationStatus failure)
            {
                bool recoveryRequired = failure == SceneMutationStatus.RecoveryRequired;
                for (int index = appliedSteps.Count - 1; index >= 0; --index)
                {
                    try
                    {
                        recoveryRequired |= !appliedSteps[index].Rollback(service).IsFullyApplied();
                    }
                    catch (Exception exception)
                    {
                        DiagnosticLog.Write(() => service._logger.LogError(exception, "object mutation step could not be restored"));
                        recoveryRequired = true;
                    }
                }
                if (restorePersistence)
                {
                    _publishPersistentRevision = true;
                    try
                    {
                        recoveryRequired |= persistence.RestoreCommitted() != PersistentMutationStatus.Success;
                    }
                    catch (Exception exception)
                    {
                        DiagnosticLog.Write(() => service._logger.LogError(exception, "object mutation persistence could not be restored"));
                        recoveryRequired = true;
                    }
                }

                if (recoveryRequired)
                    service._sceneState.MarkNeedsRefresh();

                return recoveryRequired ? SceneMutationStatus.RecoveryRequired : SceneMutationStatus.Rejected;
            }
        }
    }

    private sealed class GroupBatchStep(IReadOnlyList<SceneItemSnapshotChange> changes) : IObjectMutationBatchStep
    {
        private bool _applied;

        public SceneMutationStatus Apply(ObjectMutationService service)
        {
            if (!service._persistenceState.TryApplyGroupChanges(changes))
                return SceneMutationStatus.Rejected;
            _applied = true;
            service.IncrementMutationRevision(persistentChanged: true);
            return SceneMutationStatus.Applied;
        }

        public SceneMutationStatus Rollback(ObjectMutationService service)
            => !_applied || service._persistenceState.TryApplyGroupChanges(changes.Reverse().Select(static change => change.Reverse()).ToArray())
                ? SceneMutationStatus.Applied
                : SceneMutationStatus.RecoveryRequired;
    }

    private sealed class CreateBatchStep(IReadOnlyList<ObjectSnapshot> snapshots) : IObjectMutationBatchStep
    {
        private readonly List<ObjectSnapshot> _resultSnapshots = new(snapshots.Count);

        public SceneMutationStatus Apply(ObjectMutationService service)
        {
            _resultSnapshots.Clear();
            foreach (var snapshot in snapshots)
            {
                PersistentMutationStatus status = service.CreateObjectCore(snapshot, out ObjectSnapshot createdSnapshot);
                if (status != PersistentMutationStatus.Success)
                {
                    return ToRejectedMutationStatus(status);
                }

                _resultSnapshots.Add(createdSnapshot);
            }

            return SceneMutationStatus.Applied;
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
        private readonly List<PreparedObjectUpdate> _deferredUpdates = [];

        public SceneMutationStatus Apply(ObjectMutationService service)
        {
            _appliedUpdates.Clear();
            _persistenceApplied = false;
            List<ObjectSnapshot> snapshots = new(updates.Count);
            _deferredUpdates.Clear();
            SceneMutationStatus result = SceneMutationStatus.Applied;
            foreach (PreparedObjectUpdate update in updates)
            {
                ObjectUpdateStatus status;
                ObjectSnapshot appliedSnapshot;
                try
                {
                    status = service.ApplyPreparedObjectUpdate(update, out appliedSnapshot, ObjectUpdateMode.PrepareBatch);
                }
                catch
                {
                    // runtime code can throw after changing the object
                    if (update.HasChange)
                        _appliedUpdates.Add(update);
                    throw;
                }
                if (status == ObjectUpdateStatus.Deferred)
                {
                    _deferredUpdates.Add(update);
                }
                else if (status is not (ObjectUpdateStatus.Applied or ObjectUpdateStatus.AppliedWithRuntimeFailure))
                {
                    return ToRejectedMutationStatus(ToPersistentMutationStatus(status));
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

            PersistentMutationStatus storageStatus = Persist(service,
                _appliedUpdates.Select(static update => update.PreviousSnapshot).ToArray(), snapshots);
            if (storageStatus != PersistentMutationStatus.Success)
            {
                return ToRejectedMutationStatus(storageStatus);
            }

            _persistenceApplied = true;
            return result;
        }

        public SceneMutationStatus Complete(ObjectMutationService service)
        {
            SceneMutationStatus result = SceneMutationStatus.Applied;
            foreach (PreparedObjectUpdate update in _deferredUpdates.OrderBy(static update => update.ChangesLocation))
            {
                ObjectUpdateStatus status = service.ApplyPreparedObjectUpdate(update, out _, ObjectUpdateMode.RuntimeOnly);
                if (status is not (ObjectUpdateStatus.Applied or ObjectUpdateStatus.AppliedWithRuntimeFailure))
                    return ToRejectedMutationStatus(ToPersistentMutationStatus(status));
                result = result.MergeApplied(ToSceneMutationStatus(status));
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
            if (_persistenceApplied && Persist(service,
                    _appliedUpdates.Select(static update => update.NextSnapshot).ToArray(),
                    _appliedUpdates.Select(static update => update.PreviousSnapshot).ToArray())
                != PersistentMutationStatus.Success)
            {
                DiagnosticLog.Write(() => service._logger.LogError("could not restore persisted objects after batch update failure"));
                service._sceneState.MarkNeedsRefresh();
                status = SceneMutationStatus.RecoveryRequired;
            }

            _persistenceApplied = false;
            _appliedUpdates.Clear();
            return status;
        }

        private static PersistentMutationStatus Persist(
            ObjectMutationService service,
            IReadOnlyList<ObjectSnapshot> previousSnapshots,
            IReadOnlyList<ObjectSnapshot> snapshots)
        {
            PersistentMutationStatus status = service._persistenceState.ReplacePersistedSnapshots(previousSnapshots, snapshots);
            if (status == PersistentMutationStatus.Success)
            {
                service.IncrementMutationRevision(persistentChanged: true);
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
                    return ToRejectedMutationStatus(status);
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
    private readonly IObjectRuntimeFactory _runtimeFactory;
    private readonly ISceneLocationService _locationService;
    private readonly IObjectCollectionManager _objectCollectionManager;
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private bool _deferPersistentRevision;

    private void IncrementMutationRevision(bool persistentChanged)
        => _revisionTracker.Increment(persistentChanged && !_deferPersistentRevision);

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
        IObjectRuntimeFactory runtimeFactory,
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
        _runtimeFactory = runtimeFactory;
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
                _objectKindService.GetDisplayName(kind)),
            overrides);

        lock (_stateLock)
        {
            nextSnapshot = _persistenceState.ApplyCreationLayout(nextSnapshot);
            nextSnapshot = nextSnapshot with
            {
                Name = TextUtility.ResolveAvailableName(nextSnapshot.Name, _persistenceState.GetObjectNames(nextSnapshot.LayoutId)),
            };
            return CreateObjectCore(nextSnapshot, out snapshot) == PersistentMutationStatus.Success
                ? snapshot.Id
                : null;
        }
    }

    public PersistentMutationResult CreateObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => CreateObjectSnapshotCore(snapshot, applyCreationLayout: true));

    public PersistentMutationResult RestoreObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => CreateObjectSnapshotCore(snapshot, applyCreationLayout: false));

    private PersistentMutationResult CreateObjectSnapshotCore(ObjectSnapshot snapshot, bool applyCreationLayout)
    {
        if (!_objectKindService.CanCreate(snapshot.Kind))
        {
            return PersistentMutationResult.Failed(
                PersistentMutationStatus.InvalidRequest,
                "object kind cannot be created");
        }

        if (applyCreationLayout)
        {
            snapshot = _persistenceState.ApplyCreationLayout(snapshot);
        }

        PersistentMutationStatus status = CreateObjectCore(snapshot, out ObjectSnapshot createdSnapshot);
        if (status == PersistentMutationStatus.Success)
        {
            return PersistentMutationResult.Success(createdSnapshot.Id);
        }

        return PersistentMutationResult.Failed(status,
            applyCreationLayout ? "object could not be created" : "object could not be imported");
    }

    public PersistentMutationResult UpdateObjectSnapshot(ObjectSnapshot snapshot, long expectedRevision)
        => ExecutePersistentMutation(expectedRevision, () => UpdateObjectSnapshotCore(snapshot));

    private PersistentMutationResult UpdateObjectSnapshotCore(ObjectSnapshot snapshot)
    {
        bool accepted = TryUpdateCore(snapshot, out PersistentMutationStatus status);
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

        PersistentMutationStatus status = CreateObjectCore(duplicate, out ObjectSnapshot createdSnapshot);
        return status == PersistentMutationStatus.Success
            ? PersistentMutationResult.Success(createdSnapshot.Id)
            : PersistentMutationResult.Failed(status, "object could not be duplicated");
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
        out PersistentMutationStatus mutationStatus,
        bool allowLocationChange = false,
        ObjectUpdateMode mode = ObjectUpdateMode.Persist)
    {
        if (!TryPrepareObjectUpdate(snapshot, out PreparedObjectUpdate preparedUpdate, allowLocationChange))
        {
            mutationStatus = PersistentMutationStatus.NotFound;
            return false;
        }

        ObjectUpdateStatus status = ApplyPreparedObjectUpdate(preparedUpdate, out _, mode);
        mutationStatus = ToPersistentMutationStatus(status);
        return status is ObjectUpdateStatus.Applied or ObjectUpdateStatus.AppliedWithRuntimeFailure;
    }

    public bool TryCreateDuplicates(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicateSnapshots)
    {
        lock (_stateLock)
        {
            duplicateSnapshots = [];
            if (!TryBuildDuplicateSnapshots(snapshots, out List<SceneItemSnapshot> requestedSnapshots))
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

    public SceneMutationStatus ApplyStoredSnapshotChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        lock (_stateLock)
        {
            if (changes.Count == 0)
            {
                return SceneMutationStatus.Applied;
            }

            using IObjectPersistenceBatch batch = _persistenceState.BeginBatch();
            List<SceneItemSnapshotChange> groups = [];
            List<ObjectSnapshot> objects = [];
            bool currentOwnerChanged = false;
            foreach (SceneItemSnapshotChange change in changes)
            {
                if ((change.After ?? change.Before) is SceneGroupSnapshot)
                {
                    groups.Add(change);
                }
                else if (change.TryGetSnapshots(out ObjectSnapshot? before, out ObjectSnapshot? after))
                {
                    bool exists = _persistenceState.TryGetPersistedSnapshot((after ?? before)!.Id, out ObjectSnapshot saved);

                    if (exists ? saved != before : before is not null)
                    {
                        return SceneMutationStatus.Rejected;
                    }

                    if (after is not null)
                    {
                        if (!_objectKindService.TrySanitizeSnapshot(after, out ObjectSnapshot sanitized) || sanitized != after)
                        {
                            return SceneMutationStatus.Rejected;
                        }

                        objects.Add(after);
                    }
                    else if (!_persistenceState.TryRemovePersistedSnapshot(before!.Id))
                    {
                        return SceneMutationStatus.Rejected;
                    }
                }
                else
                {
                    return SceneMutationStatus.Rejected;
                }

                currentOwnerChanged |= change.Before is ObjectSceneItemSnapshot previousOwner && _persistenceState.IsCurrentOwner(previousOwner.LayoutId)
                    || change.After is ObjectSceneItemSnapshot nextOwner && _persistenceState.IsCurrentOwner(nextOwner.LayoutId);
            }

            if (objects.Count > 0 && _persistenceState.UpsertPersistedSnapshots(objects) != PersistentMutationStatus.Success
                || groups.Count > 0 && !_persistenceState.TryApplyGroupChanges(groups)
                || !_objectIdentityService.TryValidatePersistentState(out _))
            {
                return SceneMutationStatus.Rejected;
            }

            PersistentMutationStatus status = batch.Commit();

            if (status != PersistentMutationStatus.Success)
            {
                return status == PersistentMutationStatus.RecoveryRequired ? SceneMutationStatus.RecoveryRequired : SceneMutationStatus.Rejected;
            }

            _revisionTracker.Increment(persistentChanged: currentOwnerChanged);
            _sceneState.MarkNeedsRefresh();
            return SceneMutationStatus.Applied;
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
                || !TrySanitizeUpdatedSnapshot(after, before, out ObjectSnapshot sanitized, allowLocationChange: false)
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
                DiagnosticLog.Write(_logger, LogLevel.Error, exception, "object edit preview failed for {ObjectId}", before.Id);
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
                DiagnosticLog.Write(_logger, LogLevel.Error, exception, "object edit preview restoration failed for {ObjectId}", before.Id);
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

            if (!_persistenceState.TryRemovePersistedSnapshot(id))
            {
                failureStatus = PersistentMutationStatus.StorageFailed;
                return false;
            }

            IncrementMutationRevision(persistentChanged: true);
            bool runtimeApplied = TryRemoveActiveEntry(entry);
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

        if (!_persistenceState.TryRemovePersistedSnapshot(id))
        {
            failureStatus = PersistentMutationStatus.StorageFailed;
            return false;
        }

        IncrementMutationRevision(persistentChanged: true);
        bool persistedRuntimeApplied = TryRefreshCollectionUsage(persistedSnapshot, null);
        _sceneState.ClearRuntimeFailure(id);
        failureStatus = persistedRuntimeApplied
            ? PersistentMutationStatus.Success
            : PersistentMutationStatus.RuntimeApplyFailed;
        return true;
    }

    public bool TryCreateActiveObject(ObjectSnapshot snapshot, ObjectSceneSource source)
    {
        lock (_stateLock)
        {
            return CreateObjectCore(snapshot, out _, persistSnapshot: false, sourceOverride: source) == PersistentMutationStatus.Success;
        }
    }

    private PersistentMutationStatus CreateObjectCore(
        ObjectSnapshot snapshot,
        out ObjectSnapshot createdSnapshot,
        bool persistSnapshot = true,
        ObjectSceneSource? sourceOverride = null)
    {
        createdSnapshot = default!;
        snapshot = ApplyCreationContext(snapshot);
        if (!_objectKindService.TrySanitizeSnapshot(snapshot, out ObjectSnapshot sanitizedSnapshot))
        {
            _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.InvalidObject);
            return PersistentMutationStatus.InvalidRequest;
        }

        if (_sceneState.TryGetEntry(sanitizedSnapshot.Id, out _))
        {
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.DuplicateObject);
            return PersistentMutationStatus.Conflict;
        }

        ObjectSceneSource source = sourceOverride ?? _persistenceState.ResolveSceneSource(sanitizedSnapshot);
        bool persistSceneSnapshot = persistSnapshot && source.IsLocalPersistent;
        if (persistSceneSnapshot
            && !_objectIdentityService.TryValidatePersistentObject(sanitizedSnapshot.Id, out _))
        {
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.DuplicateObject);
            return PersistentMutationStatus.Conflict;
        }

        if (source.UsesUserHousingPolicy
            && !_housingModePolicy.TryValidateCreate(sanitizedSnapshot, GetHousingPolicySceneSnapshots(), out string housingModeError))
        {
            _logger.LogDebug("object create rejected by housing mode: {Reason}", housingModeError);
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.HousingModeRejected);
            return PersistentMutationStatus.InvalidRequest;
        }

        _objectCollectionManager.EnsureCollectionMaterialized(sanitizedSnapshot.CollectionId, [sanitizedSnapshot]);

        if (!_runtimeFactory.TryCreate(sanitizedSnapshot, out IObjectRuntime runtime, out string failureCode))
        {
            _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, failureCode);
            return PersistentMutationStatus.RuntimeApplyFailed;
        }

        ObjectSceneEntry createdEntry = new()
        {
            Runtime = runtime,
            Source = source,
            ResourceCollection = SceneResourceCollectionState.Current(runtime.ResourceRevision),
        };
        bool ownsRuntime = true;
        try
        {
            _sceneState.UpsertEntry(createdEntry);
            if (!TryRefreshCollectionUsage(null, sanitizedSnapshot))
            {
                ownsRuntime = false;
                bool recovered = TryRemoveActiveEntry(createdEntry);
                _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.CollectionUsageFailed);
                return recovered
                    ? PersistentMutationStatus.RuntimeApplyFailed
                    : PersistentMutationStatus.RecoveryRequired;
            }

            if (persistSceneSnapshot && !_persistenceState.TryUpsertPersistedSnapshot(sanitizedSnapshot))
            {
                ownsRuntime = false;
                bool recovered = TryRemoveActiveEntry(createdEntry);

                _sceneState.SetRuntimeFailure(sanitizedSnapshot.Id, ObjectRuntimeFailureCodes.PersistenceFailed);
                return recovered
                    ? PersistentMutationStatus.StorageFailed
                    : PersistentMutationStatus.RecoveryRequired;
            }

            ownsRuntime = false;
            IncrementMutationRevision(persistentChanged: persistSceneSnapshot);
            createdSnapshot = sanitizedSnapshot;
            return PersistentMutationStatus.Success;
        }
        finally
        {
            if (ownsRuntime)
            {
                _sceneState.MarkNeedsRefresh();
                _ = TryRemoveActiveEntry(createdEntry);
            }
        }
    }

    public bool TryUpdateActiveObject(ObjectSnapshot snapshot, ObjectSceneSource source)
        => TryApplyActiveObject(snapshot, source, replace: false);

    public bool TryReplaceObject(ObjectSnapshot snapshot, ObjectSceneSource source)
        => TryApplyActiveObject(snapshot, source, replace: true);

    private bool TryApplyActiveObject(ObjectSnapshot snapshot, ObjectSceneSource source, bool replace)
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

            ObjectUpdateStatus status = replace
                ? TryReplaceEntryRuntime(entry, entry.Snapshot, sanitizedSnapshot,
                    persistSnapshot: false, sourceOverride: source, updateCollectionUsage: true, out _)
                : ApplyActiveEntryUpdate(entry, entry.Snapshot, sanitizedSnapshot, ObjectUpdateMode.RuntimeOnly, source, out _);

            if (status == ObjectUpdateStatus.Rejected)
            {
                _sceneState.SetRuntimeFailure(snapshot.Id, ObjectRuntimeFailureCodes.UpdateRejected);
            }

            return status == ObjectUpdateStatus.Applied;
        }
    }

    private bool TryRemoveActiveEntry(ObjectSceneEntry entry)
    {
        _sceneState.TryRemoveEntry(entry.Snapshot.Id, out _);
        return TryDestroyEntry(entry);
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
            _ = _persistenceState.TryRemovePersistedSnapshot(entry.Snapshot.Id);
        }
    }

    private ObjectSnapshot ApplyCreationContext(ObjectSnapshot snapshot)
    {
        if (snapshot.CreatedIn.IsAssigned)
        {
            return snapshot;
        }

        SceneCreationContext current = _locationService.GetCurrentCreationContext();
        SceneCreationContext location = snapshot.CreatedIn.TerritoryId == 0 ? current : snapshot.CreatedIn;
        return snapshot with { CreatedIn = location with { Worlds = snapshot.CreatedIn.Worlds ?? current.Worlds } };
    }

    private bool TryBuildDuplicateSnapshot(Guid id, out ObjectSnapshot duplicateSnapshot)
    {
        duplicateSnapshot = default!;
        if (!TryResolveLocalPersistentSnapshot(id, out var snapshot))
        {
            return false;
        }

        if (!TryBuildDuplicateSnapshots([snapshot], out List<SceneItemSnapshot> duplicates))
            return false;
        duplicateSnapshot = (ObjectSnapshot)duplicates[0];
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

    private bool TryPrepareObjectUpdate(ObjectSnapshot snapshot, out PreparedObjectUpdate preparedUpdate, bool allowLocationChange = false)
    {
        if (_sceneState.TryGetEntry(snapshot.Id, out var entry))
        {
            if (entry.Source.IsRuntimeOnly
                || !TrySanitizeUpdatedSnapshot(snapshot, entry.Snapshot, out var nextSnapshot, allowLocationChange)
                || !_housingModePolicy.TryValidateSnapshot(nextSnapshot, out _))
            {
                preparedUpdate = default;
                return false;
            }

            preparedUpdate = new PreparedObjectUpdate(entry, entry.Snapshot, nextSnapshot);
            return true;
        }

        if (!_persistenceState.TryGetPersistedSnapshot(snapshot.Id, out var persistedSnapshot)
            || !TrySanitizeUpdatedSnapshot(snapshot, persistedSnapshot, out var persistedNextSnapshot, allowLocationChange)
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

        if (preparedUpdate.ChangesLocation && mode == ObjectUpdateMode.PrepareBatch)
        {
            return ObjectUpdateStatus.Deferred;
        }

        bool leavesCurrentLocation = preparedUpdate.ChangesLocation && preparedUpdate.Entry is not null
            && !_locationService.MatchesLocation(preparedUpdate.NextSnapshot.CreatedIn, _locationService.GetCurrentLocationScope());
        if (preparedUpdate.IsPersistedOnly || leavesCurrentLocation)
        {
            if (mode == ObjectUpdateMode.Persist)
            {
                if (!_persistenceState.TryReplacePersistedSnapshot(preparedUpdate.PreviousSnapshot, preparedUpdate.NextSnapshot))
                {
                    appliedSnapshot = default!;
                    return ObjectUpdateStatus.StorageFailed;
                }

                IncrementMutationRevision(persistentChanged: true);
            }

            if (preparedUpdate.ChangesLocation)
            {
                bool removed = preparedUpdate.Entry is null || TryRemoveActiveEntry(preparedUpdate.Entry);
                _sceneState.ClearRuntimeFailure(preparedUpdate.ObjectId);
                _sceneState.MarkNeedsRefresh();
                return removed ? ObjectUpdateStatus.Applied : ObjectUpdateStatus.AppliedWithRuntimeFailure;
            }

            if (preparedUpdate.ChangesLayout)
            {
                _sceneState.MarkNeedsRefresh();
            }

            return ObjectUpdateStatus.Applied;
        }

        ObjectUpdateStatus status = ApplyActiveEntryUpdate(
            preparedUpdate.Entry!,
            preparedUpdate.PreviousSnapshot,
            preparedUpdate.NextSnapshot,
            mode,
            sourceOverride: preparedUpdate.ChangesLayout ? _persistenceState.ResolveSceneSource(preparedUpdate.NextSnapshot) : null,
            out appliedSnapshot);
        if (status == ObjectUpdateStatus.RecoveryRequired)
        {
            _revisionTracker.Increment();
        }

        if (preparedUpdate.ChangesLocation && status is ObjectUpdateStatus.Applied or ObjectUpdateStatus.AppliedWithRuntimeFailure)
        {
            _sceneState.MarkNeedsRefresh();
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
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out List<SceneItemSnapshot> duplicateSnapshots)
    {
        duplicateSnapshots = [];
        List<ObjectSnapshot> objects = [];
        List<SceneGroupSnapshot> groups = [];
        Dictionary<Guid, SceneGroupSnapshot>? groupsById = null;
        foreach (SceneItemSnapshot snapshot in snapshots)
        {
            if (snapshot is ObjectSnapshot && TryResolveLocalPersistentSnapshot(snapshot.Id, out ObjectSnapshot current))
            {
                objects.Add(current);
            }
            else if (snapshot is SceneGroupSnapshot)
            {
                groupsById ??= _persistenceState.GetPersistedGroups().ToDictionary(static group => group.Id);
                if (!groupsById.TryGetValue(snapshot.Id, out SceneGroupSnapshot? group))
                    return false;
                groups.Add(group);
            }
            else
                return false;
        }
        if (!ObjectSnapshotUtility.TryCopyWithNewIds(objects, groups, out List<ObjectSnapshot> copies, out List<SceneGroupSnapshot> groupCopies))
            return false;
        SceneCreationContext context = _locationService.GetCurrentCreationContext();
        DateTime createdAtUtc = DateTime.UtcNow;
        int objectIndex = 0;
        int groupIndex = 0;
        Dictionary<Guid, HashSet<string>> namesByLayout = [];
        foreach (SceneItemSnapshot snapshot in snapshots)
        {
            SceneItemSnapshot copy = snapshot is ObjectSnapshot ? copies[objectIndex++] : groupCopies[groupIndex++];
            string name = copy.Name;

            if (copy is ObjectSnapshot duplicate)
            {
                Guid owner = duplicate.LayoutId.GetValueOrDefault();

                if (!namesByLayout.TryGetValue(owner, out HashSet<string>? names))
                {
                    names = _persistenceState.GetObjectNames(duplicate.LayoutId);
                    namesByLayout.Add(owner, names);
                }

                name = TextUtility.ResolveAvailableName(
                    TextUtility.TrimOrFallback(duplicate.Name, _objectKindService.GetDisplayName(duplicate.Kind)), names);
                names.Add(name);
            }

            duplicateSnapshots.Add(copy with { Name = name, CreatedIn = context, CreatedAtUtc = createdAtUtc });
        }
        return true;
    }

    private bool TrySanitizeUpdatedSnapshot(ObjectSnapshot snapshot, ObjectSnapshot currentSnapshot, out ObjectSnapshot sanitizedSnapshot, bool allowLocationChange)
    {
        if (snapshot.Kind != currentSnapshot.Kind
            || snapshot.LayoutId != currentSnapshot.LayoutId && snapshot.LayoutId.HasValue
            && _persistenceState.ResolveSceneSource(snapshot).Kind != ObjectSceneSourceKind.PersistentLayout)
        {
            sanitizedSnapshot = default!;
            return false;
        }
        SceneCreationContext location = currentSnapshot.CreatedIn;
        if (allowLocationChange && snapshot.CreatedIn != location)
        {
            if (!location.IsAssigned || !snapshot.CreatedIn.IsAssigned
                || !_locationService.IsValidWorldSelection(snapshot.CreatedIn.Worlds)
                || !ObjectHousingAddress.CanChangeLocation(location.Address, snapshot.CreatedIn.Address))
            {
                sanitizedSnapshot = default!;
                return false;
            }

            location = snapshot.CreatedIn;
        }

        return _objectKindService.TrySanitizeSnapshot(
            snapshot with
            {
                Id = currentSnapshot.Id,
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

        switch (entry.Runtime.TryUpdate(nextSnapshot))
        {
            case ObjectRuntimeUpdateResult.Applied:
                UpsertActiveEntryMetadata(entry, sourceOverride ?? entry.Source,
                    entry.ResourceCollection.WithRevision(entry.Runtime.ResourceRevision));
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
            bool stored;
            try
            {
                stored = _persistenceState.TryReplacePersistedSnapshot(previousSnapshot, entry.Snapshot);
            }
            catch
            {
                _sceneState.MarkNeedsRefresh();
                _ = TryRestoreEntryRuntime(entry, previousSnapshot);
                throw;
            }

            if (!stored)
            {
                appliedSnapshot = default!;
                return TryRestoreEntryRuntime(entry, previousSnapshot)
                    ? ObjectUpdateStatus.StorageFailed
                    : ObjectUpdateStatus.RecoveryRequired;
            }

            IncrementMutationRevision(persistentChanged: true);
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
        using IDisposable? scope = DiagnosticLog.BeginScope(_logger, "object={ObjectId}; operation=runtime replacement", nextSnapshot.Id);
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
        if (!_runtimeFactory.TryCreate(nextSnapshot, out IObjectRuntime replacement, out string failureCode))
        {
            _logger.LogDebug("object runtime replacement rejected; object={ObjectId}; retainedAddress=0x{Address:X}; failure={FailureCode}",
                nextSnapshot.Id, (ulong)previousAddress, failureCode);
            _sceneState.SetRuntimeFailure(nextSnapshot.Id, failureCode);
            return ObjectUpdateStatus.RecreateFailed;
        }

        SceneResourceCollectionState resourceCollection = SceneResourceCollectionState.Current(replacement.ResourceRevision);
        bool ownsReplacement = true;
        try
        {
            nint replacementAddress = replacement.Address;
            DiagnosticLog.Write(() => _logger.LogDebug("object runtime replacement created; object={ObjectId}; oldAddress=0x{OldAddress:X}; newAddress=0x{NewAddress:X}; revision={Revision}",
                nextSnapshot.Id, (ulong)previousAddress, (ulong)replacementAddress, resourceCollection.Revision));

            if (persistSnapshot && !_persistenceState.TryReplacePersistedSnapshot(previousSnapshot, nextSnapshot))
            {
                ownsReplacement = false;
                bool disposed = TryDisposeRuntime(replacement, nextSnapshot.Id);
                DiagnosticLog.Write(() => _logger.LogWarning("object runtime replacement rolled back; reason=storage failed; object={ObjectId}; retainedAddress=0x{OldAddress:X}; rejectedAddress=0x{NewAddress:X}; cleanupSucceeded={CleanupSucceeded}",
                    nextSnapshot.Id, (ulong)previousAddress, (ulong)replacementAddress, disposed));
                return disposed
                    ? ObjectUpdateStatus.StorageFailed
                    : ObjectUpdateStatus.RecoveryRequired;
            }

            if (persistSnapshot)
            {
                IncrementMutationRevision(persistentChanged: true);
            }

            _sceneState.UpsertEntry(new ObjectSceneEntry
            {
                Runtime = replacement,
                Source = sourceOverride ?? entry.Source,
                ResourceCollection = resourceCollection,
            });
            ownsReplacement = false;
            bool runtimeApplied = TryDestroyEntry(entry, releaseCollectionUsage: false);

            if (updateCollectionUsage)
            {
                runtimeApplied &= TryRefreshCollectionUsage(previousSnapshot, nextSnapshot);
            }

            _sceneState.ClearRuntimeFailure(nextSnapshot.Id);
            appliedSnapshot = nextSnapshot;
            DiagnosticLog.Write(() => _logger.Log(runtimeApplied ? LogLevel.Debug : LogLevel.Warning,
                "object runtime replacement completed; object={ObjectId}; oldAddress=0x{OldAddress:X}; newAddress=0x{NewAddress:X}; cleanupAndUsageSucceeded={Succeeded}",
                nextSnapshot.Id, (ulong)previousAddress, (ulong)replacementAddress, runtimeApplied));
            return runtimeApplied
                ? ObjectUpdateStatus.Applied
                : ObjectUpdateStatus.AppliedWithRuntimeFailure;
        }
        finally
        {
            if (ownsReplacement)
            {
                _ = TryDisposeRuntime(replacement, nextSnapshot.Id);
            }
        }
    }

    private bool TryRestoreEntryRuntime(ObjectSceneEntry entry, ObjectSnapshot previousSnapshot)
    {
        try
        {
            switch (entry.Runtime.TryUpdate(previousSnapshot))
            {
                case ObjectRuntimeUpdateResult.Applied:
                    UpsertActiveEntryMetadata(entry, entry.Source,
                        entry.ResourceCollection.WithRevision(entry.Runtime.ResourceRevision));
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
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(() => _logger.LogError(exception, "could not restore object runtime {ObjectId}", previousSnapshot.Id));
        }

        _sceneState.MarkNeedsRefresh();
        return false;
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

    private delegate bool RecoverSnapshot(ObjectSnapshot snapshot, out PersistentMutationStatus status);

    private SceneMutationStatus RestorePreparedObjectUpdates(IReadOnlyList<PreparedObjectUpdate> updates)
        => RecoverSnapshots(updates.Select(static update => update.PreviousSnapshot).ToArray(),
            (ObjectSnapshot snapshot, out PersistentMutationStatus status) =>
                TryUpdateCore(snapshot, out status, allowLocationChange: true, mode: ObjectUpdateMode.RuntimeOnly),
            reverse: true);

    private SceneMutationStatus RollbackCreatedSnapshots(IReadOnlyList<ObjectSnapshot> snapshots)
        => RecoverSnapshots(snapshots,
            (ObjectSnapshot snapshot, out PersistentMutationStatus status) => RemoveCore(snapshot.Id, out status),
            reverse: true);

    private SceneMutationStatus RestoreRemovedSnapshots(IReadOnlyList<ObjectSnapshot> snapshots)
        => RecoverSnapshots(snapshots,
            (ObjectSnapshot snapshot, out PersistentMutationStatus status) =>
            {
                status = CreateObjectCore(snapshot, out _);
                return status == PersistentMutationStatus.Success;
            },
            reverse: false);

    private SceneMutationStatus RecoverSnapshots(IReadOnlyList<ObjectSnapshot> snapshots, RecoverSnapshot recover, bool reverse)
    {
        SceneMutationStatus result = SceneMutationStatus.Applied;
        bool recovered = true;

        for (int index = 0; index < snapshots.Count; ++index)
        {
            ObjectSnapshot snapshot = snapshots[reverse ? snapshots.Count - index - 1 : index];

            try
            {
                if (recover(snapshot, out PersistentMutationStatus status))
                {
                    result = result.MergeApplied(ToSceneMutationStatus(status));
                    continue;
                }

                DiagnosticLog.Write(() => _logger.LogError("could not restore object {ObjectId} after a failed batch", snapshot.Id));
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(() => _logger.LogError(exception, "could not restore object {ObjectId} after a failed batch", snapshot.Id));
            }

            recovered = false;
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
            DiagnosticLog.Write(() => _logger.LogError(ex, "failed to destroy object runtime {ObjectId}", objectId));
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
            DiagnosticLog.Write(() => _logger.LogError(ex, "failed to refresh object collection usage"));
            _sceneState.MarkNeedsRefresh();
            return false;
        }
    }

    private bool TryBuildSnapshotChangeBatch(IReadOnlyList<SceneItemSnapshotChange> changes, out ObjectMutationBatch batch)
    {
        List<SceneItemSnapshotChange> groupChanges = [];
        var steps = new List<IObjectMutationBatchStep>();
        var pendingCreates = new List<ObjectSnapshot>();
        var pendingUpdates = new List<PreparedObjectUpdate>();
        var pendingRemoves = new List<ObjectSnapshot>();
        var seenIds = new HashSet<Guid>();

        void Flush(BatchChangeKind pendingKind)
        {
            switch (pendingKind)
            {
                case BatchChangeKind.Create when pendingCreates.Count > 0:
                    steps.Add(new CreateBatchStep([.. pendingCreates]));
                    pendingCreates.Clear();
                    break;
                case BatchChangeKind.Update when pendingUpdates.Count > 0:
                    steps.Add(new UpdateBatchStep([.. pendingUpdates]));
                    pendingUpdates.Clear();
                    break;
                case BatchChangeKind.Remove when pendingRemoves.Count > 0:
                    steps.Add(new RemoveBatchStep([.. pendingRemoves]));
                    pendingRemoves.Clear();
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

            if ((change.After ?? change.Before) is SceneGroupSnapshot group)
            {
                if (group.Id == Guid.Empty || !seenIds.Add(group.Id)
                    || !change.TryGetSnapshots<SceneGroupSnapshot>(out _, out _))
                {
                    batch = default!;
                    return false;
                }
                groupChanges.Add(change);
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
                    if (!TryPrepareObjectUpdate(after!, out var preparedUpdate, allowLocationChange: true))
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
        if (groupChanges.Count > 0)
        {
            steps.Add(new GroupBatchStep(groupChanges));
        }
        batch = new ObjectMutationBatch(steps);
        return true;
    }

    private static ObjectSnapshot ApplyObjectPlacementOverrides(ObjectSnapshot snapshot, ObjectPlacementOverrides? overrides)
    {
        if (overrides is null)
        {
            return snapshot;
        }

        ObjectData model = snapshot.Kind switch
        {
            ObjectKind.Light when overrides.Model is LightModel lightModel => lightModel,
            ObjectKind.BgObject when overrides.Model is BgObjectModel bgObjectModel => bgObjectModel,
            ObjectKind.Furniture when overrides.Model is FurnitureModel furnitureModel => furnitureModel,
            ObjectKind.Vfx when overrides.Model is VfxModel vfxModel => vfxModel,
            _ => snapshot.Model,
        };
        return snapshot with
        {
            Name = TextUtility.TrimOrFallback(overrides.Name, snapshot.Name),
            Visible = overrides.Visible ?? snapshot.Visible,
            FolderPath = overrides.FolderPath ?? snapshot.FolderPath,
            Transform = overrides.Scale.HasValue ? snapshot.Transform with { Scale = overrides.Scale.Value } : snapshot.Transform,
            Model = model,
        };
    }
}
