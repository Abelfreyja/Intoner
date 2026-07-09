using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Objects.Assets;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Text;
using LayoutTransform = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Transform;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Creates scene objects from sanitized object snapshots.
/// </summary>
internal interface ISceneObjectFactory
{
    /// <summary>
    /// Tries to create one scene object for the given sanitized snapshot.
    /// </summary>
    /// <param name="snapshot">The sanitized snapshot to create.</param>
    /// <param name="sceneObject">The created scene object when successful.</param>
    /// <param name="failureCode">The runtime failure code when creation fails.</param>
    /// <returns>true when the scene object was created.</returns>
    bool TryCreateSceneObject(ObjectSnapshot snapshot, out ISceneObject sceneObject, out string failureCode);
}

internal sealed unsafe class SceneObjectFactory : ISceneObjectFactory
{
    private readonly record struct SceneObjectCreateResult(ISceneObject? SceneObject, string FailureCode)
    {
        public static SceneObjectCreateResult Created(ISceneObject sceneObject)
            => new(sceneObject, string.Empty);

        public static SceneObjectCreateResult Failed(string failureCode)
            => new(null, string.IsNullOrWhiteSpace(failureCode) ? ObjectRuntimeFailureCodes.CreateFailed : failureCode);
    }

    private const short FurnitureVisualLayerId = -1;
    private const uint FurnitureVisualInstanceKey = uint.MaxValue;
    private const int FurnitureHelperSubtype = 0x0C;
    private static ReadOnlySpan<byte> VfxPoolName => "Client.System.Scheduler.Instance.VfxObject\0"u8;

    private readonly ILogger _bgObjectLogger;
    private readonly ILogger _furnitureLogger;
    private readonly ILogger _vfxLogger;
    private readonly ILogger _lightLogger;
    private readonly IFramework _framework;
    private readonly IDataManager _gameData;
    private readonly ObjectNativeBindings _nativeBindings;
    private readonly FurnitureEmoteGuard _emoteGuard;
    private readonly IObjectKindService _objectKindService;
    private readonly IObjectResourceTracker _resourceTracker;
    private readonly Func<IObjectResourceLoader> _resourceLoaderFactory;
    private readonly IVfxResourceRewriteService _vfxResourceRewriteService;
    private readonly IObjectPathResolver _pathResolver;

    public SceneObjectFactory(
        ILoggerFactory loggerFactory,
        IFramework framework,
        IDataManager gameData,
        ObjectNativeBindings nativeBindings,
        FurnitureEmoteGuard emoteGuard,
        IObjectKindService objectKindService,
        IObjectResourceTracker resourceTracker,
        Func<IObjectResourceLoader> resourceLoaderFactory,
        IVfxResourceRewriteService vfxResourceRewriteService,
        IObjectPathResolver pathResolver)
    {
        _framework = framework;
        _gameData = gameData;
        _nativeBindings = nativeBindings;
        _emoteGuard = emoteGuard;
        _objectKindService = objectKindService;
        _resourceTracker = resourceTracker;
        _resourceLoaderFactory = resourceLoaderFactory;
        _vfxResourceRewriteService = vfxResourceRewriteService;
        _pathResolver = pathResolver;
        _bgObjectLogger = loggerFactory.CreateLogger<BgSceneObject>();
        _furnitureLogger = loggerFactory.CreateLogger<FurnitureSceneObject>();
        _vfxLogger = loggerFactory.CreateLogger<VfxSceneObject>();
        _lightLogger = loggerFactory.CreateLogger<LightSceneObject>();
    }

    public bool TryCreateSceneObject(ObjectSnapshot snapshot, out ISceneObject sceneObject, out string failureCode)
    {
        SceneObjectCreateResult result = snapshot.Kind switch
        {
            ObjectKind.BgObject => TryCreateBgObject(snapshot),
            ObjectKind.Furniture => TryCreateFurniture(snapshot),
            ObjectKind.Vfx => TryCreateVfx(snapshot),
            ObjectKind.Light => TryCreateLight(snapshot),
            _ => SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.ServiceMissing),
        };

        sceneObject = result.SceneObject!;
        failureCode = result.FailureCode;
        return result.SceneObject is not null;
    }

    private SceneObjectCreateResult TryCreateBgObject(ObjectSnapshot snapshot)
        => ObjectFrameworkUtility.RunOnFrameworkThread(_framework, () => TryCreateBgObjectUnsafe(snapshot));

    private SceneObjectCreateResult TryCreateBgObjectUnsafe(ObjectSnapshot snapshot)
    {
        var bgObjectModel = (BgObjectModel)snapshot.Model;
        if (string.IsNullOrWhiteSpace(bgObjectModel.ModelPath))
        {
            _bgObjectLogger.LogDebug("skipping bgobject create because model path is empty");
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!TryResolveRootResource(
                _bgObjectLogger,
                snapshot,
                ObjectRootPathKind.BgModel,
                bgObjectModel.ModelPath,
                "bgobject",
                "model",
                out ObjectResolvedRootPath resolvedResource,
                out SceneObjectCreateResult failure))
        {
            return failure;
        }

        BgObject* bgObject;
        using var resourceLoadScope = EnterRootLoadScope(resolvedResource);
        bgObject = BgObjectSceneInterop.Create(resolvedResource.CreatePath);

        if (bgObject == null)
        {
            _bgObjectLogger.LogWarning("bgobject create returned null for model path {ModelPath}", bgObjectModel.ModelPath);
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.CreateFailed);
        }

        var sceneObject = new BgSceneObject(
            _framework,
            _bgObjectLogger,
            CreateBootstrapSnapshot(snapshot),
            bgObject,
            _gameData,
            _pathResolver,
            _resourceLoaderFactory,
            _resourceTracker,
            resolvedResource.ResolvedPath);
        return FinalizeSceneObjectCreate(
            sceneObject,
            snapshot,
            createdObject => LogCreatedRootResource(
                _bgObjectLogger,
                "bgobject",
                createdObject.Address,
                resolvedResource));
    }

    private SceneObjectCreateResult TryCreateFurniture(ObjectSnapshot snapshot)
        => ObjectFrameworkUtility.RunOnFrameworkThread(_framework, () => TryCreateFurnitureUnsafe(snapshot));

    private SceneObjectCreateResult TryCreateFurnitureUnsafe(ObjectSnapshot snapshot)
    {
        var furnitureModel = (FurnitureModel)snapshot.Model;
        if (string.IsNullOrWhiteSpace(furnitureModel.SharedGroupPath))
        {
            _furnitureLogger.LogDebug("skipping furniture create because shared group path is empty");
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!ObjectAssetPathRules.IsCatalogSharedGroupPath(furnitureModel.SharedGroupPath))
        {
            _furnitureLogger.LogWarning("furniture create rejected invalid shared group path {SharedGroupPath}", furnitureModel.SharedGroupPath);
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!TryResolveRootResource(
                _furnitureLogger,
                snapshot,
                ObjectRootPathKind.FurnitureSharedGroup,
                furnitureModel.SharedGroupPath,
                "furniture",
                "shared group",
                out ObjectResolvedRootPath resolvedResource,
                out SceneObjectCreateResult failure))
        {
            return failure;
        }

        var layoutTransform = ObjectLayoutInterop.CreateTransform(snapshot.Transform);
        var pathBytes = Encoding.UTF8.GetBytes(resolvedResource.CreatePath + '\0');
        var createSharedGroup = (delegate* unmanaged<short, uint, LayoutTransform*, byte*, byte*, byte, uint, int, nint, nint, SharedGroupLayoutInstance*>)_nativeBindings.Furniture.CreateAddress;
        var destroySharedGroup = (delegate* unmanaged<SharedGroupLayoutInstance**, nint, void>)_nativeBindings.Furniture.DestroyAddress;
        var applySharedGroupState = (delegate* unmanaged<SharedGroupLayoutInstance*, byte, void>)_nativeBindings.Furniture.ApplyStateAddress;
        SharedGroupLayoutInstance* instance;
        using var resourceLoadScope = EnterRootLoadScope(resolvedResource);
        fixed (byte* pathPtr = pathBytes)
        {
            instance = createSharedGroup(
                FurnitureVisualLayerId,
                FurnitureVisualInstanceKey,
                &layoutTransform,
                pathPtr,
                null,
                1,
                0,
                FurnitureHelperSubtype,
                0,
                0);
        }

        if (instance == null)
        {
            _furnitureLogger.LogWarning("furniture shared group create failed for path {SharedGroupPath}", furnitureModel.SharedGroupPath);
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.CreateFailed);
        }

        if (!IsVisualSharedGroup(instance, out InstanceType instanceType))
        {
            _furnitureLogger.LogError(
                "furniture create returned native layout type {InstanceType} for path {SharedGroupPath}, destroying rejected instance",
                instanceType,
                furnitureModel.SharedGroupPath);
            DestroyCreatedSharedGroup(destroySharedGroup, instance);
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.NativeLayoutRejected);
        }

        var sceneObject = new FurnitureSceneObject(
            _framework,
            _furnitureLogger,
            CreateBootstrapSnapshot(snapshot),
            instance,
            resolvedResource.ResolvedPath,
            _resourceTracker,
            _emoteGuard,
            destroySharedGroup);

        applySharedGroupState(instance, 0);
        sceneObject.RefreshCreatedVisualState();

        return FinalizeSceneObjectCreate(
            sceneObject,
            snapshot,
            createdObject => LogCreatedRootResource(
                _furnitureLogger,
                "furniture shared group",
                createdObject.Address,
                resolvedResource));
    }

    private static bool IsVisualSharedGroup(SharedGroupLayoutInstance* instance, out InstanceType instanceType)
    {
        instanceType = instance != null
            ? ((ILayoutInstance*)instance)->Id.Type
            : default;
        return instanceType == InstanceType.SharedGroup;
    }

    private static void DestroyCreatedSharedGroup(
        delegate* unmanaged<SharedGroupLayoutInstance**, nint, void> destroySharedGroup,
        SharedGroupLayoutInstance* instance)
    {
        var mutableInstance = instance;
        destroySharedGroup(&mutableInstance, 0);
    }

    private SceneObjectCreateResult TryCreateLight(ObjectSnapshot snapshot)
        => ObjectFrameworkUtility.RunOnFrameworkThread(_framework, () => TryCreateLightUnsafe(snapshot));

    private SceneObjectCreateResult TryCreateLightUnsafe(ObjectSnapshot snapshot)
    {
        var lightModel = (LightModel)snapshot.Model;

        SceneLight* light;
        fixed (byte* poolPtr = "Intoner.Light\0"u8)
        {
            light = SceneLight.Create(LightSceneObject.ToRenderLightShape(lightModel.LightType), poolPtr);
        }

        if (light == null)
        {
            _lightLogger.LogWarning("light create returned null for type {LightType}", lightModel.LightType);
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.CreateFailed);
        }

        var sceneObject = new LightSceneObject(
            _framework,
            _lightLogger,
            CreateBootstrapSnapshot(snapshot),
            light);
        return FinalizeSceneObjectCreate(
            sceneObject,
            snapshot,
            createdObject => _lightLogger.LogInformation("created light 0x{Address:X}", (ulong)createdObject.Address));
    }

    private SceneObjectCreateResult TryCreateVfx(ObjectSnapshot snapshot)
        => ObjectFrameworkUtility.RunOnFrameworkThread(_framework, () => TryCreateVfxUnsafe(snapshot));

    private SceneObjectCreateResult TryCreateVfxUnsafe(ObjectSnapshot snapshot)
    {
        var vfxModel = (VfxModel)snapshot.Model;
        if (string.IsNullOrWhiteSpace(vfxModel.VfxPath))
        {
            _vfxLogger.LogDebug("skipping vfx create because path is empty");
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!GameAssetPathRules.IsFileKind(vfxModel.VfxPath, GameAssetFileKind.Avfx))
        {
            _vfxLogger.LogWarning("vfx create rejected invalid path {VfxPath}", vfxModel.VfxPath);
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!TryResolveRootResource(
                _vfxLogger,
                snapshot,
                ObjectRootPathKind.Vfx,
                vfxModel.VfxPath,
                "vfx",
                "path",
                out ObjectResolvedRootPath resolvedResource,
                out SceneObjectCreateResult failure))
        {
            return failure;
        }

        var pathByteCount = Encoding.UTF8.GetByteCount(resolvedResource.CreatePath);
        Span<byte> pathBytes = stackalloc byte[pathByteCount + 1];
        Encoding.UTF8.GetBytes(resolvedResource.CreatePath, pathBytes);
        pathBytes[^1] = 0;

        VfxObject* vfxObject;
        using var resourceLoadScope = EnterRootLoadScope(resolvedResource);
        using var cacheIsolationScope = EnterVfxCacheIsolationScope(resolvedResource);
        using var vfxRewriteScope = _vfxResourceRewriteService.EnterRewriteScope(resolvedResource);
        fixed (byte* pathPtr = pathBytes)
        fixed (byte* poolPtr = VfxPoolName)
        {
            vfxObject = VfxObject.Create(pathPtr, poolPtr);
        }

        if (vfxObject == null)
        {
            _vfxLogger.LogWarning("vfx create returned null for path {VfxPath}", vfxModel.VfxPath);
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.CreateFailed);
        }

        var sceneObject = new VfxSceneObject(
            _framework,
            _vfxLogger,
            CreateBootstrapSnapshot(snapshot),
            vfxObject,
            resolvedResource.ResolvedPath,
            _nativeBindings.StaticVfx,
            _resourceTracker);
        return FinalizeSceneObjectCreate(
            sceneObject,
            snapshot,
            createdObject => LogCreatedRootResource(
                _vfxLogger,
                "vfx",
                createdObject.Address,
                resolvedResource));
    }

    private ObjectSnapshot CreateBootstrapSnapshot(ObjectSnapshot snapshot)
    {
        var bootstrapSnapshot = _objectKindService.CreateDefaultSnapshot(snapshot.Kind, snapshot.Transform, snapshot.Name);
        return bootstrapSnapshot with
        {
            Id = snapshot.Id,
            Name = snapshot.Name,
            FolderPath = snapshot.FolderPath,
            CollectionId = snapshot.CollectionId,
            Locked = snapshot.Locked,
            Visible = snapshot.Visible,
            Transform = snapshot.Transform,
            Model = CreateBootstrapModel(snapshot),
            LayoutId = snapshot.LayoutId,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            CreatedIn = snapshot.CreatedIn,
        };
    }

    private static ObjectData CreateBootstrapModel(ObjectSnapshot snapshot)
        => snapshot.Kind switch
        {
            ObjectKind.BgObject => new BgObjectModel
            {
                ModelPath = ((BgObjectModel)snapshot.Model).ModelPath,
            },
            ObjectKind.Furniture => new FurnitureModel
            {
                SharedGroupPath = ((FurnitureModel)snapshot.Model).SharedGroupPath,
            },
            ObjectKind.Vfx => new VfxModel
            {
                VfxPath = ((VfxModel)snapshot.Model).VfxPath,
            },
            _ => snapshot.Model,
        };

    private static SceneObjectCreateResult FinalizeSceneObjectCreate<TSceneObject>(
        TSceneObject sceneObject,
        ObjectSnapshot snapshot,
        Action<TSceneObject> onCreated)
        where TSceneObject : class, ISceneObject
    {
        if (sceneObject.TryUpdate(snapshot) != SceneObjectUpdateResult.Applied)
        {
            sceneObject.Dispose();
            return SceneObjectCreateResult.Failed(ObjectRuntimeFailureCodes.UpdateRejected);
        }

        onCreated(sceneObject);
        return SceneObjectCreateResult.Created(sceneObject);
    }

    private static void LogCreatedRootResource(
        ILogger logger,
        string objectType,
        nint address,
        ObjectResolvedRootPath resolvedResource)
    {
        if (!resolvedResource.Redirected)
        {
            logger.LogInformation(
                "created {ObjectType} 0x{Address:X} using path {Path}",
                objectType,
                (ulong)address,
                resolvedResource.ResolvedPath);
            return;
        }

        logger.LogInformation(
            "created {ObjectType} 0x{Address:X} using redirected path {ResolvedPath} from {RequestedPath} in collection {CollectionId}",
            objectType,
            (ulong)address,
            resolvedResource.ResolvedPath,
            resolvedResource.RequestedPath,
            resolvedResource.ResourceCollectionId);
    }

    private static void LogRejectedRootResource(
        ILogger logger,
        string objectType,
        string pathLabel,
        ObjectResolvedRootPath resolvedResource)
    {
        if (!resolvedResource.IsReady)
        {
            logger.LogWarning(
                "{ObjectType} create rejected {PathLabel} {Path} with status {Status}",
                objectType,
                pathLabel,
                resolvedResource.ResolvedPath,
                resolvedResource.Status);
            return;
        }

        logger.LogWarning(
            "{ObjectType} create rejected missing {PathLabel} {Path}",
            objectType,
            pathLabel,
            resolvedResource.ResolvedPath);
    }

    private bool RootResourceExists(ObjectResolvedRootPath resolvedResource)
        => ObjectResourcePathUtility.Exists(_gameData, resolvedResource);

    private IDisposable EnterRootLoadScope(ObjectResolvedRootPath resolvedResource)
        => resolvedResource.ResourceCollectionId.Length == 0
            ? default(ObjectResourceLoadScopeToken)
            : _resourceLoaderFactory().EnterRootLoadScope(resolvedResource.ResourceCollectionId);

    private IDisposable EnterVfxCacheIsolationScope(ObjectResolvedRootPath resolvedResource)
        => resolvedResource.ResourceCollectionId.Length == 0
            && resolvedResource.ResolvedPathKind == ObjectResolvedPathKind.GamePath
            && GameAssetPathRules.IsFileKind(resolvedResource.CreatePath, GameAssetFileKind.Avfx)
            ? _resourceLoaderFactory().EnterRootCacheIsolation(resolvedResource.CreatePath)
            : default(ObjectResourceLoadScopeToken);

    private bool TryResolveRootResource(
        ILogger logger,
        ObjectSnapshot snapshot,
        ObjectRootPathKind kind,
        string requestedPath,
        string objectType,
        string pathLabel,
        out ObjectResolvedRootPath resolvedResource,
        out SceneObjectCreateResult failure)
    {
        resolvedResource = _pathResolver.ResolveRootPath(snapshot, kind, requestedPath);
        if (RootResourceExists(resolvedResource))
        {
            failure = default;
            return true;
        }

        LogRejectedRootResource(logger, objectType, pathLabel, resolvedResource);
        failure = SceneObjectCreateResult.Failed(ResolveRootResourceFailureCode(resolvedResource));
        return false;
    }

    private static string ResolveRootResourceFailureCode(ObjectResolvedRootPath resolvedResource)
    {
        if (!resolvedResource.IsReady)
        {
            return resolvedResource.Status switch
            {
                ObjectResolvedRootPathStatus.ResourceHooksUnavailable => ObjectRuntimeFailureCodes.ResourceHooksUnavailable,
                ObjectResolvedRootPathStatus.InvalidRedirectKind => ObjectRuntimeFailureCodes.InvalidRedirectKind,
                ObjectResolvedRootPathStatus.UnsupportedLocalFile => ObjectRuntimeFailureCodes.UnsupportedLocalFile,
                ObjectResolvedRootPathStatus.UnsupportedMemoryResource => ObjectRuntimeFailureCodes.UnsupportedMemoryResource,
                _ => ObjectRuntimeFailureCodes.CreateFailed,
            };
        }

        return resolvedResource.Redirected
            ? ObjectRuntimeFailureCodes.MissingRedirectAsset
            : ObjectRuntimeFailureCodes.MissingAsset;
    }
}

