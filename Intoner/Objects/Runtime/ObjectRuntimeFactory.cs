using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using Intoner.Logging;
using Intoner.Objects.Assets;
using Intoner.Objects.Interop;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Text;
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
    private readonly record struct ObjectRuntimeCreateResult(ObjectRuntime? Runtime, string FailureCode)
    {
        public static ObjectRuntimeCreateResult Created(ObjectRuntime runtime)
            => new(runtime, string.Empty);

        public static ObjectRuntimeCreateResult Failed(string failureCode)
            => new(null, string.IsNullOrWhiteSpace(failureCode) ? ObjectRuntimeFailureCodes.CreateFailed : failureCode);
    }

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
    private readonly CameraCollisionService _cameraCollision;
    private readonly ObjectResourceTracker _resourceTracker;
    private readonly IObjectResourceLoader _resourceLoader;
    private readonly IVfxResourceRewriteService _vfxResourceRewriteService;
    private readonly ObjectPathResolver _pathResolver;
    private readonly ObjectFrameworkDiagnostics _frameworkDiagnostics;

    public ObjectRuntimeFactory(
        ILoggerFactory loggerFactory,
        IFramework framework,
        IDataManager gameData,
        ObjectNativeBindings nativeBindings,
        FurnitureEmoteGuard emoteGuard,
        CameraCollisionService cameraCollision,
        ObjectResourceTracker resourceTracker,
        IObjectResourceLoader resourceLoader,
        IVfxResourceRewriteService vfxResourceRewriteService,
        ObjectPathResolver pathResolver,
        ObjectFrameworkDiagnostics frameworkDiagnostics)
    {
        _framework = framework;
        _gameData = gameData;
        _nativeBindings = nativeBindings;
        _emoteGuard = emoteGuard;
        _cameraCollision = cameraCollision;
        _resourceTracker = resourceTracker;
        _resourceLoader = resourceLoader;
        _vfxResourceRewriteService = vfxResourceRewriteService;
        _pathResolver = pathResolver;
        _frameworkDiagnostics = frameworkDiagnostics;
        _bgObjectLogger = loggerFactory.CreateLogger<BgObjectRuntime>();
        _furnitureLogger = loggerFactory.CreateLogger<FurnitureObjectRuntime>();
        _vfxLogger = loggerFactory.CreateLogger<VfxObjectRuntime>();
        _lightLogger = loggerFactory.CreateLogger<LightObjectRuntime>();
        _logger = loggerFactory.CreateLogger<ObjectRuntimeFactory>();
    }

    public bool TryCreate(ObjectSnapshot snapshot, out IObjectRuntime runtime, out string failureCode)
    {
        ObjectRuntimeCreateResult result = FrameworkThreadUtility.Run(_framework, () => TryCreateUnsafe(snapshot));

        runtime = result.Runtime!;
        failureCode = result.FailureCode;
        if (result.Runtime is null)
        {
            _logger.LogWarning("object root create rejected; object={ObjectId}; kind={ObjectKind}; collection={CollectionId}; requested={RequestedPath}; failure={FailureCode}",
                snapshot.Id, snapshot.Kind, snapshot.CollectionId, ObjectSnapshotUtility.GetRootResourcePath(snapshot), failureCode);
        }

        return result.Runtime is not null;
    }

    private ObjectRuntimeCreateResult TryCreateUnsafe(ObjectSnapshot snapshot)
    {
        ObjectRuntimeCreateResult result = default;
        try
        {
            using IDisposable? scope = DiagnosticLog.BeginScope(_logger, "object={ObjectId}; kind={ObjectKind}; collection={CollectionId}; thread={ManagedThreadId}",
                snapshot.Id, snapshot.Kind, snapshot.CollectionId, Environment.CurrentManagedThreadId);
            _logger.LogDebug("object root create started; object={ObjectId}; collection={CollectionId}; requested={RequestedPath}",
                snapshot.Id, snapshot.CollectionId, ObjectSnapshotUtility.GetRootResourcePath(snapshot));
            using ObjectResourceLoadScopeToken resourceLoadScope = _resourceLoader.EnterRootLoadScope(snapshot.CollectionId);
            result = snapshot.Kind switch
            {
                ObjectKind.BgObject => TryCreateBgObjectUnsafe(snapshot, resourceLoadScope.Collection),
                ObjectKind.Furniture => TryCreateFurnitureUnsafe(snapshot, resourceLoadScope.Collection),
                ObjectKind.Vfx => TryCreateVfxUnsafe(snapshot, resourceLoadScope.Collection),
                ObjectKind.Light => TryCreateLightUnsafe(snapshot, resourceLoadScope.Collection),
                _ => ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.ServiceMissing),
            };
            if (result.Runtime is not null)
            {
                ObjectResourceRoots roots = ObjectResourceRoots.FromModel(snapshot.Model);
                result.Runtime.ResourceRevision = snapshot.CollectionId.Length == 0
                    || roots.Primary.Length == 0 && roots.Collision.Length == 0
                        ? default(ObjectResourceRevision)
                        : resourceLoadScope.Collection?.GetResourceRevision(roots);
            }
        }
        catch
        {
            result.Runtime?.Dispose();
            throw;
        }

        return result;
    }

    private ObjectRuntimeCreateResult TryCreateBgObjectUnsafe(ObjectSnapshot snapshot, ObjectCollectionResolveData? collection)
    {
        var bgObjectModel = (BgObjectModel)snapshot.Model;
        if (bgObjectModel.ColliderDefinition is { Kind: not BgCollisionKind.None } && !_cameraCollision.IsBgAvailable)
        {
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.ServiceMissing);
        }

        if (string.IsNullOrWhiteSpace(bgObjectModel.ModelPath))
        {
            _bgObjectLogger.LogDebug("skipping bgobject create because model path is empty");
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (!TryResolveRootResource(
                _bgObjectLogger,
                collection,
                ObjectRootPathKind.BgModel,
                bgObjectModel.ModelPath,
                out ObjectResolvedRootPath resolvedResource,
                out ObjectRuntimeCreateResult failure))
        {
            return failure;
        }

        BgObject* bgObject;
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
                snapshot with { Model = new BgObjectModel
                {
                    ModelPath = bgObjectModel.ModelPath,
                    ColliderDefinition = bgObjectModel.ColliderDefinition,
                    CameraCollision = bgObjectModel.CameraCollision,
                } },
                bgObject,
                _resourceTracker,
                resolvedResource,
                _resourceLoader,
                _cameraCollision,
                collection),
            () => BgObjectSceneInterop.Destroy(bgObject),
            snapshot,
            createdObject => LogCreatedRootResource(
                _bgObjectLogger,
                "bgobject",
                createdObject.Address,
                resolvedResource));
    }

    private ObjectRuntimeCreateResult TryCreateFurnitureUnsafe(ObjectSnapshot snapshot, ObjectCollectionResolveData? collection)
    {
        if (!_cameraCollision.IsFurnitureAvailable || !_emoteGuard.IsAvailable)
        {
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.ServiceMissing);
        }

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
                collection,
                ObjectRootPathKind.FurnitureSharedGroup,
                furnitureModel.SharedGroupPath,
                out ObjectResolvedRootPath resolvedResource,
                out ObjectRuntimeCreateResult failure))
        {
            return failure;
        }

        var layoutTransform = ObjectLayoutInterop.CreateTransform(snapshot.Transform);
        var pathBytes = Encoding.UTF8.GetBytes(resolvedResource.CreatePath + '\0');
        FurnitureNativeInstance native = _cameraCollision.CreateRoot(snapshot.Visible, furnitureModel.CameraCollision);
        try
        {
            using ObjectCollisionOwnership.Scope collisionScope = _cameraCollision.EnterCreation(native);
            SharedGroupLayoutInstance* instance;
            fixed (byte* pathPtr = pathBytes)
            {
                instance = _nativeBindings.Furniture.Create(&layoutTransform, pathPtr);
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
                    _nativeBindings.Furniture.Destroy(instance);
                }

                return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.NativeLayoutRejected);
            }

            try
            {
                _cameraCollision.AttachRoot(native, instance);
            }
            catch
            {
                _nativeBindings.Furniture.Destroy(instance);
                throw;
            }

            return FinalizeRuntimeCreate(
                () => new FurnitureObjectRuntime(
                    _framework,
                    _furnitureLogger,
                    snapshot with { Model = new FurnitureModel { SharedGroupPath = furnitureModel.SharedGroupPath, CameraCollision = furnitureModel.CameraCollision } },
                    native,
                    resolvedResource.ResolvedPath,
                    _resourceTracker,
                    _emoteGuard,
                    _frameworkDiagnostics,
                    resolvedResource.ResourceCollectionId),
                native.Dispose,
                snapshot,
                createdObject => LogCreatedRootResource(
                    _furnitureLogger,
                    "furniture shared group",
                    createdObject.Address,
                    resolvedResource),
                createdObject => createdObject.RefreshCreatedVisualState());
        }
        finally
        {
            if (native.Instance == null)
            {
                native.Dispose();
            }
        }
    }

    private static bool IsVisualSharedGroup(SharedGroupLayoutInstance* instance, out InstanceType instanceType)
    {
        instanceType = instance != null
            ? ((ILayoutInstance*)instance)->Id.Type
            : default;
        return instanceType == InstanceType.SharedGroup;
    }

    private ObjectRuntimeCreateResult TryCreateLightUnsafe(ObjectSnapshot snapshot, ObjectCollectionResolveData? collection)
    {
        var lightModel = (LightModel)snapshot.Model;
        ObjectResolvedRootPath projectionResource = default;

        if (lightModel.ProjectionTexturePath.Length > 0
         && (!LightModel.SupportsProjectionTexture(lightModel.LightType)
             || !GameAssetPathRules.IsFileKind(lightModel.ProjectionTexturePath, GameAssetFileKind.Tex)))
        {
            return ObjectRuntimeCreateResult.Failed(ObjectRuntimeFailureCodes.InvalidAssetPath);
        }

        if (lightModel.ProjectionTexturePath.Length > 0
         && !TryResolveRootResource(
                _lightLogger,
                collection,
                ObjectRootPathKind.LightProjection,
                lightModel.ProjectionTexturePath,
                out projectionResource,
                out ObjectRuntimeCreateResult failure))
        {
            return failure;
        }

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
                light,
                projectionResource),
            () => DrawObjectRuntime.DestroyNative((DrawObject*)light),
            snapshot,
            createdObject => _lightLogger.LogInformation("created light 0x{Address:X}", (ulong)createdObject.Address));
    }

    private ObjectRuntimeCreateResult TryCreateVfxUnsafe(ObjectSnapshot snapshot, ObjectCollectionResolveData? collection)
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
                collection,
                ObjectRootPathKind.Vfx,
                vfxModel.VfxPath,
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
        using var cacheIsolationScope = EnterVfxCacheIsolationScope(resolvedResource);
        using var vfxRewriteScope = _vfxResourceRewriteService.EnterRewriteScope(resolvedResource, cacheIsolationScope.ResourceHash);
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
                _resourceTracker,
                resolvedResource.ResourceCollectionId),
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

    private ObjectRootCacheIsolationScope EnterVfxCacheIsolationScope(ObjectResolvedRootPath resolvedResource)
        => resolvedResource.ResourceCollectionId.Length == 0
            && resolvedResource.ResolvedPathKind == ObjectResolvedPathKind.GamePath
            && GameAssetPathRules.IsFileKind(resolvedResource.CreatePath, GameAssetFileKind.Avfx)
            ? _resourceLoader.EnterRootCacheIsolation(resolvedResource.CreatePath)
            : default;

    private bool TryResolveRootResource(
        ILogger logger,
        ObjectCollectionResolveData? collection,
        ObjectRootPathKind kind,
        string requestedPath,
        out ObjectResolvedRootPath resolvedResource,
        out ObjectRuntimeCreateResult failure)
    {
        resolvedResource = _pathResolver.ResolveRootPath(kind, requestedPath, collection);
        logger.LogDebug("object root path resolved; collection={CollectionId}; requested={RequestedPath}; resolved={ResolvedPath}; resolvedKind={ResolvedKind}; status={Status}",
            resolvedResource.ResourceCollectionId, resolvedResource.RequestedPath,
            resolvedResource.ResolvedPath, resolvedResource.ResolvedPathKind, resolvedResource.Status);
        if (ObjectResourcePathUtility.Exists(_gameData, resolvedResource))
        {
            failure = default;
            return true;
        }

        (string objectType, string pathLabel) = kind switch
        {
            ObjectRootPathKind.BgModel => ("bgobject", "model"),
            ObjectRootPathKind.FurnitureSharedGroup => ("furniture", "shared group"),
            ObjectRootPathKind.LightProjection => ("light", "projection texture"),
            ObjectRootPathKind.Vfx => ("vfx", "path"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        if (!resolvedResource.IsReady)
        {
            logger.LogDebug(
                "{ObjectType} create rejected {PathLabel} {Path} with status {Status}",
                objectType,
                pathLabel,
                resolvedResource.ResolvedPath,
                resolvedResource.Status);
        }
        else
        {
            logger.LogDebug(
                "{ObjectType} create rejected missing {PathLabel} {Path}",
                objectType,
                pathLabel,
                resolvedResource.ResolvedPath);
        }

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
