using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Instance;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Resource;
using Intoner.Objects.Utils;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using Penumbra.String;
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
    /// Enters one temporary object resource load scope for the given collection id.
    /// Nested native resource requests during that scope may resolve through the object collection.
    /// </summary>
    /// <param name="resourceCollectionId">the active object collection id</param>
    /// <returns>a disposable scope that restores the previous object collection context</returns>
    IDisposable EnterRootLoadScope(string resourceCollectionId);

    /// <summary>
    /// Enters one temporary root cache isolation scope that keeps the real game path but prevents the game's native cache reuse.
    /// </summary>
    /// <param name="rootPath">the game resource path being passed to native creation</param>
    /// <returns>a disposable scope that restores the previous root cache isolation context</returns>
    IDisposable EnterRootCacheIsolation(string rootPath);
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
    }

    private readonly record struct ScopedResourceRequest(
        ObjectCollectionResolveData Collection,
        string RequestedPath,
        bool WasScoped);

    internal readonly record struct ResolvedResourceLoad(
        ObjectCollectionResolveData Collection,
        string HandlePath,
        string TrackedPath);

    private sealed class RootCacheIsolation(
        string path,
        long loadId,
        RootCacheIsolation? previousScope)
    {
        private int _threadScopeActive = 1;
        private int _disposed;

        public string Path { get; } = path;
        public long LoadId { get; } = loadId;
        public RootCacheIsolation? PreviousScope { get; } = previousScope;

        public bool IsActive
            => Volatile.Read(ref _disposed) == 0
            && Path.Length > 0
            && LoadId > 0;

        public void RestoreThreadScope(ObjectResourceLoader owner)
        {
            if (Interlocked.Exchange(ref _threadScopeActive, 0) != 0)
            {
                owner.TryWriteRootCacheIsolation(PreviousScope);
            }
        }

        public void Release(ObjectResourceLoader owner)
        {
            RestoreThreadScope(owner);
            Deactivate();
        }

        public void Deactivate()
            => _ = Interlocked.Exchange(ref _disposed, 1);
    }

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

    private readonly struct ResourceRequest(
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
        public readonly bool IsSync = isSync;
        public readonly ResourceManager* ResourceManager = resourceManager;
        public readonly ResourceHandleType* HandleType = handleType;
        public readonly uint* ResourceType = resourceType;
        public readonly uint* ResourceHash = resourceHash;
        public readonly byte* Path = path;
        public readonly ObjectGetResourceParameters* Parameters = parameters;
        public readonly byte HasHandleLock = hasHandleLock;
        public readonly byte* File = file;
        public readonly uint Line = line;

        public bool HasLockedHandle
            => HasHandleLock != 0;

        public uint Type
            => ResourceType == null ? 0 : *ResourceType;
    }

    private enum ResourceType : uint
    {
        Atex = 0x61746578,
        Avfx = 0x61766678,
        Eid = 0x00656964,
        Mdl = 0x006D646C,
        Mtrl = 0x6D74726C,
        Pap = 0x00706170,
        Scd = 0x00736364,
        Sgb = 0x00736762,
        Shpk = 0x7368706B,
        Sklb = 0x736B6C62,
        Tex = 0x00746578,
        Tmb = 0x00746D62,
    }

    private const int SharedGroupResourceEventListenerOffset = 0x30;

    private readonly ILogger<ObjectResourceLoader> _logger;
    private readonly IDataManager _gameData;
    private readonly ObjectFileReadService _fileReadService;
    private readonly ObjectResourceTracker _resourceTracker;
    private readonly ObjectResourceLoadScope _loadScope;
    private readonly ThreadLocal<RootCacheIsolation?> _rootCacheIsolation = new(static () => default);
    private readonly ObjectResourceIncRefGuard _incRefGuard;
    private readonly ResolveResourceHandleTypeDelegate? _resolveResourceHandleType;
    private readonly ObjectResourceHooks _hooks;
    private readonly DisposalState _disposeState = new();
    private long _nextRootCacheIsolationId;

    public ObjectResourceLoader(
        ILogger<ObjectResourceLoader> logger,
        IDataManager gameData,
        ObjectFileReadService fileReadService,
        ObjectResourceTracker resourceTracker,
        ObjectResourceLoadScope loadScope,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _gameData = gameData;
        _fileReadService = fileReadService;
        _resourceTracker = resourceTracker;
        _loadScope = loadScope;
        _incRefGuard = new ObjectResourceIncRefGuard(_logger);
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

    public IDisposable EnterRootLoadScope(string resourceCollectionId)
    {
        if (IsDisposing)
        {
            return default(ObjectResourceLoadScopeToken);
        }

        string normalizedCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(resourceCollectionId);
        ObjectResourceLoadScopeToken scope = normalizedCollectionId.Length == 0
            ? _loadScope.Suspend()
            : EnterCollectionScopeToken(normalizedCollectionId);
        _logger.LogDebug("object resource root scope entered; collection={CollectionId}; generation={ResourceScopeId}; revision={Revision}; active={Active}",
            normalizedCollectionId, scope.Collection?.ResourceScopeId ?? 0, scope.Collection?.Revision ?? 0, scope.IsActive);
        return scope;
    }

    public IDisposable EnterRootCacheIsolation(string rootPath)
    {
        if (IsDisposing
         || !_hooks.CanResolveResourceRequests()
         || !TryNormalizeRootCacheIsolationPath(rootPath, out string normalizedPath)
         || !ObjectThreadLocalUtility.TryRead(_rootCacheIsolation, null, out RootCacheIsolation? previousScope))
        {
            return default(RootCacheIsolationScopeToken);
        }

        _hooks.Enable();
        var isolation = new RootCacheIsolation(
            normalizedPath,
            Interlocked.Increment(ref _nextRootCacheIsolationId),
            previousScope);
        if (TryWriteRootCacheIsolation(isolation))
        {
            return new RootCacheIsolationScopeToken(this, isolation);
        }

        isolation.Deactivate();
        return default(RootCacheIsolationScopeToken);
    }

    private bool TryWriteRootCacheIsolation(RootCacheIsolation? isolation)
    {
        if (IsDisposing)
        {
            return false;
        }

        return ObjectThreadLocalUtility.TryWrite(_rootCacheIsolation, isolation);
    }

    public bool CanResolveCollectionResources(ObjectRootPathKind kind)
    {
        if (IsDisposing
            || !_hooks.CanResolveCollectionResources(kind)
            || !_fileReadService.CanRouteScopedGamePaths())
        {
            return false;
        }

        _hooks.Enable();
        return true;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        _hooks.Dispose();
        _rootCacheIsolation.Dispose();
        _incRefGuard.Dispose();
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
        TState state,
        ObjectResourcePathEncoding.TemporaryHandlePathAction<TState, TResult> callOriginal)
    {
        using var scope = EnterRegisteredHandleScopeToken((nint)handle, out bool canLoad);
        if (!canLoad)
        {
            return default!;
        }

        if (!ObjectResourcePathEncoding.TryReadActualScopedHandlePath(handle, out string actualPath))
        {
            return callOriginal(state);
        }

        return ObjectResourcePathEncoding.WithTemporaryHandlePath(
            handle,
            actualPath,
            state,
            callOriginal);
    }

    private byte ModelResourceLoadDetour(ModelResourceHandle* handle, void* contents, byte flag)
    {
        try
        {
            var resourceHandle = (ResourceHandle*)handle;
            using var scope = EnterRegisteredHandleScopeToken((nint)resourceHandle, out bool canLoad);
            if (!canLoad)
            {
                return ObjectModelResourceLoadGuard.FailureResult;
            }

            bool hasScopedPath = ObjectResourcePathEncoding.TryReadActualScopedHandlePath(resourceHandle, out string actualPath);
            if ((scope.IsActive || hasScopedPath) && !TryValidateModelResource(handle, resourceHandle, actualPath))
            {
                return ObjectModelResourceLoadGuard.FailureResult;
            }

            if (!hasScopedPath)
            {
                return _hooks.ModelResourceLoadHook!.Original(handle, contents, flag);
            }

            return ObjectResourcePathEncoding.WithTemporaryHandlePath(
                resourceHandle,
                actualPath,
                (Hooks: _hooks, Handle: (nint)handle, Contents: (nint)contents, Flag: flag),
                static state => state.Hooks.ModelResourceLoadHook!.Original(
                    (ModelResourceHandle*)state.Handle,
                    (void*)state.Contents,
                    state.Flag));
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "model resource load", (ResourceHandle*)handle);
            return IsObjectScopedHandle((ResourceHandle*)handle)
                ? ObjectModelResourceLoadGuard.FailureResult
                : _hooks.ModelResourceLoadHook!.Original(handle, contents, flag);
        }
    }

    private bool ModelResourceLoadMaterialsDetour(ModelResourceHandle* handle)
    {
        try
        {
            return CallWithRegisteredResourceScope(
                (ResourceHandle*)handle,
                (Hooks: _hooks, Handle: (nint)handle),
                static state => state.Hooks.ModelResourceLoadMaterialsHook!.Original((ModelResourceHandle*)state.Handle));
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "model material load", (ResourceHandle*)handle);
            return !IsObjectScopedHandle((ResourceHandle*)handle)
                && _hooks.ModelResourceLoadMaterialsHook!.Original(handle);
        }
    }

    private byte ApricotResourceLoadDetour(ResourceHandle* handle, nint unknown0, byte flag)
    {
        try
        {
            return CallWithRegisteredResourceScope(
                handle,
                (Hooks: _hooks, Handle: (nint)handle, Unknown0: unknown0, Flag: flag),
                static state => state.Hooks.ApricotResourceLoadHook!.Original(
                    (ResourceHandle*)state.Handle,
                    state.Unknown0,
                    state.Flag));
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "apricot resource load", handle);
            return IsObjectScopedHandle(handle)
                ? (byte)0
                : _hooks.ApricotResourceLoadHook!.Original(handle, unknown0, flag);
        }
    }

    private bool BgObjectLoadAnimationDataDetour(SceneBgObject* bgObject, byte* modelPath)
    {
        try
        {
            using var scope = EnterBgObjectModelScopeToken(bgObject, out bool canLoad);
            return canLoad && _hooks.BgObjectLoadAnimationDataHook!.Original(bgObject, modelPath);
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "bgobject animation load", bgObject == null ? null : (ResourceHandle*)bgObject->ModelResourceHandle);
            return _hooks.BgObjectLoadAnimationDataHook!.Original(bgObject, modelPath);
        }
    }

    private byte MaterialResourceLoadTexFilesDetour(MaterialResourceHandle* handle)
    {
        try
        {
            return CallWithRegisteredResourceScope(
                (ResourceHandle*)handle,
                (Hooks: _hooks, Handle: (nint)handle),
                static state => state.Hooks.MaterialResourceLoadTexFilesHook!.Original((MaterialResourceHandle*)state.Handle));
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "material texture load", (ResourceHandle*)handle);
            return IsObjectScopedHandle((ResourceHandle*)handle)
                ? (byte)0
                : _hooks.MaterialResourceLoadTexFilesHook!.Original(handle);
        }
    }

    private byte MaterialResourceLoadShpkFilesDetour(MaterialResourceHandle* handle)
    {
        try
        {
            return CallWithRegisteredResourceScope(
                (ResourceHandle*)handle,
                (Hooks: _hooks, Handle: (nint)handle),
                static state => state.Hooks.MaterialResourceLoadShpkFilesHook!.Original((MaterialResourceHandle*)state.Handle));
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "material shader load", (ResourceHandle*)handle);
            return IsObjectScopedHandle((ResourceHandle*)handle)
                ? (byte)0
                : _hooks.MaterialResourceLoadShpkFilesHook!.Original(handle);
        }
    }

    private void SharedGroupLayoutResourceLoadDetour(ResourceEventListener* listener, ResourceHandle* handle)
    {
        try
        {
            SharedGroupLayoutInstance* sharedGroup = ResolveSharedGroupInstance(listener);
            using ObjectResourceLoadScopeToken scope = EnterSharedGroupResourceLoadScopeToken(sharedGroup, handle, out bool canLoad);
            if (!canLoad)
            {
                return;
            }

            if (!ObjectResourcePathEncoding.TryReadActualScopedHandlePath(handle, out string actualPath))
            {
                _hooks.SharedGroupLayoutResourceLoadHook!.Original(listener, handle);
                return;
            }

            _ = ObjectResourcePathEncoding.WithTemporaryHandlePath(
                handle,
                actualPath,
                (Hooks: _hooks, Listener: (nint)listener, Handle: (nint)handle),
                static state =>
                {
                    state.Hooks.SharedGroupLayoutResourceLoadHook!.Original(
                        (ResourceEventListener*)state.Listener,
                        (ResourceHandle*)state.Handle);
                    return true;
                });
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "shared group resource load", handle);
            if (!IsObjectScopedHandle(handle))
            {
                _hooks.SharedGroupLayoutResourceLoadHook!.Original(listener, handle);
            }
        }
    }

    private void LayoutSharedGroupInsertObjectDetour(LayoutSharedGroupObject* instance, ILayoutInstance* layoutInstance)
    {
        try
        {
            SharedGroupLayoutInstance* sharedGroup = instance != null ? instance->Instance : null;
            using ObjectResourceLoadScopeToken scope = EnterSharedGroupInstanceScopeToken(sharedGroup, out bool canLoad);
            if (canLoad)
            {
                _hooks.LayoutSharedGroupInsertObjectHook!.Original(instance, layoutInstance);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "object shared group insert hook failed");
            _hooks.LayoutSharedGroupInsertObjectHook!.Original(instance, layoutInstance);
        }
    }

    private nint ResourceHandleDestructorDetour(ResourceHandle* handle)
    {
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

    private nint ResourceHandleIncRefDetour(ResourceHandle* handle)
    {
        try
        {
            if (IsDisposing || handle == null || handle->RefCount != 0)
            {
                return _hooks.ResourceHandleIncRefHook!.OriginalDisposeSafe(handle);
            }

            using var scope = _incRefGuard.EnterScope();
            return _hooks.ResourceHandleIncRefHook!.OriginalDisposeSafe(handle);
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "resource handle inc ref", handle);
            return _hooks.ResourceHandleIncRefHook!.OriginalDisposeSafe(handle);
        }
    }

    private ulong SchedulerTimelineLoadResourcesDetour(SchedulerTimeline* timeline)
    {
        try
        {
            if (IsDisposing)
            {
                return _hooks.SchedulerTimelineLoadResourcesHook!.Original(timeline);
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
            return _hooks.SchedulerTimelineLoadResourcesHook!.Original(timeline);
        }
    }

    private SchedulerResource* GetCachedScheduleResourceDetour(
        SchedulerResourceManagement* resourceManagement,
        ScheduleResourceLoadData* loadData,
        byte useMap)
    {
        try
        {
            if (TryReadActiveCollectionId(out string activeCollectionId) && activeCollectionId.Length > 0)
            {
                return null;
            }

            return _hooks.GetCachedScheduleResourceHook!.Original(resourceManagement, loadData, useMap);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "object scheduler cache hook failed");
            return _hooks.GetCachedScheduleResourceHook!.Original(resourceManagement, loadData, useMap);
        }
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

    private ResourceHandle* GetResourceDetour(ResourceRequest request)
    {
        try
        {
            return GetResourceDetourCore(request);
        }
        catch (Exception ex)
        {
            LogNativePathHookFailure(ex, "resource request", request.Path);
            return ObjectResourcePathEncoding.TryReadNativePath(request.Path, out string requestedPath)
                && ObjectScopedResourcePathUtility.IsObjectScopedPath(requestedPath)
                ? null
                : CallUnscopedOriginal(request);
        }
    }

    private ResourceHandle* GetResourceDetourCore(ResourceRequest request)
    {
        if (IsDisposing
            || _incRefGuard.ShouldBypassRedirect(
                request.IsSync,
                request.HandleType,
                request.ResourceType,
                request.ResourceHash,
                request.Path,
                request.Parameters,
                request.HasLockedHandle))
        {
            return CallUnscopedOriginal(request);
        }

        if (!ObjectResourcePathEncoding.TryReadNativePath(request.Path, out string requestedPath)
            || ObjectScopedResourcePathUtility.IsForeignScopedPath(requestedPath))
        {
            return CallUnscopedOriginal(request);
        }

        if (TryResolveRootCacheIsolation(requestedPath, request, out uint cacheIsolationHash))
        {
            _logger.LogDebug("using temporary root resource hash for {Path}: 0x{ResourceHash:X8}", requestedPath, cacheIsolationHash);
            return CallOriginalWithHash(request, cacheIsolationHash);
        }

        using ObjectResourceLoadScopeToken scope = EnterResourceRequestScope(requestedPath, out ScopedResourceRequest scopedRequest);
        if (!scope.IsActive)
        {
            return ObjectScopedResourcePathUtility.IsObjectScopedPath(requestedPath)
                ? null
                : CallUnscopedOriginal(request);
        }

        RedirectResolution redirect = ResolveResourceLoad(scopedRequest, request.Type);
        if (redirect.Status == RedirectResolutionStatus.NotFound)
        {
            return CallUnscopedOriginal(request);
        }

        if (redirect.Status == RedirectResolutionStatus.Rejected)
        {
            LogRejectedResourceRedirect(scopedRequest, request.Type, redirect);
            return null;
        }

        ResourceHandle* resourceHandle = CallOriginalWithPath(request, redirect.ResolvedLoad.HandlePath);
        if (resourceHandle == null)
        {
            _logger.LogWarning("object resource request returned null; collection={CollectionId}; generation={ResourceScopeId}; requested={RequestedPath}; resolved={ResolvedPath}; resourceType=0x{ResourceType:X8}",
                scopedRequest.Collection.CollectionId, scopedRequest.Collection.ResourceScopeId, scopedRequest.RequestedPath,
                redirect.ResolvedLoad.TrackedPath, request.Type);
        }

        RegisterLoadedHandle(resourceHandle, redirect.ResolvedLoad, request.Type);
        return resourceHandle;
    }

    private ResourceHandle* CallUnscopedOriginal(ResourceRequest request)
    {
        using ObjectResourceLoadScopeToken scope = _loadScope.Suspend();
        return CallOriginal(request);
    }

    private RedirectResolution ResolveResourceLoad(ScopedResourceRequest request, uint resourceType)
    {
        string normalizedRequestedPath = ObjectResourcePathUtility.NormalizeTrackedPath(request.RequestedPath);
        if (normalizedRequestedPath.Length == 0)
        {
            return RedirectResolution.NotFound();
        }

        bool requestedLocalFile = ObjectLocalFilePathUtility.IsLocalFilePath(normalizedRequestedPath);
        if (request.Collection.Redirects.Count == 0)
        {
            if (!request.WasScoped)
            {
                return RedirectResolution.NotFound();
            }

            return RedirectResolution.Resolved(new ResolvedResourceLoad(
                request.Collection,
                CreateResourceHandlePath(request.Collection, normalizedRequestedPath, resourceType),
                normalizedRequestedPath));
        }

        string loadPath = normalizedRequestedPath;
        string trackedPath = normalizedRequestedPath;
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
                && !_fileReadService.CanLoadLocalFilePath(redirectedPath.Path))
            {
                return RedirectResolution.Rejected(RedirectRejectionReason.UnsupportedLocalFile, redirectedPath);
            }

            if (redirectedPath.IsMemory
                && !_fileReadService.CanLoadMemoryResourcePath(redirectedPath.Path))
            {
                return RedirectResolution.Rejected(RedirectRejectionReason.UnsupportedMemoryResource, redirectedPath);
            }

            loadPath = redirectedPath.Path;
            trackedPath = redirectedPath.Path;
        }
        else if (!request.WasScoped && !ShouldIsolateResourceCache(resourceType))
        {
            return RedirectResolution.NotFound();
        }

        return RedirectResolution.Resolved(new ResolvedResourceLoad(
            request.Collection,
            CreateResourceHandlePath(request.Collection, loadPath, resourceType),
            trackedPath));
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
         || ObjectScopedResourcePathUtility.IsObjectScopedPath(requestedPath)
         || !ObjectThreadLocalUtility.TryRead(_rootCacheIsolation, null, out RootCacheIsolation? isolation)
         || isolation is null
         || !isolation.IsActive)
        {
            return false;
        }

        string normalizedRequestedPath = ObjectResourcePathUtility.NormalizeTrackedPath(requestedPath);
        if (normalizedRequestedPath.Length == 0
         || !string.Equals(normalizedRequestedPath, isolation.Path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resourceHash = unchecked((uint)ComputeRootCacheIsolationHash(normalizedRequestedPath, isolation.LoadId, request.Parameters));
        isolation.RestoreThreadScope(this);
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

        if (!DoesTrackedHandleMatch(resourceHandleAddress, handleScope.ResolvedPath))
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

    internal ObjectResourceLoadScopeToken EnterSharedGroupResourceLoadScopeToken(SharedGroupLayoutInstance* instance, ResourceHandle* handle, out bool canLoad)
    {
        if (instance == null || (ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
         && (ObjectScopedResourcePathUtility.IsObjectScopedPath(handlePath)
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

        if (!DoesTrackedSharedGroupInstanceMatch(instance, instanceScope.ResolvedPath))
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

    private bool TryValidateModelResource(ModelResourceHandle* handle, ResourceHandle* resourceHandle, string actualPath)
    {
        ObjectModelResourceValidationResult validation = ObjectModelResourceLoadGuard.Validate(handle);
        if (validation.IsValid)
        {
            return true;
        }

        string handlePath = ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string currentPath)
            ? currentPath
            : string.Empty;
        string collectionId = TryReadActiveCollectionId(out string activeCollectionId)
            ? activeCollectionId
            : string.Empty;
        _logger.LogWarning(
            "object model resource load rejected: {Reason}; collection={CollectionId}; path={Path}; actual={ActualPath}; length={Length}; version=0x{Version:X8}",
            validation.Reason,
            collectionId,
            handlePath,
            actualPath,
            validation.Length,
            validation.Version);
        return false;
    }

    private ulong CallSchedulerTimelineLoadResourcesOriginal(SchedulerTimeline* timeline)
    {
        ResourceHandle* resourceHandle = ResolveSchedulerTimelineResourceHandle(timeline);
        if (!ObjectResourcePathEncoding.TryReadActualScopedHandlePath(resourceHandle, out string actualPath))
        {
            return _hooks.SchedulerTimelineLoadResourcesHook!.Original(timeline);
        }

        return ObjectResourcePathEncoding.WithTemporaryHandlePath(
            resourceHandle,
            actualPath,
            (Hooks: _hooks, Timeline: (nint)timeline),
            static state => state.Hooks.SchedulerTimelineLoadResourcesHook!.Original((SchedulerTimeline*)state.Timeline));
    }

    private static bool ShouldIsolateResourceCache(uint resourceType)
        => (ResourceType)resourceType is ResourceType.Avfx
            or ResourceType.Mdl
            or ResourceType.Mtrl
            or ResourceType.Sgb
            or ResourceType.Tmb;

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

    private static int ComputeResourceHash(string path, ObjectGetResourceParameters* getResourceParameters)
    {
        if (!CiByteString.FromString(path, out var gamePath, MetaDataComputation.Crc32))
        {
            throw new InvalidOperationException($"could not encode redirected resource path '{path}'");
        }

        try
        {
            if (getResourceParameters == null || !getResourceParameters->IsPartialRead)
            {
                return gamePath.Crc32;
            }

            var partialPath = string.Concat(
                path,
                ".",
                getResourceParameters->SegmentOffset.ToString("x", CultureInfo.InvariantCulture),
                ".",
                getResourceParameters->SegmentLength.ToString("x", CultureInfo.InvariantCulture));
            if (!CiByteString.FromString(partialPath, out var partialGamePath, MetaDataComputation.Crc32))
            {
                return gamePath.Crc32;
            }

            try
            {
                return partialGamePath.Crc32;
            }
            finally
            {
                partialGamePath.Dispose();
            }
        }
        finally
        {
            gamePath.Dispose();
        }
    }

    private static int ComputeRootCacheIsolationHash(string path, long loadId, ObjectGetResourceParameters* getResourceParameters)
    {
        string isolatedPath = string.Concat(path, ".intoner.", loadId.ToString("x", CultureInfo.InvariantCulture));
        return ComputeResourceHash(isolatedPath, getResourceParameters);
    }

    private ResourceHandle* CallOriginalWithPath(ResourceRequest request, string resourcePath)
        => (ResourceHandle*)ObjectResourcePathEncoding.WithNullTerminatedUtf8(
            resourcePath,
            (Owner: this, Request: request, ResourcePath: resourcePath),
            static (pathPointer, _, state) =>
            {
                ResourceHandleType resolvedHandleType = default;
                ResourceHandleType* handleTypePointer = state.Owner.TryResolveResourceHandleType(
                    state.Request.HandleType,
                    state.ResourcePath,
                    out resolvedHandleType)
                    ? &resolvedHandleType
                    : state.Request.HandleType;
                var resourceHash = unchecked((uint)ComputeResourceHash(state.ResourcePath, state.Request.Parameters));
                return (nint)state.Owner.CallOriginal(new ResourceRequest(
                    state.Request.IsSync,
                    state.Request.ResourceManager,
                    handleTypePointer,
                    state.Request.ResourceType,
                    &resourceHash,
                    pathPointer,
                    state.Request.Parameters,
                    state.Request.HasHandleLock,
                    state.Request.File,
                    state.Request.Line));
            });

    private ResourceHandle* CallOriginalWithHash(ResourceRequest request, uint resourceHash)
        => CallOriginal(new ResourceRequest(
            request.IsSync,
            request.ResourceManager,
            request.HandleType,
            request.ResourceType,
            &resourceHash,
            request.Path,
            request.Parameters,
            request.HasHandleLock,
            request.File,
            request.Line));

    private bool TryResolveResourceHandleType(
        ResourceHandleType* currentHandleType,
        string resourcePath,
        out ResourceHandleType resolvedHandleType)
    {
        resolvedHandleType = default;
        if (currentHandleType == null || _resolveResourceHandleType == null)
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
        if (IsDisposing)
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

    private static bool DoesTrackedHandleMatch(nint resourceHandleAddress, string resolvedPath)
    {
        var resourceHandle = (ResourceHandle*)resourceHandleAddress;
        if (resourceHandle == null)
        {
            return false;
        }

        if (!ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string handlePath))
        {
            return false;
        }

        string currentPath = ObjectResourcePathUtility.NormalizeTrackedPath(handlePath);
        string trackedPath = ObjectResourcePathUtility.NormalizeTrackedPath(resolvedPath);
        if (currentPath.Length == 0)
        {
            return false;
        }

        return string.Equals(currentPath, trackedPath, StringComparison.OrdinalIgnoreCase)
            || (ObjectMemoryResourcePathUtility.TryParse(trackedPath, out ObjectMemoryResourcePath memoryPath)
                && string.Equals(currentPath, memoryPath.GamePath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool DoesTrackedSharedGroupInstanceMatch(SharedGroupLayoutInstance* instance, string resolvedPath)
    {
        if (instance == null || string.IsNullOrWhiteSpace(resolvedPath))
        {
            return false;
        }

        var primaryPath = instance->GetPrimaryPath();
        if (!primaryPath.HasValue)
        {
            return false;
        }

        string currentPath = ObjectResourcePathUtility.NormalizeTrackedPath(primaryPath.ToString());
        return currentPath.Length > 0
            && string.Equals(currentPath, ObjectResourcePathUtility.NormalizeTrackedPath(resolvedPath), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsObjectScopedHandle(ResourceHandle* handle)
        => ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            && ObjectScopedResourcePathUtility.IsObjectScopedPath(handlePath);

    private void LogRejectedResourceRedirect(
        ScopedResourceRequest request,
        uint resourceType,
        RedirectResolution redirect)
    {
        _logger.LogWarning(
            "object resource redirect rejected: {Reason}; collection={CollectionId}; revision={Revision}; requested={RequestedPath}; resolved={ResolvedPath}; resolvedKind={ResolvedKind}; resourceType=0x{ResourceType:X8}; scoped={WasScoped}",
            redirect.RejectionReason,
            request.Collection.CollectionId,
            request.Collection.Revision,
            request.RequestedPath,
            redirect.RejectedPath.Path,
            redirect.RejectedPath.Kind,
            resourceType,
            request.WasScoped);
    }

    private void LogRejectedHandleScope(string reason, nint address, string path, ObjectResourceScope scope = default)
        => _logger.LogWarning("object resource callback rejected; reason={Reason}; address=0x{Address:X}; path={Path}; collection={CollectionId}; generation={ResourceScopeId}; expectedPath={ExpectedPath}",
            reason, (ulong)address, path, scope.ResourceCollectionId, scope.ResourceScopeId, scope.ResolvedPath);

    internal void LogNativeHookFailure(Exception exception, string hook, ResourceHandle* handle)
        => ObjectResourceLog.LogHookFailure(_logger, _resourceTracker, exception, hook, (nint)handle);

    private void LogNativePathHookFailure(Exception exception, string hook, byte* path)
    {
        _logger.LogError(exception, "object resource {Hook} hook failed; pathAddress=0x{PathAddress:X}", hook, (ulong)path);
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

    private readonly struct RootCacheIsolationScopeToken(ObjectResourceLoader? owner, RootCacheIsolation? isolation) : IDisposable
    {
        public void Dispose()
        {
            if (owner is not null && isolation is not null)
            {
                isolation.Release(owner);
            }
        }
    }
}

