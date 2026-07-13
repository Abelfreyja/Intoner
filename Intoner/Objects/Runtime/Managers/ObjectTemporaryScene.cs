using Intoner.Objects.Models;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Owns temporary source mutation flow, including snapshot sanitization, revision updates, and runtime application.
/// </summary>
internal interface IObjectTemporaryScene
{
    /// <summary>
    /// Replaces a full temporary layout snapshot for one source.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="name">The temporary layout name.</param>
    /// <param name="objects">The replacement temporary objects.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary layout mutation.</returns>
    ObjectTemporaryMutationResult TryApplyTemporaryLayout(string sourceKey, Guid sessionId, string name, IReadOnlyList<ObjectSnapshot> objects, long revision = 0);

    /// <summary>
    /// Applies a batched temporary change set for one source.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="name">The temporary layout name.</param>
    /// <param name="changes">The ordered temporary changes to apply.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary mutation batch.</returns>
    ObjectTemporaryMutationResult TryApplyTemporaryChanges(string sourceKey, Guid sessionId, string name, IReadOnlyList<ObjectTemporaryChange> changes, long revision = 0);

    /// <summary>
    /// Creates or updates one temporary object for a source.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="name">The temporary layout name.</param>
    /// <param name="snapshot">The incoming temporary object snapshot.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary object mutation.</returns>
    ObjectTemporaryMutationResult TryUpsertTemporaryObject(string sourceKey, Guid sessionId, string name, ObjectSnapshot snapshot, long revision = 0);

    /// <summary>
    /// Applies a partial update to one temporary object.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="name">The temporary layout name.</param>
    /// <param name="objectId">The source object id.</param>
    /// <param name="patch">The partial object patch to apply.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary object mutation.</returns>
    ObjectTemporaryMutationResult TryPatchTemporaryObject(string sourceKey, Guid sessionId, string name, Guid objectId, ObjectSnapshotPatch patch, long revision = 0);

    /// <summary>
    /// Removes one temporary object from a source layout.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="objectId">The source object id.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary object removal.</returns>
    ObjectTemporaryMutationResult TryRemoveTemporaryObject(string sourceKey, Guid sessionId, Guid objectId, long revision = 0);

    /// <summary>
    /// Removes one temporary source layout entirely.
    /// </summary>
    /// <param name="sourceKey">The temporary source key.</param>
    /// <param name="sessionId">The source session id.</param>
    /// <param name="revision">The source revision for this write.</param>
    /// <returns>The result of the temporary layout removal.</returns>
    ObjectTemporaryMutationResult TryRemoveTemporaryLayout(string sourceKey, Guid sessionId, long revision = 0);
}

internal sealed class ObjectTemporaryScene : IObjectTemporaryScene
{
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectKindService _objectKindService;
    private readonly IObjectRevisionTracker _revisionTracker;
    private readonly IObjectMutationService _mutationService;
    private readonly Lazy<IObjectTemporaryCollectionService> _temporaryCollectionService;
    private readonly IObjectScene _scene;

    public ObjectTemporaryScene(
        IObjectLayoutManager layoutManager,
        IObjectKindService objectKindService,
        IObjectRevisionTracker revisionTracker,
        IObjectMutationService mutationService,
        Func<IObjectTemporaryCollectionService> temporaryCollectionServiceFactory,
        IObjectScene scene)
    {
        _layoutManager = layoutManager;
        _objectKindService = objectKindService;
        _revisionTracker = revisionTracker;
        _mutationService = mutationService;
        _temporaryCollectionService = new Lazy<IObjectTemporaryCollectionService>(temporaryCollectionServiceFactory);
        _scene = scene;
    }

    public ObjectTemporaryMutationResult TryApplyTemporaryLayout(string sourceKey, Guid sessionId, string name, IReadOnlyList<ObjectSnapshot> objects, long revision = 0)
    {
        string sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (!TrySanitizeSnapshots(objects, out IReadOnlyList<ObjectSnapshot> sanitizedObjects))
        {
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidObject, 0);
        }

        ObjectTemporaryMutationResult result = _layoutManager.TryApplyTemporaryLayout(
            sanitizedSourceKey,
            sessionId,
            name,
            sanitizedObjects,
            revision);
        return FinalizeTemporarySourceMutation(sanitizedSourceKey, sessionId, result);
    }

    public ObjectTemporaryMutationResult TryApplyTemporaryChanges(string sourceKey, Guid sessionId, string name, IReadOnlyList<ObjectTemporaryChange> changes, long revision = 0)
    {
        string sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (!TrySanitizeTemporaryChanges(changes, out IReadOnlyList<ObjectTemporaryChange> sanitizedChanges))
        {
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidObject, 0);
        }

        ObjectTemporaryMutationResult result = _layoutManager.TryApplyTemporaryChanges(
            sanitizedSourceKey,
            sessionId,
            name,
            sanitizedChanges,
            revision);
        return FinalizeTemporarySourceMutation(sanitizedSourceKey, sessionId, result);
    }

    public ObjectTemporaryMutationResult TryUpsertTemporaryObject(string sourceKey, Guid sessionId, string name, ObjectSnapshot snapshot, long revision = 0)
    {
        string sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (!TrySanitizeSnapshot(snapshot, out ObjectSnapshot sanitizedSnapshot))
        {
            return new ObjectTemporaryMutationResult(ObjectTemporaryMutationStatus.InvalidObject, 0);
        }

        ObjectTemporaryMutationResult result = _layoutManager.TryUpsertTemporaryObject(
            sanitizedSourceKey,
            sessionId,
            name,
            sanitizedSnapshot,
            revision,
            out ObjectSnapshot appliedSnapshot);
        return FinalizeTemporarySourceMutation(
            sanitizedSourceKey,
            sessionId,
            result,
            () => _mutationService.TryApplyTemporaryObjectUpsert(sanitizedSourceKey, appliedSnapshot, _scene.GetCurrentLocationScope()));
    }

    public ObjectTemporaryMutationResult TryPatchTemporaryObject(string sourceKey, Guid sessionId, string name, Guid objectId, ObjectSnapshotPatch patch, long revision = 0)
    {
        string sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        if (_layoutManager.TryGetTemporaryObjectSnapshot(sanitizedSourceKey, objectId, out ObjectSnapshot existingSnapshot)
            && !TrySanitizeSnapshot(ObjectSnapshotUtility.ApplyPatch(existingSnapshot, patch), out _))
        {
            return new ObjectTemporaryMutationResult(
                ObjectTemporaryMutationStatus.InvalidObject,
                _layoutManager.GetTemporarySourceRevision(sanitizedSourceKey));
        }

        ObjectTemporaryMutationResult result = _layoutManager.TryPatchTemporaryObject(
            sanitizedSourceKey,
            sessionId,
            name,
            objectId,
            patch,
            revision,
            out ObjectSnapshot appliedSnapshot);
        return FinalizeTemporarySourceMutation(
            sanitizedSourceKey,
            sessionId,
            result,
            () => _mutationService.TryApplyTemporaryObjectUpsert(sanitizedSourceKey, appliedSnapshot, _scene.GetCurrentLocationScope()));
    }

    public ObjectTemporaryMutationResult TryRemoveTemporaryObject(string sourceKey, Guid sessionId, Guid objectId, long revision = 0)
    {
        string sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        ObjectTemporaryMutationResult result = _layoutManager.TryRemoveTemporaryObject(
            sanitizedSourceKey,
            sessionId,
            objectId,
            revision,
            out Guid mappedObjectId);
        return FinalizeTemporarySourceMutation(
            sanitizedSourceKey,
            sessionId,
            result,
            () => _mutationService.TryApplyTemporaryObjectRemoval(sanitizedSourceKey, mappedObjectId));
    }

    public ObjectTemporaryMutationResult TryRemoveTemporaryLayout(string sourceKey, Guid sessionId, long revision = 0)
    {
        string sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        ObjectTemporaryMutationResult result = _layoutManager.TryRemoveTemporaryLayout(sanitizedSourceKey, sessionId, revision);
        if (!result.IsSuccess)
        {
            return result;
        }

        ObjectTemporaryMutationResult collectionResult = TemporaryCollectionService.TryClearTemporaryCollectionSource(
            sanitizedSourceKey,
            sessionId,
            revision: 0);
        if (!collectionResult.IsSuccess
            && collectionResult.Status != ObjectTemporaryMutationStatus.ObjectNotFound)
        {
            return collectionResult;
        }

        return FinalizeTemporaryMutation(result);
    }

    private bool TrySanitizeSnapshot(ObjectSnapshot snapshot, out ObjectSnapshot sanitizedSnapshot)
        => _objectKindService.TrySanitizeSnapshot(snapshot, out sanitizedSnapshot);

    private bool TrySanitizeSnapshots(IReadOnlyList<ObjectSnapshot> snapshots, out IReadOnlyList<ObjectSnapshot> sanitizedSnapshots)
    {
        List<ObjectSnapshot> result = [];
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (!TrySanitizeSnapshot(snapshot, out ObjectSnapshot sanitizedSnapshot))
            {
                sanitizedSnapshots = [];
                return false;
            }

            result.Add(sanitizedSnapshot);
        }

        sanitizedSnapshots = result;
        return true;
    }

    private bool TrySanitizeTemporaryChanges(IReadOnlyList<ObjectTemporaryChange> changes, out IReadOnlyList<ObjectTemporaryChange> sanitizedChanges)
    {
        List<ObjectTemporaryChange> result = [];
        foreach (ObjectTemporaryChange change in changes)
        {
            if (change.Kind == ObjectTemporaryChangeKind.Upsert && change.Snapshot is not null)
            {
                if (!TrySanitizeSnapshot(change.Snapshot, out ObjectSnapshot sanitizedSnapshot))
                {
                    sanitizedChanges = [];
                    return false;
                }

                result.Add(change with { Snapshot = sanitizedSnapshot });
                continue;
            }

            result.Add(change);
        }

        sanitizedChanges = result;
        return true;
    }

    private ObjectTemporaryMutationResult FinalizeTemporarySourceMutation(
        string sourceKey,
        Guid sessionId,
        ObjectTemporaryMutationResult result,
        Func<ObjectTemporaryMutationStatus>? applyRuntimeChanges = null)
    {
        if (!result.IsSuccess)
        {
            return result;
        }

        TemporaryCollectionService.ResetTemporarySourceSessionIfNeeded(sourceKey, sessionId);
        return FinalizeTemporaryMutation(result, applyRuntimeChanges);
    }

    private ObjectTemporaryMutationResult FinalizeTemporaryMutation(
        ObjectTemporaryMutationResult result,
        Func<ObjectTemporaryMutationStatus>? applyRuntimeChanges = null)
    {
        IncrementSceneRevision();
        if (!_scene.ShouldApplyChangesNow())
        {
            _scene.MarkNeedsRefresh();
            return result;
        }

        ObjectTemporaryMutationStatus applyResult = applyRuntimeChanges is null
            ? ToTemporaryMutationStatus(_scene.ReloadForCurrentLocation())
            : _scene.RunOnSceneThread(applyRuntimeChanges);
        return applyResult == ObjectTemporaryMutationStatus.Success
            ? result
            : new ObjectTemporaryMutationResult(applyResult, result.SourceRevision);
    }

    private static ObjectTemporaryMutationStatus ToTemporaryMutationStatus(SceneReloadResult result)
        => result.IsApplied
            ? ObjectTemporaryMutationStatus.Success
            : ObjectTemporaryMutationStatus.RuntimeApplyFailed;

    private void IncrementSceneRevision()
        => _revisionTracker.Increment();

    private IObjectTemporaryCollectionService TemporaryCollectionService
        => _temporaryCollectionService.Value;
}

