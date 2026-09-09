using Dalamud.Plugin.Services;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Utils;
using System.Numerics;
using System.Runtime.InteropServices;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

/// <summary> active scene reload result </summary>
/// <param name="LoadedAll">true when every desired object and resource update was applied</param>
/// <param name="CanApply">true when the current location can receive scene objects</param>
/// <param name="NeedsRetry">true when a transient failure or newer request left a refresh pending</param>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneReloadResult(bool LoadedAll, bool CanApply, bool NeedsRetry)
{
    public static SceneReloadResult InvalidLocation { get; } = new(false, false, false);

    public static SceneReloadResult LoadedLocation(bool loadedAll, bool needsRetry)
        => new(loadedAll, true, needsRetry);

    /// <summary> reconciliation finished, including any permanent object failures </summary>
    public bool IsApplied
        => CanApply && !NeedsRetry;
}

/// <summary>
/// Owns the active object scene, including reload flow, framework updates, and bounds publication.
/// </summary>
internal interface IObjectScene
{
    /// <summary>raised after a deferred scene refresh finishes reconciling desired state</summary>
    event Action SceneRefreshed;

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
    SceneLocationScope GetCurrentLocationScope();

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

internal sealed class ObjectScene : IObjectScene, IDisposable
{
    private readonly IFramework            _framework;
    private readonly IObjectKindService    _objectKindService;
    private readonly IObjectSceneSnapshotResolver _snapshotResolver;
    private readonly IObjectSceneState     _sceneState;
    private readonly IObjectSceneMutationService _sceneMutations;
    private readonly ISceneLocationService _locationService;
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly List<ObjectBoundsSnapshot> _boundsBuffer = [];

    public event Action? SceneRefreshed;

    public ObjectScene(
        IFramework framework,
        IObjectKindService objectKindService,
        IObjectSceneSnapshotResolver snapshotResolver,
        IObjectSceneState sceneState,
        IObjectSceneMutationService sceneMutations,
        ISceneLocationService locationService,
        IObjectResolvedCollectionStore collectionStore)
    {
        _framework = framework;
        _objectKindService = objectKindService;
        _snapshotResolver = snapshotResolver;
        _sceneState = sceneState;
        _sceneMutations = sceneMutations;
        _locationService = locationService;
        _collectionStore = collectionStore;

        _locationService.LocationInvalidated += HandleLocationInvalidated;
    }

    public IReadOnlyList<ObjectSnapshot> GetObjectSnapshots()
        => _sceneState.GetObjectSnapshots();

    public IReadOnlyList<ObjectBoundsSnapshot> GetBoundsSnapshots()
        => _sceneState.GetBoundsSnapshots();

    public SceneLocationScope GetCurrentLocationScope()
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
            _sceneState.TryGetRuntimeFailureCode(id, out string? failureCode);
            snapshot = CreateActiveRuntimeStateSnapshot(id, failureCode);
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

    public void Dispose()
        => _locationService.LocationInvalidated -= HandleLocationInvalidated;

    public SceneReloadResult ReloadForCurrentLocation()
        => FrameworkThreadUtility.Run(_framework, ReloadForCurrentLocationInternal);

    public void FrameworkUpdate()
    {
        RefreshObjectsForCurrentLocation();

        IReadOnlyList<ObjectSceneEntry> entries = _sceneState.GetEntriesSnapshot();
        ProcessFrameworkUpdates(entries);
        PublishBounds(entries);
    }

    public void HandleZoneSwitchStart()
    {
        _sceneState.BeginZoning();
        _sceneMutations.ClearAllActiveEntries(removePersistedState: false);
        SceneRefreshed?.Invoke();
    }

    public void HandleZoneSwitchEnd()
        => _sceneState.EndZoning();

    public void HandleLogout()
    {
        _sceneState.HandleLogout();
        _sceneMutations.ClearAllActiveEntries(removePersistedState: false);
        SceneRefreshed?.Invoke();
    }

    private void RefreshObjectsForCurrentLocation()
    {
        var refreshState = _sceneState.GetRefreshState();
        if (!ShouldReloadForCurrentLocation(refreshState, out var currentLocation))
        {
            return;
        }

        SceneReloadResult result = ReloadForCurrentLocationInternal(currentLocation, refreshState.Revision);
        if (result.IsApplied)
        {
            SceneRefreshed?.Invoke();
        }
    }

    private SceneReloadResult ReloadForCurrentLocationInternal()
    {
        long refreshRevision = _sceneState.GetRefreshState().Revision;
        return ReloadForCurrentLocationInternal(_locationService.GetCurrentLocationScope(), refreshRevision);
    }

    private SceneReloadResult ReloadForCurrentLocationInternal(SceneLocationScope currentLocation, long refreshRevision)
    {
        if (!currentLocation.IsValid)
        {
            return ClearSceneForInvalidLocation();
        }

        var desiredRequests = _snapshotResolver.GetLoadRequests(currentLocation);
        SceneReloadResult loadResult = ReconcileEntries(desiredRequests);

        bool needsRetry = _sceneState.CompleteRefresh(currentLocation, refreshRevision, loadResult.NeedsRetry);
        return loadResult with { NeedsRetry = needsRetry };
    }

    private static void ProcessFrameworkUpdates(IReadOnlyList<ObjectSceneEntry> entries)
    {
        for (int index = 0; index < entries.Count; ++index)
        {
            IObjectRuntime runtime = entries[index].Runtime;
            if (!runtime.NeedsFrameworkUpdates)
            {
                continue;
            }

            runtime.FrameworkUpdate();
        }
    }

    private void PublishBounds(IReadOnlyList<ObjectSceneEntry> entries)
    {
        _boundsBuffer.Clear();
        foreach (ObjectSceneEntry entry in entries)
        {
            if (!TryCreateBoundsSnapshot(entry.Runtime, out ObjectBoundsSnapshot boundsSnapshot))
            {
                continue;
            }

            _boundsBuffer.Add(boundsSnapshot);
        }

        _sceneState.SetBoundsSnapshots(_boundsBuffer);
    }

    private bool ShouldReloadForCurrentLocation(ObjectSceneRefreshState refreshState, out SceneLocationScope currentLocation)
    {
        currentLocation = default;
        if (refreshState.IsZoning
            || refreshState.HasLoadedLocation && !refreshState.NeedsRefresh)
        {
            return false;
        }

        if (!HasSceneLoadState(refreshState))
        {
            return false;
        }

        currentLocation = _locationService.GetCurrentLocationScope();
        return currentLocation.IsValid;
    }

    private bool HasSceneLoadState(ObjectSceneRefreshState refreshState)
        => refreshState.NeedsRefresh
            || refreshState.HasLoadedLocation
            || _snapshotResolver.HasAnyLoadState();

    private void HandleLocationInvalidated()
        => _sceneState.MarkNeedsRefresh();

    private SceneReloadResult ClearSceneForInvalidLocation()
    {
        _sceneMutations.ClearAllActiveEntries(removePersistedState: false);
        _sceneState.ClearLoadedLocation(needsRefresh: false);
        return SceneReloadResult.InvalidLocation;
    }

    private SceneReloadResult ReconcileEntries(IReadOnlyList<ObjectSceneLoadRequest> desiredRequests)
    {
        HashSet<Guid> desiredIds = desiredRequests.Select(static request => request.Snapshot.Id).ToHashSet();
        List<ObjectSceneEntry> entriesToDestroy = [];
        foreach (ObjectSceneEntry entry in _sceneState.GetEntriesSnapshot().Where(entry => !desiredIds.Contains(entry.Snapshot.Id)))
        {
            _sceneState.TryRemoveEntry(entry.Snapshot.Id, out _);
            entriesToDestroy.Add(entry);
        }

        _sceneState.ClearRuntimeFailuresExcept(desiredIds);
        _sceneMutations.DestroyEntries(entriesToDestroy);
        bool loadedAll = true;
        bool needsRetry = false;
        foreach (ObjectSceneLoadRequest request in desiredRequests)
        {
            if (TryReconcileEntry(request))
            {
                continue;
            }

            _sceneState.TryGetRuntimeFailureCode(request.Snapshot.Id, out string? failureCode);
            loadedAll = false;
            needsRetry |= ObjectRuntimeFailureCodes.ShouldRetrySceneLoad(failureCode);
        }

        return SceneReloadResult.LoadedLocation(loadedAll, needsRetry);
    }

    private bool TryReconcileEntry(ObjectSceneLoadRequest request)
    {
        if (_sceneState.TryGetEntry(request.Snapshot.Id, out ObjectSceneEntry entry))
        {
            switch (TryReuseActiveEntry(entry, request))
            {
                case ObjectRuntimeUpdateResult.Applied:
                    _sceneState.ClearRuntimeFailure(request.Snapshot.Id);
                    return true;
                case ObjectRuntimeUpdateResult.RequiresRecreate:
                    return _sceneMutations.TryReplaceObject(request.Snapshot, request.Source);
                default:
                    return false;
            }
        }

        if (!_objectKindService.CanCreate(request.Snapshot.Kind))
        {
            _sceneState.SetRuntimeFailure(request.Snapshot.Id, ObjectRuntimeFailureCodes.ServiceMissing);
            return false;
        }

        return _sceneMutations.TryCreateObject(
            request.Snapshot,
            out _,
            applyDefaultLayout: false,
            persistSnapshot: false,
            sourceOverride: request.Source);
    }

    private ObjectRuntimeUpdateResult TryReuseActiveEntry(ObjectSceneEntry entry, ObjectSceneLoadRequest desiredRequest)
    {
        if (entry.Source != desiredRequest.Source
            || entry.Runtime.Kind != desiredRequest.Snapshot.Kind
            || entry.Snapshot != desiredRequest.Snapshot)
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        long currentCollectionRevision = _collectionStore.GetCollectionRevision(
            desiredRequest.Snapshot.CollectionId,
            ObjectSnapshotUtility.GetRootResourcePath(desiredRequest.Snapshot));
        if (entry.ResourceCollection.MatchesCurrentRevision(currentCollectionRevision))
        {
            return ObjectRuntimeUpdateResult.Applied;
        }

        ObjectRuntimeUpdateResult result = entry.ResourceCollection.IsPendingWithoutRuntimeRevision(currentCollectionRevision)
            ? ObjectRuntimeUpdateResult.Applied
            : entry.Runtime.TryRefreshResources(desiredRequest.Snapshot);
        switch (result)
        {
            case ObjectRuntimeUpdateResult.Applied:
                UpsertEntryResourceCollection(entry, SceneResourceCollectionState.Current(currentCollectionRevision));
                break;
            case ObjectRuntimeUpdateResult.Rejected:
                _sceneState.SetRuntimeFailure(entry.Snapshot.Id, ObjectRuntimeFailureCodes.UpdateRejected);
                break;
        }

        return result;
    }

    private void UpsertEntryResourceCollection(ObjectSceneEntry entry, SceneResourceCollectionState resourceCollection)
        => _sceneState.UpsertEntry(new ObjectSceneEntry
        {
            Runtime = entry.Runtime,
            Source = entry.Source,
            ResourceCollection = resourceCollection,
        });

    private bool TryCreateInactiveRuntimeStateSnapshot(
        Guid id,
        IReadOnlyList<ObjectSnapshot> sceneSnapshots,
        SceneLocationScope currentLocation,
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

    private static ObjectRuntimeStateSnapshot CreateActiveRuntimeStateSnapshot(Guid id, string? failureCode)
        => new(
            id,
            string.IsNullOrEmpty(failureCode) ? ObjectRuntimeStateKind.Active : ObjectRuntimeStateKind.LoadFailed,
            failureCode);

    private static bool TryCreateBoundsSnapshot(IObjectRuntime runtime, out ObjectBoundsSnapshot boundsSnapshot)
    {
        var snapshot = runtime.Snapshot;
        var hasWorldBounds = runtime.TryGetBounds(out var worldBounds);
        var hasLocalBounds = runtime.TryGetOrientedBounds(out var localBounds);
        var hasPlacementClearance = runtime.TryGetPlacementClearance(out ObjectPlacementClearance placementClearance);
        var overlayShapes = CreateOverlayShapes(snapshot);

        if (!hasWorldBounds && !hasLocalBounds && !hasPlacementClearance && overlayShapes is null)
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
            runtime.Address,
            min,
            max,
            hasLocalBounds ? localBounds : null,
            hasPlacementClearance ? placementClearance : null,
            runtime.PlacementSurfaceSupport,
            overlayShapes);
        return true;
    }

    private static ObjectRuntimeStateSnapshot CreateRuntimeStateSnapshot(
        ObjectSnapshot snapshot,
        bool isActive,
        string? failureCode,
        SceneLocationScope currentLocation)
    {
        if (isActive)
        {
            return CreateActiveRuntimeStateSnapshot(snapshot.Id, failureCode);
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

    private static IReadOnlyList<ObjectOverlayShapeSnapshot>? CreateOverlayShapes(ObjectSnapshot snapshot)
    {
        if (snapshot.Kind != ObjectKind.Light || snapshot.Model is not LightModel lightModel)
        {
            return null;
        }

        var transform = ObjectShapeMath.CreateRigidTransform(snapshot.Transform.Position, snapshot.Transform.RotationDegrees);
        var range = MathF.Max(lightModel.Shape.Range, 0.01f);
        return lightModel.LightType switch
        {
            LightType.WorldLight => [],
            LightType.AreaLight => [new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.Sphere, transform, range, 0f)],
            LightType.SpotLight => CreateSpotLightOverlayShapes(transform, range, lightModel.Shape),
            LightType.FlatLight => CreateFlatLightOverlayShapes(snapshot.Transform, range, lightModel.Shape),
            _ => [],
        };
    }

    private static ObjectOverlayShapeSnapshot[] CreateSpotLightOverlayShapes(Matrix4x4 transform, float range, LightShape shape)
    {
        var lightAngle = Math.Clamp(shape.LightAngle, 0f, 180f);
        var falloffAngle = Math.Clamp(lightAngle + shape.FalloffAngle, 0f, 180f);
        if (falloffAngle <= lightAngle + 0.01f)
        {
            return [new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.Cone, transform, range, lightAngle)];
        }

        return
        [
            new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.Cone, transform, range, lightAngle),
            new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.Cone, transform, range, falloffAngle, 0.4f),
        ];
    }

    private static ObjectOverlayShapeSnapshot[] CreateFlatLightOverlayShapes(SceneTransform transform, float range, LightShape shape)
    {
        Matrix4x4 coreTransform = ObjectShapeMath.CreateForwardSkewedBoxTransform(transform, range, shape.AngleDegrees);
        float falloffPadding = MathF.Max(shape.FalloffAngle / 50f, 0f);
        if (falloffPadding <= 0.0001f)
        {
            return [new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.Box, coreTransform, 0f, 0f)];
        }

        return
        [
            new ObjectOverlayShapeSnapshot(ObjectOverlayShapeKind.Box, coreTransform, 0f, 0f),
            new ObjectOverlayShapeSnapshot(
                ObjectOverlayShapeKind.Box,
                ObjectShapeMath.CreateForwardSkewedBoxTransform(transform, range, shape.AngleDegrees, falloffPadding),
                0f,
                0f,
                0.4f),
        ];
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

