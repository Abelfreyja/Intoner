using Dalamud.Plugin.Services;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Scene;
using Microsoft.Extensions.Logging;
using DrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class BgObjectRuntime : DrawObjectRuntime
{
    private SceneBgObject* _bgObject;
    private DeferredVisualState _deferredVisualState;
    private readonly ObjectResourceTracker _resourceTracker;
    private string _modelPath;
    private ObjectResourceRegistration _rootHandleRegistration = new();
    private BgObjectSceneInterop.ModelLoadState? _lastModelLoadState;

    public override ObjectKind Kind
        => ObjectKind.BgObject;

    public override bool NeedsFrameworkUpdates
        => true;

    public override nint Address
        => (nint)_bgObject;

    protected override DrawObject* DrawObjectPointer
        => _bgObject != null ? (DrawObject*)_bgObject : null;

    internal BgObjectRuntime(
        IFramework framework,
        ILogger logger,
        ObjectSnapshot snapshot,
        SceneBgObject* bgObject,
        ObjectResourceTracker resourceTracker,
        string modelPath)
        : base(framework, logger, snapshot)
    {
        _bgObject = bgObject;
        _resourceTracker = resourceTracker;
        _modelPath = modelPath;
        _deferredVisualState = new DeferredVisualState();
    }

    internal override void Initialize()
    {
        LogModelLoadState();
        UpdateRegisteredRootHandle(Snapshot);
    }

    protected override void FrameworkUpdateUnsafe()
    {
        if (_bgObject == null)
        {
            return;
        }

        LogModelLoadState();
        _deferredVisualState.Replay<BgObjectModel>(
            Snapshot,
            ApplyRuntimeStateUnsafe,
            TryApplyVisualStateUnsafe);
        UpdateRegisteredRootHandle(Snapshot);
    }

    protected override ObjectRuntimeUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
    {
        var bgObjectModel = (BgObjectModel)snapshot.Model;

        if (_bgObject == null)
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        if (string.IsNullOrWhiteSpace(bgObjectModel.ModelPath))
        {
            Logger.LogDebug("skipping bgobject update because model path is empty");
            return ObjectRuntimeUpdateResult.Rejected;
        }

        return NeedsModelReload(snapshot, Snapshot)
            ? ObjectRuntimeUpdateResult.RequiresRecreate
            : ObjectRuntimeUpdateResult.Applied;
    }

    protected override ObjectRuntimeUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot)
    {
        var applyResult = _deferredVisualState.Apply<BgObjectModel>(
            snapshot,
            previousSnapshot,
            ApplyRuntimeStateUnsafe,
            static (model, previousModel) => model.NeedsVisualState(previousModel),
            TryApplyVisualStateUnsafe);
        UpdateRegisteredRootHandle(snapshot);
        return applyResult;
    }

    protected override ObjectRuntimeUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
        => ObjectRuntimeUpdateResult.RequiresRecreate;

    protected override bool CanResolveDrawObjectBounds(DrawObject* drawObject)
        => drawObject != null && HasLoadedGraphics();

    public override void AppendSelectionDraws(SceneSelectionCollector collector)
    {
        if (_bgObject == null
            || !Snapshot.Visible
            || !HasLoadedGraphics()
            || string.IsNullOrWhiteSpace(_modelPath))
        {
            return;
        }

        collector.AddModel(Snapshot, _modelPath, Snapshot.Transform);
    }

    protected override void DisposeUnsafe()
    {
        if (_bgObject == null)
        {
            return;
        }

        Logger.LogInformation(
            "destroying bgobject 0x{Address:X} using model {ModelPath}",
            (ulong)(nint)_bgObject,
            _modelPath);

        _rootHandleRegistration.Clear(_resourceTracker);
        BgObjectSceneInterop.Destroy(_bgObject);

        _bgObject = null;
        _modelPath = string.Empty;
        _deferredVisualState.Reset();
    }

    private void ApplyRuntimeStateUnsafe(ObjectSnapshot snapshot)
        => BgObjectSceneInterop.ApplyRuntimeState(_bgObject, snapshot);

    private bool TryApplyVisualStateUnsafe(BgObjectModel bgObjectModel)
    {
        LogModelLoadState();
        return BgObjectSceneInterop.TryApplyVisualState(_bgObject, bgObjectModel);
    }

    private void LogModelLoadState()
    {
        if (_bgObject == null)
        {
            return;
        }

        BgObjectSceneInterop.ModelLoadState state = BgObjectSceneInterop.GetModelLoadState(_bgObject);
        if (_lastModelLoadState == state)
        {
            return;
        }

        _lastModelLoadState = state;
        Logger.LogInformation(
            "bgobject model state: object {ObjectId}, address 0x{Address:X}, model {ModelPath}, handle 0x{Handle:X}, "
            + "draw {DrawState}, read {ReadState}, load {LoadState}, resource lod bytes 0x{ResourceLodBytes:X8}, "
            + "lod count {LodCount}, current lod {CurrentLod}",
            Snapshot.Id, (ulong)(nint)_bgObject, _modelPath, (ulong)state.Handle,
            state.DrawState, state.ReadState, state.LoadState, state.ResourceLodBytes,
            state.LodCount, BgObjectSceneInterop.GetCurrentLod(_bgObject));
    }

    private bool HasLoadedGraphics()
        => BgObjectSceneInterop.IsModelLoaded(_bgObject);

    private static bool NeedsModelReload(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot)
    {
        var model = (BgObjectModel)snapshot.Model;
        var previousModel = (BgObjectModel)previousSnapshot.Model;
        return !string.Equals(snapshot.CollectionId, previousSnapshot.CollectionId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(model.ModelPath, previousModel.ModelPath, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateRegisteredRootHandle(ObjectSnapshot snapshot)
    {
        if (_bgObject == null || snapshot.CollectionId.Length == 0)
        {
            _rootHandleRegistration.Clear(_resourceTracker);
            return;
        }

        var currentRootHandle = (nint)_bgObject->ModelResourceHandle;
        _rootHandleRegistration.UpdateRootHandle(
            _resourceTracker,
            currentRootHandle,
            new ObjectResourceScope(snapshot.CollectionId, _modelPath));
    }

}

