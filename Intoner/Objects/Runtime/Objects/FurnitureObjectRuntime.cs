using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Scene;
using Microsoft.Extensions.Logging;
using System.Text;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class FurnitureObjectRuntime : LayoutObjectRuntime
{
    private readonly FurnitureEmoteGuard _emoteGuard;
    private readonly FurnitureNativeInstance _native;
    private readonly nint _rootAddress;
    private readonly ObjectResourceTracker _resourceTracker;
    private readonly ObjectFrameworkDiagnostics _frameworkDiagnostics;

    private SharedGroupLayoutInstance* Instance => _native.Instance;
    private DeferredVisualState _deferredVisualState;
    private readonly FurnitureNativeState _nativeState;
    private bool _visualChildReady;
    private byte[] _primaryPathBytes = [];
    private readonly string _resourceCollectionId;
    private ObjectResourceScope _resourceScope;
    private string _sharedGroupPath;
    private ObjectResourceRegistration _rootHandleRegistration = new();
    private ObjectResourceRegistration _rootInstanceRegistration = new();

    public override ObjectKind Kind
        => ObjectKind.Furniture;

    public override bool NeedsFrameworkUpdates
        => true;

    public override ObjectPlacementSurfaceSupport PlacementSurfaceSupport
        => ObjectLayoutInterop.GetSharedGroupPlacementSurfaceSupport(Instance);

    protected override ILayoutInstance* LayoutInstance
        => (ILayoutInstance*)Instance;

    internal FurnitureObjectRuntime(
        IFramework framework,
        ILogger logger,
        ObjectSnapshot snapshot,
        FurnitureNativeInstance native,
        string sharedGroupPath,
        ObjectResourceTracker resourceTracker,
        FurnitureEmoteGuard emoteGuard,
        ObjectFrameworkDiagnostics frameworkDiagnostics,
        string resourceCollectionId)
        : base(framework, logger, snapshot)
    {
        _native = native;
        _rootAddress = (nint)native.Instance;
        _sharedGroupPath = sharedGroupPath;
        _resourceTracker = resourceTracker;
        _emoteGuard = emoteGuard;
        _frameworkDiagnostics = frameworkDiagnostics;
        _resourceCollectionId = resourceCollectionId;
        _nativeState = new FurnitureNativeState(frameworkDiagnostics);
        _deferredVisualState = new DeferredVisualState();
        _deferredVisualState.RequestReplay();
    }

    internal override void Initialize()
        => UpdateRegisteredRootHandle();

    protected override void FrameworkUpdateUnsafe()
    {
        if (Instance == null)
        {
            return;
        }

        _frameworkDiagnostics.Count(ObjectFrameworkCounter.FurnitureObjects);
        bool childReady;
        using (_frameworkDiagnostics.Measure(ObjectFrameworkStage.FurnitureReadiness))
        {
            childReady = ObjectLayoutInterop.IsPrimaryLoaded(Instance);
        }
        using (_frameworkDiagnostics.Measure(ObjectFrameworkStage.FurnitureObservation))
        {
            RefreshNativeState(childReady);
        }
        _frameworkDiagnostics.Count(ObjectFrameworkCounter.FurnitureNodes, _nativeState.Instances.Count);
        _frameworkDiagnostics.Count(ObjectFrameworkCounter.FurnitureNotReady, childReady ? 0 : 1);
        _frameworkDiagnostics.Count(ObjectFrameworkCounter.FurnitureActiveColliders, _nativeState.HasActiveColliders ? 1 : 0);
        using (_frameworkDiagnostics.Measure(ObjectFrameworkStage.FurnitureVisuals))
        {
            RefreshVisualChildStateUnsafe(childReady);
            if (_deferredVisualState.NeedsReplay)
            {
                _frameworkDiagnostics.Count(ObjectFrameworkCounter.FurnitureReplayAttempts);
                _deferredVisualState.Replay<FurnitureModel>(Snapshot, ApplyRuntimeStateUnsafe, TryApplyVisualStateUnsafe);
            }
        }
        _frameworkDiagnostics.Count(ObjectFrameworkCounter.FurnitureReplayPending, _deferredVisualState.NeedsReplay ? 1 : 0);
        using (_frameworkDiagnostics.Measure(ObjectFrameworkStage.FurnitureResources))
        {
            UpdateRegisteredRootHandle();
        }
    }

    public override bool TryGetBounds(out AxisAlignedBounds bounds)
        => _nativeState.TryGetBounds(Instance, out bounds);

    public override bool TryGetOrientedBounds(out OrientedBounds bounds)
        => _nativeState.TryGetOrientedBounds(Instance, out bounds);

    public override bool TryGetPlacementClearance(out ObjectPlacementClearance clearance)
        => _nativeState.TryGetPlacementClearance(Instance, out clearance);

    public override bool ContainsCollider(nint colliderAddress)
        => ObjectLayoutInterop.SharedGroupContainsCollider((nint)Instance, colliderAddress);

    public override void AppendSelectionDraws(SceneSelectionCollector collector)
    {
        if (Instance == null || !Snapshot.Visible)
        {
            return;
        }

        FurnitureSceneInterop.AppendSelectionDraws(Instance, Snapshot, collector);
    }

    protected override void DisposeUnsafe()
        => DestroyUnsafe();

    protected override ObjectRuntimeUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
    {
        var previousModel = (FurnitureModel)Snapshot.Model;
        var furnitureModel = (FurnitureModel)snapshot.Model;

        if (string.IsNullOrWhiteSpace(furnitureModel.SharedGroupPath))
        {
            Logger.LogDebug("skipping furniture update because shared group path is empty");
            return ObjectRuntimeUpdateResult.Rejected;
        }

        if (Instance == null)
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        if (!string.Equals(Snapshot.CollectionId, snapshot.CollectionId, StringComparison.OrdinalIgnoreCase))
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        if (!string.Equals(previousModel.SharedGroupPath, furnitureModel.SharedGroupPath, StringComparison.OrdinalIgnoreCase))
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        if (furnitureModel.RequiresRecreateToClearColor(previousModel))
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        return ObjectRuntimeUpdateResult.Applied;
    }

    protected override ObjectRuntimeUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot)
    {
        var applyResult = _deferredVisualState.Apply<FurnitureModel>(
            snapshot,
            previousSnapshot,
            ApplyRuntimeStateUnsafe,
            static (model, previousModel) => model.NeedsVisualState(previousModel),
            TryApplyVisualStateUnsafe);
        UpdateRegisteredRootHandle();
        return applyResult;
    }

    protected override ObjectRuntimeUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
        => ObjectRuntimeUpdateResult.RequiresRecreate;

    private void DestroyUnsafe()
    {
        nint address = (nint)Instance;
        string sharedGroupPath = _sharedGroupPath;
        ObjectResourceLog.Write(() => Logger.LogInformation(
            "destroying furniture shared group 0x{Address:X} for path {SharedGroupPath}",
            (ulong)address,
            sharedGroupPath));

        _rootHandleRegistration.Clear(_resourceTracker);
        _rootInstanceRegistration.Clear(_resourceTracker);
        _emoteGuard.UnregisterInstanceTree((SharedGroupLayoutInstance*)_rootAddress);
        _nativeState.InvalidateBounds();
        _native.Dispose();
        _visualChildReady = false;
        _sharedGroupPath = string.Empty;
        _deferredVisualState.Reset();
    }

    private void ApplyRuntimeStateUnsafe(ObjectSnapshot snapshot)
    {
        _nativeState.InvalidateBounds();
        _visualChildReady = FurnitureSceneInterop.ApplyRuntimeState(_native, snapshot);
    }

    internal void RefreshCreatedVisualState()
    {
        if (Instance == null)
        {
            return;
        }

        _visualChildReady = ObjectLayoutInterop.IsPrimaryLoaded(Instance);
        RefreshNativeState(_visualChildReady);
        RefreshVisualStateUnsafe();
    }

    private bool TryApplyVisualStateUnsafe(FurnitureModel model)
        => FurnitureSceneInterop.TryApplyVisualState(Logger, Instance, model);

    private void RefreshNativeState(bool childReady)
    {
        _nativeState.Observe(Instance, childReady);
        if (_nativeState.MembershipChanged)
        {
            _emoteGuard.RegisterInstanceTree((nint)Instance, _nativeState.Instances);
        }
    }

    private void RefreshVisualChildStateUnsafe(bool childReady)
    {
        if (!Snapshot.Visible)
        {
            ObjectLayoutInterop.ApplySharedGroupVisibility(Instance, false);
        }

        if (!childReady)
        {
            _visualChildReady = false;
            return;
        }

        if (_nativeState.VisualsChanged || childReady != _visualChildReady)
        {
            _deferredVisualState.RequestReplay();
        }

        _visualChildReady = childReady;
    }

    private void RefreshVisualStateUnsafe()
    {
        _nativeState.InvalidateBounds();
        _ = FurnitureSceneInterop.RefreshVisualState(_native);
    }

    private void UpdateRegisteredRootHandle()
    {
        if (Instance == null || _resourceCollectionId.Length == 0)
        {
            _rootHandleRegistration.Clear(_resourceTracker);
            _rootInstanceRegistration.Clear(_resourceTracker);
            return;
        }

        ReadOnlySpan<byte> primaryPath = Instance->GetPrimaryPath().AsSpan();

        if (string.IsNullOrEmpty(_resourceScope.ResourceCollectionId) || !primaryPath.SequenceEqual(_primaryPathBytes))
        {
            _frameworkDiagnostics.Count(ObjectFrameworkCounter.FurnitureResourceChanges);
            _primaryPathBytes = primaryPath.ToArray();
            _resourceScope = new ObjectResourceScope(_resourceCollectionId,
                primaryPath.IsEmpty ? _sharedGroupPath : Encoding.UTF8.GetString(primaryPath));
        }

        _rootInstanceRegistration.UpdateRootInstance(_resourceTracker, (nint)Instance, _resourceScope);

        var currentRootHandle = (nint)Instance->ResourceHandle;
        _rootHandleRegistration.UpdateRootHandle(
            _resourceTracker,
            currentRootHandle,
            _resourceScope);
    }
}
