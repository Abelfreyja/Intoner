using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Base;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Instance;
using FFXIVClientStructs.FFXIV.Client.System.Scheduler.Resource;
using Intoner.Objects.Utils;
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

    private readonly record struct ScopedResourceRequest(
        ObjectCollectionResolveData Collection,
        string RequestedPath,
        bool WasScoped);

    private readonly record struct ResolvedResourceLoad(
        string ResourceCollectionId,
        string LoadPath,
        string HashPath,
        string TrackedPath);

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
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly Func<IObjectFileReadService> _fileReadServiceFactory;
    private readonly IObjectResourceTracker _resourceTracker;
    private readonly ObjectResourceLoadScope _loadScope;
    private readonly ObjectResourceIncRefGuard _incRefGuard;
    private readonly ResolveResourceHandleTypeDelegate? _resolveResourceHandleType;
    private readonly ObjectResourceHooks _hooks;
    private readonly ObjectDisposalState _disposeState = new();

    public ObjectResourceLoader(
        ILogger<ObjectResourceLoader> logger,
        IDataManager gameData,
        IObjectResolvedCollectionStore collectionStore,
        Func<IObjectFileReadService> fileReadServiceFactory,
        IObjectResourceTracker resourceTracker,
        ObjectResourceLoadScope loadScope,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _gameData = gameData;
        _collectionStore = collectionStore;
        _fileReadServiceFactory = fileReadServiceFactory;
        _resourceTracker = resourceTracker;
        _loadScope = loadScope;
        _incRefGuard = new ObjectResourceIncRefGuard(_logger);
        _resolveResourceHandleType = ObjectInteropHookUtility.CreateDelegate<ResolveResourceHandleTypeDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceHandleTypeFromPath);

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
        return normalizedCollectionId.Length == 0
            ? default(ObjectResourceLoadScopeToken)
            : EnterCollectionScopeToken(normalizedCollectionId);
    }

    public bool CanResolveCollectionResources(ObjectRootPathKind kind)
    {
        if (IsDisposing || !_hooks.CanResolveCollectionResources(kind))
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
        _incRefGuard.Dispose();
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private IObjectFileReadService FileReadService
        => _fileReadServiceFactory();

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
        using var scope = EnterRegisteredHandleScopeToken((nint)handle);
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
            using var scope = EnterRegisteredHandleScopeToken((nint)resourceHandle);
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
            using var scope = EnterBgObjectModelScopeToken(bgObject);
            return _hooks.BgObjectLoadAnimationDataHook!.Original(bgObject, modelPath);
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
            using ObjectResourceLoadScopeToken scope = EnterSharedGroupResourceLoadScopeToken(sharedGroup, handle);
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
            using ObjectResourceLoadScopeToken scope = EnterSharedGroupInstanceScopeToken(sharedGroup);
            _hooks.LayoutSharedGroupInsertObjectHook!.Original(instance, layoutInstance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "object shared group insert hook failed");
            _hooks.LayoutSharedGroupInsertObjectHook!.Original(instance, layoutInstance);
        }
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
            if (IsDisposing || !TryReadActiveCollectionId(out string activeCollectionId))
            {
                return _hooks.SchedulerTimelineLoadResourcesHook!.Original(timeline);
            }

            ObjectResourceLoadScopeToken scope = default;
            if (activeCollectionId.Length == 0)
            {
                scope = EnterSchedulerTimelineResourceScopeToken(timeline);
                _ = TryReadActiveCollectionId(out activeCollectionId);
            }

            try
            {
                ulong result = CallSchedulerTimelineLoadResourcesOriginal(timeline);
                if (!IsDisposing)
                {
                    TryRegisterSchedulerTimelineResource(timeline, activeCollectionId);
                }

                return result;
            }
            finally
            {
                scope.Dispose();
            }
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
                : CallOriginal(request);
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
            return CallOriginal(request);
        }

        if (!ObjectResourcePathEncoding.TryReadNativePath(request.Path, out string requestedPath)
            || ObjectScopedResourcePathUtility.IsForeignScopedPath(requestedPath))
        {
            return CallOriginal(request);
        }

        if (!TryResolveScopedResourceRequest(requestedPath, out ScopedResourceRequest scopedRequest))
        {
            return ObjectScopedResourcePathUtility.IsObjectScopedPath(requestedPath)
                ? null
                : CallOriginal(request);
        }

        using var scope = EnterCollectionScopeToken(scopedRequest.Collection.CollectionId);
        var redirectStatus = ResolveResourceLoad(
            scopedRequest,
            request.Type,
            out ResolvedResourceLoad resolvedLoad);
        if (redirectStatus == RedirectResolutionStatus.NotFound)
        {
            return CallOriginal(request);
        }

        if (redirectStatus == RedirectResolutionStatus.Rejected)
        {
            return null;
        }

        ResourceHandle* resourceHandle = CallOriginalWithPath(request, resolvedLoad.LoadPath, resolvedLoad.HashPath);
        RegisterLoadedHandle(resourceHandle, resolvedLoad);
        return resourceHandle;
    }

    private RedirectResolutionStatus ResolveResourceLoad(
        ScopedResourceRequest request,
        uint resourceType,
        out ResolvedResourceLoad resolvedLoad)
    {
        resolvedLoad = default;

        string normalizedRequestedPath = ObjectResourcePathUtility.NormalizeTrackedPath(request.RequestedPath);
        if (normalizedRequestedPath.Length == 0)
        {
            return RedirectResolutionStatus.NotFound;
        }

        bool requestedLocalFile = ObjectResourcePathUtility.IsLocalFilePath(normalizedRequestedPath);
        if (request.Collection.Redirects.Count == 0)
        {
            if (!request.WasScoped)
            {
                return RedirectResolutionStatus.NotFound;
            }

            resolvedLoad = new ResolvedResourceLoad(
                request.Collection.CollectionId,
                normalizedRequestedPath,
                CreateResourceHashPath(request.Collection, normalizedRequestedPath, resourceType),
                normalizedRequestedPath);
            return RedirectResolutionStatus.Resolved;
        }

        string loadPath = normalizedRequestedPath;
        string trackedPath = normalizedRequestedPath;
        if (!requestedLocalFile && request.Collection.TryResolvePath(normalizedRequestedPath, out ObjectResolvedPath redirectedPath))
        {
            if (!ObjectResourcePathUtility.IsSupportedRedirection(normalizedRequestedPath, redirectedPath))
            {
                return RedirectResolutionStatus.Rejected;
            }

            if (!ObjectResourcePathUtility.Exists(_gameData, redirectedPath))
            {
                return RedirectResolutionStatus.Rejected;
            }

            if (redirectedPath.IsLocalFile
                && !FileReadService.CanLoadLocalFilePath(redirectedPath.Path))
            {
                return RedirectResolutionStatus.Rejected;
            }

            if (redirectedPath.IsMemory
                && !FileReadService.CanLoadMemoryResourcePath(redirectedPath.Path))
            {
                return RedirectResolutionStatus.Rejected;
            }

            loadPath = redirectedPath.Path;
            trackedPath = redirectedPath.Path;
        }
        else if (!request.WasScoped && !ShouldIsolateResourceCache(resourceType))
        {
            return RedirectResolutionStatus.NotFound;
        }

        resolvedLoad = new ResolvedResourceLoad(
            request.Collection.CollectionId,
            loadPath,
            CreateResourceHashPath(request.Collection, loadPath, resourceType),
            trackedPath);
        return RedirectResolutionStatus.Resolved;
    }

    private bool TryResolveScopedResourceRequest(string requestedPath, out ScopedResourceRequest request)
    {
        request = default;
        if (ObjectScopedResourcePathUtility.TryParse(requestedPath, out ObjectScopedResourcePath scopedPath))
        {
            if (!_collectionStore.TryGetCollectionByResourceScopeId(scopedPath.ResourceScopeId, out ObjectCollectionResolveData scopedCollection))
            {
                return false;
            }

            request = new ScopedResourceRequest(scopedCollection, scopedPath.Path, WasScoped: true);
            return true;
        }

        if (!TryReadActiveCollectionId(out string activeCollectionId)
            || activeCollectionId.Length == 0
            || !_collectionStore.TryGetCollection(activeCollectionId, out ObjectCollectionResolveData collection))
        {
            return false;
        }

        request = new ScopedResourceRequest(collection, requestedPath, WasScoped: false);
        return true;
    }

    private ObjectResourceLoadScopeToken EnterRegisteredHandleScopeToken(nint resourceHandleAddress)
    {
        if (IsDisposing || resourceHandleAddress == nint.Zero)
        {
            return default;
        }

        if (TryEnterScopedHandleScope(resourceHandleAddress, out ObjectResourceLoadScopeToken scopedHandleScope))
        {
            return scopedHandleScope;
        }

        if (!_resourceTracker.TryGetHandleScope(resourceHandleAddress, out var handleScope))
        {
            return default;
        }

        if (!DoesTrackedHandleMatch(resourceHandleAddress, handleScope.ResolvedPath))
        {
            _resourceTracker.RemoveTrackedHandle(resourceHandleAddress);
            return default;
        }

        string resourceCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(handleScope.ResourceCollectionId);
        if (resourceCollectionId.Length == 0)
        {
            return default;
        }

        return EnterCollectionScopeToken(resourceCollectionId);
    }

    private bool TryEnterScopedHandleScope(nint resourceHandleAddress, out ObjectResourceLoadScopeToken scope)
    {
        scope = default;
        var resourceHandle = (ResourceHandle*)resourceHandleAddress;
        if (resourceHandle == null
            || !ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string handlePath)
            || !ObjectScopedResourcePathUtility.TryParse(handlePath, out ObjectScopedResourcePath scopedPath)
            || !_collectionStore.TryGetCollectionByResourceScopeId(scopedPath.ResourceScopeId, out ObjectCollectionResolveData collection))
        {
            return false;
        }

        scope = EnterCollectionScopeToken(collection.CollectionId);
        return true;
    }

    private ObjectResourceLoadScopeToken EnterSharedGroupInstanceScopeToken(SharedGroupLayoutInstance* instance)
    {
        if (instance == null)
        {
            return default;
        }

        if (TryEnterTrackedSharedGroupInstanceScope(instance, out ObjectResourceLoadScopeToken instanceScope))
        {
            return instanceScope;
        }

        return EnterRegisteredHandleScopeToken((nint)instance->ResourceHandle);
    }

    private ObjectResourceLoadScopeToken EnterSharedGroupResourceLoadScopeToken(SharedGroupLayoutInstance* instance, ResourceHandle* handle)
    {
        ObjectResourceLoadScopeToken scope = EnterSharedGroupInstanceScopeToken(instance);
        return scope.IsActive
            ? scope
            : EnterRegisteredHandleScopeToken((nint)handle);
    }

    private static SharedGroupLayoutInstance* ResolveSharedGroupInstance(ResourceEventListener* listener)
        => listener == null
            ? null
            : (SharedGroupLayoutInstance*)((byte*)listener - SharedGroupResourceEventListenerOffset);

    private bool TryEnterTrackedSharedGroupInstanceScope(
        SharedGroupLayoutInstance* instance,
        out ObjectResourceLoadScopeToken scope)
    {
        scope = default;
        if (instance == null || !_resourceTracker.TryGetInstanceScope((nint)instance, out var instanceScope))
        {
            return false;
        }

        if (!DoesTrackedSharedGroupInstanceMatch(instance, instanceScope.ResolvedPath))
        {
            _resourceTracker.RemoveTrackedInstance((nint)instance);
            return false;
        }

        string resourceCollectionId = ObjectCollectionKeyUtility.NormalizeCollectionId(instanceScope.ResourceCollectionId);
        if (resourceCollectionId.Length == 0)
        {
            return false;
        }

        scope = EnterCollectionScopeToken(resourceCollectionId);
        return scope.IsActive;
    }

    private ObjectResourceLoadScopeToken EnterBgObjectModelScopeToken(SceneBgObject* bgObject)
        => bgObject == null
            ? default
            : EnterRegisteredHandleScopeToken((nint)bgObject->ModelResourceHandle);

    private ObjectResourceLoadScopeToken EnterSchedulerTimelineResourceScopeToken(SchedulerTimeline* timeline)
    {
        ResourceHandle* resourceHandle = ResolveSchedulerTimelineResourceHandle(timeline);
        return resourceHandle == null
            ? default
            : EnterRegisteredHandleScopeToken((nint)resourceHandle);
    }

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

    private static string CreateResourceHashPath(ObjectCollectionResolveData collection, string loadPath, uint resourceType)
    {
        // isolate dependency handles without exposing private scope paths to file readers
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

    private ResourceHandle* CallOriginalWithPath(ResourceRequest request, string resourcePath, string hashPath)
        => (ResourceHandle*)ObjectResourcePathEncoding.WithNullTerminatedUtf8(
            resourcePath,
            (Owner: this, Request: request, ResourcePath: resourcePath, HashPath: hashPath),
            static (pathPointer, _, state) =>
            {
                ResourceHandleType resolvedHandleType = default;
                ResourceHandleType* handleTypePointer = state.Owner.TryResolveResourceHandleType(
                    state.Request.HandleType,
                    state.ResourcePath,
                    out resolvedHandleType)
                    ? &resolvedHandleType
                    : state.Request.HandleType;
                var resourceHash = unchecked((uint)ComputeResourceHash(state.HashPath, state.Request.Parameters));
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
        if (typePath.Length == 0 || ObjectResourcePathUtility.IsLocalFilePath(typePath))
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

    private void RegisterLoadedHandle(ResourceHandle* resourceHandle, ResolvedResourceLoad resolvedLoad)
    {
        if (IsDisposing
            || resourceHandle == null
            || resolvedLoad.ResourceCollectionId.Length == 0
            || resolvedLoad.TrackedPath.Length == 0)
        {
            return;
        }

        try
        {
            _resourceTracker.RegisterOrUpdateHandleScope(
                (nint)resourceHandle,
                new ObjectResourceScope(resolvedLoad.ResourceCollectionId, resolvedLoad.TrackedPath));
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "tracked resource handle registration", resourceHandle);
        }
    }

    private void TryRegisterSchedulerTimelineResource(SchedulerTimeline* timeline, string resourceCollectionId)
    {
        try
        {
            RegisterSchedulerTimelineResource(timeline, resourceCollectionId);
        }
        catch (Exception ex)
        {
            LogNativeHookFailure(ex, "scheduler timeline registration", ResolveSchedulerTimelineResourceHandle(timeline));
        }
    }

    private void RegisterSchedulerTimelineResource(SchedulerTimeline* timeline, string resourceCollectionId)
    {
        if (IsDisposing || resourceCollectionId.Length == 0)
        {
            return;
        }

        ResourceHandle* resourceHandle = ResolveSchedulerTimelineResourceHandle(timeline);
        if (resourceHandle == null)
        {
            return;
        }

        if (!ObjectResourcePathEncoding.TryReadHandlePath(resourceHandle, out string handlePath))
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
            new ObjectResourceScope(resourceCollectionId, resourcePath));
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

        var currentPath = ObjectResourcePathUtility.NormalizeTrackedPath(handlePath);
        return currentPath.Length > 0
            && string.Equals(currentPath, ObjectResourcePathUtility.NormalizeTrackedPath(resolvedPath), StringComparison.OrdinalIgnoreCase);
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

    private bool IsObjectScopedHandle(ResourceHandle* handle)
        => ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            && ObjectScopedResourcePathUtility.IsObjectScopedPath(handlePath);

    private void LogNativeHookFailure(Exception exception, string hook, ResourceHandle* handle)
    {
        string path = ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            ? handlePath
            : string.Empty;
        _logger.LogError(exception, "object resource {Hook} hook failed; path={Path}", hook, path);
    }

    private void LogNativePathHookFailure(Exception exception, string hook, byte* path)
    {
        string requestedPath = ObjectResourcePathEncoding.TryReadNativePath(path, out string nativePath)
            ? nativePath
            : string.Empty;
        _logger.LogError(exception, "object resource {Hook} hook failed; path={Path}", hook, requestedPath);
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

