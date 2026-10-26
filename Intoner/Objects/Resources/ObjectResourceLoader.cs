using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Instance;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Resource;
using InteropGenerator.Runtime;
using Intoner.Objects.Interop;
using Intoner.Objects.Utils;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using System.Globalization;
using SceneBgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;

namespace Intoner.Objects.Resources;

/// <summary>
/// Provides object scoped native resource loading for root creates and typed dependent loads.
/// </summary>
internal interface IObjectResourceLoader : IDisposable
{
    /// <summary>
    /// Checks whether object scoped resource redirects can be applied for one root resource kind.
    /// </summary>
    /// <param name="kind">the root object resource kind</param>
    /// <returns>true when all required hooks for that resource family are available</returns>
    bool CanResolveCollectionResources(ObjectRootPathKind kind);

    /// <summary>
    /// Enables shared group callbacks before native furniture creation.
    /// </summary>
    /// <returns>true when shared group loads can be observed</returns>
    bool EnableSharedGroupLoads();

    /// <summary>
    /// Enters one temporary object resource load scope for the given collection id.
    /// Nested native resource requests during that scope may resolve through the object collection.
    /// </summary>
    /// <param name="resourceCollectionId">the active object collection id</param>
    /// <returns>a disposable scope that restores the previous object collection and root cache context, or an empty scope during shutdown</returns>
    ObjectResourceLoadScopeToken EnterRootLoadScope(string resourceCollectionId);

    /// <summary>
    /// Restores the retained resource generation for a shared group.
    /// </summary>
    /// <param name="instance">the owning shared group</param>
    /// <param name="canLoad">false when its resource ownership is invalid or unavailable</param>
    /// <returns>the retained collection scope, or a suspended scope for vanilla resources</returns>
    unsafe ObjectResourceLoadScopeToken EnterSharedGroupLoadScope(SharedGroupLayoutInstance* instance, out bool canLoad);

    /// <summary>
    /// Enters one temporary root cache isolation scope that keeps the real game path but prevents the game's native cache reuse.
    /// </summary>
    /// <param name="rootPath">the game resource path being passed to native creation</param>
    /// <returns>a scope with the isolated hash, or an empty scope when isolation is unavailable</returns>
    ObjectRootCacheIsolationScope EnterRootCacheIsolation(string rootPath);
}

/// <summary> keeps a root cache identity alive until native creation returns </summary>
internal readonly struct ObjectRootCacheIsolationScope(IDisposable scope, uint resourceHash) : IDisposable
{
    /// <summary> native hash for a full root read, or null when no isolation was established </summary>
    public uint? ResourceHash { get; } = resourceHash;

    public void Dispose()
        => scope?.Dispose();
}

internal sealed unsafe class ObjectResourceLoader : IObjectResourceLoader
{
    private delegate void ResolveResourceHandleTypeDelegate(ResourceHandleType* handleType, byte* path);

    private enum RedirectResolutionStatus
    {
        NotFound,
        Resolved,
        Rejected,
    }

    private enum RedirectRejectionReason
    {
        None,
        UnsupportedResourceKind,
        MissingResolvedPath,
        UnsupportedLocalFile,
        UnsupportedMemoryResource,
        InvalidNativePath,
    }

    private readonly record struct ScopedResourceRequest(
        ObjectCollectionResolveData Collection,
        string RequestedPath,
        bool WasScoped);

    internal readonly record struct ResolvedResourceLoad(
        ObjectCollectionResolveData Collection,
        string HandlePath,
        string TrackedPath);

    private readonly record struct ResourceRequestPlan(
        ResolvedResourceLoad? Load = null,
        uint? CacheIsolationHash = null,
        string? RejectedPath = null);

    private readonly record struct RedirectResolution(
        RedirectResolutionStatus Status,
        ResolvedResourceLoad ResolvedLoad,
        RedirectRejectionReason RejectionReason,
        ObjectResolvedPath RejectedPath)
    {
        public static RedirectResolution NotFound()
            => new(RedirectResolutionStatus.NotFound, default, RedirectRejectionReason.None, default);

        public static RedirectResolution Resolved(ResolvedResourceLoad resolvedLoad)
            => new(RedirectResolutionStatus.Resolved, resolvedLoad, RedirectRejectionReason.None, default);

        public static RedirectResolution Rejected(RedirectRejectionReason reason, ObjectResolvedPath rejectedPath)
            => new(RedirectResolutionStatus.Rejected, default, reason, rejectedPath);
    }

    internal readonly struct ResourceRequest(
        bool isSync,
        ResourceManager* resourceManager,
        ResourceHandleType* handleType,
        uint* resourceType,
        uint* resourceHash,
        byte* path,
        ObjectGetResourceParameters* parameters,
        byte hasHandleLock,
        byte* file,
        uint line)
    {
        public bool IsSync { get; init; } = isSync;
        public ResourceManager* ResourceManager { get; init; } = resourceManager;
        public ResourceHandleType* HandleType { get; init; } = handleType;
        public uint* ResourceType { get; init; } = resourceType;
        public uint* ResourceHash { get; init; } = resourceHash;
        public byte* Path { get; init; } = path;
        public ObjectGetResourceParameters* Parameters { get; init; } = parameters;
        public byte HasHandleLock { get; init; } = hasHandleLock;
        public byte* File { get; init; } = file;
        public uint Line { get; init; } = line;

        public bool HasLockedHandle
            => HasHandleLock != 0;

        public uint Type
            => ResourceType == null ? 0 : *ResourceType;
    }

    private const int SharedGroupResourceEventListenerOffset = 0x30;

    private readonly ILogger<ObjectResourceLoader> _logger;
    private readonly IDataManager _gameData;
    private readonly ObjectFileReadService _fileReadService;
    private readonly ObjectResourceTracker _resourceTracker;
    private readonly ObjectResourceLoadScope _loadScope;
    private readonly ObjectCollisionOwnership _collisionOwnership;
    private readonly ObjectResourceIncRefGuard _incRefGuard;
    private readonly ResolveResourceHandleTypeDelegate? _resolveResourceHandleType;
    private readonly ObjectResourceHooks _hooks;
    private readonly DisposalState _disposeState = new();

    public ObjectResourceLoader(
        ILogger<ObjectResourceLoader> logger,
        IDataManager gameData,
        ObjectFileReadService fileReadService,
        ObjectResourceTracker resourceTracker,
        ObjectResourceLoadScope loadScope,
        ObjectCollisionOwnership collisionOwnership,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _gameData = gameData;
        _fileReadService = fileReadService;
        _resourceTracker = resourceTracker;
        _loadScope = loadScope;
        _collisionOwnership = collisionOwnership;
        _incRefGuard = new ObjectResourceIncRefGuard(_logger);
        try
        {
            _resolveResourceHandleType = InteropHookUtility.CreateDelegate<ResolveResourceHandleTypeDelegate>(
                _logger,
                sigScanner,
                IntonerSignatures.ResourceHandleTypeFromPath);

            _hooks = new ObjectResourceHooks(
                _logger,
                gameInteropProvider,
                sigScanner,
                GetResourceSyncDetour,
                GetResourceAsyncDetour,
                ModelResourceLoadDetour,
                ModelResourceLoadMaterialsDetour,
                MaterialResourceLoadTexFilesDetour,
                MaterialResourceLoadShpkFilesDetour,
                ApricotResourceLoadDetour,
                BgObjectLoadAnimationDataDetour,
                SharedGroupLayoutResourceLoadDetour,
                LayoutSharedGroupInsertObjectDetour,
                ResourceHandleIncRefDetour,
                ResourceHandleDestructorDetour,
                SchedulerTimelineLoadResourcesDetour,
                GetCachedScheduleResourceDetour);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ObjectResourceLoadScopeToken EnterRootLoadScope(string resourceCollectionId)
    {
        if (IsDisposing)
        {
            return default(ObjectResourceLoadScopeToken);
        }

        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(resourceCollectionId);
        ObjectResourceLoadScopeToken scope = normalizedCollectionId.Length == 0
            ? _loadScope.Suspend()
            : EnterCollectionScopeToken(normalizedCollectionId);

        try
        {
            _logger.LogDebug("object resource root scope entered; collection={CollectionId}; generation={ResourceScopeId}; revision={Revision}; active={Active}",
                normalizedCollectionId, scope.Collection?.ResourceScopeId ?? 0, scope.Collection?.Revision ?? 0, scope.IsActive);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public ObjectRootCacheIsolationScope EnterRootCacheIsolation(string rootPath)
    {
        if (IsDisposing
         || !_hooks.CanResolveResourceRequests()
         || !TryNormalizeRootCacheIsolationPath(rootPath, out string normalizedPath))
        {
            return default;
        }

        if (!_hooks.Enable())
        {
            return default;
        }

        long loadId = ObjectResourceIdentity.Next();
        uint resourceHash = unchecked((uint)ComputeRootCacheIsolationHash(normalizedPath, loadId, null));
        ObjectResourceLoadScope.RootCacheIsolation? isolation = _loadScope.EnterRootCacheIsolation(normalizedPath, loadId);

        return isolation is null ? default : new ObjectRootCacheIsolationScope(isolation, resourceHash);
    }

    public bool CanResolveCollectionResources(ObjectRootPathKind kind)
    {
        if (IsDisposing
            || !_hooks.CanResolveCollectionResources(kind)
            || !_fileReadService.CanRouteScopedGamePaths())
        {
            return false;
        }

        return _hooks.Enable();
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose() && _hooks?.IsDisposed != false)
        {
            return;
        }

        _hooks?.Dispose();

        if (_hooks?.IsDisposed != false)
        {
            _incRefGuard.Dispose();
        }
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private bool TryReadActiveCollectionId(out string activeCollectionId)
    {
        activeCollectionId = string.Empty;
        if (IsDisposing)
        {
            return false;
        }

        return _loadScope.TryReadActiveCollectionId(out activeCollectionId);
    }

    private TResult CallWithRegisteredResourceScope<TState, TResult>(
        ResourceHandle* handle,
        string hook,
        TState state,
        ObjectResourcePathEncoding.TemporaryHandlePathAction<TState, TResult> callOriginal)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            using ObjectResourceLoadScopeToken scope = EnterRegisteredHandleScopeToken((nint)handle, out bool canLoad);

            if (!canLoad)
            {
                return default!;
            }

            return ObjectResourcePathEncoding.TryReadActualScopedHandlePath(handle, out string actualPath)
                ? ObjectResourcePathEncoding.WithTemporaryHandlePath(handle, actualPath, state, callOriginal)
                : callOriginal(state);
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, hook, handle);
            return default!;
        }
    }

    private byte ModelResourceLoadDetour(ResourceHandle* handle, void* contents, byte flag)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            using var scope = EnterRegisteredHandleScopeToken((nint)handle, out bool canLoad);
            if (!canLoad)
            {
                return ObjectModelResourceLoadGuard.FailureResult;
            }

            bool hasScopedPath = ObjectResourcePathEncoding.TryReadActualScopedHandlePath(handle, out string actualPath);
            if ((scope.IsActive || hasScopedPath) && !TryValidateModelResource(handle, actualPath))
            {
                return ObjectModelResourceLoadGuard.FailureResult;
            }

            if (!hasScopedPath)
            {
                return _hooks.ModelResourceLoadHook!.OriginalDisposeSafe(handle, contents, flag);
            }

            return ObjectResourcePathEncoding.WithTemporaryHandlePath(
                handle,
                actualPath,
                (Hook: _hooks.ModelResourceLoadHook!, Handle: (nint)handle, Contents: (nint)contents, Flag: flag),
                static state => state.Hook.OriginalDisposeSafe(
                    (ResourceHandle*)state.Handle,
                    (void*)state.Contents,
                    state.Flag));
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "model resource load", handle);
            return ObjectModelResourceLoadGuard.FailureResult;
        }
    }

    private bool ModelResourceLoadMaterialsDetour(ModelResourceHandle* handle)
        => CallWithRegisteredResourceScope(
            (ResourceHandle*)handle,
            "model material load",
            (Hook: _hooks.ModelResourceLoadMaterialsHook!, Handle: (nint)handle),
            static state => state.Hook.OriginalDisposeSafe((ModelResourceHandle*)state.Handle));

    private byte ApricotResourceLoadDetour(ResourceHandle* handle, void* contents, byte flag)
        => CallWithRegisteredResourceScope(
            handle,
            "apricot resource load",
            (Hook: _hooks.ApricotResourceLoadHook!, Handle: (nint)handle, Contents: (nint)contents, Flag: flag),
            static state => state.Hook.OriginalDisposeSafe((ResourceHandle*)state.Handle, (void*)state.Contents, state.Flag));

    private bool BgObjectLoadAnimationDataDetour(SceneBgObject* bgObject, CStringPointer modelPath)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            using var scope = EnterBgObjectModelScopeToken(bgObject, out bool canLoad);
            return canLoad && _hooks.BgObjectLoadAnimationDataHook!.OriginalDisposeSafe(bgObject, modelPath);
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "bgobject animation load", bgObject == null ? null : (ResourceHandle*)bgObject->ModelResourceHandle);
            return false;
        }
    }

    private byte MaterialResourceLoadTexFilesDetour(MaterialResourceHandle* handle)
        => CallWithRegisteredResourceScope(
            (ResourceHandle*)handle,
            "material texture load",
            (Hook: _hooks.MaterialResourceLoadTexFilesHook!, Handle: (nint)handle),
            static state => state.Hook.OriginalDisposeSafe((MaterialResourceHandle*)state.Handle));

    private byte MaterialResourceLoadShpkFilesDetour(MaterialResourceHandle* handle)
        => CallWithRegisteredResourceScope(
            (ResourceHandle*)handle,
            "material shader load",
            (Hook: _hooks.MaterialResourceLoadShpkFilesHook!, Handle: (nint)handle),
            static state => state.Hook.OriginalDisposeSafe((MaterialResourceHandle*)state.Handle));

    public bool EnableSharedGroupLoads()
        => _hooks.EnableSharedGroupLoads();

    private void SharedGroupLayoutResourceLoadDetour(ResourceEventListener* listener, ResourceHandle* handle)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterSharedGroupCallback();

        if (!callback.IsActive)
        {
            _hooks.SharedGroupLayoutResourceLoadHook!.OriginalDisposeSafe(listener, handle);
            return;
        }

        SharedGroupLayoutInstance* sharedGroup = ResolveSharedGroupInstance(listener);
        using ObjectCollisionOwnership.Scope collisionScope = _collisionOwnership.EnterInstance((ILayoutInstance*)sharedGroup);
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        bool nativeCallStarted = false;
        bool canForward = false;
        try
        {
            canForward = _collisionOwnership.CurrentOwner is null && !IsOwnedSharedGroupResource(sharedGroup, handle);
            using ObjectResourceLoadScopeToken scope = EnterSharedGroupResourceLoadScopeToken(sharedGroup, handle, out bool canLoad);
            if (!canLoad)
            {
                return;
            }

            if (!ObjectResourcePathEncoding.TryReadActualScopedHandlePath(handle, out string actualPath))
            {
                nativeCallStarted = true;
                _hooks.SharedGroupLayoutResourceLoadHook!.OriginalDisposeSafe(listener, handle);
                return;
            }

            nativeCallStarted = true;
            _ = ObjectResourcePathEncoding.WithTemporaryHandlePath(
                handle,
                actualPath,
                (Hook: _hooks.SharedGroupLayoutResourceLoadHook!, Listener: (nint)listener, Handle: (nint)handle),
                static state =>
                {
                    state.Hook.OriginalDisposeSafe(
                        (ResourceEventListener*)state.Listener,
                        (ResourceHandle*)state.Handle);
                    return true;
                });
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "shared group resource load", handle);
            if (!nativeCallStarted && canForward)
            {
                using ObjectResourceLoadScopeToken suspended = _loadScope.Suspend();
                _hooks.SharedGroupLayoutResourceLoadHook!.OriginalDisposeSafe(listener, handle);
            }
        }
    }

    private void LayoutSharedGroupInsertObjectDetour(LayoutSharedGroupObject* instance, ILayoutInstance* layoutInstance)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();

        if (!callback.IsActive)
        {
            _hooks.LayoutSharedGroupInsertObjectHook!.OriginalDisposeSafe(instance, layoutInstance);
            return;
        }

        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        bool nativeCallStarted = false;
        bool canForward = false;
        ResourceHandle* handle = null;
        try
        {
            SharedGroupLayoutInstance* sharedGroup = instance != null ? instance->Instance : null;
            handle = sharedGroup == null ? null : sharedGroup->ResourceHandle;
            canForward = !IsOwnedSharedGroupResource(sharedGroup, handle);
            using ObjectResourceLoadScopeToken scope = EnterSharedGroupResourceLoadScopeToken(sharedGroup, handle, out bool canLoad);

            if (canLoad)
            {
                nativeCallStarted = true;
                _hooks.LayoutSharedGroupInsertObjectHook!.OriginalDisposeSafe(instance, layoutInstance);
            }
        }
        catch (Exception ex)
        {
            ObjectResourceLog.Write(() => _logger.LogError(ex, "object shared group insert hook failed"));

            if (!nativeCallStarted && canForward)
            {
                using ObjectResourceLoadScopeToken suspended = _loadScope.Suspend();
                _hooks.LayoutSharedGroupInsertObjectHook!.OriginalDisposeSafe(instance, layoutInstance);
            }
        }
    }

    private nint ResourceHandleDestructorDetour(ResourceHandle* handle)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            _resourceTracker.RemoveTrackedHandle((nint)handle, "native destruction");
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "resource handle destruction", handle);
        }

        return _hooks.ResourceHandleDestructorHook!.OriginalDisposeSafe(handle);
    }

    private ResourceHandle* ResourceHandleIncRefDetour(ResourceHandle* handle)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        ObjectResourceIncRefGuard.Scope scope = default;
        try
        {
            if (!IsDisposing && handle != null && handle->RefCount == 0)
            {
                scope = _incRefGuard.EnterScope();
            }
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "resource handle inc ref", handle);
        }

        using (scope)
        {
            return _hooks.ResourceHandleIncRefHook!.OriginalDisposeSafe(handle);
        }
    }

    private ulong SchedulerTimelineLoadResourcesDetour(SchedulerTimeline* timeline)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            if (IsDisposing)
            {
                return _hooks.SchedulerTimelineLoadResourcesHook!.OriginalDisposeSafe(timeline);
            }

            using ObjectResourceLoadScopeToken scope = EnterSchedulerTimelineResourceScopeToken(timeline, out bool canLoad);
            if (!canLoad)
            {
                return 0;
            }

            ulong result = CallSchedulerTimelineLoadResourcesOriginal(timeline);
            if (!IsDisposing && scope.Collection is { } collection)
            {
                TryRegisterSchedulerTimelineResource(timeline, collection);
            }

            return result;
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "scheduler timeline load", ResolveSchedulerTimelineResourceHandle(timeline));
            return 0;
        }
    }

    private SchedulerResource* GetCachedScheduleResourceDetour(
        SchedulerResourceManagement* resourceManagement,
        ScheduleResourceLoadData* loadData,
        byte useMap)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            if (!IsDisposing && _loadScope.TryReadActiveCollection(out ObjectCollectionResolveData collection)
                && collection.Redirects.Count > 0)
            {
                return null;
            }

        }
        catch (Exception ex)
        {
            ObjectResourceLog.Write(() => _logger.LogError(ex, "object scheduler cache hook failed"));
        }

        return _hooks.GetCachedScheduleResourceHook!.OriginalDisposeSafe(resourceManagement, loadData, useMap);
    }

    private ResourceHandle* GetResourceSyncDetour(
        ResourceManager* resourceManager,
        ResourceHandleType* handleType,
        uint* resourceType,
        uint* resourceHash,
        byte* path,
        ObjectGetResourceParameters* getResourceParameters,
        byte* file,
        uint line)
        => GetResourceDetour(new ResourceRequest(
            true,
            resourceManager,
            handleType,
            resourceType,
            resourceHash,
            path,
            getResourceParameters,
            0,
            file,
            line));

    private ResourceHandle* GetResourceAsyncDetour(
        ResourceManager* resourceManager,
        ResourceHandleType* handleType,
        uint* resourceType,
        uint* resourceHash,
        byte* path,
        ObjectGetResourceParameters* getResourceParameters,
        byte hasHandleLock,
        byte* file,
        uint line)
        => GetResourceDetour(new ResourceRequest(
            false,
            resourceManager,
            handleType,
            resourceType,
            resourceHash,
            path,
            getResourceParameters,
            hasHandleLock,
            file,
            line));

    internal ResourceHandle* GetResourceDetour(ResourceRequest request)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        ObjectResourceLoadScopeToken scope = default;

        try
        {
            ResourceRequestPlan plan = PrepareResourceRequest(request, ref scope);

            if (plan.RejectedPath is string rejectedPath)
            {
                using ObjectResourceLoadScopeToken suspended = _loadScope.Suspend();
                return CallOriginalWithPath(request, rejectedPath);
            }

            if (plan.CacheIsolationHash is uint hash)
            {
                return CallOriginalWithHash(request, hash);
            }

            if (plan.Load is not ResolvedResourceLoad load)
            {
                return CallUnscopedOriginal(request);
            }

            ResourceHandle* handle = CallOriginalWithPath(request, load.HandlePath);

            if (handle == null)
            {
                uint resourceType = request.Type;
                ObjectResourceLog.Write(() => _logger.LogWarning("object resource request returned null; collection={CollectionId}; generation={ResourceScopeId}; resolved={ResolvedPath}; resourceType=0x{ResourceType:X8}",
                    load.Collection.CollectionId, load.Collection.ResourceScopeId, load.TrackedPath, resourceType));
            }

            RegisterLoadedHandle(handle, load, request.Type);
            return handle;
        }
        finally
        {
            scope.Dispose();
        }
    }

    private ResourceRequestPlan PrepareResourceRequest(ResourceRequest request, ref ObjectResourceLoadScopeToken scope)
    {
        ScopedResourceRequest? admittedCollision = null;
        try
        {
            if (IsDisposing)
            {
                if (!ObjectResourcePathEncoding.TryReadNativePath(request.Path, out string disposedPath))
                {
                    return default;
                }

                if (ObjectResourcePathUtility.IsOwnedResourcePath(disposedPath))
                {
                    return RejectPrivateRequest(disposedPath);
                }

                if ((ResourceType)request.Type == ResourceType.Pcb
                 && _loadScope.TryConsumeCollisionRequest(disposedPath, out ObjectCollectionResolveData? disposedCollection)
                 && disposedCollection is not null)
                {
                    admittedCollision = new ScopedResourceRequest(disposedCollection, disposedPath, WasScoped: false);
                    return RejectPrivateRequest(admittedCollision.Value);
                }

                return default;
            }

            if (_incRefGuard.ShouldBypassRedirect(
                    request.IsSync,
                    request.HandleType,
                    request.ResourceType,
                    request.ResourceHash,
                    request.Path,
                    request.Parameters,
                    request.HasLockedHandle))
            {
                return default;
            }

            if (!ObjectResourcePathEncoding.TryReadNativePath(request.Path, out string requestedPath)
                || IsForeignResourceRequest(requestedPath))
            {
                return default;
            }

            if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(requestedPath)
             && !ObjectMemoryResourcePathUtility.TryParse(requestedPath, out _))
            {
                return RejectPrivateRequest(requestedPath);
            }

            ObjectCollectionResolveData? collisionCollection = null;
            bool privateCollision = (ResourceType)request.Type == ResourceType.Pcb
                && !ObjectScopedResourcePathUtility.IsObjectScopedPath(requestedPath);

            if (privateCollision
             && (!_loadScope.TryConsumeCollisionRequest(requestedPath, out collisionCollection) || collisionCollection is null))
            {
                return default;
            }

            if (!privateCollision && TryResolveRootCacheIsolation(requestedPath, request, out uint cacheIsolationHash))
            {
                ObjectResourceLog.Write(() => _logger.LogDebug("using temporary root resource hash for {Path}: 0x{ResourceHash:X8}", requestedPath, cacheIsolationHash));
                return new(CacheIsolationHash: cacheIsolationHash);
            }

            ScopedResourceRequest scopedRequest;
            if (privateCollision)
            {
                scopedRequest = new(collisionCollection!, requestedPath, WasScoped: false);
                admittedCollision = scopedRequest;
                scope = _loadScope.EnterResourceScope(scopedRequest.Collection.ResourceScopeId);
            }
            else
            {
                scope = EnterResourceRequestScope(requestedPath, out scopedRequest);
            }

            if (!scope.IsActive)
            {
                if (privateCollision)
                {
                    return RejectPrivateRequest(scopedRequest);
                }

                return ObjectScopedResourcePathUtility.IsObjectScopedPath(requestedPath)
                    ? RejectPrivateRequest(requestedPath) : default;
            }

            RedirectResolution redirect = ResolveResourceLoad(scopedRequest, request.Type);
            if (redirect.Status == RedirectResolutionStatus.NotFound)
            {
                return default;
            }

            if (redirect.Status == RedirectResolutionStatus.Rejected)
            {
                LogRejectedResourceRedirect(scopedRequest, request.Type, redirect);
                return RejectPrivateRequest(scopedRequest);
            }

            return new(Load: redirect.ResolvedLoad);
        }
        catch (Exception ex)
        {
            LogNativePathHookFailure(ex, "resource request", request.Path);

            if (admittedCollision is { } admitted)
            {
                return RejectPrivateRequest(admitted);
            }

            if (!ObjectResourcePathEncoding.TryReadNativePath(request.Path, out string requestedPath))
            {
                return default;
            }

            if (ObjectResourcePathUtility.IsOwnedResourcePath(requestedPath))
            {
                return RejectPrivateRequest(requestedPath);
            }

            if ((ResourceType)request.Type == ResourceType.Pcb && !scope.IsActive)
            {
                return default;
            }

            if (IsForeignResourceRequest(requestedPath))
            {
                return default;
            }

            return _loadScope.TryReadActiveCollection(out ObjectCollectionResolveData collection)
                && collection.Redirects.Count > 0
                    ? new(RejectedPath: ObjectScopedResourcePathUtility.CreateRejected(collection.ResourceScopeId, requestedPath))
                : default;
        }
    }

    private static ResourceRequestPlan RejectPrivateRequest(ScopedResourceRequest request)
        => new(RejectedPath: ObjectScopedResourcePathUtility.CreateRejected(request.Collection.ResourceScopeId, request.RequestedPath));

    private static ResourceRequestPlan RejectPrivateRequest(string requestedPath)
        => ObjectScopedResourcePathUtility.TryParse(requestedPath, out ObjectScopedResourcePath path)
            ? new(RejectedPath: ObjectScopedResourcePathUtility.CreateRejected(path.ResourceScopeId, path.Path))
            : new(RejectedPath: ObjectScopedResourcePathUtility.CreateRejected(0, requestedPath));

    private static bool IsForeignResourceRequest(string path)
        => ObjectScopedResourcePathUtility.IsForeignScopedPath(path)
            || ObjectLocalFilePathUtility.IsLocalFilePath(path);

    private ResourceHandle* CallUnscopedOriginal(ResourceRequest request)
    {
        using ObjectResourceLoadScopeToken scope = _loadScope.Suspend();
        return CallOriginal(request);
    }

    private RedirectResolution ResolveResourceLoad(ScopedResourceRequest request, uint resourceType)
    {
        string normalizedRequestedPath = ObjectScopedResourcePathUtility.Create(0, request.RequestedPath);

        if (normalizedRequestedPath.Length == 0)
        {
            return RedirectResolution.Rejected(RedirectRejectionReason.InvalidNativePath, default);
        }

        bool requestedLocalFile = ObjectLocalFilePathUtility.IsLocalFilePath(normalizedRequestedPath);
        if (request.Collection.Redirects.Count == 0)
        {
            if (!request.WasScoped)
            {
                return RedirectResolution.NotFound();
            }

            return ResolveResourceHandlePath(request.Collection, normalizedRequestedPath, resourceType);
        }

        string loadPath = normalizedRequestedPath;

        if (!requestedLocalFile && request.Collection.TryResolvePath(normalizedRequestedPath, out ObjectResolvedPath redirectedPath))
        {
            if (!ObjectResourcePathUtility.IsSupportedRedirection(normalizedRequestedPath, redirectedPath))
            {
                return RedirectResolution.Rejected(RedirectRejectionReason.UnsupportedResourceKind, redirectedPath);
            }

            if (!ObjectResourcePathUtility.Exists(_gameData, redirectedPath))
            {
                return RedirectResolution.Rejected(RedirectRejectionReason.MissingResolvedPath, redirectedPath);
            }

            if (redirectedPath.IsLocalFile
                && !_fileReadService.CanLoadLocalFilePath(redirectedPath.Path, (ResourceType)resourceType))
            {
                return RedirectResolution.Rejected(RedirectRejectionReason.UnsupportedLocalFile, redirectedPath);
            }

            if (redirectedPath.IsMemory
                && !_fileReadService.CanLoadMemoryResourcePath(redirectedPath.Path))
            {
                return RedirectResolution.Rejected(RedirectRejectionReason.UnsupportedMemoryResource, redirectedPath);
            }

            loadPath = redirectedPath.Path;
        }
        else if (!request.WasScoped && !RequiresCollectionScope(resourceType))
        {
            return RedirectResolution.NotFound();
        }

        return ResolveResourceHandlePath(request.Collection, loadPath, resourceType);
    }

    private static RedirectResolution ResolveResourceHandlePath(
        ObjectCollectionResolveData collection, string loadPath, uint resourceType)
    {
        string handlePath = CreateResourceHandlePath(collection, loadPath, resourceType);

        if (handlePath.Length == 0)
        {
            _ = ObjectResolvedPath.TryCreate(loadPath, out ObjectResolvedPath rejectedPath);
            return RedirectResolution.Rejected(RedirectRejectionReason.InvalidNativePath, rejectedPath);
        }

        return RedirectResolution.Resolved(new ResolvedResourceLoad(collection, handlePath, loadPath));
    }

    private ObjectResourceLoadScopeToken EnterResourceRequestScope(string requestedPath, out ScopedResourceRequest request)
    {
        request = default;
        if (ObjectScopedResourcePathUtility.TryParse(requestedPath, out ObjectScopedResourcePath scopedPath))
        {
            ObjectResourceLoadScopeToken scope = _loadScope.EnterResourceScope(scopedPath.ResourceScopeId);
            if (scope.Collection is { } collection)
            {
                request = new ScopedResourceRequest(collection, scopedPath.Path, WasScoped: true);
            }

            return scope;
        }

        if (ObjectScopedResourcePathUtility.IsObjectScopedPath(requestedPath))
        {
            return default;
        }

        ObjectResourceLoadScopeToken activeScope = EnterActiveCollectionScopeToken();
        if (activeScope.Collection is { } activeCollection)
        {
            request = new ScopedResourceRequest(activeCollection, requestedPath, WasScoped: false);
        }

        return activeScope;
    }

    private bool TryResolveRootCacheIsolation(string requestedPath, ResourceRequest request, out uint resourceHash)
    {
        resourceHash = 0;
        if (!ShouldIsolateResourceCache(request.Type)
         || ObjectScopedResourcePathUtility.IsObjectScopedPath(requestedPath))
        {
            return false;
        }

        string normalizedRequestedPath = ObjectResourcePathUtility.NormalizeTrackedPath(requestedPath);
        if (normalizedRequestedPath.Length == 0
         || !_loadScope.TryConsumeRootCacheIsolation(normalizedRequestedPath, out long loadId))
        {
            return false;
        }

        resourceHash = unchecked((uint)ComputeRootCacheIsolationHash(normalizedRequestedPath, loadId, request.Parameters));
        return true;
    }

    internal ObjectResourceLoadScopeToken EnterRegisteredHandleScopeToken(nint resourceHandleAddress, out bool canLoad)
    {
        canLoad = true;
        if (IsDisposing)
        {
            return default;
        }

        if (resourceHandleAddress == nint.Zero)
        {
            return _loadScope.Suspend();
        }

        var resourceHandle = (ResourceHandle*)resourceHandleAddress;
        if (ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string handlePath))
        {
            if (ObjectScopedResourcePathUtility.TryParse(handlePath, out ObjectScopedResourcePath scopedPath))
            {
                ObjectResourceLoadScopeToken scopedHandleScope = _resourceTracker.EnterHandleScope(resourceHandleAddress, scopedPath);
                canLoad = scopedHandleScope.IsActive;
                if (!canLoad)
                {
                    LogRejectedHandleScope("generation unavailable", resourceHandleAddress, handlePath, new(string.Empty, scopedPath.Path, scopedPath.ResourceScopeId));
                }

                return scopedHandleScope;
            }

            if (ObjectScopedResourcePathUtility.IsObjectScopedPath(handlePath))
            {
                canLoad = false;
                LogRejectedHandleScope("malformed private path", resourceHandleAddress, handlePath);
                return _loadScope.Suspend();
            }

            if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(handlePath)
             && !ObjectMemoryResourcePathUtility.TryParse(handlePath, out _))
            {
                canLoad = false;
                LogRejectedHandleScope("malformed memory path", resourceHandleAddress, handlePath);
                return _loadScope.Suspend();
            }

            if (ObjectScopedResourcePathUtility.IsForeignScopedPath(handlePath))
            {
                return _loadScope.Suspend();
            }
        }

        if (!_resourceTracker.TryGetHandleScope(resourceHandleAddress, out var handleScope, out bool isTracked))
        {
            canLoad = !isTracked;
            if (!canLoad)
            {
                LogRejectedHandleScope("conflicting ownership", resourceHandleAddress, handlePath);
            }

            return _loadScope.Suspend();
        }

        if (!DoesTrackedHandleMatch(resourceHandleAddress, handleScope))
        {
            LogRejectedHandleScope("path mismatch", resourceHandleAddress, handlePath, handleScope);
            _resourceTracker.RemoveTrackedHandle(resourceHandleAddress, "path mismatch");
            canLoad = false;
            return _loadScope.Suspend();
        }

        ObjectResourceLoadScopeToken scope = _loadScope.EnterResourceScope(handleScope.ResourceScopeId);
        canLoad = scope.IsActive;
        if (!canLoad)
        {
            LogRejectedHandleScope("generation unavailable", resourceHandleAddress, handlePath, handleScope);
        }

        return scope;
    }

    private ObjectResourceLoadScopeToken EnterSharedGroupInstanceScopeToken(SharedGroupLayoutInstance* instance, out bool canLoad)
    {
        canLoad = true;
        if (instance == null)
        {
            return _loadScope.Suspend();
        }

        if (TryEnterTrackedSharedGroupInstanceScope(instance, out ObjectResourceLoadScopeToken instanceScope, out canLoad))
        {
            return instanceScope;
        }

        return EnterRegisteredHandleScopeToken((nint)instance->ResourceHandle, out canLoad);
    }

    public ObjectResourceLoadScopeToken EnterSharedGroupLoadScope(SharedGroupLayoutInstance* instance, out bool canLoad)
        => EnterSharedGroupResourceLoadScopeToken(instance, instance != null ? instance->ResourceHandle : null, out canLoad);

    internal ObjectResourceLoadScopeToken EnterSharedGroupResourceLoadScopeToken(SharedGroupLayoutInstance* instance, ResourceHandle* handle, out bool canLoad)
    {
        if (instance == null || (ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
         && (ObjectResourcePathUtility.IsOwnedResourcePath(handlePath)
             || ObjectScopedResourcePathUtility.IsForeignScopedPath(handlePath))))
        {
            return EnterRegisteredHandleScopeToken((nint)handle, out canLoad);
        }

        ObjectResourceLoadScopeToken scope = EnterSharedGroupInstanceScopeToken(instance, out canLoad);
        if (scope.IsActive || !canLoad)
        {
            return scope;
        }

        scope.Dispose();
        return EnterRegisteredHandleScopeToken((nint)handle, out canLoad);
    }

    private static SharedGroupLayoutInstance* ResolveSharedGroupInstance(ResourceEventListener* listener)
        => listener == null
            ? null
            : (SharedGroupLayoutInstance*)((byte*)listener - SharedGroupResourceEventListenerOffset);

    private bool TryEnterTrackedSharedGroupInstanceScope(
        SharedGroupLayoutInstance* instance,
        out ObjectResourceLoadScopeToken scope,
        out bool canLoad)
    {
        scope = default;
        canLoad = true;
        if (instance == null)
        {
            return false;
        }

        if (!_resourceTracker.TryGetInstanceScope((nint)instance, out var instanceScope, out bool isTracked))
        {
            if (isTracked)
            {
                LogRejectedHandleScope("conflicting instance ownership", (nint)instance, string.Empty);
                scope = _loadScope.Suspend();
                canLoad = false;
            }

            return isTracked;
        }

        if (!DoesTrackedSharedGroupInstanceMatch(instance, instanceScope))
        {
            LogRejectedHandleScope("instance path mismatch", (nint)instance, string.Empty, instanceScope);
            _resourceTracker.RemoveTrackedInstance((nint)instance, "path mismatch");
            scope = _loadScope.Suspend();
            canLoad = false;
            return true;
        }

        scope = _loadScope.EnterResourceScope(instanceScope.ResourceScopeId);
        canLoad = scope.IsActive;
        if (!canLoad)
        {
            LogRejectedHandleScope("instance generation unavailable", (nint)instance, string.Empty, instanceScope);
        }

        return true;
    }

    private ObjectResourceLoadScopeToken EnterBgObjectModelScopeToken(SceneBgObject* bgObject, out bool canLoad)
    {
        canLoad = true;
        return bgObject == null
            ? _loadScope.Suspend()
            : EnterRegisteredHandleScopeToken((nint)bgObject->ModelResourceHandle, out canLoad);
    }

    internal ObjectResourceLoadScopeToken EnterSchedulerTimelineResourceScopeToken(SchedulerTimeline* timeline, out bool canLoad)
    {
        canLoad = true;
        if (timeline == null)
        {
            return _loadScope.Suspend();
        }

        ResourceHandle* resourceHandle = ResolveSchedulerTimelineResourceHandle(timeline);
        // initial timeline loads request the TMB before assigning a resource handle
        return resourceHandle == null
            ? EnterActiveCollectionScopeToken()
            : EnterRegisteredHandleScopeToken((nint)resourceHandle, out canLoad);
    }

    private ObjectResourceLoadScopeToken EnterActiveCollectionScopeToken()
        => !IsDisposing && _loadScope.TryReadActiveCollection(out ObjectCollectionResolveData collection)
            ? _loadScope.EnterResourceScope(collection.ResourceScopeId)
            : default;

    private ObjectResourceLoadScopeToken EnterCollectionScopeToken(string resourceCollectionId)
        => IsDisposing ? default : _loadScope.EnterCollectionScope(resourceCollectionId);

    private bool TryValidateModelResource(ResourceHandle* handle, string actualPath)
    {
        ObjectModelResourceValidationResult validation = ObjectModelResourceLoadGuard.Validate((ModelResourceHandle*)handle);
        if (validation.IsValid)
        {
            return true;
        }

        string handlePath = ObjectResourcePathEncoding.TryReadHandlePath(handle, out string currentPath)
            ? currentPath
            : string.Empty;
        string collectionId = TryReadActiveCollectionId(out string activeCollectionId)
            ? activeCollectionId
            : string.Empty;
        ObjectResourceLog.Write(() => _logger.LogWarning(
            "object model resource load rejected: {Reason}; collection={CollectionId}; path={Path}; actual={ActualPath}; length={Length}; version=0x{Version:X8}",
            validation.Reason,
            collectionId,
            handlePath,
            actualPath,
            validation.Length,
            validation.Version));
        return false;
    }

    private ulong CallSchedulerTimelineLoadResourcesOriginal(SchedulerTimeline* timeline)
    {
        ResourceHandle* resourceHandle = ResolveSchedulerTimelineResourceHandle(timeline);
        if (!ObjectResourcePathEncoding.TryReadActualScopedHandlePath(resourceHandle, out string actualPath))
        {
            return _hooks.SchedulerTimelineLoadResourcesHook!.OriginalDisposeSafe(timeline);
        }

        return ObjectResourcePathEncoding.WithTemporaryHandlePath(
            resourceHandle,
            actualPath,
            (Hook: _hooks.SchedulerTimelineLoadResourcesHook!, Timeline: (nint)timeline),
            static state => state.Hook.OriginalDisposeSafe((SchedulerTimeline*)state.Timeline));
    }

    private static bool RequiresCollectionScope(uint resourceType)
        => (ResourceType)resourceType is ResourceType.Avfx
            or ResourceType.Mdl
            or ResourceType.Mtrl
            or ResourceType.Sgb
            or ResourceType.Tmb
            or ResourceType.Sklb
            or ResourceType.Pap
            or ResourceType.Pcb;

    private static bool ShouldIsolateResourceCache(uint resourceType)
        => RequiresCollectionScope(resourceType)
        || (ResourceType)resourceType is ResourceType.Tex
            or ResourceType.Shpk
            or ResourceType.Atex
            or ResourceType.Scd;

    private static bool TryNormalizeRootCacheIsolationPath(string rootPath, out string normalizedPath)
    {
        normalizedPath = ObjectResourcePathUtility.NormalizeTrackedPath(rootPath);
        return normalizedPath.Length > 0
            && !ObjectLocalFilePathUtility.IsLocalFilePath(normalizedPath)
            && !ObjectMemoryResourcePathUtility.IsMemoryResourcePath(normalizedPath);
    }

    private static string CreateResourceHandlePath(ObjectCollectionResolveData collection, string loadPath, uint resourceType)
    {
        // scoped handle paths let async callbacks recover the collection before tracker registration
        return ShouldIsolateResourceCache(resourceType)
            ? ObjectScopedResourcePathUtility.Create(collection.ResourceScopeId, loadPath)
            : loadPath;
    }

    private static int ComputeRootCacheIsolationHash(string path, long loadId, ObjectGetResourceParameters* getResourceParameters)
    {
        string isolatedPath = string.Concat(path, ".intoner.process.", loadId.ToString("x", CultureInfo.InvariantCulture));
        return ObjectResourcePathEncoding.ComputeResourceHash(isolatedPath, getResourceParameters);
    }

    private ResourceHandle* CallOriginalWithPath(ResourceRequest request, string resourcePath)
        => (ResourceHandle*)ObjectResourcePathEncoding.WithNullTerminatedUtf8(
            resourcePath,
            (Owner: this, Request: request, ResourcePath: resourcePath),
            static (pathPointer, _, state) =>
            {
                ResourceHandleType resolvedHandleType = (ResourceHandleType)ResourceCategory.Common;
                ResourceHandleType* handleTypePointer = ObjectScopedResourcePathUtility.IsRejectedPath(state.ResourcePath)
                    || state.Owner.TryResolveResourceHandleType(
                        state.Request.HandleType,
                        state.ResourcePath,
                        out resolvedHandleType)
                    ? &resolvedHandleType
                    : state.Request.HandleType;
                var resourceHash = unchecked((uint)ObjectResourcePathEncoding.ComputeResourceHash(state.ResourcePath, state.Request.Parameters));
                return (nint)state.Owner.CallOriginal(state.Request with
                {
                    HandleType = handleTypePointer,
                    ResourceHash = &resourceHash,
                    Path = pathPointer,
                });
            });

    private ResourceHandle* CallOriginalWithHash(ResourceRequest request, uint resourceHash)
        => CallOriginal(request with { ResourceHash = &resourceHash });

    private bool TryResolveResourceHandleType(
        ResourceHandleType* currentHandleType,
        string resourcePath,
        out ResourceHandleType resolvedHandleType)
    {
        resolvedHandleType = default;
        if (currentHandleType == null || _resolveResourceHandleType == null
            || ObjectScopedResourcePathUtility.IsRejectedPath(resourcePath))
        {
            return false;
        }

        string unscopedPath = ObjectScopedResourcePathUtility.Strip(resourcePath);
        string typePath = ObjectMemoryResourcePathUtility.GetGamePathOrSelf(unscopedPath);
        if (typePath.Length == 0 || ObjectLocalFilePathUtility.IsLocalFilePath(typePath))
        {
            return false;
        }

        resolvedHandleType = ObjectResourcePathEncoding.WithNullTerminatedUtf8(
            typePath,
            (Owner: this, HandleType: (nint)currentHandleType),
            static (pathPointer, _, state) =>
            {
                ResourceHandleType handleType = *(ResourceHandleType*)state.HandleType;
                state.Owner._resolveResourceHandleType!(&handleType, pathPointer);
                return handleType;
            });

        return resolvedHandleType.Value != uint.MaxValue;
    }

    internal void RegisterLoadedHandle(ResourceHandle* resourceHandle, ResolvedResourceLoad resolvedLoad, uint resourceType)
    {
        if (IsDisposing
            || resourceHandle == null
            || (!ShouldIsolateResourceCache(resourceType) && !ObjectMemoryResourcePathUtility.IsMemoryResourcePath(resolvedLoad.TrackedPath))
            || resolvedLoad.Collection.CollectionId.Length == 0
            || resolvedLoad.TrackedPath.Length == 0)
        {
            return;
        }

        try
        {
            _resourceTracker.RegisterOrUpdateHandleScope(
                (nint)resourceHandle,
                new ObjectResourceScope(resolvedLoad.Collection.CollectionId, resolvedLoad.TrackedPath, resolvedLoad.Collection.ResourceScopeId));
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "tracked resource handle registration", resourceHandle);
        }
    }

    private void TryRegisterSchedulerTimelineResource(SchedulerTimeline* timeline, ObjectCollectionResolveData collection)
    {
        try
        {
            RegisterSchedulerTimelineResource(timeline, collection);
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "scheduler timeline registration", ResolveSchedulerTimelineResourceHandle(timeline));
        }
    }

    private void RegisterSchedulerTimelineResource(SchedulerTimeline* timeline, ObjectCollectionResolveData collection)
    {
        if (IsDisposing || collection.Redirects.Count == 0)
        {
            return;
        }

        ResourceHandle* resourceHandle = ResolveSchedulerTimelineResourceHandle(timeline);
        if (resourceHandle == null)
        {
            return;
        }

        if (!ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string handlePath)
         || ObjectScopedResourcePathUtility.IsForeignScopedPath(handlePath))
        {
            return;
        }

        if (ObjectScopedResourcePathUtility.IsObjectScopedPath(handlePath)
         && (!ObjectScopedResourcePathUtility.TryParse(handlePath, out ObjectScopedResourcePath scopedPath)
             || scopedPath.ResourceScopeId != collection.ResourceScopeId))
        {
            return;
        }

        string resourcePath = ObjectResourcePathUtility.NormalizeTrackedPath(handlePath);
        if (resourcePath.Length == 0)
        {
            return;
        }

        _resourceTracker.RegisterOrUpdateHandleScope(
            (nint)resourceHandle,
            new ObjectResourceScope(collection.CollectionId, resourcePath, collection.ResourceScopeId));
    }

    private static ResourceHandle* ResolveSchedulerTimelineResourceHandle(SchedulerTimeline* timeline)
        => timeline == null || timeline->SchedulerResource == null
            ? null
            : timeline->SchedulerResource->Resource;

    private static bool DoesTrackedHandleMatch(nint resourceHandleAddress, ObjectResourceScope scope)
        => ObjectResourcePathEncoding.TryReadHandlePath((ResourceHandle*)resourceHandleAddress, out string handlePath)
            && scope.MatchesPath(handlePath);

    private static bool DoesTrackedSharedGroupInstanceMatch(SharedGroupLayoutInstance* instance, ObjectResourceScope scope)
    {
        if (instance == null)
        {
            return false;
        }

        var primaryPath = instance->GetPrimaryPath();
        if (!primaryPath.HasValue)
        {
            return false;
        }

        return scope.MatchesPath(primaryPath.ToString());
    }

    private bool IsOwnedSharedGroupResource(SharedGroupLayoutInstance* instance, ResourceHandle* handle)
    {
        _resourceTracker.TryGetInstanceScope((nint)instance, out _, out bool instanceTracked);
        _resourceTracker.TryGetHandleScope((nint)handle, out _, out bool handleTracked);
        return instanceTracked || handleTracked
            || (ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
                && ObjectResourcePathUtility.IsOwnedResourcePath(handlePath));
    }

    private void LogRejectedResourceRedirect(
        ScopedResourceRequest request,
        uint resourceType,
        RedirectResolution redirect)
    {
        ObjectResourceLog.Write(() => _logger.LogWarning(
            "object resource redirect rejected: {Reason}; collection={CollectionId}; revision={Revision}; requested={RequestedPath}; resolved={ResolvedPath}; resolvedKind={ResolvedKind}; resourceType=0x{ResourceType:X8}; scoped={WasScoped}",
            redirect.RejectionReason,
            request.Collection.CollectionId,
            request.Collection.Revision,
            request.RequestedPath,
            redirect.RejectedPath.Path,
            redirect.RejectedPath.Kind,
            resourceType,
            request.WasScoped));
    }

    private void LogRejectedHandleScope(string reason, nint address, string path, ObjectResourceScope scope = default)
        => ObjectResourceLog.Write(() => _logger.LogWarning("object resource callback rejected; reason={Reason}; address=0x{Address:X}; path={Path}; collection={CollectionId}; generation={ResourceScopeId}; expectedPath={ExpectedPath}",
            reason, (ulong)address, path, scope.ResourceCollectionId, scope.ResourceScopeId, scope.ResolvedPath));

    internal void LogNativeHookFailure(Exception exception, string hook, ResourceHandle* handle)
        => ObjectResourceLog.LogHookFailure(_logger, _resourceTracker, exception, hook, (nint)handle);

    private void LogNativePathHookFailure(Exception exception, string hook, byte* path)
    {
        nint pathAddress = (nint)path;
        ObjectResourceLog.Write(() => _logger.LogError(exception, "object resource {Hook} hook failed; pathAddress=0x{PathAddress:X}", hook, (ulong)pathAddress));
    }

    private ResourceHandle* CallOriginal(ResourceRequest request)
        => request.IsSync
            ? _hooks.GetResourceSyncHook!.OriginalDisposeSafe(
                request.ResourceManager,
                request.HandleType,
                request.ResourceType,
                request.ResourceHash,
                request.Path,
                request.Parameters,
                request.File,
                request.Line)
            : _hooks.GetResourceAsyncHook!.OriginalDisposeSafe(
                request.ResourceManager,
                request.HandleType,
                request.ResourceType,
                request.ResourceHash,
                request.Path,
                request.Parameters,
                request.HasHandleLock,
                request.File,
                request.Line);
}
