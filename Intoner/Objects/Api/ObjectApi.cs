using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using TemporaryObjectChangeKindDto = Intoner.Objects.Api.TemporaryObjectChangeKind;
using TemporaryObjectChangeKindModel = Intoner.Objects.Models.ObjectTemporaryChangeKind;

namespace Intoner.Objects.Api;

internal sealed class ObjectApi(
    ObjectPluginStateApi pluginState,
    ObjectLayoutApi layout,
    ObjectTemporaryLayoutApi temporaryLayouts,
    ObjectTemporaryObjectApi temporaryObjects,
    ObjectTemporaryCollectionApi temporaryCollections,
    ObjectTemporarySourceBuildApi temporarySourceBuild,
    ObjectQueryApi query,
    ObjectMutationApi mutation,
    ObjectRuntimeApi runtime)
{
    public const int BreakingVersion = 1;
    public const int FeatureVersion = 0;

    public readonly ObjectPluginStateApi          PluginState          = pluginState;
    public readonly ObjectLayoutApi               Layout               = layout;
    public readonly ObjectTemporaryLayoutApi      TemporaryLayouts     = temporaryLayouts;
    public readonly ObjectTemporaryObjectApi      TemporaryObjects     = temporaryObjects;
    public readonly ObjectTemporaryCollectionApi  TemporaryCollections = temporaryCollections;
    public readonly ObjectTemporarySourceBuildApi TemporarySourceBuild = temporarySourceBuild;
    public readonly ObjectQueryApi                Query                = query;
    public readonly ObjectMutationApi             Mutation             = mutation;
    public readonly ObjectRuntimeApi              Runtime              = runtime;
}

internal sealed class ObjectPluginStateApi
{
    public ObjectApiVersion GetApiVersion()
        => new(ObjectApi.BreakingVersion, ObjectApi.FeatureVersion);
}

internal sealed class ObjectLayoutApi(IObjectLayoutManager layoutManager, IObjectManager objectManager)
{
    public IReadOnlyList<SavedObjectLayout> GetLayouts()
        => layoutManager.GetLayouts().Select(ObjectApiMapper.ToDto).ToList();

    public IReadOnlyList<LoadedObjectLayout> GetLoadedLayouts()
        => layoutManager.GetLoadedLayouts().Select(ObjectApiMapper.ToDto).ToList();

    public Guid? GetDefaultLayoutId()
        => layoutManager.GetDefaultLayoutId();

    public Guid CreateLayout(string name)
        => objectManager.CreateEmptyLayout(name);

    public Guid? SaveCurrentAsLayout(string name)
        => objectManager.TrySaveCurrentObjectsAsLayout(name, out var layoutId)
            ? layoutId
            : null;

    public bool SetDefaultLayout(Guid layoutId)
        => objectManager.TrySelectLayout(layoutId);

    public bool ClearDefaultLayout()
        => objectManager.TrySelectLayout(null);

    public bool DeleteLayout(Guid layoutId)
        => objectManager.TryDeleteLayout(layoutId);
}

internal sealed class ObjectTemporaryLayoutApi(IObjectLayoutManager layoutManager, IObjectTemporaryScene temporaryScene)
{
    private static readonly TemporarySourceMutationResult InvalidObjectResult = new(TemporarySourceMutationStatus.InvalidObject, 0);

    public IReadOnlyList<LoadedObjectLayout> GetLoadedLayouts()
        => layoutManager.GetLoadedLayouts()
            .Where(static layout => layout.Kind == ObjectLoadedLayoutKind.Temporary)
            .Select(ObjectApiMapper.ToDto)
            .ToList();

    public TemporarySourceMutationResult ApplyLayout(TemporaryLayoutApplyRequest? dto)
    {
        if (dto?.Objects is null || !ObjectApiMapper.TryToDetachedSnapshots(dto.Objects, out var snapshots))
        {
            return InvalidObjectResult;
        }

        return ObjectApiMapper.ToDto(temporaryScene.TryApplyTemporaryLayout(
            dto.SourceKey,
            dto.SourceSessionId,
            dto.Name,
            snapshots,
            dto.Revision));
    }

    public TemporarySourceMutationResult RemoveLayout(TemporaryLayoutRemoval? dto)
        => dto is null
            ? InvalidObjectResult
            : ObjectApiMapper.ToDto(temporaryScene.TryRemoveTemporaryLayout(dto.SourceKey, dto.SourceSessionId, dto.Revision));
}

internal sealed class ObjectTemporaryObjectApi(IObjectLayoutManager layoutManager, IObjectTemporaryScene temporaryScene)
{
    private static readonly TemporarySourceMutationResult InvalidObjectResult = new(TemporarySourceMutationStatus.InvalidObject, 0);
    private static readonly TemporarySourceMutationResult InvalidSourceResult = new(TemporarySourceMutationStatus.InvalidSource, 0);

    public TemporarySourceMutationResult ApplyChanges(TemporaryObjectChangeSet? dto)
    {
        if (dto?.Changes is null)
        {
            return InvalidObjectResult;
        }

        if (!TryToChanges(dto.SourceKey, dto.SourceSessionId, dto.Changes, out var changes, out var error))
        {
            return error;
        }

        return ObjectApiMapper.ToDto(temporaryScene.TryApplyTemporaryChanges(
            dto.SourceKey,
            dto.SourceSessionId,
            dto.Name,
            changes,
            dto.Revision));
    }

    public TemporarySourceMutationResult UpsertObject(TemporaryObjectUpsert? dto)
    {
        if (dto?.Object is null || !ObjectApiMapper.TryToDetachedSnapshot(dto.Object, out var snapshot))
        {
            return InvalidObjectResult;
        }

        return ObjectApiMapper.ToDto(temporaryScene.TryUpsertTemporaryObject(
            dto.SourceKey,
            dto.SourceSessionId,
            dto.Name,
            snapshot,
            dto.Revision));
    }

    public TemporarySourceMutationResult PatchObject(TemporaryObjectPatch? dto)
    {
        if (dto?.Patch is null)
        {
            return InvalidObjectResult;
        }

        if (string.IsNullOrWhiteSpace(dto.SourceKey))
        {
            return InvalidSourceResult;
        }

        var sourceRevision = layoutManager.GetTemporarySourceRevision(dto.SourceKey);
        if (!layoutManager.TryGetTemporaryObjectSnapshot(dto.SourceKey, dto.ObjectId, out var snapshot))
        {
            return new TemporarySourceMutationResult(TemporarySourceMutationStatus.ObjectNotFound, sourceRevision);
        }

        if (!ObjectApiMapper.TryToPatch(dto.Patch, snapshot.Kind, out var patch))
        {
            return new TemporarySourceMutationResult(TemporarySourceMutationStatus.InvalidObject, sourceRevision);
        }

        return ObjectApiMapper.ToDto(temporaryScene.TryPatchTemporaryObject(
            dto.SourceKey,
            dto.SourceSessionId,
            dto.Name,
            dto.ObjectId,
            patch,
            dto.Revision));
    }

    public TemporarySourceMutationResult RemoveObject(TemporaryObjectRemoval? dto)
    {
        if (dto is null)
        {
            return InvalidObjectResult;
        }

        return ObjectApiMapper.ToDto(temporaryScene.TryRemoveTemporaryObject(
            dto.SourceKey,
            dto.SourceSessionId,
            dto.ObjectId,
            dto.Revision));
    }

    private bool TryToChanges(
        string sourceKey,
        Guid sessionId,
        IReadOnlyList<TemporaryObjectChange>? dtos,
        out List<ObjectTemporaryChange> changes,
        out TemporarySourceMutationResult error)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            changes = [];
            error = InvalidSourceResult;
            return false;
        }

        if (dtos is null)
        {
            changes = [];
            error = InvalidObjectResult;
            return false;
        }

        var sanitizedSourceKey = ObjectTemporarySourceUtility.NormalizeSourceKey(sourceKey);
        var existingSourceLayout = layoutManager.TryGetTemporaryLayout(sanitizedSourceKey, out var resolvedLayout)
            ? resolvedLayout
            : null;
        var isNewSession = existingSourceLayout is not null
                           && sessionId != Guid.Empty
                           && sessionId != existingSourceLayout.SourceSessionId;
        var sourceLayout = isNewSession
            ? null
            : existingSourceLayout;
        var sourceRevision = isNewSession
            ? 0
            : sourceLayout?.Revision ?? layoutManager.GetTemporarySourceRevision(sanitizedSourceKey);
        var resolvedSnapshots = sourceLayout?.Objects.ToDictionary(static snapshot => snapshot.Id)
                             ?? new Dictionary<Guid, ObjectSnapshot>();
        changes = new List<ObjectTemporaryChange>(dtos.Count);
        foreach (TemporaryObjectChange? dto in dtos)
        {
            if (dto is null)
            {
                changes = [];
                error = new TemporarySourceMutationResult(TemporarySourceMutationStatus.InvalidObject, sourceRevision);
                return false;
            }

            switch (dto.Kind)
            {
                case TemporaryObjectChangeKindDto.Upsert when dto.Object is not null:
                    if (!ObjectApiMapper.TryToDetachedSnapshot(dto.Object, out var snapshot))
                    {
                        changes = [];
                        error = new TemporarySourceMutationResult(TemporarySourceMutationStatus.InvalidObject, sourceRevision);
                        return false;
                    }

                    var remappedSnapshot = snapshot with
                    {
                        Id = ObjectIdentityUtility.CreateTemporaryObjectId(sanitizedSourceKey, snapshot.Id),
                        LayoutId = null,
                    };
                    resolvedSnapshots[remappedSnapshot.Id] = remappedSnapshot;
                    changes.Add(new ObjectTemporaryChange(
                        TemporaryObjectChangeKindModel.Upsert,
                        snapshot,
                        Guid.Empty));
                    break;
                case TemporaryObjectChangeKindDto.Patch when dto.Patch is not null:
                    var mappedObjectId = ObjectIdentityUtility.CreateTemporaryObjectId(sanitizedSourceKey, dto.ObjectId);
                    if (!resolvedSnapshots.TryGetValue(mappedObjectId, out var existingSnapshot))
                    {
                        changes = [];
                        error = new TemporarySourceMutationResult(TemporarySourceMutationStatus.ObjectNotFound, sourceRevision);
                        return false;
                    }

                    if (!ObjectApiMapper.TryToPatch(dto.Patch, existingSnapshot.Kind, out var patch))
                    {
                        changes = [];
                        error = new TemporarySourceMutationResult(TemporarySourceMutationStatus.InvalidObject, sourceRevision);
                        return false;
                    }

                    changes.Add(new ObjectTemporaryChange(
                        TemporaryObjectChangeKindModel.Patch,
                        null,
                        dto.ObjectId,
                        patch));
                    resolvedSnapshots[mappedObjectId] = ObjectSnapshotUtility.ApplyPatch(existingSnapshot, patch);
                    break;
                case TemporaryObjectChangeKindDto.Remove:
                    resolvedSnapshots.Remove(ObjectIdentityUtility.CreateTemporaryObjectId(sanitizedSourceKey, dto.ObjectId));
                    changes.Add(new ObjectTemporaryChange(
                        TemporaryObjectChangeKindModel.Remove,
                        null,
                        dto.ObjectId));
                    break;
                default:
                    changes = [];
                    error = new TemporarySourceMutationResult(TemporarySourceMutationStatus.InvalidObject, sourceRevision);
                    return false;
            }
        }

        error = null!;
        return true;
    }
}

internal sealed class ObjectTemporaryCollectionApi(Func<IObjectTemporaryCollectionService> temporaryCollectionServiceFactory)
{
    private static readonly TemporarySourceMutationResult InvalidObjectResult = new(TemporarySourceMutationStatus.InvalidObject, 0);

    public TemporarySourceMutationResult ApplyCollections(TemporaryCollectionsApplyRequest? dto)
    {
        if (dto is null || !ObjectApiMapper.TryToTemporaryCollections(dto.Collections, out List<ObjectTemporaryCollectionData> collections))
        {
            return InvalidObjectResult;
        }

        return ObjectApiMapper.ToDto(temporaryCollectionServiceFactory().TryApplyTemporaryCollections(
            dto.SourceKey,
            dto.SourceSessionId,
            dto.Name,
            collections,
            dto.Revision));
    }

    public TemporarySourceMutationResult UpsertCollection(TemporaryCollectionUpsert? dto)
    {
        if (dto is null || !ObjectApiMapper.TryToTemporaryCollection(dto.Collection, out ObjectTemporaryCollectionData collection))
        {
            return InvalidObjectResult;
        }

        return ObjectApiMapper.ToDto(temporaryCollectionServiceFactory().TryUpsertTemporaryCollection(
            dto.SourceKey,
            dto.SourceSessionId,
            dto.Name,
            collection,
            dto.Revision));
    }

    public TemporarySourceMutationResult RemoveCollections(TemporaryCollectionsRemoveRequest? dto)
        => dto?.CollectionIds is null
            ? InvalidObjectResult
            : ObjectApiMapper.ToDto(temporaryCollectionServiceFactory().TryRemoveTemporaryCollections(
                dto.SourceKey,
                dto.SourceSessionId,
                dto.CollectionIds,
                dto.Revision));
}

internal sealed class ObjectTemporarySourceBuildApi(IObjectTemporarySourceBuilder sourceBuilder)
{
    public Task<TemporarySourceBuildResult> BuildTemporarySource(TemporarySourceBuildRequest? dto)
        => sourceBuilder.BuildTemporarySourceAsync(dto, CancellationToken.None);
}

internal sealed class ObjectQueryApi(IObjectLayoutManager layoutManager, IObjectSceneView sceneView)
{
    public ObjectSceneSnapshot GetSceneSnapshot()
        => new(
            sceneView.GetSceneRevision(),
            sceneView.GetPersistentSceneRevision(),
            layoutManager.GetDefaultLayoutId(),
            sceneView.GetStandaloneObjectSnapshots().Select(ObjectApiMapper.ToDto).ToList(),
            layoutManager.GetLoadedLayouts().Select(ObjectApiMapper.ToDto).ToList(),
            sceneView.GetRuntimeStateSnapshots().Select(ObjectApiMapper.ToDto).ToList(),
            ObjectApiMapper.ToLocationDto(sceneView.GetCurrentLocationContext()));

    public WorldObject? GetObject(Guid id)
        => sceneView.TryGetSceneObjectSnapshot(id, out var snapshot)
            ? ObjectApiMapper.ToDto(snapshot)
            : null;
}

internal sealed class ObjectMutationApi(IObjectMutationService mutationService, IObjectSceneView sceneView)
{
    public Guid? Create(WorldObject dto)
        => WithDetachedSnapshotOrNull(dto, snapshot => mutationService.CreateObjectSnapshot(snapshot with
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
        }));

    public Guid? Import(WorldObject dto)
        => WithSnapshotOrNull(dto, mutationService.ImportObjectSnapshot);

    public bool Update(WorldObject dto)
        => WithSnapshotOrFalse(dto, mutationService.TryUpdate);

    public bool Patch(ObjectPatchUpdate dto)
    {
        if (!sceneView.TryGetPersistedObjectSnapshot(dto.ObjectId, out var snapshot)
            || !ObjectApiMapper.TryToPatch(dto.Patch, snapshot.Kind, out var patch))
        {
            return false;
        }

        return mutationService.TryPatch(dto.ObjectId, patch);
    }

    public bool Remove(Guid id)
        => mutationService.Remove(id);

    public Guid? Duplicate(Guid id)
        => mutationService.TryDuplicate(id, out var duplicateId)
            ? duplicateId
            : null;

    private static Guid? WithSnapshotOrNull(WorldObject dto, Func<ObjectSnapshot, Guid?> apply)
        => ObjectApiMapper.TryToSnapshot(dto, out var snapshot)
            ? apply(snapshot)
            : null;

    private static Guid? WithDetachedSnapshotOrNull(WorldObject dto, Func<ObjectSnapshot, Guid?> apply)
        => ObjectApiMapper.TryToDetachedSnapshot(dto, out var snapshot)
            ? apply(snapshot)
            : null;

    private static bool WithSnapshotOrFalse(WorldObject dto, Func<ObjectSnapshot, bool> apply)
        => ObjectApiMapper.TryToSnapshot(dto, out var snapshot)
            && apply(snapshot);
}

internal sealed class ObjectRuntimeApi(IObjectSceneView sceneView)
{
    public IReadOnlyList<RuntimeObjectState> GetStates()
        => sceneView.GetRuntimeStateSnapshots().Select(ObjectApiMapper.ToDto).ToList();

    public RuntimeObjectState? GetState(Guid id)
        => sceneView.TryGetRuntimeStateSnapshot(id, out var snapshot)
            ? ObjectApiMapper.ToDto(snapshot)
            : null;
}

