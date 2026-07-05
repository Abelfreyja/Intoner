using Dalamud.Plugin.Services;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using System.Numerics;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

/// <summary> active scene reload result </summary>
/// <param name="LoadedAll">true when every desired object is active</param>
/// <param name="CanApply">true when the current location can receive scene objects</param>
/// <param name="NeedsRetry">true when a transient load failure left a refresh pending</param>
internal readonly record struct SceneReloadResult(bool LoadedAll, bool CanApply, bool NeedsRetry)
{
    public static SceneReloadResult InvalidLocation { get; } = new(false, false, false);

    public static SceneReloadResult LoadedLocation(bool loadedAll, bool needsRetry)
        => new(loadedAll, true, needsRetry);

    public bool IsApplied
        => CanApply && !NeedsRetry;
}

/// <summary>
/// Owns the active object scene, including reload flow, framework updates, and bounds publication.
/// </summary>
internal interface IObjectScene
{
    /// <summary>
    /// Gets the currently active object snapshots.
    /// </summary>
    /// <returns>The active object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetObjectSnapshots();

    /// <summary>
    /// Gets the current bounds snapshots for active scene objects.
    /// </summary>
    /// <returns>The active object bounds snapshots.</returns>
    IReadOnlyList<ObjectBoundsSnapshot> GetBoundsSnapshots();

    /// <summary>
    /// Gets the current location scope used for active scene loading and runtime state resolution.
    /// </summary>
    /// <returns>The current location scope.</returns>
    ObjectLocationScope GetCurrentLocationScope();

    /// <summary>
    /// Gets the runtime state snapshots for the given composed scene snapshots.
    /// </summary>
    /// <param name="sceneSnapshots">The composed scene snapshots to resolve.</param>
    /// <returns>The runtime state snapshots for the scene.</returns>
    IReadOnlyList<ObjectRuntimeStateSnapshot> GetRuntimeStateSnapshots(IReadOnlyList<ObjectSnapshot> sceneSnapshots);

    /// <summary>
    /// Tries to resolve one active scene object snapshot.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="snapshot">The resolved active snapshot when found.</param>
    /// <returns>true when the object is active in the scene.</returns>
    bool TryGetObjectSnapshot(Guid id, out ObjectSnapshot snapshot);

    /// <summary>
    /// Tries to resolve one runtime state snapshot from the given composed scene snapshots.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="sceneSnapshots">The composed scene snapshots to search.</param>
    /// <param name="snapshot">The resolved runtime state snapshot when found.</param>
    /// <returns>true when the scene contains that object.</returns>
    bool TryGetRuntimeStateSnapshot(Guid id, IReadOnlyList<ObjectSnapshot> sceneSnapshots, out ObjectRuntimeStateSnapshot snapshot);

    /// <summary>
    /// Marks that the active scene should be refreshed on the next framework update.
    /// </summary>
    void MarkNeedsRefresh();

    /// <summary>
    /// Checks whether temporary scene mutations can be applied immediately.
    /// </summary>
    /// <returns>true when the scene is loaded and ready for immediate runtime mutation.</returns>
    bool ShouldApplyChangesNow();

    /// <summary>
    /// Runs one scene mutation on the framework thread.
    /// </summary>
    /// <param name="applyRuntimeChanges">The scene mutation to apply.</param>
    /// <returns>The scene mutation result.</returns>
    ObjectTemporaryMutationStatus RunOnSceneThread(Func<ObjectTemporaryMutationStatus> applyRuntimeChanges);

    /// <summary>
    /// Reloads the active scene for the current location scope.
    /// </summary>
    /// <returns>the scene reload result.</returns>
    SceneReloadResult ReloadForCurrentLocation();

    /// <summary>
    /// Runs one framework update step for the active scene.
    /// </summary>
    void FrameworkUpdate();

    /// <summary>
    /// Handles the start of a zone transition for the active scene.
    /// </summary>
    void HandleZoneSwitchStart();

    /// <summary>
    /// Handles the end of a zone transition for the active scene.
    /// </summary>
    void HandleZoneSwitchEnd();

    /// <summary>
    /// Handles logout for the active scene.
    /// </summary>
    void HandleLogout();
}

internal sealed class ObjectScene : IObjectScene
{
    private enum ActiveEntryReuseStatus
    {
        Reused,
        Recreate,
        Rejected,
    }

    private readonly IFramework            _framework;
    private readonly IObjectKindService    _objectKindService;
    private readonly IObjectSceneSnapshotResolver _snapshotResolver;
    private readonly IObjectSceneState     _sceneState;
    private readonly IObjectMutationService _mutationService;
    private readonly IObjectRuntimeLocationService _locationService;
    private readonly IObjectResolvedCollectionStore _collectionStore;

    public ObjectScene(
        IFramework framework,
        IObjectKindService objectKindService,
        IObjectSceneSnapshotResolver snapshotResolver,
        IObjectSceneState sceneState,
        IObjectMutationService mutationService,
        IObjectRuntimeLocationService locationService,
        IObjectResolvedCollectionStore collectionStore)
    {
        _framework = framework;
        _objectKindService = objectKindService;
        _snapshotResolver = snapshotResolver;
        _sceneState = sceneState;
        _mutationService = mutationService;
        _locationService = locationService;
        _collectionStore = collectionStore;
    }

    public IReadOnlyList<ObjectSnapshot> GetObjectSnapshots()
        => _sceneState.GetObjectSnapshots();

    public IReadOnlyList<ObjectBoundsSnapshot> GetBoundsSnapshots()
        => _sceneState.GetBoundsSnapshots();

    public ObjectLocationScope GetCurrentLocationScope()
        => _locationService.GetCurrentLocationScope();

    public IReadOnlyList<ObjectRuntimeStateSnapshot> GetRuntimeStateSnapshots(IReadOnlyList<ObjectSnapshot> sceneSnapshots)
    {
        var currentLocation = GetCurrentLocationScope();
        var activeObjectIds = _sceneState.GetActiveObjectIds();
        var runtimeFailureCodes = _sceneState.GetRuntimeFailureCodes();

        var runtimeSnapshots = new List<ObjectRuntimeStateSnapshot>(sceneSnapshots.Count);
        foreach (var snapshot in sceneSnapshots)
        {
            runtimeSnapshots.Add(CreateRuntimeStateSnapshot(
                snapshot,
                activeObjectIds.Contains(snapshot.Id),
                runtimeFailureCodes.GetValueOrDefault(snapshot.Id),
                currentLocation));
        }

        return runtimeSnapshots;
    }

    public bool TryGetObjectSnapshot(Guid id, out ObjectSnapshot snapshot)
        => _sceneState.TryGetObjectSnapshot(id, out snapshot);

    public bool TryGetRuntimeStateSnapshot(Guid id, IReadOnlyList<ObjectSnapshot> sceneSnapshots, out ObjectRuntimeStateSnapshot snapshot)
    {
        if (_sceneState.TryGetEntry(id, out _))
        {
            snapshot = CreateActiveRuntimeStateSnapshot(id);
            return true;
        }

        return TryCreateInactiveRuntimeStateSnapshot(
            id,
            sceneSnapshots,
            GetCurrentLocationScope(),
            out snapshot);
    }

    public void MarkNeedsRefresh()
        => _sceneState.MarkNeedsRefresh();

    public bool ShouldApplyChangesNow()
    {
        var refreshState = _sceneState.GetRefreshState();
        return !refreshState.IsZoning && GetCurrentLocationScope().IsValid;
    }

    public ObjectTemporaryMutationStatus RunOnSceneThread(Func<ObjectTemporaryMutationStatus> applyRuntimeChanges)
        => ObjectFrameworkUtility.RunOnFrameworkThread(_framework, applyRuntimeChanges);

    public SceneReloadResult ReloadForCurrentLocation()
        => ObjectFrameworkUtility.RunOnFrameworkThread(_framework, ReloadForCurrentLocationInternal);

    public void FrameworkUpdate()
    {
        RefreshObjectsForCurrentLocation();

        var sceneObjects = GetActiveSceneObjects();
        ProcessFrameworkUpdates(sceneObjects);
        PublishBounds(sceneObjects);
    }

    public void HandleZoneSwitchStart()
    {
        _sceneState.BeginZoning();
        _mutationService.ClearAllActiveEntries(removePersistedState: false);
    }

    public void HandleZoneSwitchEnd()
        => _sceneState.EndZoning();

    public void HandleLogout()
    {
        _sceneState.HandleLogout();
        _mutationService.ClearAllActiveEntries(removePersistedState: false);
    }

    private void RefreshObjectsForCurrentLocation()
    {
        var refreshState = _sceneState.GetRefreshState();
        if (!ShouldReloadForCurrentLocation(refreshState, out var currentLocation))
        {
            return;
        }

        ReloadForCurrentLocationInternal(currentLocation);
    }

    private SceneReloadResult ReloadForCurrentLocationInternal()
        => ReloadForCurrentLocationInternal(_locationService.GetCurrentLocationScope());

    private SceneReloadResult ReloadForCurrentLocationInternal(ObjectLocationScope currentLocation)
    {
        if (!currentLocation.IsValid)
        {
            return ClearSceneForInvalidLocation();
        }

        var desiredRequests = _snapshotResolver.GetLoadRequests(currentLocation);
        var activeObjectIds = ReconcileActiveEntries(desiredRequests);
        SceneReloadResult loadResult = LoadMissingEntries(desiredRequests, activeObjectIds);

        _sceneState.SetLoadedLocation(currentLocation, needsRefresh: loadResult.NeedsRetry);
        return loadResult;
    }

    private static bool SnapshotsMatch(ObjectSceneEntry entry, ObjectSnapshot desiredSnapshot)
    {
        if (entry.SceneObject.Kind != desiredSnapshot.Kind)
        {
            return false;
        }

        return entry.Snapshot == desiredSnapshot;
    }

    private List<ISceneObject> GetActiveSceneObjects()
    {
        var entries = _sceneState.GetEntriesSnapshot();
        var sceneObjects = new List<ISceneObject>(entries.Count);
        foreach (var entry in entries)
        {
            sceneObjects.Add(entry.SceneObject);
        }

        return sceneObjects;
    }

    private void ProcessFrameworkUpdates(IReadOnlyList<ISceneObject> sceneObjects)
    {
        foreach (var sceneObject in sceneObjects)
        {
            if (!sceneObject.NeedsFrameworkUpdates)
            {
                continue;
            }

            sceneObject.FrameworkUpdate();
        }
    }

    private void PublishBounds(IReadOnlyList<ISceneObject> sceneObjects)
    {
        List<ObjectBoundsSnapshot> boundsSnapshots = [];
        foreach (var sceneObject in sceneObjects)
        {
            if (!TryCreateBoundsSnapshot(sceneObject, out var boundsSnapshot))
            {
                continue;
            }

            boundsSnapshots.Add(boundsSnapshot);
        }

        _sceneState.SetBoundsSnapshots(boundsSnapshots);
    }

    private bool ShouldReloadForCurrentLocation(ObjectSceneRefreshState refreshState, out ObjectLocationScope currentLocation)
    {
        currentLocation = default;
        if (refreshState.IsZoning)
        {
            return false;
        }

        if (!HasSceneLoadState(refreshState))
        {
            return false;
        }

        currentLocation = _locationService.GetCurrentLocationScope();
        return currentLocation.IsValid
            && (refreshState.NeedsRefresh
                || !refreshState.HasLoadedLocation
                || refreshState.LoadedLocation != currentLocation);
    }

    private bool HasSceneLoadState(ObjectSceneRefreshState refreshState)
        => refreshState.NeedsRefresh
            || refreshState.HasLoadedLocation
            || _snapshotResolver.HasAnyLoadState();

    private SceneReloadResult ClearSceneForInvalidLocation()
    {
        _mutationService.ClearAllActiveEntries(removePersistedState: false);
        _sceneState.ClearLoadedLocation(needsRefresh: false);
        return SceneReloadResult.InvalidLocation;
    }

    private HashSet<Guid> ReconcileActiveEntries(IReadOnlyList<ObjectSceneLoadRequest> desiredRequests)
    {
        var desiredRequestsById = desiredRequests.ToDictionary(static request => request.Snapshot.Id);

        List<ObjectSceneEntry> entriesToDestroy = [];
        List<ObjectSceneEntry> entriesToReplace = [];
        HashSet<Guid> activeObjectIds = [];
        foreach (var entry in _sceneState.GetEntriesSnapshot())
        {
            var reuseStatus = TryReuseActiveEntry(entry, desiredRequestsById);
            if (reuseStatus == ActiveEntryReuseStatus.Recreate)
            {
                _sceneState.TryRemoveEntry(entry.Snapshot.Id, out _);
                if (desiredRequestsById.TryGetValue(entry.Snapshot.Id, out var desiredRequest))
                {
                    _mutationService.RefreshCollectionUsage(entry.Snapshot, desiredRequest.Snapshot);
                    entriesToReplace.Add(entry);
                }
                else
                {
                    entriesToDestroy.Add(entry);
                }

                continue;
            }

            activeObjectIds.Add(entry.Snapshot.Id);
            if (reuseStatus == ActiveEntryReuseStatus.Reused)
            {
                _sceneState.ClearRuntimeFailure(entry.Snapshot.Id);
            }
        }

        _sceneState.ClearRuntimeFailuresExcept(desiredRequestsById.Keys);
        _mutationService.DestroyEntries(entriesToDestroy);
        _mutationService.DestroyReplacedEntries(entriesToReplace);
        return activeObjectIds;
    }

    private SceneReloadResult LoadMissingEntries(IReadOnlyList<ObjectSceneLoadRequest> desiredRequests, IReadOnlySet<Guid> activeObjectIds)
    {
        var loadedAll = true;
        var needsRetry = false;
        foreach (var request in desiredRequests)
        {
            if (activeObjectIds.Contains(request.Snapshot.Id))
            {
                continue;
            }

            if (!TryLoadDesiredEntry(request, out string? failureCode))
            {
                loadedAll = false;
                needsRetry |= ObjectRuntimeFailureCodes.ShouldRetrySceneLoad(failureCode);
            }
        }

        return SceneReloadResult.LoadedLocation(loadedAll, needsRetry);
    }

    private bool TryLoadDesiredEntry(ObjectSceneLoadRequest request, out string? failureCode)
    {
        failureCode = null;
        if (!_objectKindService.CanCreate(request.Snapshot.Kind))
        {
            failureCode = ObjectRuntimeFailureCodes.ServiceMissing;
            _sceneState.SetRuntimeFailure(request.Snapshot.Id, failureCode);
            return false;
        }

        bool loaded = _mutationService.TryCreateObject(
            request.Snapshot,
            out _,
            applyDefaultLayout: false,
            persistSnapshot: false,
            sourceOverride: request.Source);
        if (!loaded)
        {
            _ = _sceneState.TryGetRuntimeFailureCode(request.Snapshot.Id, out failureCode);
        }

        return loaded;
    }

    private ActiveEntryReuseStatus TryReuseActiveEntry(ObjectSceneEntry entry, IReadOnlyDictionary<Guid, ObjectSceneLoadRequest> desiredRequestsById)
    {
        if (!desiredRequestsById.TryGetValue(entry.Snapshot.Id, out var desiredRequest)
         || entry.Source != desiredRequest.Source
         || !SnapshotsMatch(entry, desiredRequest.Snapshot))
        {
            return ActiveEntryReuseStatus.Recreate;
        }

        long currentCollectionRevision = _collectionStore.GetCollectionRevision(
            desiredRequest.Snapshot.CollectionId,
            ObjectSnapshotUtility.GetRootResourcePath(desiredRequest.Snapshot));
        if (entry.ResourceCollection.MatchesCurrentRevision(currentCollectionRevision))
        {
            return ActiveEntryReuseStatus.Reused;
        }

        if (entry.ResourceCollection.IsPendingWithoutRuntimeRevision(currentCollectionRevision))
        {
            UpsertEntryResourceCollection(entry, SceneResourceCollectionState.Current(currentCollectionRevision));
            return ActiveEntryReuseStatus.Reused;
        }

        var refreshResult = entry.SceneObject.TryRefreshResources(desiredRequest.Snapshot);
        switch (refreshResult)
        {
            case SceneObjectUpdateResult.Applied:
                break;
            case SceneObjectUpdateResult.RequiresRecreate:
                return ActiveEntryReuseStatus.Recreate;
            default:
                _sceneState.SetRuntimeFailure(entry.Snapshot.Id, ObjectRuntimeFailureCodes.UpdateRejected);
                return ActiveEntryReuseStatus.Rejected;
        }

        UpsertEntryResourceCollection(entry, SceneResourceCollectionState.Current(currentCollectionRevision));
        return ActiveEntryReuseStatus.Reused;
    }

    private void UpsertEntryResourceCollection(ObjectSceneEntry entry, SceneResourceCollectionState resourceCollection)
        => _sceneState.UpsertEntry(new ObjectSceneEntry
        {
            SceneObject = entry.SceneObject,
            Source = entry.Source,
            ResourceCollection = resourceCollection,
        });

    private bool TryCreateInactiveRuntimeStateSnapshot(
        Guid id,
        IReadOnlyList<ObjectSnapshot> sceneSnapshots,
        ObjectLocationScope currentLocation,
        out ObjectRuntimeStateSnapshot snapshot)
    {
        foreach (var sceneSnapshot in sceneSnapshots)
        {
            if (sceneSnapshot.Id != id)
            {
                continue;
            }

            _sceneState.TryGetRuntimeFailureCode(id, out var failureCode);
            snapshot = CreateRuntimeStateSnapshot(
                sceneSnapshot,
                isActive: false,
                failureCode,
                currentLocation);
            return true;
        }

        snapshot = default!;
        return false;
    }

    private static ObjectRuntimeStateSnapshot CreateActiveRuntimeStateSnapshot(Guid id)
        => new(
            id,
            ObjectRuntimeStateKind.Active,
            null);

    private static bool TryCreateBoundsSnapshot(ISceneObject sceneObject, out ObjectBoundsSnapshot boundsSnapshot)
    {
        var snapshot = sceneObject.Snapshot;
        var hasWorldBounds = sceneObject.TryGetBounds(out var worldBounds);
        var hasLocalBounds = sceneObject.TryGetOrientedBounds(out var localBounds);
        var hasPlacementClearance = sceneObject.TryGetPlacementClearance(out ObjectPlacementClearance placementClearance);
        var overlayShape = CreateOverlayShape(snapshot);

        if (!hasWorldBounds && !hasLocalBounds && !hasPlacementClearance && overlayShape is null)
        {
            boundsSnapshot = default!;
            return false;
        }

        var min = snapshot.Transform.Position;
        var max = snapshot.Transform.Position;
        if (hasWorldBounds)
        {
            min = worldBounds.Min;
            max = worldBounds.Max;
        }
        else if (hasLocalBounds)
        {
            var axisBounds = CreateAxisAlignedBounds(localBounds);
            min = axisBounds.Min;
            max = axisBounds.Max;
        }

        boundsSnapshot = new ObjectBoundsSnapshot(
            snapshot.Id,
            snapshot.Name,
            snapshot.Kind,
            sceneObject.Address,
            min,
            max,
            hasLocalBounds ? localBounds : null,
            hasPlacementClearance ? placementClearance : null,
            sceneObject.PlacementSurfaceSupport,
            overlayShape);
        return true;
    }

    private static ObjectRuntimeStateSnapshot CreateRuntimeStateSnapshot(
        ObjectSnapshot snapshot,
        bool isActive,
        string? failureCode,
        ObjectLocationScope currentLocation)
    {
        if (isActive)
        {
            return new ObjectRuntimeStateSnapshot(
                snapshot.Id,
                ObjectRuntimeStateKind.Active,
                null);
        }

        if (currentLocation.IsValid && !ObjectSnapshotUtility.MatchesLocation(snapshot, currentLocation))
        {
            return new ObjectRuntimeStateSnapshot(
                snapshot.Id,
                ObjectRuntimeStateKind.LocationMismatch,
                null);
        }

        if (!string.IsNullOrEmpty(failureCode))
        {
            return new ObjectRuntimeStateSnapshot(
                snapshot.Id,
                ObjectRuntimeStateKind.LoadFailed,
                failureCode);
        }

        return new ObjectRuntimeStateSnapshot(
            snapshot.Id,
            ObjectRuntimeStateKind.Inactive,
            null);
    }

    private static ObjectOverlayShapeSnapshot? CreateOverlayShape(ObjectSnapshot snapshot)
    {
        if (snapshot.Kind != ObjectKind.Light || snapshot.Model is not LightModel lightModel)
        {
            return null;
        }

        var transform = ObjectShapeMath.CreateRigidTransform(snapshot.Transform.Position, snapshot.Transform.RotationDegrees);
        var range = MathF.Max(lightModel.Shape.Range, 0.01f);
        return lightModel.LightType switch
        {
            LightType.WorldLight => null,
            LightType.AreaLight => new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.Sphere, transform, range, 0f),
            LightType.SpotLight => new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.Cone, transform, range, lightModel.Shape.LightAngle),
            LightType.FlatLight => new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.SquarePyramid, transform, range, lightModel.Shape.FalloffAngle),
            _ => null,
        };
    }

    private static AxisAlignedBounds CreateAxisAlignedBounds(OrientedBounds bounds)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        ObjectShapeMath.CopyOrientedBoxCorners(bounds, corners);

        var min = corners[0];
        var max = corners[0];
        for (var index = 1; index < corners.Length; ++index)
        {
            min = Vector3.Min(min, corners[index]);
            max = Vector3.Max(max, corners[index]);
        }

        return new AxisAlignedBounds(min, max);
    }
}

