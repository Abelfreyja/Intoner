using Dalamud.Plugin.Services;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using DrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class BgSceneObject : DrawSceneObject
{
    private SceneBgObject* _bgObject;
    private DeferredVisualState _deferredVisualState;
    private readonly IDataManager _gameData;
    private readonly IObjectPathResolver _pathResolver;
    private readonly Func<IObjectResourceLoader> _resourceLoaderFactory;
    private readonly IObjectResourceTracker _resourceTracker;
    private string _modelPath = string.Empty;
    private ObjectResourceRegistration _rootHandleRegistration;

    public override ObjectKind Kind
        => ObjectKind.BgObject;

    public override bool NeedsFrameworkUpdates
        => true;

    public override nint Address
        => (nint)_bgObject;

    protected override DrawObject* DrawObjectPointer
        => _bgObject != null ? (DrawObject*)_bgObject : null;

    internal BgSceneObject(
        IFramework framework,
        ILogger logger,
        ObjectSnapshot snapshot,
        SceneBgObject* bgObject,
        IDataManager gameData,
        IObjectPathResolver pathResolver,
        Func<IObjectResourceLoader> resourceLoaderFactory,
        IObjectResourceTracker resourceTracker,
        string modelPath)
        : base(framework, logger, snapshot)
    {
        _bgObject = bgObject;
        _gameData = gameData;
        _pathResolver = pathResolver;
        _resourceLoaderFactory = resourceLoaderFactory;
        _resourceTracker = resourceTracker;
        _modelPath = modelPath;
        UpdateRegisteredRootHandle(snapshot);
    }

    protected override void FrameworkUpdateUnsafe()
    {
        if (_bgObject == null)
        {
            return;
        }

        _deferredVisualState.Replay<BgObjectModel>(
            Snapshot,
            ApplyRuntimeStateUnsafe,
            TryApplyVisualStateUnsafe);
        UpdateRegisteredRootHandle(Snapshot);
    }

    protected override SceneObjectUpdateResult ValidateSnapshotUpdate(ObjectSnapshot snapshot)
    {
        var bgObjectModel = (BgObjectModel)snapshot.Model;

        if (_bgObject == null)
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        if (string.IsNullOrWhiteSpace(bgObjectModel.ModelPath))
        {
            Logger.LogDebug("skipping bgobject update because model path is empty");
            return SceneObjectUpdateResult.Rejected;
        }

        return SceneObjectUpdateResult.Applied;
    }

    protected override SceneObjectUpdateResult ApplySnapshotUnsafe(ObjectSnapshot snapshot, ObjectSnapshot previousSnapshot)
    {
        if (NeedsModelReload(snapshot, previousSnapshot))
        {
            SceneObjectUpdateResult modelUpdateResult = SetModelUnsafe(snapshot);
            if (modelUpdateResult != SceneObjectUpdateResult.Applied)
            {
                return modelUpdateResult;
            }
        }

        var applyResult = _deferredVisualState.Apply<BgObjectModel>(
            snapshot,
            previousSnapshot,
            ApplyRuntimeStateUnsafe,
            static (model, previousModel) => model.NeedsVisualState(previousModel),
            TryApplyVisualStateUnsafe);
        UpdateRegisteredRootHandle(snapshot);
        return applyResult;
    }

    protected override SceneObjectUpdateResult RefreshResourcesUnsafe(ObjectSnapshot snapshot)
    {
        SceneObjectUpdateResult modelUpdateResult = SetModelUnsafe(snapshot);
        if (modelUpdateResult != SceneObjectUpdateResult.Applied)
        {
            return modelUpdateResult;
        }

        return _deferredVisualState.Apply<BgObjectModel>(
            snapshot,
            Snapshot,
            ApplyRuntimeStateUnsafe,
            static (model, _) => model.NeedsVisualState(null),
            TryApplyVisualStateUnsafe);
    }

    protected override bool CanResolveDrawObjectBounds(DrawObject* drawObject)
        => drawObject != null && HasLoadedGraphics();

    public override void AppendSelectionDraws(ObjectSelectionCollector collector)
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
        => BgObjectSceneInterop.TryApplyVisualState(_bgObject, bgObjectModel);

    private SceneObjectUpdateResult SetModelUnsafe(ObjectSnapshot snapshot)
    {
        if (_bgObject == null)
        {
            return SceneObjectUpdateResult.RequiresRecreate;
        }

        var bgObjectModel = (BgObjectModel)snapshot.Model;
        ObjectResolvedRootPath resolvedResource = _pathResolver.ResolveRootPath(snapshot, ObjectRootPathKind.BgModel, bgObjectModel.ModelPath);
        if (!ObjectResourceCategoryUtility.TryResolveBgModelResourceCategory(resolvedResource.CreatePath, out uint resourceCategoryValue)
         || !RootResourceExists(resolvedResource))
        {
            LogRejectedModel(resolvedResource);
            return SceneObjectUpdateResult.Rejected;
        }

        var resourceCategory = (ResourceCategory)resourceCategoryValue;

        using (EnterRootLoadScope(resolvedResource))
        {
            if (!BgObjectSceneInterop.SetModel(_bgObject, resourceCategory, resolvedResource.CreatePath))
            {
                Logger.LogWarning("bgobject set model rejected by native SetModel for path {ModelPath}", resolvedResource.ResolvedPath);
                return SceneObjectUpdateResult.Rejected;
            }
        }

        if (!CurrentModelMatches(resolvedResource))
        {
            Logger.LogWarning(
                "bgobject set model rejected because assigned handle did not match requested path {ModelPath}",
                resolvedResource.ResolvedPath);
            return SceneObjectUpdateResult.Rejected;
        }

        _modelPath = resolvedResource.ResolvedPath;
        UpdateRegisteredRootHandle(snapshot);
        return SceneObjectUpdateResult.Applied;
    }

    private bool RootResourceExists(ObjectResolvedRootPath resolvedResource)
        => ObjectResourcePathUtility.Exists(_gameData, resolvedResource);

    private void LogRejectedModel(ObjectResolvedRootPath resolvedResource)
    {
        if (!resolvedResource.IsReady)
        {
            Logger.LogWarning(
                "bgobject set model rejected path {ModelPath} with status {Status}",
                resolvedResource.ResolvedPath,
                resolvedResource.Status);
            return;
        }

        Logger.LogWarning("bgobject set model rejected missing or invalid model path {ModelPath}", resolvedResource.ResolvedPath);
    }

    private bool CurrentModelMatches(ObjectResolvedRootPath resolvedResource)
    {
        string currentPath = BgObjectSceneInterop.GetCurrentModelPath(_bgObject);
        string expectedPath = ObjectResourcePathUtility.NormalizeTrackedPath(resolvedResource.ResolvedPath);
        return currentPath.Length > 0
            && string.Equals(currentPath, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private bool HasLoadedGraphics()
        => BgObjectSceneInterop.IsModelLoaded(_bgObject);

    private IDisposable EnterRootLoadScope(ObjectResolvedRootPath resolvedResource)
        => resolvedResource.ResourceCollectionId.Length == 0
            ? default(ObjectResourceLoadScopeToken)
            : _resourceLoaderFactory().EnterRootLoadScope(resolvedResource.ResourceCollectionId);

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

