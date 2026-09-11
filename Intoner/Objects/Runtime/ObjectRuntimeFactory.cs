using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Objects.Assets;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Text;
using LayoutTransform = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Transform;
using SceneLight = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Light;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Creates object runtimes from sanitized object snapshots.
/// </summary>
internal interface IObjectRuntimeFactory
{
    /// <summary>
    /// Tries to create one object runtime for the given sanitized snapshot.
    /// </summary>
    /// <param name="snapshot">The sanitized snapshot to create.</param>
    /// <param name="runtime">The created object runtime when successful.</param>
    /// <param name="failureCode">The runtime failure code when creation fails.</param>
    /// <returns>true when the object runtime was created.</returns>
    bool TryCreate(ObjectSnapshot snapshot, out IObjectRuntime runtime, out string failureCode);
}

internal sealed unsafe class ObjectRuntimeFactory : IObjectRuntimeFactory
{
    private readonly record struct ObjectRuntimeCreateResult(IObjectRuntime? Runtime, string FailureCode)
    {
        public static ObjectRuntimeCreateResult Created(IObjectRuntime runtime)
            => new(runtime, string.Empty);

        public static ObjectRuntimeCreateResult Failed(string failureCode)
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
    private readonly ILogger<ObjectRuntimeFactory> _logger;
    private readonly IFramework _framework;
    private readonly IDataManager _gameData;
    private readonly ObjectNativeBindings _nativeBindings;
    private readonly FurnitureEmoteGuard _emoteGuard;
    private readonly ObjectResourceTracker _resourceTracker;
    private readonly IObjectResourceLoader _resourceLoader;
    private readonly IVfxResourceRewriteService _vfxResourceRewriteService;
    private readonly ObjectPathResolver _pathResolver;

    public ObjectRuntimeFactory(
        ILoggerFactory loggerFactory,
        IFramework framework,
        IDataManager gameData,
        ObjectNativeBindings nativeBindings,
        FurnitureEmoteGuard emoteGuard,
        ObjectResourceTracker resourceTracker,
        IObjectResourceLoader resourceLoader,
        IVfxResourceRewriteService vfxResourceRewriteService,
        ObjectPathResolver pathResolver)
    {
        _framework = framework;
        _gameData = gameData;
        _nativeBindings = nativeBindings;
        _emoteGuard = emoteGuard;
        _resourceTracker = resourceTracker;
        _resourceLoader = resourceLoader;
        _vfxResourceRewriteService = vfxResourceRewriteService;
        _pathResolver = pathResolver;
        _bgObjectLogger = loggerFactory.CreateLogger<BgObjectRuntime>();
        _furnitureLogger = loggerFactory.CreateLogger<FurnitureObjectRuntime>();
        _vfxLogger = loggerFactory.CreateLogger<VfxObjectRuntime>();
        _lightLogger = loggerFactory.CreateLogger<LightObjectRuntime>();
        _logger = loggerFactory.CreateLogger<ObjectRuntimeFactory>();
    }

    public bool TryCreate(ObjectSnapshot snapshot, out IObjectRuntime runtime, out string failureCode)
    {
        ObjectRuntimeCreateResult result = FrameworkThreadUtility.Run(_framework, () =>
        {
            using IDisposable? scope = _logger.BeginScope("object={ObjectId}; kind={ObjectKind}; collection={CollectionId}; thread={ManagedThreadId}",
                snapshot.Id, snapshot.Kind, snapshot.CollectionId, Environment.CurrentManagedThreadId);
            _logger.LogDebug("object root create started; object={ObjectId}; collection={CollectionId}; requested={RequestedPath}",
                snapshot.Id, snapshot.CollectionId, ObjectSnapshotUtility.GetRootResourcePath(snapshot));
            return snapshot.Kind switch
            {
                ObjectKind.BgObject => TryCreateBgObjectUnsafe(snapshot),
                ObjectKind.Furniture => TryCreateFurnitureUnsafe(snapshot),
                ObjectKind.Vfx => TryCreateVfxUnsafe(snapshot),
                ObjectKind.Light => TryCreateLightUnsafe(snapshot),
                _ => ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.ServiceMissing),
            };
        });

        runtime = result.Runtime!;
        failureCode = result.FailureCode;
        if (result.Runtime is null)
        {
            _logger.LogWarning("object root create rejected; object={ObjectId}; kind={ObjectKind}; collection={CollectionId}; requested={RequestedPath}; failure={FailureCode}",
                snapshot.Id, snapshot.Kind, snapshot.CollectionId, ObjectSnapshotUtility.GetRootResourcePath(snapshot), failureCode);
        }

        return result.Runtime is not null;
    }

    private ObjectRuntimeCreateResult TryCreateBgObjectUnsafe(ObjectSnapshot snapshot)
    {
        var bgObjectModel = (BgObjectModel)snapshot.Model;
        if (string.IsNullOrWhiteSpace(bgObjectModel.ModelPath))
        {
            _bgObjectLogger.LogDebug("skipping bgobject create because model path is empty");
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!TryResolveRootResource(
                _bgObjectLogger,
                snapshot,
                ObjectRootPathKind.BgModel,
                bgObjectModel.ModelPath,
                "bgobject",
                "model",
                out ObjectResolvedRootPath resolvedResource,
                out ObjectRuntimeCreateResult failure))
        {
            return failure;
        }

        BgObject* bgObject;
        using var resourceLoadScope = EnterRootLoadScope(resolvedResource);
        bgObject = BgObjectSceneInterop.Create(resolvedResource.CreatePath);

        if (bgObject == null)
        {
            _bgObjectLogger.LogDebug("bgobject create returned null for model path {ModelPath}", bgObjectModel.ModelPath);
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.CreateFailed);
        }

        return FinalizeRuntimeCreate(
            () => new BgObjectRuntime(
                _framework,
                _bgObjectLogger,
                snapshot with { Model = new BgObjectModel { ModelPath = bgObjectModel.ModelPath } },
                bgObject,
                _resourceTracker,
                resolvedResource.ResolvedPath),
            () => BgObjectSceneInterop.Destroy(bgObject),
            snapshot,
            createdObject => LogCreatedRootResource(
                _bgObjectLogger,
                "bgobject",
                createdObject.Address,
                resolvedResource));
    }

    private ObjectRuntimeCreateResult TryCreateFurnitureUnsafe(ObjectSnapshot snapshot)
    {
        var furnitureModel = (FurnitureModel)snapshot.Model;
        if (string.IsNullOrWhiteSpace(furnitureModel.SharedGroupPath))
        {
            _furnitureLogger.LogDebug("skipping furniture create because shared group path is empty");
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!ObjectAssetPathRules.IsCatalogSharedGroupPath(furnitureModel.SharedGroupPath))
        {
            _furnitureLogger.LogDebug("furniture create rejected invalid shared group path {SharedGroupPath}", furnitureModel.SharedGroupPath);
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!TryResolveRootResource(
                _furnitureLogger,
                snapshot,
                ObjectRootPathKind.FurnitureSharedGroup,
                furnitureModel.SharedGroupPath,
                "furniture",
                "shared group",
                out ObjectResolvedRootPath resolvedResource,
                out ObjectRuntimeCreateResult failure))
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
            _furnitureLogger.LogDebug("furniture shared group create failed for path {SharedGroupPath}", furnitureModel.SharedGroupPath);
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.CreateFailed);
        }

        if (!IsVisualSharedGroup(instance, out InstanceType instanceType))
        {
            try
            {
                _furnitureLogger.LogDebug(
                    "furniture create returned native layout type {InstanceType} for path {SharedGroupPath}, destroying rejected instance",
                    instanceType,
                    furnitureModel.SharedGroupPath);
            }
            finally
            {
                DestroyCreatedSharedGroup(destroySharedGroup, instance);
            }

            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.NativeLayoutRejected);
        }

        return FinalizeRuntimeCreate(
            () => new FurnitureObjectRuntime(
                _framework,
                _furnitureLogger,
                snapshot with { Model = new FurnitureModel { SharedGroupPath = furnitureModel.SharedGroupPath } },
                instance,
                resolvedResource.ResolvedPath,
                _resourceTracker,
                _emoteGuard,
                destroySharedGroup),
            () => DestroyCreatedSharedGroup(destroySharedGroup, instance),
            snapshot,
            createdObject => LogCreatedRootResource(
                _furnitureLogger,
                "furniture shared group",
                createdObject.Address,
                resolvedResource),
            createdObject =>
            {
                applySharedGroupState(instance, 0);
                createdObject.RefreshCreatedVisualState();
            });
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

    private ObjectRuntimeCreateResult TryCreateLightUnsafe(ObjectSnapshot snapshot)
    {
        var lightModel = (LightModel)snapshot.Model;

        SceneLight* light;
        fixed (byte* poolPtr = "Intoner.Light\0"u8)
        {
            light = SceneLight.Create(LightObjectRuntime.ToRenderLightShape(lightModel.LightType), poolPtr);
        }

        if (light == null)
        {
            _lightLogger.LogDebug("light create returned null for type {LightType}", lightModel.LightType);
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.CreateFailed);
        }

        return FinalizeRuntimeCreate(
            () => new LightObjectRuntime(
                _framework,
                _lightLogger,
                snapshot,
                light),
            () => DrawObjectRuntime.DestroyNative((DrawObject*)light),
            snapshot,
            createdObject => _lightLogger.LogInformation("created light 0x{Address:X}", (ulong)createdObject.Address));
    }

    private ObjectRuntimeCreateResult TryCreateVfxUnsafe(ObjectSnapshot snapshot)
    {
        var vfxModel = (VfxModel)snapshot.Model;
        if (string.IsNullOrWhiteSpace(vfxModel.VfxPath))
        {
            _vfxLogger.LogDebug("skipping vfx create because path is empty");
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!GameAssetPathRules.IsFileKind(vfxModel.VfxPath, GameAssetFileKind.Avfx))
        {
            _vfxLogger.LogDebug("vfx create rejected invalid path {VfxPath}", vfxModel.VfxPath);
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!TryResolveRootResource(
                _vfxLogger,
                snapshot,
                ObjectRootPathKind.Vfx,
                vfxModel.VfxPath,
                "vfx",
                "path",
                out ObjectResolvedRootPath resolvedResource,
                out ObjectRuntimeCreateResult failure))
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
            _vfxLogger.LogDebug("vfx create returned null for path {VfxPath}", vfxModel.VfxPath);
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.CreateFailed);
        }

        return FinalizeRuntimeCreate(
            () => new VfxObjectRuntime(
                _framework,
                _vfxLogger,
                snapshot with { Model = new VfxModel { VfxPath = vfxModel.VfxPath } },
                vfxObject,
                resolvedResource.ResolvedPath,
                _nativeBindings.Vfx,
                _resourceTracker),
            () => DrawObjectRuntime.DestroyNative((DrawObject*)vfxObject),
            snapshot,
            createdObject => LogCreatedRootResource(
                _vfxLogger,
                "vfx",
                createdObject.Address,
                resolvedResource));
    }

    private static ObjectRuntimeCreateResult FinalizeRuntimeCreate<TRuntime>(
        Func<TRuntime> createRuntime,
        Action destroyNative,
        ObjectSnapshot snapshot,
        Action<TRuntime> onCreated,
        Action<TRuntime>? initializeNative = null)
        where TRuntime : ObjectRuntime
    {
        TRuntime? runtime = null;
        bool transferred = false;
        try
        {
            runtime = createRuntime();
            runtime.Initialize();
            initializeNative?.Invoke(runtime);
            if (runtime.TryUpdate(snapshot) != ObjectRuntimeUpdateResult.Applied)
            {
                return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.UpdateRejected);
            }

            onCreated(runtime);
            transferred = true;
            return ObjectRuntimeCreateResult.Created(runtime);
        }
        finally
        {
            if (!transferred)
            {
                if (runtime is not null)
                {
                    runtime.Dispose();
                }
                else
                {
                    destroyNative();
                }
            }
        }
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
            logger.LogDebug(
                "{ObjectType} create rejected {PathLabel} {Path} with status {Status}",
                objectType,
                pathLabel,
                resolvedResource.ResolvedPath,
                resolvedResource.Status);
            return;
        }

        logger.LogDebug(
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
            : _resourceLoader.EnterRootLoadScope(resolvedResource.ResourceCollectionId);

    private IDisposable EnterVfxCacheIsolationScope(ObjectResolvedRootPath resolvedResource)
        => resolvedResource.ResourceCollectionId.Length == 0
            && resolvedResource.ResolvedPathKind == ObjectResolvedPathKind.GamePath
            && GameAssetPathRules.IsFileKind(resolvedResource.CreatePath, GameAssetFileKind.Avfx)
            ? _resourceLoader.EnterRootCacheIsolation(resolvedResource.CreatePath)
            : default(ObjectResourceLoadScopeToken);

    private bool TryResolveRootResource(
        ILogger logger,
        ObjectSnapshot snapshot,
        ObjectRootPathKind kind,
        string requestedPath,
        string objectType,
        string pathLabel,
        out ObjectResolvedRootPath resolvedResource,
        out ObjectRuntimeCreateResult failure)
    {
        resolvedResource = _pathResolver.ResolveRootPath(snapshot, kind, requestedPath);
        logger.LogDebug("object root path resolved; object={ObjectId}; collection={CollectionId}; requested={RequestedPath}; resolved={ResolvedPath}; resolvedKind={ResolvedKind}; status={Status}",
            snapshot.Id, resolvedResource.ResourceCollectionId, resolvedResource.RequestedPath,
            resolvedResource.ResolvedPath, resolvedResource.ResolvedPathKind, resolvedResource.Status);
        if (RootResourceExists(resolvedResource))
        {
            failure = default;
            return true;
        }

        LogRejectedRootResource(logger, objectType, pathLabel, resolvedResource);
        failure = ObjectRuntimeCreateResult.Failed(ResolveRootResourceFailureCode(resolvedResource));
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
