using Dalamud.Plugin.Services;
using Intoner.Objects.Interop.Ipc;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using Intoner.Services;
using Intoner.Utils;
using TemporaryObjectChangeKindDto = Intoner.Objects.Api.TemporaryObjectChangeKind;
using TemporaryObjectChangeKindModel = Intoner.Objects.Models.ObjectTemporaryChangeKind;

namespace Intoner.Objects.Api;

internal sealed class ObjectApi(
    ApiState pluginState,
    LayoutApi layouts,
    TemporarySourceApi temporarySources,
    SourceBuilderApi sharing,
    SceneApi scene,
    PersistentSceneApi persistentScene,
    ObjectMutationApi objects,
    RuntimeApi runtime)
{
    public readonly ApiState PluginState = pluginState;
    public readonly LayoutApi Layouts = layouts;
    public readonly TemporarySourceApi TemporarySources = temporarySources;
    public readonly SourceBuilderApi Sharing = sharing;
    public readonly SceneApi Scene = scene;
    public readonly PersistentSceneApi PersistentScene = persistentScene;
    public readonly ObjectMutationApi Objects = objects;
    public readonly RuntimeApi Runtime = runtime;
}

internal sealed class ApiState(IntonerBuildInfoService buildInfo)
{
    private const ObjectApiCapabilities Capabilities =
        ObjectApiCapabilities.SceneQueries
        | ObjectApiCapabilities.PersistentObjects
        | ObjectApiCapabilities.Layouts
        | ObjectApiCapabilities.TemporarySources
        | ObjectApiCapabilities.TemporaryObjectChanges
        | ObjectApiCapabilities.SourceBuilder
        | ObjectApiCapabilities.RuntimeState
        | ObjectApiCapabilities.RevisionEvents
        | ObjectApiCapabilities.PersistentSceneApply
        | ObjectApiCapabilities.SavedLayoutEvents;

    private readonly Guid _instanceId = Guid.NewGuid();
    private int _state = (int)ObjectApiHostState.Ready;

    public ObjectApiInfo GetInfo()
        => CreateInfo((ObjectApiHostState)Volatile.Read(ref _state));

    public ObjectApiInfo BeginDisposing()
    {
        Interlocked.Exchange(ref _state, (int)ObjectApiHostState.Disposing);
        return GetInfo();
    }

    private ObjectApiInfo CreateInfo(ObjectApiHostState state)
        => new(ObjectApiVersions.Current, buildInfo.DisplayVersion, _instanceId, state, Capabilities);
}

internal sealed class LayoutApi(
    ApiState apiState,
    IObjectLayoutManager layoutManager,
    IObjectManager objectManager,
    IObjectRevisionTracker revisionTracker,
    IFramework framework,
    ObjectStateLock stateLock)
{
    public SavedObjectLayoutsSnapshot GetAll()
        => FrameworkThreadUtility.Run(framework, () =>
        {
            lock (stateLock.Value)
            {
                return new SavedObjectLayoutsSnapshot(
                    apiState.GetInfo().InstanceId,
                    revisionTracker.GetSavedLayoutsRevision(),
                    layoutManager.GetLayouts().Select(ObjectApiMapper.ToSavedLayoutInfo).ToList());
            }
        });

    public SavedObjectLayout? Get(Guid id)
        => FrameworkThreadUtility.Run(framework, () =>
        {
            lock (stateLock.Value)
            {
                return id != Guid.Empty && layoutManager.TryGetLayout(id, out ObjectLayoutSnapshot layout)
                    ? ObjectApiMapper.ToSavedLayout(layout)
                    : null;
            }
        });

    public Guid? GetDefault()
        => layoutManager.GetDefaultLayoutId();

    public SavedObjectLayoutMutationResult Create(string name)
        => ToMutationResult(objectManager.CreateEmptyLayout(name));

    public SavedObjectLayoutMutationResult SaveCurrent(SavedObjectLayoutSaveRequest? request)
        => request is null
            ? InvalidRequest("layout save request is invalid")
            : ToMutationResult(objectManager.SaveCurrentObjectsAsLayout(request.Name, request.ExpectedPersistentRevision));

    public SavedObjectLayoutMutationResult SetDefault(SavedObjectLayoutSetDefaultRequest? request)
        => request is null || request.LayoutId == Guid.Empty
            ? InvalidRequest("set default layout request is invalid")
            : ToMutationResult(objectManager.SelectLayout(
                request.LayoutId,
                request.ExpectedPersistentRevision,
                request.ExpectedLayoutRevision));

    public SavedObjectLayoutMutationResult ClearDefault(long expectedPersistentRevision)
        => ToMutationResult(objectManager.SelectLayout(
            null,
            expectedPersistentRevision,
            expectedLayoutRevision: null));

    public SavedObjectLayoutMutationResult Delete(SavedObjectLayoutDeleteRequest? request)
        => request is null || request.LayoutId == Guid.Empty
            ? InvalidRequest("layout deletion request is invalid")
            : ToMutationResult(objectManager.DeleteLayout(
                request.LayoutId,
                request.ExpectedLayoutRevision,
                request.ExpectedPersistentRevision));

    private SavedObjectLayoutMutationResult ToMutationResult(PersistentMutationResult result)
    {
        ObjectRevisionSnapshot revisions = result.SceneRevision > 0
            ? new ObjectRevisionSnapshot(
                result.SceneRevision,
                result.PersistentRevision,
                result.SavedLayoutsRevision)
            : revisionTracker.GetSnapshot();
        return new SavedObjectLayoutMutationResult(
            ApiResultMapper.ToDto(result.Status),
            result.IsAccepted,
            result.EntityId,
            revisions.SavedLayoutsRevision,
            revisions.SceneRevision,
            revisions.PersistentSceneRevision,
            result.Message);
    }

    private SavedObjectLayoutMutationResult InvalidRequest(string message)
        => ToMutationResult(PersistentMutationResult.Failed(PersistentMutationStatus.InvalidRequest, message));
}

internal sealed class TemporarySourceApi(
    ITemporarySourceStore sourceStore,
    ITemporarySourceService temporarySourceService,
    IObjectSceneView sceneView)
{
    public IReadOnlyList<TemporarySourceInfo> GetSources(string ownerPrefix)
    {
        List<TemporarySourceInfo> sources = [];
        foreach (ObjectTemporarySourceSnapshot source in sourceStore.GetSources())
        {
            if (IpcSourceOwnership.TryGetSourceId(ownerPrefix, source.SourceKey, out string sourceId))
            {
                sources.Add(ObjectApiMapper.ToTemporarySource(source, sourceId));
            }
        }

        return sources;
    }

    public TemporarySourceMutationResult OwnershipFailure()
        => new(
            TemporarySourceMutationStatus.OwnershipMismatch,
            0,
            false,
            sceneView.GetSceneRevision(),
            "temporary source caller identity is unavailable");

    public TemporarySourceMutationResult InvalidRequest(string message)
        => new(
            TemporarySourceMutationStatus.InvalidSource,
            0,
            false,
            sceneView.GetSceneRevision(),
            message);

    public TemporarySourceMutationResult ApplySource(string sourceKey, TemporarySourceApplyRequest dto)
    {
        if (dto.Objects is null || !ObjectApiMapper.TryToDetachedSnapshots(dto.Objects, out List<ObjectSnapshot> snapshots))
        {
            return Failure(
                TemporarySourceMutationStatus.InvalidObject,
                sourceKey,
                "temporary source contains invalid object data");
        }

        if (!ObjectApiMapper.TryToTemporaryCollections(dto.Collections, out List<ObjectTemporaryCollectionData> collections))
        {
            return Failure(
                TemporarySourceMutationStatus.InvalidCollection,
                sourceKey,
                "temporary source contains invalid collection data");
        }

        return ToDto(temporarySourceService.TryApply(
            sourceKey,
            dto.SessionId,
            dto.Name,
            snapshots,
            collections,
            dto.Revision));
    }

    public TemporarySourceMutationResult ApplyObjectChanges(string sourceKey, TemporaryObjectChangeSet dto)
    {
        if (dto.Changes is null)
        {
            return Failure(
                TemporarySourceMutationStatus.InvalidObject,
                sourceKey,
                "temporary source contains invalid object changes");
        }

        if (!TryToChanges(dto.Changes, out List<ObjectTemporaryChange> changes))
        {
            return Failure(
                TemporarySourceMutationStatus.InvalidObject,
                sourceKey,
                "temporary source contains invalid object changes");
        }

        return ToDto(temporarySourceService.TryApplyObjectChanges(
            sourceKey,
            dto.SessionId,
            dto.Name,
            changes,
            dto.Revision));
    }

    public TemporarySourceMutationResult RemoveSource(string sourceKey, TemporarySourceRemoveRequest dto)
        => ToDto(temporarySourceService.TryRemove(sourceKey, dto.SessionId, dto.Revision));

    private static bool TryToChanges(
        IReadOnlyList<TemporaryObjectChange> dtos,
        out List<ObjectTemporaryChange> changes)
    {
        changes = new List<ObjectTemporaryChange>(dtos.Count);
        foreach (TemporaryObjectChange? dto in dtos)
        {
            if (dto is null)
            {
                changes = [];
                return false;
            }

            switch (dto.Kind)
            {
                case TemporaryObjectChangeKindDto.Upsert when dto.Object is not null:
                    if (!ObjectApiMapper.TryToDetachedSnapshot(dto.Object, out ObjectSnapshot snapshot))
                    {
                        changes = [];
                        return false;
                    }

                    changes.Add(new ObjectTemporaryChange(TemporaryObjectChangeKindModel.Upsert, snapshot, Guid.Empty));
                    break;
                case TemporaryObjectChangeKindDto.Patch when dto.Patch is not null && dto.ObjectId != Guid.Empty:
                    if (!ObjectApiMapper.TryToPatch(dto.Patch, out ObjectSnapshotPatch patch))
                    {
                        changes = [];
                        return false;
                    }

                    changes.Add(new ObjectTemporaryChange(TemporaryObjectChangeKindModel.Patch, null, dto.ObjectId, patch));
                    break;
                case TemporaryObjectChangeKindDto.Remove when dto.ObjectId != Guid.Empty:
                    changes.Add(new ObjectTemporaryChange(TemporaryObjectChangeKindModel.Remove, null, dto.ObjectId));
                    break;
                default:
                    changes = [];
                    return false;
            }
        }

        return true;
    }

    private TemporarySourceMutationResult ToDto(ObjectTemporaryMutationResult result)
    {
        TemporarySourceMutationStatus status = ObjectApiMapper.ToTemporaryMutationStatus(result.Status);
        return new(
            status,
            result.SourceRevision,
            result.IsAccepted,
            sceneView.GetSceneRevision(),
            GetStatusMessage(status, result.IsAccepted));
    }

    private TemporarySourceMutationResult Failure(
        TemporarySourceMutationStatus status,
        string sourceKey,
        string message)
        => new(status, sourceStore.GetRevision(sourceKey), false, sceneView.GetSceneRevision(), message);

    private static string GetStatusMessage(TemporarySourceMutationStatus status, bool accepted)
        => status switch
        {
            TemporarySourceMutationStatus.Success => string.Empty,
            TemporarySourceMutationStatus.AlreadyApplied => string.Empty,
            TemporarySourceMutationStatus.InvalidSource => "source id, session id, and a positive revision are required",
            TemporarySourceMutationStatus.InvalidObject => "temporary source contains invalid object data",
            TemporarySourceMutationStatus.InvalidCollection => "temporary source contains invalid collection data",
            TemporarySourceMutationStatus.StaleRevision => "source revision is older than the authoritative revision",
            TemporarySourceMutationStatus.ObjectNotFound => "temporary source or object was not found",
            TemporarySourceMutationStatus.SourceMismatch => "source session does not match the authoritative session",
            TemporarySourceMutationStatus.RuntimeApplyFailed when accepted => "source state was accepted but runtime reconciliation failed",
            TemporarySourceMutationStatus.RuntimeApplyFailed => "source runtime recovery must complete before another mutation",
            TemporarySourceMutationStatus.OwnershipMismatch => "temporary source is not owned by the calling plugin",
            TemporarySourceMutationStatus.IdentityConflict => "one or more runtime object ids belong to another scene source",
            _ => "temporary source mutation failed",
        };

}

internal sealed class SourceBuilderApi(ITemporarySourceBuilder sourceBuilder)
{
    public async Task<byte[]> BuildTemporarySource(TemporarySourceBuildRequest? dto, CancellationToken cancellationToken)
    {
        TemporarySourceBuildResult result = await sourceBuilder
            .BuildTemporarySourceAsync(dto, cancellationToken)
            .ConfigureAwait(false);
        return TemporarySourceBuildResultWire.Serialize(result);
    }
}

internal sealed class SceneApi(
    IObjectLayoutManager layoutManager,
    IObjectSceneView sceneView,
    IFramework framework,
    ObjectStateLock stateLock)
{
    public ObjectSceneSnapshot GetSceneSnapshot()
        => FrameworkThreadUtility.Run(framework, () =>
        {
            lock (stateLock.Value)
            {
                return CreateSceneSnapshot();
            }
        });

    public WorldObject? GetObject(Guid id)
        => sceneView.TryGetSceneObjectSnapshot(id, out ObjectSnapshot snapshot)
            ? ObjectApiMapper.ToWorldObject(snapshot)
            : null;

    public IReadOnlyList<LoadedObjectLayout> GetLoadedLayouts()
        => layoutManager.GetLoadedLayouts().Select(ObjectApiMapper.ToLoadedLayout).ToList();

    private ObjectSceneSnapshot CreateSceneSnapshot()
        => new(
            sceneView.GetSceneRevision(),
            sceneView.GetPersistentSceneRevision(),
            layoutManager.GetDefaultLayoutId(),
            sceneView.GetStandaloneObjectSnapshots().Select(ObjectApiMapper.ToWorldObject).ToList(),
            layoutManager.GetLoadedLayouts().Select(ObjectApiMapper.ToLoadedLayout).ToList(),
            sceneView.GetRuntimeStateSnapshots().Select(ObjectApiMapper.ToRuntimeState).ToList(),
            ObjectApiMapper.ToLocation(sceneView.GetCurrentLocationContext()));
}

internal sealed class PersistentSceneApi(
    IObjectLayoutManager layoutManager,
    IObjectFolderService folderService,
    IObjectSceneView sceneView,
    IObjectManager objectManager,
    IObjectKindService objectKindService,
    IObjectRevisionTracker revisionTracker,
    IFramework framework,
    ObjectStateLock stateLock)
{
    public PersistentObjectSceneSnapshot GetSnapshot()
        => FrameworkThreadUtility.Run(framework, () =>
        {
            lock (stateLock.Value)
            {
                return CreateSnapshot();
            }
        });

    public PersistentObject? GetObject(Guid id)
        => sceneView.TryGetPersistentSceneObjectSnapshot(id, out ObjectSnapshot snapshot)
            ? ObjectApiMapper.ToPersistentObject(snapshot)
            : null;

    public PersistentObjectSceneMutationResult Apply(PersistentObjectSceneApplyRequest? dto)
    {
        if (!ObjectApiMapper.TryToPersistentSceneUpdate(dto, out ObjectPersistentSceneUpdate update)
            || !TrySanitizeObjects(update.StandaloneObjects, out List<ObjectSnapshot> standaloneObjects)
            || !TrySanitizeObjects(update.DefaultLayoutObjects, out List<ObjectSnapshot> defaultLayoutObjects))
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "persistent scene payload is invalid");
        }

        PersistentMutationResult result = objectManager.TryApplyPersistentScene(update with
        {
            StandaloneObjects = standaloneObjects,
            DefaultLayoutObjects = defaultLayoutObjects,
        });
        ObjectRevisionSnapshot revisions = result.SceneRevision > 0
            ? new ObjectRevisionSnapshot(
                result.SceneRevision,
                result.PersistentRevision,
                result.SavedLayoutsRevision)
            : revisionTracker.GetSnapshot();
        return new PersistentObjectSceneMutationResult(
            ApiResultMapper.ToDto(result.Status),
            result.IsAccepted,
            revisions.SceneRevision,
            revisions.PersistentSceneRevision,
            result.Message);
    }

    private PersistentObjectSceneSnapshot CreateSnapshot()
    {
        SavedObjectLayout? defaultLayout = null;
        ObjectFolderSceneState folderState = folderService.CaptureSceneState();
        Guid? defaultLayoutId = layoutManager.GetDefaultLayoutId();
        if (defaultLayoutId.HasValue && layoutManager.TryGetLayout(defaultLayoutId.Value, out ObjectLayoutSnapshot layout))
        {
            defaultLayout = ObjectApiMapper.ToSavedLayout(layout);
        }

        return new PersistentObjectSceneSnapshot(
            sceneView.GetPersistentSceneRevision(),
            defaultLayout,
            ObjectApiMapper.ToPersistentSet(
                sceneView.GetStandaloneObjectSnapshots(),
                folderState.StandaloneFolders),
            ObjectApiMapper.ToLocation(sceneView.GetCurrentLocationContext()));
    }

    private bool TrySanitizeObjects(
        IReadOnlyList<ObjectSnapshot> objects,
        out List<ObjectSnapshot> sanitizedObjects)
    {
        sanitizedObjects = new List<ObjectSnapshot>(objects.Count);
        foreach (ObjectSnapshot snapshot in objects)
        {
            if (!objectKindService.TrySanitizeSnapshot(snapshot, out ObjectSnapshot sanitizedSnapshot))
            {
                sanitizedObjects = [];
                return false;
            }

            sanitizedObjects.Add(sanitizedSnapshot);
        }

        return true;
    }

    private PersistentObjectSceneMutationResult Failure(ObjectApiResultStatus status, string message)
    {
        ObjectRevisionSnapshot revisions = revisionTracker.GetSnapshot();
        return new PersistentObjectSceneMutationResult(
            status,
            false,
            revisions.SceneRevision,
            revisions.PersistentSceneRevision,
            message);
    }
}

internal sealed class ObjectMutationApi(
    IObjectMutationService mutationService,
    IObjectSceneView sceneView,
    IObjectKindService objectKindService)
{
    public ObjectMutationResult Create(PersistentObjectWriteRequest? dto)
    {
        if (dto is null
            || !ObjectApiMapper.TryToPersistentSnapshot(dto.Object, out ObjectSnapshot snapshot))
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object payload is invalid");
        }

        snapshot = snapshot with
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
        };
        if (!objectKindService.TrySanitizeSnapshot(snapshot, out snapshot))
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object payload is invalid");
        }

        return ToDto(mutationService.CreateObjectSnapshot(snapshot, dto.ExpectedRevision));
    }

    public ObjectMutationResult Import(PersistentObjectWriteRequest? dto)
    {
        if (dto is null
            || !ObjectApiMapper.TryToPersistentSnapshot(dto.Object, out ObjectSnapshot snapshot))
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object payload is invalid");
        }

        snapshot = snapshot with
        {
            Id = snapshot.Id == Guid.Empty ? Guid.NewGuid() : snapshot.Id,
            CreatedAtUtc = snapshot.CreatedAtUtc == default ? DateTime.UtcNow : snapshot.CreatedAtUtc,
        };
        if (!objectKindService.TrySanitizeSnapshot(snapshot, out snapshot))
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object payload is invalid");
        }

        return ToDto(mutationService.RestoreObjectSnapshot(snapshot, dto.ExpectedRevision));
    }

    public ObjectMutationResult Update(PersistentObjectWriteRequest? dto)
    {
        if (dto is null
            || !ObjectApiMapper.TryToPersistentSnapshot(dto.Object, out ObjectSnapshot snapshot)
            || snapshot.Id == Guid.Empty)
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object payload is invalid");
        }

        if (!objectKindService.TrySanitizeSnapshot(snapshot, out snapshot))
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object payload is invalid");
        }

        return ToDto(mutationService.UpdateObjectSnapshot(snapshot, dto.ExpectedRevision));
    }

    public ObjectMutationResult Patch(PersistentObjectPatchRequest? dto)
    {
        if (dto is null
            || dto.ObjectId == Guid.Empty
            || dto.Patch is null)
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object patch is invalid");
        }

        if (!sceneView.TryGetPersistentSceneObjectSnapshot(dto.ObjectId, out ObjectSnapshot snapshot))
        {
            return Failure(ObjectApiResultStatus.NotFound, "object was not found");
        }

        if (!ObjectApiMapper.TryToPersistentPatch(dto.Patch, snapshot.Kind, out ObjectSnapshotPatch patch))
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object patch is invalid");
        }

        if (!objectKindService.TrySanitizeSnapshot(ObjectSnapshotUtility.ApplyPatch(snapshot, patch), out _))
        {
            return Failure(ObjectApiResultStatus.InvalidRequest, "object patch is invalid");
        }

        return ToDto(mutationService.PatchObjectSnapshot(dto.ObjectId, patch, dto.ExpectedRevision));
    }

    public ObjectMutationResult Remove(PersistentObjectTargetRequest? dto)
    {
        if (!TryValidateTarget(dto, out Guid id, out ObjectMutationResult? failure))
        {
            return failure!;
        }

        return ToDto(mutationService.RemoveObjectSnapshot(id, dto!.ExpectedRevision));
    }

    public ObjectMutationResult Duplicate(PersistentObjectTargetRequest? dto)
    {
        if (!TryValidateTarget(dto, out Guid id, out ObjectMutationResult? failure))
        {
            return failure!;
        }

        return ToDto(mutationService.DuplicateObjectSnapshot(id, dto!.ExpectedRevision));
    }

    private ObjectMutationResult ToDto(PersistentMutationResult result)
    {
        ObjectMutationReceipt? receipt = result.IsAccepted
            ? new ObjectMutationReceipt(
                result.EntityId,
                result.SceneRevision,
                result.PersistentRevision)
            : null;
        return new ObjectMutationResult(
            ApiResultMapper.ToDto(result.Status),
            result.IsAccepted,
            receipt,
            result.Message);
    }

    private bool TryValidateTarget(
        PersistentObjectTargetRequest? dto,
        out Guid id,
        out ObjectMutationResult? failure)
    {
        id = dto?.ObjectId ?? Guid.Empty;
        if (id != Guid.Empty)
        {
            failure = null;
            return true;
        }

        failure = Failure(ObjectApiResultStatus.InvalidRequest, "object id is invalid");
        return false;
    }

    private static ObjectMutationResult Failure(ObjectApiResultStatus status, string message)
        => new(status, false, null, message);
}

internal static class ApiResultMapper
{
    public static ObjectApiResultStatus ToDto(PersistentMutationStatus status)
        => status switch
        {
            PersistentMutationStatus.Success => ObjectApiResultStatus.Success,
            PersistentMutationStatus.InvalidRequest => ObjectApiResultStatus.InvalidRequest,
            PersistentMutationStatus.NotFound => ObjectApiResultStatus.NotFound,
            PersistentMutationStatus.Conflict => ObjectApiResultStatus.Conflict,
            PersistentMutationStatus.StorageFailed => ObjectApiResultStatus.StorageFailed,
            PersistentMutationStatus.RecoveryRequired => ObjectApiResultStatus.RecoveryRequired,
            PersistentMutationStatus.RuntimeApplyFailed => ObjectApiResultStatus.RuntimeApplyFailed,
            _ => ObjectApiResultStatus.RuntimeApplyFailed,
        };
}

internal sealed class RuntimeApi(IObjectSceneView sceneView)
{
    public IReadOnlyList<RuntimeObjectState> GetStates()
        => sceneView.GetRuntimeStateSnapshots().Select(ObjectApiMapper.ToRuntimeState).ToList();

    public RuntimeObjectState? GetState(Guid id)
        => sceneView.TryGetRuntimeStateSnapshot(id, out ObjectRuntimeStateSnapshot snapshot)
            ? ObjectApiMapper.ToRuntimeState(snapshot)
            : null;
}
