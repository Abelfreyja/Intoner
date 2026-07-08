using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Assets;
using Intoner.Objects.Interop;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using Penumbra.String.Classes;
using ClientFileDescriptor = FFXIVClientStructs.FFXIV.Client.System.File.FileDescriptor;
using ClientFileInterface = FFXIVClientStructs.FFXIV.Client.System.File.FileInterface;
using ClientFileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;
using ClientFileThread = FFXIVClientStructs.FFXIV.Client.System.File.FileThread;

namespace Intoner.Objects.Resources;

/// <summary>
/// Tracks active rooted local object resource paths and reports whether the Intoner resource loader can load them.
/// </summary>
internal interface IObjectFileReadService : IDisposable
{
    /// <summary>
    /// Checks whether scoped game resource paths can be restored before native file jobs read them.
    /// </summary>
    /// <returns>true when scoped game paths can be routed to their real game paths</returns>
    bool CanRouteScopedGamePaths();

    /// <summary>
    /// Checks whether one rooted local file path can be loaded by the active object resource hooks.
    /// </summary>
    /// <param name="localFilePath">The rooted local file path in object resource format</param>
    /// <returns>true when the local file path can be loaded by the active object resource hooks</returns>
    bool CanLoadLocalFilePath(string localFilePath);

    /// <summary>
    /// Checks whether one memory resource path can be loaded by the active object resource hooks.
    /// </summary>
    /// <param name="memoryResourcePath">the object memory resource path</param>
    /// <returns>true when the memory resource path can be loaded by the active object resource hooks</returns>
    bool CanLoadMemoryResourcePath(string memoryResourcePath);
}

internal sealed unsafe class ObjectFileReadService : IObjectFileReadService
{
    private const int CreateFilePathScratchOffset = 0x11;
    private const int CreateFilePathScratchSize = 0x11 + 0x0B + 14;
    private const int FileDescriptorFilePathOffset = 0x70;
    private static readonly nint CustomModelFileFlag = new(0x0B1EC700);

    private delegate byte FileJobDelegate(ClientFileThread* fileThread, ClientFileDescriptor* fileDescriptor, int priority, bool isSync);
    private delegate byte ReadFileDelegate(ClientFileThread* fileThread, ClientFileDescriptor* fileDescriptor, int priority, bool isSync);
    private delegate nint CheckFileStateDelegate(nint service, ulong crc64);
    private delegate byte LoadMdlFileExternDelegate(ResourceHandle* handle, nint unknown0, bool unknown1, nint state);
    private delegate byte LoadMdlFileLocalDelegate(ResourceHandle* handle, nint unknown0, bool unknown1);
    private delegate byte TextureOnLoadDelegate(TextureResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0);
    private delegate byte LoadTexFileLocalDelegate(TextureResourceHandle* handle, int lodLevel, ClientFileDescriptor* descriptor, bool unknown0);
    private delegate void UpdateTextureCategoryDelegate(TextureResourceHandle* handle);
    private delegate byte SoundOnLoadDelegate(ResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0);
    private delegate byte LoadScdFileLocalDelegate(ResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0);

    private readonly record struct ActiveLocalFileJob(
        nint ResourceHandle,
        string Path,
        ObjectLocalFileKind Kind,
        ulong PathCrc64,
        ulong HandleCrc64);

    private readonly ILogger<ObjectFileReadService> _logger;
    private readonly IObjectResolvedCollectionStore _collectionStore;
    private readonly ObjectResourceLoadScope _loadScope;
    private readonly ThreadLocal<ActiveLocalFileJob?> _activeLocalFileJob = new(static () => default);
    private readonly ObjectTextureLodService _lodService;
    private readonly Func<IObjectMemoryResourceService> _memoryResourceServiceFactory;
    private readonly ObjectResourceCreateFileHook _createFileHook;
    private readonly ObjectLocalFileTracker _localFileTracker;
    private readonly ObjectLockedOnce _enableOnce = new();

    private readonly Hook<FileJobDelegate>? _fileJobHook;
    private readonly Hook<CheckFileStateDelegate>? _checkFileStateHook;
    private readonly Hook<LoadMdlFileExternDelegate>? _loadMdlFileExternHook;
    private readonly Hook<TextureOnLoadDelegate>? _textureOnLoadHook;
    private readonly Hook<SoundOnLoadDelegate>? _soundOnLoadHook;
    private readonly ReadFileDelegate? _readFile;
    private readonly LoadMdlFileLocalDelegate? _loadMdlFileLocal;
    private readonly LoadTexFileLocalDelegate? _loadTexFileLocal;
    private readonly UpdateTextureCategoryDelegate? _updateTextureCategory;
    private readonly LoadScdFileLocalDelegate? _loadScdFileLocal;
    private readonly nint* _rsfService;
    private readonly ObjectDisposalState _disposeState = new();
    private bool _loggedUninitializedRsfService;

    public ObjectFileReadService(
        ILogger<ObjectFileReadService> logger,
        IObjectResolvedCollectionStore collectionStore,
        ObjectResourceLoadScope loadScope,
        Func<IObjectMemoryResourceService> memoryResourceServiceFactory,
        ObjectTextureLodService lodService,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _collectionStore = collectionStore;
        _loadScope = loadScope;
        _memoryResourceServiceFactory = memoryResourceServiceFactory;
        _lodService = lodService;
        _createFileHook = new ObjectResourceCreateFileHook(gameInteropProvider);

        _fileJobHook = ObjectInteropHookUtility.CreateHookFromAddress<FileJobDelegate>(
            _logger,
            gameInteropProvider,
            ObjectSignatures.ResourceFileJob,
            FileJobDetour);
        _checkFileStateHook = ObjectInteropHookUtility.CreateHook<CheckFileStateDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            ObjectSignatures.ResourceCheckFileState,
            CheckFileStateDetour);
        _loadMdlFileExternHook = ObjectInteropHookUtility.CreateHook<LoadMdlFileExternDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            ObjectSignatures.ResourceLoadMdlFileExtern,
            LoadMdlFileExternDetour);
        _textureOnLoadHook = ObjectInteropHookUtility.CreateHook<TextureOnLoadDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            ObjectSignatures.ResourceTextureOnLoad,
            TextureOnLoadDetour);
        _soundOnLoadHook = ObjectInteropHookUtility.CreateHook<SoundOnLoadDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            ObjectSignatures.ResourceSoundOnLoad,
            SoundOnLoadDetour);

        _readFile = ObjectInteropHookUtility.CreateDelegate<ReadFileDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceReadFile);
        _loadMdlFileLocal = ObjectInteropHookUtility.CreateDelegate<LoadMdlFileLocalDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceLoadMdlFileLocal);
        _loadTexFileLocal = ObjectInteropHookUtility.CreateDelegate<LoadTexFileLocalDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceLoadTexFileLocal);
        _loadScdFileLocal = ObjectInteropHookUtility.CreateDelegate<LoadScdFileLocalDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceLoadScdFileLocal);
        _updateTextureCategory = ObjectInteropHookUtility.CreateDelegate<UpdateTextureCategoryDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceUpdateTextureCategory);
        _rsfService = (nint*)ObjectNativeAddressResolver.TryResolveStaticAddress(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceRsfService);

        _localFileTracker = new ObjectLocalFileTracker(collectionStore, TryNormalizeLoadableLocalFilePath);
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        ObjectInteropHookUtility.DisposeHook(_fileJobHook);
        ObjectInteropHookUtility.DisposeHook(_checkFileStateHook);
        ObjectInteropHookUtility.DisposeHook(_loadMdlFileExternHook);
        ObjectInteropHookUtility.DisposeHook(_textureOnLoadHook);
        ObjectInteropHookUtility.DisposeHook(_soundOnLoadHook);
        _localFileTracker.Dispose();
        _createFileHook.Dispose();
        _activeLocalFileJob.Dispose();
    }

    public bool CanLoadLocalFilePath(string localFilePath)
        => TryGetLoadableLocalFileKind(localFilePath, out _, out _);

    public bool CanRouteScopedGamePaths()
        => TryEnableHooks() && CanHandleScopedGamePaths();

    public bool CanLoadMemoryResourcePath(string memoryResourcePath)
        => TryEnableHooks()
            && MemoryResourceService.TryGetResource(memoryResourcePath, out ObjectMemoryResource memoryResource)
            && CanLoadMemoryResource(memoryResource);

    private string? TryNormalizeLoadableLocalFilePath(string localFilePath)
        => TryGetLoadableLocalFileKind(localFilePath, out string normalizedLocalFilePath, out _)
            ? normalizedLocalFilePath
            : null;

    private bool TryGetLoadableLocalFileKind(string localFilePath, out string normalizedLocalFilePath, out ObjectLocalFileKind kind)
    {
        normalizedLocalFilePath = ObjectLocalFilePathUtility.NormalizeLocalFilePath(localFilePath);
        kind = ObjectLocalFileKind.DirectRead;
        if (IsDisposing)
        {
            return false;
        }

        if (normalizedLocalFilePath.Length == 0)
        {
            return false;
        }

        if (!TryEnableHooks())
        {
            return false;
        }

        if (!CanLoadRootedLocalFiles())
        {
            return false;
        }

        kind = ClassifyCustomLocalFile(normalizedLocalFilePath);
        if (!CanLoadLocalFileKind(kind))
        {
            return false;
        }

        return true;
    }

    private byte FileJobDetour(ClientFileThread* fileThread, ClientFileDescriptor* fileDescriptor, int priority, bool isSync)
    {
        try
        {
            if (IsDisposing
                || fileDescriptor == null
                || fileDescriptor->ResourceHandle == null
                || fileDescriptor->FileMode != ClientFileMode.LoadSqPackResource)
            {
                return CallFileJobOriginal(fileThread, fileDescriptor, priority, isSync);
            }

            if (!ObjectResourcePathEncoding.TryReadHandlePath(fileDescriptor->ResourceHandle, out string handlePath))
            {
                return CallFileJobOriginal(fileThread, fileDescriptor, priority, isSync);
            }

            if (ObjectScopedResourcePathUtility.TryParse(handlePath, out ObjectScopedResourcePath scopedPath))
            {
                return _collectionStore.TryGetCollectionByResourceScopeId(scopedPath.ResourceScopeId, out _)
                    ? RouteFileJobPath(fileThread, fileDescriptor, priority, isSync, scopedPath.Path, restoreScopedPath: true)
                    : (byte)0;
            }

            if (ObjectScopedResourcePathUtility.IsObjectScopedPath(handlePath))
            {
                return 0;
            }

            if (ObjectScopedResourcePathUtility.IsForeignScopedPath(handlePath)
                || !ShouldRouteUnscopedFileJobPath(handlePath))
            {
                return CallFileJobOriginal(fileThread, fileDescriptor, priority, isSync);
            }

            string normalizedPath = ObjectResourcePathUtility.NormalizeTrackedPath(handlePath);
            if (normalizedPath.Length == 0)
            {
                return CallFileJobOriginal(fileThread, fileDescriptor, priority, isSync);
            }

            return RouteFileJobPath(fileThread, fileDescriptor, priority, isSync, normalizedPath, restoreScopedPath: false);
        }
        catch (Exception ex)
        {
            LogFileHookFailure(ex, "file job", fileDescriptor == null ? null : fileDescriptor->ResourceHandle);
            return ShouldFailClosedFileJob(fileDescriptor)
                ? (byte)0
                : CallFileJobOriginal(fileThread, fileDescriptor, priority, isSync);
        }
    }

    private byte RouteFileJobPath(
        ClientFileThread* fileThread,
        ClientFileDescriptor* fileDescriptor,
        int priority,
        bool isSync,
        string normalizedPath,
        bool restoreScopedPath)
    {
        if (fileDescriptor == null)
        {
            return CallFileJobOriginal(fileThread, fileDescriptor, priority, isSync);
        }

        if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(normalizedPath))
        {
            return MemoryResourceService.TryGetResource(normalizedPath, out ObjectMemoryResource memoryResource)
                && CanLoadMemoryResource(memoryResource)
                ? MemoryResourceService.ReadResource(fileDescriptor, memoryResource)
                : (byte)0;
        }

        if (!ObjectLocalFilePathUtility.IsLocalFilePath(normalizedPath))
        {
            return restoreScopedPath
                ? RunFileJobWithTemporaryResourcePath(fileThread, fileDescriptor, priority, isSync, normalizedPath)
                : CallFileJobOriginal(fileThread, fileDescriptor, priority, isSync);
        }

        if (_readFile != null
            && (restoreScopedPath || _localFileTracker.ContainsLocalFilePath(normalizedPath)))
        {
            ObjectLocalFileKind kind = ClassifyCustomLocalFile(normalizedPath);
            using IDisposable activeJob = EnterActiveLocalFileJob(fileDescriptor->ResourceHandle, normalizedPath, kind);
            return ReadLocalFile(fileThread, fileDescriptor, priority, isSync, normalizedPath, restoreScopedPath);
        }

        return restoreScopedPath
            ? (byte)0
            : CallFileJobOriginal(fileThread, fileDescriptor, priority, isSync);
    }

    private static bool ShouldRouteUnscopedFileJobPath(string path)
        => ObjectMemoryResourcePathUtility.IsMemoryResourcePath(path)
            || ObjectLocalFilePathUtility.IsLocalFilePath(path);

    private static bool ShouldFailClosedFileJob(ClientFileDescriptor* fileDescriptor)
    {
        if (fileDescriptor == null
            || fileDescriptor->ResourceHandle == null
            || !ObjectResourcePathEncoding.TryReadHandlePath(fileDescriptor->ResourceHandle, out string handlePath))
        {
            return false;
        }

        if (ObjectScopedResourcePathUtility.IsObjectScopedPath(handlePath))
        {
            return true;
        }

        string normalizedPath = ObjectResourcePathUtility.NormalizeTrackedPath(handlePath);
        return ObjectMemoryResourcePathUtility.IsMemoryResourcePath(normalizedPath);
    }

    private byte CallFileJobOriginal(ClientFileThread* fileThread, ClientFileDescriptor* fileDescriptor, int priority, bool isSync)
        => _fileJobHook!.Original(fileThread, fileDescriptor, priority, isSync);

    private byte RunFileJobWithTemporaryResourcePath(
        ClientFileThread* fileThread,
        ClientFileDescriptor* fileDescriptor,
        int priority,
        bool isSync,
        string path)
        => ObjectResourcePathEncoding.WithTemporaryHandlePath(
            fileDescriptor->ResourceHandle,
            path,
            (Hook: _fileJobHook!, FileThread: (nint)fileThread, FileDescriptor: (nint)fileDescriptor, Priority: priority, IsSync: isSync),
            static state => state.Hook.Original(
                (ClientFileThread*)state.FileThread,
                (ClientFileDescriptor*)state.FileDescriptor,
                state.Priority,
                state.IsSync));

    private byte ReadLocalFile(
        ClientFileThread* fileThread,
        ClientFileDescriptor* fileDescriptor,
        int priority,
        bool isSync,
        string normalizedPath,
        bool restoreScopedPath)
        => ObjectResourcePathEncoding.WithNullTerminatedUtf8(
            normalizedPath,
            (
                Owner: this,
                FileThread: (nint)fileThread,
                FileDescriptor: (nint)fileDescriptor,
                Priority: priority,
                IsSync: isSync,
                RestoreScopedPath: restoreScopedPath),
            static (pathPointer, pathByteCount, state) =>
            {
                var fileDescriptor = (ClientFileDescriptor*)state.FileDescriptor;
                fileDescriptor->FileMode = ClientFileMode.LoadUnpackedResource;
                Span<char> descriptorScratch = stackalloc char[CreateFilePathScratchSize];
                descriptorScratch.Clear();
                fixed (char* scratchPointer = descriptorScratch)
                {
                    fileDescriptor->FileInterface = (ClientFileInterface*)((byte*)scratchPointer + 1);
                    ObjectResourceCreateFileHook.WritePointerPayload(scratchPointer + CreateFilePathScratchOffset, pathPointer, pathByteCount);
                    ObjectResourceCreateFileHook.WritePointerPayload(GetFilePathPointer(fileDescriptor), pathPointer, pathByteCount);

                    if (!state.RestoreScopedPath)
                    {
                        return state.Owner._readFile!(
                            (ClientFileThread*)state.FileThread,
                            fileDescriptor,
                            state.Priority,
                            state.IsSync);
                    }

                    using var resourcePath = new ObjectResourceHandlePathScope(
                        fileDescriptor->ResourceHandle,
                        pathPointer,
                        pathByteCount);
                    return state.Owner._readFile!(
                        (ClientFileThread*)state.FileThread,
                        fileDescriptor,
                        state.Priority,
                        state.IsSync);
                }
            });

    private nint CheckFileStateDetour(nint service, ulong crc64)
    {
        try
        {
            if (IsDisposing)
            {
                return _checkFileStateHook!.Original(service, crc64);
            }

            if (TryGetActiveLocalFileJob(crc64, out ActiveLocalFileJob activeJob))
            {
                return activeJob.Kind switch
                {
                    ObjectLocalFileKind.Model when CanLoadLocalModels() => CustomModelFileFlag,
                    ObjectLocalFileKind.Texture when CanLoadLocalTextures() => nint.Zero,
                    ObjectLocalFileKind.Sound when CanLoadLocalSounds() => nint.Zero,
                    _ => _checkFileStateHook!.Original(service, crc64),
                };
            }

            return _checkFileStateHook!.Original(service, crc64);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "object resource check file state hook failed");
            return _checkFileStateHook!.Original(service, crc64);
        }
    }

    private byte LoadMdlFileExternDetour(ResourceHandle* handle, nint unknown0, bool unknown1, nint state)
    {
        try
        {
            if (IsDisposing || state != CustomModelFileFlag)
            {
                return _loadMdlFileExternHook!.Original(handle, unknown0, unknown1, state);
            }

            if (_loadMdlFileLocal == null
                || !TryGetMatchingActiveLocalFileJob(ObjectLocalFileKind.Model, handle, null, out ActiveLocalFileJob activeJob))
            {
                return 0;
            }

            using var scope = EnterScopedHandleCollectionScope(handle);
            if (!ObjectResourcePathEncoding.TryReadActualScopedHandlePath(handle, out string actualPath))
            {
                return _loadMdlFileLocal(handle, unknown0, unknown1);
            }

            if (!string.Equals(ObjectResourcePathUtility.NormalizeTrackedPath(actualPath), activeJob.Path, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return ObjectResourcePathEncoding.WithTemporaryHandlePath(
                handle,
                actualPath,
                (Owner: this, Handle: (nint)handle, Unknown0: unknown0, Unknown1: unknown1),
                static state => state.Owner._loadMdlFileLocal!(
                    (ResourceHandle*)state.Handle,
                    state.Unknown0,
                    state.Unknown1));
        }
        catch (Exception ex)
        {
            LogFileHookFailure(ex, "model extern load", handle);
            return state == CustomModelFileFlag
                ? (byte)0
                : _loadMdlFileExternHook!.Original(handle, unknown0, unknown1, state);
        }
    }

    private byte TextureOnLoadDetour(TextureResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0)
    {
        byte result = 0;
        bool calledOriginal = false;
        try
        {
            if (ObjectMemoryResourceService.IsMemoryFileDescriptor(descriptor)
             && MemoryResourceService.TryReadTextureResource(handle, descriptor, unknown0 != 0, out byte memoryResult))
            {
                return memoryResult;
            }

            result = _textureOnLoadHook!.Original(handle, descriptor, unknown0);
            calledOriginal = true;
            if (!TryGetMatchingActiveLocalFileJob(ObjectLocalFileKind.Texture, (ResourceHandle*)handle, descriptor, out _))
            {
                return result;
            }

            if (_loadTexFileLocal == null)
            {
                return 0;
            }

            result = _loadTexFileLocal(handle, _lodService.GetLod(handle), descriptor, unknown0 != 0);
            _updateTextureCategory?.Invoke(handle);
            return result;
        }
        catch (Exception ex)
        {
            LogFileHookFailure(ex, "texture on load", (ResourceHandle*)handle);
            return calledOriginal
                ? result
                : _textureOnLoadHook!.Original(handle, descriptor, unknown0);
        }
    }

    private byte SoundOnLoadDetour(ResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0)
    {
        byte result = 0;
        bool calledOriginal = false;
        try
        {
            if (ObjectMemoryResourceService.IsMemoryFileDescriptor(descriptor)
             && MemoryResourceService.TryReadSoundResource(handle, descriptor, unknown0 != 0, out byte memoryResult))
            {
                return memoryResult;
            }

            result = CallSoundOnLoadOriginal(handle, descriptor, unknown0);
            calledOriginal = true;
            if (!TryGetMatchingActiveLocalFileJob(ObjectLocalFileKind.Sound, handle, descriptor, out _))
            {
                return result;
            }

            return _loadScdFileLocal != null
                ? _loadScdFileLocal(handle, descriptor, unknown0)
                : (byte)0;
        }
        catch (Exception ex)
        {
            LogFileHookFailure(ex, "sound on load", handle);
            return calledOriginal
                ? result
                : CallSoundOnLoadOriginal(handle, descriptor, unknown0);
        }
    }

    private byte CallSoundOnLoadOriginal(ResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0)
    {
        if (_rsfService == null || *_rsfService != nint.Zero)
        {
            return _soundOnLoadHook!.Original(handle, descriptor, unknown0);
        }

        if (!_loggedUninitializedRsfService)
        {
            _loggedUninitializedRsfService = true;
            _logger.LogDebug("object local sound resource load reached RSF before service initialization");
        }

        *_rsfService = 1;
        try
        {
            return _soundOnLoadHook!.Original(handle, descriptor, unknown0);
        }
        finally
        {
            *_rsfService = nint.Zero;
        }
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private bool TryEnableHooks()
        => _enableOnce.TryExecute(
            () =>
            {
                if (!_createFileHook.Enable())
                {
                    return false;
                }

                _fileJobHook?.Enable();
                _checkFileStateHook?.Enable();
                _loadMdlFileExternHook?.Enable();
                _textureOnLoadHook?.Enable();
                _soundOnLoadHook?.Enable();
                return true;
            },
            () => !IsDisposing);

    private IDisposable EnterActiveLocalFileJob(ResourceHandle* handle, string normalizedPath, ObjectLocalFileKind kind)
    {
        if (IsDisposing
            || handle == null
            || kind == ObjectLocalFileKind.DirectRead
            || normalizedPath.Length == 0
            || !ObjectThreadLocalUtility.TryRead(_activeLocalFileJob, null, out ActiveLocalFileJob? previousJob))
        {
            return default(ActiveLocalFileJobToken);
        }

        ulong pathCrc = new FullPath(normalizedPath).Crc64;
        ulong handleCrc = ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            ? new FullPath(handlePath).Crc64
            : 0;
        ActiveLocalFileJob activeJob = new((nint)handle, normalizedPath, kind, pathCrc, handleCrc);
        return ObjectThreadLocalUtility.TryWrite(_activeLocalFileJob, activeJob)
            ? new ActiveLocalFileJobToken(this, previousJob)
            : default(ActiveLocalFileJobToken);
    }

    private bool TryGetActiveLocalFileJob(ulong crc64, out ActiveLocalFileJob activeJob)
    {
        activeJob = default;
        if (!TryReadActiveLocalFileJob(out ActiveLocalFileJob job))
        {
            return false;
        }

        if (crc64 != job.PathCrc64 && (job.HandleCrc64 == 0 || crc64 != job.HandleCrc64))
        {
            return false;
        }

        activeJob = job;
        return true;
    }

    private bool TryGetMatchingActiveLocalFileJob(
        ObjectLocalFileKind kind,
        ResourceHandle* handle,
        ClientFileDescriptor* descriptor,
        out ActiveLocalFileJob activeJob)
    {
        activeJob = default;
        if (handle == null
            || !TryReadActiveLocalFileJob(out ActiveLocalFileJob job)
            || job.Kind != kind
            || job.ResourceHandle != (nint)handle
            || (descriptor != null && descriptor->ResourceHandle != handle))
        {
            return false;
        }

        if (TryResolveHandleActualPath(handle, out string actualPath)
            && ObjectLocalFilePathUtility.IsLocalFilePath(actualPath)
            && !string.Equals(ObjectLocalFilePathUtility.NormalizeLocalFilePath(actualPath), job.Path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        activeJob = job;
        return true;
    }

    private bool TryReadActiveLocalFileJob(out ActiveLocalFileJob activeJob)
    {
        activeJob = default;
        if (IsDisposing
            || !ObjectThreadLocalUtility.TryRead(_activeLocalFileJob, null, out ActiveLocalFileJob? currentJob)
            || currentJob is not { } job)
        {
            return false;
        }

        activeJob = job;
        return true;
    }

    private static bool TryResolveHandleActualPath(ResourceHandle* handle, out string actualPath)
    {
        if (ObjectResourcePathEncoding.TryReadActualScopedHandlePath(handle, out actualPath))
        {
            return true;
        }

        if (!ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath))
        {
            actualPath = string.Empty;
            return false;
        }

        actualPath = ObjectResourcePathUtility.NormalizeTrackedPath(handlePath);
        return actualPath.Length > 0;
    }

    private void RestoreActiveLocalFileJob(ActiveLocalFileJob? previousJob)
        => _ = ObjectThreadLocalUtility.TryWrite(_activeLocalFileJob, previousJob);

    private void LogFileHookFailure(Exception exception, string hook, ResourceHandle* handle)
    {
        string path = ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            ? handlePath
            : string.Empty;
        _logger.LogError(exception, "object resource {Hook} hook failed; path={Path}", hook, path);
    }

    private static char* GetFilePathPointer(ClientFileDescriptor* fileDescriptor)
        => (char*)((byte*)fileDescriptor + FileDescriptorFilePathOffset);

    private ObjectResourceLoadScopeToken EnterScopedHandleCollectionScope(ResourceHandle* handle)
    {
        if (handle == null
            || !ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            || !ObjectScopedResourcePathUtility.TryParse(handlePath, out ObjectScopedResourcePath scopedPath)
            || !_collectionStore.TryGetCollectionByResourceScopeId(scopedPath.ResourceScopeId, out ObjectCollectionResolveData collection))
        {
            return default;
        }

        return _loadScope.EnterCollectionScope(collection.CollectionId);
    }

    private bool CanLoadLocalModels()
        => _checkFileStateHook != null && _loadMdlFileExternHook != null && _loadMdlFileLocal != null;

    private bool CanLoadLocalTextures()
        => _checkFileStateHook != null && _textureOnLoadHook != null && _loadTexFileLocal != null;

    private bool CanLoadLocalSounds()
        => _checkFileStateHook != null && _soundOnLoadHook != null && _loadScdFileLocal != null && _rsfService != null;

    private bool CanLoadLocalFileKind(ObjectLocalFileKind kind)
        => kind switch
        {
            ObjectLocalFileKind.Model => CanLoadLocalModels(),
            ObjectLocalFileKind.Texture => CanLoadLocalTextures(),
            ObjectLocalFileKind.Sound => CanLoadLocalSounds(),
            _ => true,
        };

    private bool CanLoadMemoryResource(ObjectMemoryResource memoryResource)
        => CanHandleCustomFileJobs()
            && MemoryResourceService.CanLoadMemoryResource(memoryResource)
            && CanRouteMemoryResourceKind(memoryResource.GamePath);

    private IObjectMemoryResourceService MemoryResourceService
        => _memoryResourceServiceFactory();

    private bool CanRouteMemoryResourceKind(string gamePath)
        => ObjectAssetPathRules.ClassifyResourcePath(gamePath) switch
        {
            ObjectResourcePathKind.Texture or ObjectResourcePathKind.Atex => _textureOnLoadHook != null,
            ObjectResourcePathKind.Sound => _soundOnLoadHook != null,
            _ => true,
        };

    private bool CanLoadRootedLocalFiles()
        => CanHandleCustomFileJobs() && _readFile != null;

    private bool CanHandleScopedGamePaths()
        => !IsDisposing && _fileJobHook != null;

    private bool CanHandleCustomFileJobs()
        => !IsDisposing && _fileJobHook != null;

    private static ObjectLocalFileKind ClassifyCustomLocalFile(string path)
        => GameAssetPathRules.ClassifyFilePath(path) switch
        {
            GameAssetFileKind.Mdl => ObjectLocalFileKind.Model,
            GameAssetFileKind.Tex => ObjectLocalFileKind.Texture,
            GameAssetFileKind.Scd => ObjectLocalFileKind.Sound,
            _ => ObjectLocalFileKind.DirectRead,
        };

    private readonly struct ActiveLocalFileJobToken(ObjectFileReadService? owner, ActiveLocalFileJob? previousJob) : IDisposable
    {
        public void Dispose()
            => owner?.RestoreActiveLocalFileJob(previousJob);
    }
}
