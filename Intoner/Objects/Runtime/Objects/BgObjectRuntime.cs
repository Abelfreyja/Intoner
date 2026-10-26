using Dalamud.Plugin.Services;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Scene;
using Microsoft.Extensions.Logging;
using AxisAlignedBounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using DrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class BgObjectRuntime : DrawObjectRuntime
{
    private SceneBgObject* _bgObject;
    private readonly BgCollisionInstance _collision;
    private readonly IObjectResourceLoader _resourceLoader;
    private readonly string _collectionId;
    private readonly long? _primaryRevision;
    private BgColliderDefinition? _requestedColliderDefinition;
    private DeferredVisualState _deferredVisualState;
    private readonly ObjectResourceTracker _resourceTracker;
    private readonly string _resourceCollectionId;
    private string _modelPath;
    private ObjectResourceRegistration _rootHandleRegistration = new();
    private BgObjectSceneInterop.ModelLoadState? _lastModelLoadState;
    private NativeBoundsInput _boundsInput = new();
    private AxisAlignedBounds _worldBounds;
    private OrientedBounds _orientedBounds;
    private bool _worldBoundsValid;
    private bool _orientedBoundsValid;

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
        ObjectResolvedRootPath resource,
        IObjectResourceLoader resourceLoader,
        CameraCollisionService cameraCollision,
        ObjectCollectionResolveData? collection)
        : base(framework, logger, snapshot)
    {
        _bgObject = bgObject;
        _resourceTracker = resourceTracker;
        _modelPath = resource.ResolvedPath;
        _resourceCollectionId = resource.ResourceCollectionId;
        _resourceLoader = resourceLoader;
        _collectionId = snapshot.CollectionId;
        _primaryRevision = GetPrimaryRevision(snapshot, collection);
        BgObjectModel model = (BgObjectModel)snapshot.Model;
        _requestedColliderDefinition = model.ColliderDefinition;
        _collision = cameraCollision.CreateBg(GetAvailableColliderDefinition(snapshot, collection),
            snapshot.Transform, snapshot.Visible, model.CameraCollision);
        _deferredVisualState = new DeferredVisualState();
    }

    internal override void Initialize()
    {
        LogModelLoadState();
        UpdateRegisteredRootHandle();
        UpdateCollision(Snapshot);
    }

    protected override void FrameworkUpdateUnsafe()
    {
        if (_bgObject == null)
        {
            return;
        }

        LogModelLoadState();
        if (_deferredVisualState.NeedsReplay)
        {
            _deferredVisualState.Replay<BgObjectModel>(Snapshot, ApplyRuntimeStateUnsafe, TryApplyVisualStateUnsafe);
        }

        UpdateRegisteredRootHandle();
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
        UpdateRegisteredRootHandle();
        return applyResult;
    }

    protected override ObjectRuntimeUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
    {
        using ObjectResourceLoadScopeToken scope = _resourceLoader.EnterRootLoadScope(snapshot.CollectionId);
        long? currentPrimary = GetPrimaryRevision(snapshot, scope.Collection);

        if (!string.Equals(_collectionId, snapshot.CollectionId, StringComparison.OrdinalIgnoreCase)
         || !_primaryRevision.HasValue || currentPrimary != _primaryRevision)
        {
            return ObjectRuntimeUpdateResult.RequiresRecreate;
        }

        _collision.Replace(GetAvailableColliderDefinition(snapshot, scope.Collection), snapshot.Transform,
            snapshot.Visible, ((BgObjectModel)snapshot.Model).CameraCollision);
        _requestedColliderDefinition = ((BgObjectModel)snapshot.Model).ColliderDefinition;
        ResourceRevision = scope.Collection is { } collection
            ? collection.GetResourceRevision(ObjectResourceRoots.FromModel(snapshot.Model))
            : default(ObjectResourceRevision);
        return ObjectRuntimeUpdateResult.Applied;
    }

    public override (bool World, bool Oriented) GetBounds(out AxisAlignedBounds worldBounds, out OrientedBounds orientedBounds)
    {
        worldBounds = default;
        orientedBounds = default;

        if (!ObserveBoundsInput())
        {
            return (false, false);
        }

        return (TryGetCachedWorldBounds(out worldBounds), TryGetCachedOrientedBounds(out orientedBounds));
    }

    public override bool TryGetBounds(out AxisAlignedBounds bounds)
    {
        bounds = default;
        return ObserveBoundsInput() && TryGetCachedWorldBounds(out bounds);
    }

    public override bool TryGetOrientedBounds(out OrientedBounds bounds)
    {
        bounds = default;
        return ObserveBoundsInput() && TryGetCachedOrientedBounds(out bounds);
    }

    private bool TryGetCachedWorldBounds(out AxisAlignedBounds bounds)
    {
        if (!_worldBoundsValid)
        {
            _worldBoundsValid = TryGetDrawObjectBounds(DrawObjectPointer, out _worldBounds);
        }

        bounds = _worldBounds;
        return _worldBoundsValid;
    }

    private bool TryGetCachedOrientedBounds(out OrientedBounds bounds)
    {
        if (!_orientedBoundsValid)
        {
            _orientedBoundsValid = TryGetDrawObjectOrientedBounds(DrawObjectPointer, out _orientedBounds);
        }

        bounds = _orientedBounds;
        return _orientedBoundsValid;
    }

    private bool ObserveBoundsInput()
    {
        bool ready = _boundsInput.TryObserve(_bgObject, out bool changed);
        if (changed)
        {
            _worldBoundsValid = false;
            _orientedBoundsValid = false;
        }
        return ready;
    }

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

        nint address = (nint)_bgObject;
        string modelPath = _modelPath;
        ObjectResourceLog.Write(() => Logger.LogInformation(
            "destroying bgobject 0x{Address:X} using model {ModelPath}",
            (ulong)address,
            modelPath));

        _collision.Dispose();
        _rootHandleRegistration.Clear(_resourceTracker);
        BgObjectSceneInterop.Destroy(_bgObject);

        _bgObject = null;
        _modelPath = string.Empty;
        _deferredVisualState.Reset();
    }

    private void ApplyRuntimeStateUnsafe(ObjectSnapshot snapshot)
    {
        UpdateCollision(snapshot);
        BgObjectSceneInterop.ApplyRuntimeState(_bgObject, snapshot);
    }

    private static long? GetPrimaryRevision(ObjectSnapshot snapshot, ObjectCollectionResolveData? collection)
    {
        if (snapshot.CollectionId.Length == 0)
        {
            return 0;
        }

        return collection is not null && collection.TryGetResourceRevision(
            ((BgObjectModel)snapshot.Model).ModelPath, out long revision) ? revision : null;
    }

    private static BgColliderDefinition? GetAvailableColliderDefinition(ObjectSnapshot snapshot, ObjectCollectionResolveData? collection)
    {
        BgColliderDefinition? definition = ((BgObjectModel)snapshot.Model).ColliderDefinition;
        if (definition is { Kind: BgCollisionKind.Mesh } && snapshot.CollectionId.Length > 0
         && collection?.GetResourceRevision(definition.PcbPath) is null)
        {
            return null;
        }

        return definition;
    }

    private void UpdateCollision(ObjectSnapshot snapshot)
    {
        BgObjectModel model = (BgObjectModel)snapshot.Model;
        bool resolveDefinition = model.ColliderDefinition != _requestedColliderDefinition
            || model.CameraCollision && !_collision.CameraCollision;
        using ObjectResourceLoadScopeToken scope = resolveDefinition
            ? _resourceLoader.EnterRootLoadScope(snapshot.CollectionId) : default;
        _collision.Apply(resolveDefinition ? GetAvailableColliderDefinition(snapshot, scope.Collection) : _collision.Definition,
            snapshot.Transform, snapshot.Visible, model.CameraCollision);

        if (resolveDefinition)
        {
            ObjectResourceRevision? revision = snapshot.CollectionId.Length == 0
                ? default(ObjectResourceRevision)
                : scope.Collection?.GetResourceRevision(ObjectResourceRoots.FromModel(model));
            ResourceRevision = ResourceRevision is { } current && revision is { } applied
                ? current with { Collision = applied.Collision } : null;
            _requestedColliderDefinition = model.ColliderDefinition;
        }
    }

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

    private void UpdateRegisteredRootHandle()
    {
        if (_bgObject == null || _resourceCollectionId.Length == 0)
        {
            _rootHandleRegistration.Clear(_resourceTracker);
            return;
        }

        var currentRootHandle = (nint)_bgObject->ModelResourceHandle;
        _rootHandleRegistration.UpdateRootHandle(
            _resourceTracker,
            currentRootHandle,
            new ObjectResourceScope(_resourceCollectionId, _modelPath));
    }
}
