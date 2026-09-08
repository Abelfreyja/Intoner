using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Runtime;

/// <summary> commits caller owned temporary sources and reconciles their runtime state </summary>
internal interface ITemporarySourceService
{
    /// <summary> replaces the complete object and collection state for one source </summary>
    ObjectTemporaryMutationResult TryApply(
        string sourceKey,
        Guid sessionId,
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectTemporaryCollectionData> collections,
        long revision);

    /// <summary> applies ordered object changes to one existing source </summary>
    ObjectTemporaryMutationResult TryApplyObjectChanges(
        string sourceKey,
        Guid sessionId,
        string name,
        IReadOnlyList<ObjectTemporaryChange> changes,
        long revision);

    /// <summary> removes one source and all of its runtime collections </summary>
    ObjectTemporaryMutationResult TryRemove(string sourceKey, Guid sessionId, long revision);
}

internal sealed class TemporarySourceService : ITemporarySourceService, IDisposable
{
    internal const int MaximumPendingRuntimeRecoveries = TemporarySourceStore.MaximumRemovedSourceTombstones;

    private sealed class RuntimeRecoveryState(
        ObjectTemporarySourceState sourceState,
        bool isRemoval,
        string retiredMemoryOwnerId)
    {
        public ObjectTemporarySourceState SourceState { get; } = sourceState;
        public bool IsRemoval { get; } = isRemoval;
        public string RetiredMemoryOwnerId { get; } = retiredMemoryOwnerId;
        public bool CollectionsApplied { get; set; }
        public bool SceneApplied { get; set; }
    }

    private readonly ILogger<TemporarySourceService> _logger;
    private readonly Lock _stateLock;
    private readonly ITemporarySourceStore _sourceStore;
    private readonly IObjectIdentityService _objectIdentityService;
    private readonly IObjectKindService _objectKindService;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly ITemporaryCollectionService _temporaryCollectionService;
    private readonly IObjectScene _scene;
    private readonly Lock _mutationLock = new();
    private readonly Dictionary<string, RuntimeRecoveryState> _runtimeRecoveries = new(StringComparer.OrdinalIgnoreCase);

    public TemporarySourceService(
        ILogger<TemporarySourceService> logger,
        ObjectStateLock stateLock,
        ITemporarySourceStore sourceStore,
        IObjectIdentityService objectIdentityService,
        IObjectKindService objectKindService,
        IObjectRevisionTracker revisionTracker,
        ITemporaryCollectionService temporaryCollectionService,
        IObjectScene scene)
    {
        _logger = logger;
        _stateLock = stateLock.Value;
        _sourceStore = sourceStore;
        _objectIdentityService = objectIdentityService;
        _objectKindService = objectKindService;
        _revisionTracker = revisionTracker;
        _temporaryCollectionService = temporaryCollectionService;
        _scene = scene;
        _scene.SceneRefreshed += HandleSceneRefreshed;
    }

    public void Dispose()
        => _scene.SceneRefreshed -= HandleSceneRefreshed;

    public ObjectTemporaryMutationResult TryApply(
        string sourceKey,
        Guid sessionId,
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectTemporaryCollectionData> collections,
        long revision)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        if (!IsValidWrite(normalizedSourceKey, sessionId, revision))
        {
            return Failure(ObjectTemporaryMutationStatus.InvalidSource, normalizedSourceKey);
        }

        if (!TrySanitizeSnapshots(objects, out IReadOnlyList<ObjectSnapshot> sanitizedObjects))
        {
            return Failure(ObjectTemporaryMutationStatus.InvalidObject, normalizedSourceKey);
        }

        if (!ValidateCollectionReferences(sanitizedObjects, collections))
        {
            return Failure(ObjectTemporaryMutationStatus.InvalidCollection, normalizedSourceKey);
        }

        ObjectTemporaryMutationResult prepareResult = _temporaryCollectionService.TryPrepare(
            normalizedSourceKey,
            sessionId,
            revision,
            collections,
            out PreparedTemporaryCollections prepared);
        if (!prepareResult.IsSuccess)
        {
            return Failure(prepareResult.Status, normalizedSourceKey);
        }

        lock (_mutationLock)
        {
            if (!TryCompleteExistingRecovery(normalizedSourceKey, out ObjectTemporaryMutationResult recoveryFailure))
            {
                _temporaryCollectionService.Discard(prepared);
                return recoveryFailure;
            }

            if (!CanTrackRuntimeRecovery(normalizedSourceKey))
            {
                _temporaryCollectionService.Discard(prepared);
                return RuntimeUnavailable(normalizedSourceKey);
            }

            IReadOnlyList<ObjectSnapshot> runtimeObjects = TemporarySourceUtility.CreateRuntimeObjects(
                normalizedSourceKey,
                sanitizedObjects);
            ObjectTemporarySourceSnapshot nextSource = new()
            {
                SourceKey = normalizedSourceKey,
                SessionId = sessionId,
                Name = name,
                Revision = revision,
                Objects = TemporarySourceUtility.OrderObjects(sanitizedObjects),
                Collections = prepared.SourceCollections,
                RuntimeObjects = runtimeObjects,
                RuntimeCollections = prepared.RuntimeCollections,
                MemoryOwnerId = prepared.MemoryOwnerId,
            };
            ObjectTemporaryMutationResult result;
            ObjectTemporarySourceSnapshot? previousSource;
            ObjectTemporarySourceSnapshot currentSource;
            lock (_stateLock)
            {
                if (!_objectIdentityService.TryValidateTemporarySourceReplacement(
                        normalizedSourceKey,
                        runtimeObjects,
                        out _))
                {
                    _temporaryCollectionService.Discard(prepared);
                    return Failure(ObjectTemporaryMutationStatus.IdentityConflict, normalizedSourceKey);
                }

                result = _sourceStore.TryReplace(
                    nextSource,
                    out previousSource,
                    out currentSource);
            }
            if (result.Status == ObjectTemporaryMutationStatus.AlreadyApplied)
            {
                _temporaryCollectionService.Discard(prepared);
                return result;
            }

            if (!result.IsSuccess)
            {
                _temporaryCollectionService.Discard(prepared);
                return result;
            }

            _revisionTracker.Increment();
            RuntimeRecoveryState recovery = new(
                new ObjectTemporarySourceState(currentSource.SessionId, currentSource.Revision),
                isRemoval: false,
                previousSource?.MemoryOwnerId ?? string.Empty);
            _runtimeRecoveries[normalizedSourceKey] = recovery;
            try
            {
                _temporaryCollectionService.Publish(normalizedSourceKey, prepared);
                recovery.CollectionsApplied = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to publish temporary source collections for {SourceKey}", normalizedSourceKey);
                _scene.MarkNeedsRefresh();
                return RuntimeFailure(result);
            }

            return ReconcileRuntimeState(normalizedSourceKey, result);
        }
    }

    public ObjectTemporaryMutationResult TryApplyObjectChanges(
        string sourceKey,
        Guid sessionId,
        string name,
        IReadOnlyList<ObjectTemporaryChange> changes,
        long revision)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        lock (_mutationLock)
        {
            if (!IsValidWrite(normalizedSourceKey, sessionId, revision))
            {
                return Failure(ObjectTemporaryMutationStatus.InvalidSource, normalizedSourceKey);
            }

            if (!_sourceStore.TryGetSource(normalizedSourceKey, out ObjectTemporarySourceSnapshot existing))
            {
                return Failure(ObjectTemporaryMutationStatus.ObjectNotFound, normalizedSourceKey);
            }

            if (existing.SessionId != sessionId)
            {
                return Failure(ObjectTemporaryMutationStatus.SourceMismatch, normalizedSourceKey);
            }

            if (revision < existing.Revision)
            {
                return Failure(ObjectTemporaryMutationStatus.StaleRevision, normalizedSourceKey);
            }

            if (existing.Revision == revision)
            {
                ObjectTemporaryMutationResult duplicate = new(ObjectTemporaryMutationStatus.AlreadyApplied, revision);
                return _runtimeRecoveries.ContainsKey(normalizedSourceKey)
                    ? ReconcileRuntimeState(normalizedSourceKey, duplicate)
                    : duplicate;
            }

            if (!TryCompleteExistingRecovery(normalizedSourceKey, out ObjectTemporaryMutationResult recoveryFailure))
            {
                return recoveryFailure;
            }

            if (!CanTrackRuntimeRecovery(normalizedSourceKey))
            {
                return RuntimeUnavailable(normalizedSourceKey);
            }

            ObjectTemporaryMutationStatus prepareStatus = TryPrepareObjects(
                existing,
                changes,
                out IReadOnlyList<ObjectSnapshot> nextObjects);
            if (prepareStatus != ObjectTemporaryMutationStatus.Success)
            {
                return Failure(prepareStatus, normalizedSourceKey);
            }

            IReadOnlyList<ObjectSnapshot> runtimeObjects = TemporarySourceUtility.CreateRuntimeObjects(
                normalizedSourceKey,
                nextObjects);
            ObjectTemporaryMutationResult result;
            ObjectTemporarySourceSnapshot currentSource;
            lock (_stateLock)
            {
                if (!_objectIdentityService.TryValidateTemporarySourceReplacement(
                        normalizedSourceKey,
                        runtimeObjects,
                        out _))
                {
                    return Failure(ObjectTemporaryMutationStatus.IdentityConflict, normalizedSourceKey);
                }

                result = _sourceStore.TryReplaceObjects(
                    normalizedSourceKey,
                    sessionId,
                    name,
                    revision,
                    nextObjects,
                    runtimeObjects,
                    out currentSource);
            }
            if (result.Status == ObjectTemporaryMutationStatus.AlreadyApplied)
            {
                return result;
            }

            if (!result.IsSuccess)
            {
                return result;
            }

            _revisionTracker.Increment();
            _runtimeRecoveries[normalizedSourceKey] = new RuntimeRecoveryState(
                new ObjectTemporarySourceState(currentSource.SessionId, currentSource.Revision),
                isRemoval: false,
                retiredMemoryOwnerId: string.Empty)
            {
                CollectionsApplied = true,
            };
            return ReconcileRuntimeState(normalizedSourceKey, result);
        }
    }

    public ObjectTemporaryMutationResult TryRemove(string sourceKey, Guid sessionId, long revision)
    {
        string normalizedSourceKey = TemporarySourceUtility.NormalizeKey(sourceKey);
        lock (_mutationLock)
        {
            if (!IsValidWrite(normalizedSourceKey, sessionId, revision))
            {
                return Failure(ObjectTemporaryMutationStatus.InvalidSource, normalizedSourceKey);
            }

            if (_runtimeRecoveries.TryGetValue(normalizedSourceKey, out RuntimeRecoveryState? pendingRemoval)
                && pendingRemoval.IsRemoval)
            {
                ObjectTemporaryMutationResult duplicateRemoval = _sourceStore.TryRemove(
                    normalizedSourceKey,
                    sessionId,
                    revision,
                    out _);
                return duplicateRemoval.Status == ObjectTemporaryMutationStatus.AlreadyApplied
                    ? ReconcileRuntimeState(normalizedSourceKey, duplicateRemoval)
                    : duplicateRemoval;
            }

            if (!TryCompleteExistingRecovery(normalizedSourceKey, out ObjectTemporaryMutationResult recoveryFailure))
            {
                return recoveryFailure;
            }

            if (!CanTrackRuntimeRecovery(normalizedSourceKey))
            {
                return RuntimeUnavailable(normalizedSourceKey);
            }

            ObjectTemporaryMutationResult result = _sourceStore.TryRemove(
                normalizedSourceKey,
                sessionId,
                revision,
                out ObjectTemporarySourceSnapshot? removedSource);
            if (result.Status == ObjectTemporaryMutationStatus.AlreadyApplied)
            {
                return result;
            }

            if (!result.IsSuccess)
            {
                return result;
            }

            _revisionTracker.Increment();
            _runtimeRecoveries[normalizedSourceKey] = new RuntimeRecoveryState(
                new ObjectTemporarySourceState(sessionId, revision),
                isRemoval: true,
                removedSource?.MemoryOwnerId ?? string.Empty);
            return ReconcileRuntimeState(normalizedSourceKey, result);
        }
    }

    private bool TryCompleteExistingRecovery(
        string sourceKey,
        out ObjectTemporaryMutationResult failure)
    {
        if (!_runtimeRecoveries.TryGetValue(sourceKey, out RuntimeRecoveryState? recovery))
        {
            failure = default;
            return true;
        }

        _ = ReconcileRuntimeState(
            sourceKey,
            new ObjectTemporaryMutationResult(
                ObjectTemporaryMutationStatus.AlreadyApplied,
                recovery.SourceState.Revision));
        if (!_runtimeRecoveries.ContainsKey(sourceKey))
        {
            failure = default;
            return true;
        }

        failure = RuntimeUnavailable(sourceKey);
        return false;
    }

    private ObjectTemporaryMutationResult ReconcileRuntimeState(
        string sourceKey,
        ObjectTemporaryMutationResult result)
    {
        if (!_runtimeRecoveries.TryGetValue(sourceKey, out RuntimeRecoveryState? recovery))
        {
            return result;
        }

        if (!recovery.CollectionsApplied)
        {
            if (!TryApplyRuntimeCollections(sourceKey, recovery))
            {
                _scene.MarkNeedsRefresh();
                return RuntimeFailure(result);
            }

            recovery.CollectionsApplied = true;
        }

        if (!recovery.SceneApplied)
        {
            if (!_scene.ShouldApplyChangesNow())
            {
                _scene.MarkNeedsRefresh();
                return result;
            }

            SceneReloadResult reloadResult;
            try
            {
                reloadResult = _scene.ReloadForCurrentLocation();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "failed to reconcile temporary source runtime for {SourceKey}", sourceKey);
                _scene.MarkNeedsRefresh();
                return RuntimeFailure(result);
            }

            if (!reloadResult.IsApplied)
            {
                return RuntimeFailure(result);
            }

            recovery.SceneApplied = true;
        }

        return TryCompleteRuntimeRecovery(sourceKey, recovery)
            ? result
            : RuntimeFailure(result);
    }

    private bool TryApplyRuntimeCollections(string sourceKey, RuntimeRecoveryState recovery)
    {
        try
        {
            if (recovery.IsRemoval)
            {
                _temporaryCollectionService.Remove(sourceKey);
                return true;
            }

            if (!_sourceStore.TryGetSource(sourceKey, out ObjectTemporarySourceSnapshot source)
                || source.SessionId != recovery.SourceState.SessionId
                || source.Revision != recovery.SourceState.Revision)
            {
                return false;
            }

            ObjectTemporaryMutationResult prepareResult = _temporaryCollectionService.TryPrepareCommitted(
                source,
                out PreparedTemporaryCollections prepared);
            if (!prepareResult.IsSuccess)
            {
                return false;
            }

            _temporaryCollectionService.Publish(sourceKey, prepared);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to apply temporary source collections for {SourceKey}", sourceKey);
            return false;
        }
    }

    private bool TryCompleteRuntimeRecovery(string sourceKey, RuntimeRecoveryState recovery)
    {
        try
        {
            string activeOwnerId = !recovery.IsRemoval
                && _sourceStore.TryGetSource(sourceKey, out ObjectTemporarySourceSnapshot source)
                ? source.MemoryOwnerId
                : string.Empty;
            if (recovery.RetiredMemoryOwnerId.Length > 0)
            {
                _temporaryCollectionService.ReleaseRetiredMemory(
                    [recovery.RetiredMemoryOwnerId],
                    activeOwnerId);
            }

            _runtimeRecoveries.Remove(sourceKey);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to release retired temporary source memory for {SourceKey}", sourceKey);
            _scene.MarkNeedsRefresh();
            return false;
        }
    }

    private bool CanTrackRuntimeRecovery(string sourceKey)
        => _runtimeRecoveries.ContainsKey(sourceKey)
            || _runtimeRecoveries.Count < MaximumPendingRuntimeRecoveries;

    private ObjectTemporaryMutationStatus TryPrepareObjects(
        ObjectTemporarySourceSnapshot source,
        IReadOnlyList<ObjectTemporaryChange> changes,
        out IReadOnlyList<ObjectSnapshot> objects)
    {
        if (changes is null)
        {
            objects = [];
            return ObjectTemporaryMutationStatus.InvalidObject;
        }

        Dictionary<Guid, ObjectSnapshot> objectMap = TemporarySourceUtility.CreateObjectMap(source.Objects);
        HashSet<string> collectionIds = CreateCollectionIdSet(source.Collections);
        foreach (ObjectTemporaryChange change in changes)
        {
            switch (change.Kind)
            {
                case ObjectTemporaryChangeKind.Upsert when change.Snapshot is not null:
                    if (!TrySanitizeSnapshot(change.Snapshot, out ObjectSnapshot sanitizedUpsert))
                    {
                        objects = [];
                        return ObjectTemporaryMutationStatus.InvalidObject;
                    }

                    if (!ValidateCollectionReference(sanitizedUpsert, collectionIds))
                    {
                        objects = [];
                        return ObjectTemporaryMutationStatus.InvalidCollection;
                    }

                    objectMap[sanitizedUpsert.Id] = sanitizedUpsert;
                    break;
                case ObjectTemporaryChangeKind.Patch when change.Patch is not null && change.ObjectId != Guid.Empty:
                    if (!objectMap.TryGetValue(change.ObjectId, out ObjectSnapshot? existing))
                    {
                        objects = [];
                        return ObjectTemporaryMutationStatus.ObjectNotFound;
                    }

                    if (change.Patch.ModelKind.HasValue && change.Patch.ModelKind.Value != existing.Kind
                        || !TrySanitizeSnapshot(ObjectSnapshotUtility.ApplyPatch(existing, change.Patch), out ObjectSnapshot sanitizedPatch))
                    {
                        objects = [];
                        return ObjectTemporaryMutationStatus.InvalidObject;
                    }

                    if (!ValidateCollectionReference(sanitizedPatch, collectionIds))
                    {
                        objects = [];
                        return ObjectTemporaryMutationStatus.InvalidCollection;
                    }

                    objectMap[change.ObjectId] = sanitizedPatch;
                    break;
                case ObjectTemporaryChangeKind.Remove when change.ObjectId != Guid.Empty:
                    if (!objectMap.Remove(change.ObjectId))
                    {
                        objects = [];
                        return ObjectTemporaryMutationStatus.ObjectNotFound;
                    }

                    break;
                default:
                    objects = [];
                    return ObjectTemporaryMutationStatus.InvalidObject;
            }
        }

        objects = TemporarySourceUtility.OrderObjects(objectMap.Values);
        return ObjectTemporaryMutationStatus.Success;
    }

    private bool TrySanitizeSnapshot(ObjectSnapshot snapshot, out ObjectSnapshot sanitizedSnapshot)
    {
        if (snapshot.Id == Guid.Empty
            || snapshot.CreatedAtUtc == default
            || !_objectKindService.TrySanitizeSnapshot(snapshot, out sanitizedSnapshot))
        {
            sanitizedSnapshot = null!;
            return false;
        }

        sanitizedSnapshot = sanitizedSnapshot with
        {
            CollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(sanitizedSnapshot.CollectionId),
        };
        return true;
    }

    private bool TrySanitizeSnapshots(IReadOnlyList<ObjectSnapshot> snapshots, out IReadOnlyList<ObjectSnapshot> sanitizedSnapshots)
    {
        if (snapshots is null)
        {
            sanitizedSnapshots = [];
            return false;
        }

        List<ObjectSnapshot> result = [];
        HashSet<Guid> objectIds = [];
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (!TrySanitizeSnapshot(snapshot, out ObjectSnapshot sanitizedSnapshot)
                || !objectIds.Add(sanitizedSnapshot.Id))
            {
                sanitizedSnapshots = [];
                return false;
            }

            result.Add(sanitizedSnapshot);
        }

        sanitizedSnapshots = result;
        return true;
    }

    private void HandleSceneRefreshed()
    {
        lock (_mutationLock)
        {
            foreach ((string sourceKey, RuntimeRecoveryState recovery) in _runtimeRecoveries.ToList())
            {
                if (!recovery.CollectionsApplied)
                {
                    continue;
                }

                recovery.SceneApplied = true;
                _ = TryCompleteRuntimeRecovery(sourceKey, recovery);
            }
        }
    }

    private ObjectTemporaryMutationResult Failure(ObjectTemporaryMutationStatus status, string sourceKey)
        => new(status, _sourceStore.GetRevision(sourceKey));

    private ObjectTemporaryMutationResult RuntimeUnavailable(string sourceKey)
        => Failure(ObjectTemporaryMutationStatus.RuntimeApplyFailed, sourceKey);

    private static bool IsValidWrite(string sourceKey, Guid sessionId, long revision)
        => sourceKey.Length > 0 && sessionId != Guid.Empty && revision > 0;

    private static ObjectTemporaryMutationResult RuntimeFailure(ObjectTemporaryMutationResult result)
        => new(
            ObjectTemporaryMutationStatus.RuntimeApplyFailed,
            result.SourceRevision,
            AcceptedAfterRuntimeFailure: result.IsAccepted);

    private static bool ValidateCollectionReferences(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectTemporaryCollectionData> collections)
    {
        if (collections is null)
        {
            return false;
        }

        HashSet<string> collectionIds = CreateCollectionIdSet(collections);
        return objects.All(snapshot => ValidateCollectionReference(snapshot, collectionIds));
    }

    private static HashSet<string> CreateCollectionIdSet(IEnumerable<ObjectTemporaryCollectionData> collections)
        => new(
            collections.Select(static collection => ObjectCollectionKeyUtility.NormalizeCollectionId(collection.CollectionId)),
            StringComparer.OrdinalIgnoreCase);

    private static bool ValidateCollectionReference(ObjectSnapshot snapshot, IReadOnlySet<string> collectionIds)
    {
        string collectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(snapshot.CollectionId);
        return collectionId.Length == 0
            || collectionIds.Contains(collectionId);
    }

}
