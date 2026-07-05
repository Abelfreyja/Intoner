using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Microsoft.Extensions.Logging;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class FurnitureSceneObject : LayoutSceneObject
{
    private readonly FurnitureEmoteGuard _emoteGuard;
    private readonly delegate* unmanaged<SharedGroupLayoutInstance**, nint, void> _destroySharedGroup;
    private readonly IObjectResourceTracker _resourceTracker;

    private SharedGroupLayoutInstance* _instance;
    private DeferredVisualState _deferredVisualState;
    private SharedGroupChildState _visualChildState;
    private bool _visualChildReady;
    private string _sharedGroupPath = string.Empty;
    private ObjectResourceRegistration _rootHandleRegistration;
    private ObjectResourceRegistration _rootInstanceRegistration;

    public override ObjectKind Kind
        => ObjectKind.Furniture;

    public override bool NeedsFrameworkUpdates
        => true;

    public override ObjectPlacementSurfaceSupport PlacementSurfaceSupport
        => ObjectLayoutInterop.GetSharedGroupPlacementSurfaceSupport(_instance);

    protected override ILayoutInstance* LayoutInstance
        => (ILayoutInstance*)_instance;

    internal FurnitureSceneObject(
        IFramework framework,
        ILogger logger,
        ObjectSnapshot snapshot,
        SharedGroupLayoutInstance* instance,
        string sharedGroupPath,
        IObjectResourceTracker resourceTracker,
        FurnitureEmoteGuard emoteGuard,
        delegate* unmanaged<SharedGroupLayoutInstance**, nint, void> destroySharedGroup)
        : base(framework, logger, snapshot)
    {
        _instance = instance;
        _sharedGroupPath = sharedGroupPath;
        _resourceTracker = resourceTracker;
        _emoteGuard = emoteGuard;
        _destroySharedGroup = destroySharedGroup;
        _rootHandleRegistration = new ObjectResourceRegistration(snapshot.Id);
        _rootInstanceRegistration = new ObjectResourceRegistration(snapshot.Id);
        UpdateRegisteredRootHandle(snapshot);
    }

    protected override void FrameworkUpdateUnsafe()
    {
        if (_instance == null)
        {
            return;
        }

        RefreshVisualChildStateUnsafe();

        // shared group child targets can appear after create, so keep the tracked tree fresh
        _emoteGuard.RegisterInstanceTree(_instance);

        _deferredVisualState.Replay<FurnitureModel>(
            Snapshot,
            ApplyRuntimeStateUnsafe,
            TryApplyVisualStateUnsafe);
        UpdateRegisteredRootHandle(Snapshot);
    }

    public override bool TryGetOrientedBounds(out OrientedBounds bounds)
        => ObjectLayoutInterop.TryGetSharedGroupOrientedBounds(_instance, out bounds);

    public override bool TryGetPlacementClearance(out ObjectPlacementClearance clearance)
        => ObjectLayoutInterop.TryGetSharedGroupPlacementClearance(_instance, out clearance);

    public override void AppendSelectionDraws(ObjectSelectionCollector collector)
    {
        if (_instance == null || !Snapshot.Visible)
        {
            return;
        }

        FurnitureSceneInterop.AppendSelectionDraws(_instance, Snapshot, collector);
    }

    protected override void DisposeUnsafe()
        => DestroyUnsafe();

    protected override SceneObjectUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
    {
        var previousModel = (FurnitureModel)Snapshot.Model;
        var furnitureModel = (FurnitureModel)snapshot.Model;

        if (string.IsNullOrWhiteSpace(furnitureModel.SharedGroupPath))
        {
            Logger.LogDebug("skipping furniture update because shared group path is empty");
            return SceneObjectUpdateResult.Rejected;
        }

        if (_instance == null)
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        if (!string.Equals(Snapshot.CollectionId, snapshot.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        if (!string.Equals(previousModel.SharedGroupPath, furnitureModel.SharedGroupPath, StringComparison.OrdinalIgnoreCase))
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        if (furnitureModel.RequiresRecreateToClearColor(previousModel))
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        return SceneObjectUpdateResult.Applied;
    }

    protected override SceneObjectUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot)
    {
        var applyResult = _deferredVisualState.Apply<FurnitureModel>(
            snapshot,
            previousSnapshot,
            ApplyRuntimeStateUnsafe,
            static (model, previousModel) => model.NeedsVisualState(previousModel),
            TryApplyVisualStateUnsafe);
        UpdateRegisteredRootHandle(snapshot);
        return applyResult;
    }

    protected override SceneObjectUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
        => SceneObjectUpdateResult.RequiresRecreate;

    private void DestroyUnsafe()
    {
        if (_instance == null)
        {
            return;
        }

        Logger.LogInformation(
            "destroying furniture shared group 0x{Address:X} for path {SharedGroupPath}",
            (ulong)(nint)_instance,
            _sharedGroupPath);

        _rootHandleRegistration.Clear(_resourceTracker);
        _rootInstanceRegistration.Clear(_resourceTracker);
        _emoteGuard.UnregisterInstanceTree(_instance);
        var instance = _instance;
        _destroySharedGroup(&instance, 0);

        _instance = null;
        _visualChildState = default;
        _visualChildReady = false;
        _sharedGroupPath = string.Empty;
        _deferredVisualState.Reset();
    }

    private void ApplyRuntimeStateUnsafe(ObjectSnapshot snapshot)
    {
        FurnitureSceneInterop.ApplyRuntimeState(_instance, snapshot);
        _visualChildState = ObjectLayoutInterop.GetSharedGroupChildState(_instance);
        _visualChildReady = ObjectLayoutInterop.IsSharedGroupReady(_instance);
    }

    internal void RefreshCreatedVisualState()
    {
        if (_instance == null)
        {
            return;
        }

        _visualChildState = ObjectLayoutInterop.GetSharedGroupChildState(_instance);
        _visualChildReady = ObjectLayoutInterop.IsSharedGroupReady(_instance);
        _ = ObjectLayoutInterop.TryRefreshVisualSharedGroupState(_instance);
    }

    private bool TryApplyVisualStateUnsafe(FurnitureModel model)
        => FurnitureSceneInterop.TryApplyVisualState(Logger, _instance, model);

    private void RefreshVisualChildStateUnsafe()
    {
        SharedGroupChildState childState = ObjectLayoutInterop.GetSharedGroupChildState(_instance);
        bool childReady = ObjectLayoutInterop.IsSharedGroupReady(_instance);
        if (!childReady)
        {
            _visualChildState = childState;
            _visualChildReady = false;
            return;
        }

        bool collidersActive = ObjectLayoutInterop.HasActiveVisualSharedGroupColliders(_instance);
        if (childState == _visualChildState && childReady == _visualChildReady && !collidersActive)
        {
            return;
        }

        _visualChildState = childState;
        _visualChildReady = childReady;
        _ = ObjectLayoutInterop.TryRefreshVisualSharedGroupState(_instance);
    }

    private void UpdateRegisteredRootHandle(ObjectSnapshot snapshot)
    {
        if (_instance == null || snapshot.CollectionId.Length == 0)
        {
            _rootHandleRegistration.Clear(_resourceTracker);
            _rootInstanceRegistration.Clear(_resourceTracker);
            return;
        }

        var scope = new ObjectResourceScope(snapshot.CollectionId, GetPrimaryPath(_instance, _sharedGroupPath));
        _rootInstanceRegistration.UpdateRootInstance(_resourceTracker, (nint)_instance, scope);

        var currentRootHandle = (nint)_instance->ResourceHandle;
        _rootHandleRegistration.UpdateRootHandle(
            _resourceTracker,
            currentRootHandle,
            scope);
    }

    private static string GetPrimaryPath(SharedGroupLayoutInstance* instance, string fallbackPath)
    {
        if (instance == null)
        {
            return fallbackPath;
        }

        var primaryPath = instance->GetPrimaryPath();
        if (!primaryPath.HasValue)
        {
            return fallbackPath;
        }

        string path = primaryPath.ToString();
        return path.Length == 0 ? fallbackPath : path;
    }
}

