using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using System.Runtime.InteropServices;
using ClientFileDescriptor = FFXIVClientStructs.FFXIV.Client.System.File.FileDescriptor;
using ClientFileInterface = FFXIVClientStructs.FFXIV.Client.System.File.FileInterface;
using ClientFileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;
using ClientFileThread = FFXIVClientStructs.FFXIV.Client.System.File.FileThread;

namespace Intoner.Objects.Resources;

/// <summary> routes owned file jobs and checks local and memory resource support </summary>
internal sealed unsafe class ObjectFileReadService : IDisposable
{
    internal enum ObjectLocalFileKind
    {
        DirectRead,
        Model,
        Texture,
        Sound,
    }

    internal enum FileJobRoute
    {
        Original,
        Reject,
        GamePath,
        Local,
        Memory,
    }

    internal readonly record struct FileJobPlan(
        FileJobRoute Route,
        string Path = "",
        ObjectMemoryResource Memory = default);

    private const int CreateFilePathScratchOffset = 0x11;
    private const int CreateFilePathScratchSize = 0x11 + 0x0B + ObjectResourceCreateFileHook.PointerPayloadSize / sizeof(char);
    private const int FileDescriptorFilePathOffset = 0x70;
    private static readonly nint CustomModelFileFlag = new(0x0B1EC700);

    private delegate nint CheckFileStateDelegate(nint service, ulong crc64);
    private delegate byte LoadMdlFileExternDelegate(ResourceHandle* handle, nint unknown0, [MarshalAs(UnmanagedType.I1)] bool unknown1, nint state);
    private delegate byte TextureOnLoadDelegate(TextureResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0);

    private readonly record struct ActiveLocalFileJob(
        nint FileDescriptor,
        nint ResourceHandle,
        string Path,
        ObjectLocalFileKind Kind,
        ulong PathCrc64,
        ulong HandleCrc64);

    private readonly ILogger<ObjectFileReadService> _logger;
    private readonly ObjectResourceLoadScope _loadScope;
    private readonly ObjectResourceTracker _resourceTracker;
    private readonly IObjectMemoryResourceService _memoryResourceService;
    private readonly ObjectTextureLodService _lodService;
    private readonly ObjectResourceReadBindings _readBindings;

    private readonly ObjectResourceCreateFileHook _createFileHook;
    private readonly ThreadLocal<ActiveLocalFileJob?> _activeLocalFileJob = new(static () => default);
    private readonly InteropHookCollection _hooks;

    private readonly Hook<ClientFileThread.Delegates.DoFileJob>? _fileJobHook;
    private readonly Hook<CheckFileStateDelegate>? _checkFileStateHook;
    private readonly Hook<LoadMdlFileExternDelegate>? _loadMdlFileExternHook;
    private readonly Hook<TextureOnLoadDelegate>? _textureOnLoadHook;
    private readonly Hook<ObjectResourceReadBindings.LoadSoundDelegate>? _soundOnLoadHook;
    private readonly ClientFileThread.Delegates.DoFileJob? _readFile;
    private readonly DisposalState _disposeState = new();

    public ObjectFileReadService(
        ILogger<ObjectFileReadService> logger,
        ObjectResourceLoadScope loadScope,
        ObjectResourceTracker resourceTracker,
        IObjectMemoryResourceService memoryResourceService,
        ObjectTextureLodService lodService,
        ObjectResourceReadBindings readBindings,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _loadScope = loadScope;
        _resourceTracker = resourceTracker;
        _memoryResourceService = memoryResourceService;
        _lodService = lodService;
        _readBindings = readBindings;
        _hooks = new InteropHookCollection(logger);
        try
        {
            _createFileHook = new ObjectResourceCreateFileHook(gameInteropProvider, logger);

            _fileJobHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress<ClientFileThread.Delegates.DoFileJob>(
                _logger,
                gameInteropProvider,
                IntonerSignatures.ResourceFileJob,
                FileJobDetour));
            _checkFileStateHook = _hooks.Add(InteropHookUtility.CreateHook<CheckFileStateDelegate>(
                _logger,
                gameInteropProvider,
                sigScanner,
                IntonerSignatures.ResourceCheckFileState,
                CheckFileStateDetour));
            _loadMdlFileExternHook = _hooks.Add(InteropHookUtility.CreateHook<LoadMdlFileExternDelegate>(
                _logger,
                gameInteropProvider,
                sigScanner,
                IntonerSignatures.ResourceLoadMdlFileExtern,
                LoadMdlFileExternDetour));
            _textureOnLoadHook = _hooks.Add(InteropHookUtility.CreateHook<TextureOnLoadDelegate>(
                _logger,
                gameInteropProvider,
                sigScanner,
                IntonerSignatures.ResourceTextureOnLoad,
                TextureOnLoadDetour));
            _soundOnLoadHook = _hooks.Add(InteropHookUtility.CreateHook<ObjectResourceReadBindings.LoadSoundDelegate>(
                _logger,
                gameInteropProvider,
                sigScanner,
                IntonerSignatures.ResourceSoundOnLoad,
                SoundOnLoadDetour));

            _readFile = InteropHookUtility.CreateDelegate<ClientFileThread.Delegates.DoFileJob>(
                _logger,
                sigScanner,
                IntonerSignatures.ResourceReadFile);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose() && _hooks.IsDisposed && _createFileHook?.IsDisposed != false)
        {
            return;
        }

        _hooks.Dispose();

        if (!_hooks.IsDisposed)
        {
            return;
        }

        _createFileHook?.Dispose();

        if (_createFileHook?.IsDisposed != false)
        {
            _activeLocalFileJob.Dispose();
        }
    }

    public bool CanLoadLocalFilePath(string localFilePath, ResourceType resourceType)
        => !IsDisposing
            && ObjectLocalFilePathUtility.NormalizeLocalFilePath(localFilePath).Length > 0
            && TryEnableHooks()
            && CanLoadRootedLocalFiles()
            && CanLoadLocalFileKind(ClassifyLocalResource(resourceType));

    public bool CanRouteScopedGamePaths()
        => TryEnableHooks() && CanHandleCustomFileJobs();

    public bool CanLoadMemoryResourcePath(string memoryResourcePath)
        => TryEnableHooks()
            && _memoryResourceService.TryGetResource(memoryResourcePath, out ObjectMemoryResource memoryResource)
            && CanLoadMemoryResource(memoryResource);

    internal byte FileJobDetour(ClientFileThread* fileThread, ClientFileDescriptor* fileDescriptor, int priority, bool isSync)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        using InteropHookCollection.CallbackScope operation = _hooks.EnterOperation();
        using ActiveLocalFileJobToken parentJob = SuspendActiveLocalFileJob();
        ObjectResourceLoadScopeToken scope = default;

        try
        {
            FileJobPlan plan = PrepareFileJob(fileDescriptor, ref scope);
            return ExecuteFileJob(fileThread, fileDescriptor, priority, isSync, plan);
        }
        finally
        {
            scope.Dispose();
        }
    }

    private FileJobPlan PrepareFileJob(ClientFileDescriptor* fileDescriptor, ref ObjectResourceLoadScopeToken scope)
    {
        nint resourceHandleAddress = nint.Zero;

        try
        {
            if (fileDescriptor == null
                || fileDescriptor->ResourceHandle == null
                || fileDescriptor->FileMode != ClientFileMode.LoadSqPackResource)
            {
                return new(FileJobRoute.Original);
            }

            resourceHandleAddress = (nint)fileDescriptor->ResourceHandle;

            if (!ObjectResourcePathEncoding.TryReadHandlePath(fileDescriptor->ResourceHandle, out string handlePath))
            {
                return new(FileJobRoute.Original);
            }

            if (IsDisposing)
            {
                return new(ObjectResourcePathUtility.IsOwnedResourcePath(handlePath) ? FileJobRoute.Reject : FileJobRoute.Original);
            }

            if (ObjectScopedResourcePathUtility.TryParse(handlePath, out ObjectScopedResourcePath scopedPath))
            {
                scope = _resourceTracker.EnterHandleScope(resourceHandleAddress, scopedPath);

                if (scope.IsActive)
                {
                    return PrepareFileJobPath(fileDescriptor, scopedPath.Path);
                }

                LogRejectedFileJob("generation unavailable", resourceHandleAddress, handlePath, scopedPath.ResourceScopeId);
                return new(FileJobRoute.Reject);
            }

            if (ObjectScopedResourcePathUtility.IsObjectScopedPath(handlePath))
            {
                string reason = ObjectScopedResourcePathUtility.IsRejectedPath(handlePath)
                    ? "resource request rejected"
                    : "malformed private path";
                LogRejectedFileJob(reason, resourceHandleAddress, handlePath);
                return new(FileJobRoute.Reject);
            }

            if (!ObjectMemoryResourcePathUtility.IsMemoryResourcePath(handlePath))
            {
                return new(FileJobRoute.Original);
            }

            string normalizedPath = ObjectResourcePathUtility.NormalizeTrackedPath(handlePath);

            if (normalizedPath.Length == 0)
            {
                LogRejectedFileJob("malformed memory path", resourceHandleAddress, handlePath);
                return new(FileJobRoute.Reject);
            }

            return PrepareMemoryFileJob(resourceHandleAddress, normalizedPath);
        }
        catch (Exception ex)
        {
            LogFileHookFailure(ex, "file job", (ResourceHandle*)resourceHandleAddress);
            return new(ShouldFailClosedFileJob(fileDescriptor) ? FileJobRoute.Reject : FileJobRoute.Original);
        }
    }

    private FileJobPlan PrepareFileJobPath(ClientFileDescriptor* fileDescriptor, string normalizedPath)
    {
        nint resourceHandleAddress = (nint)fileDescriptor->ResourceHandle;

        if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(normalizedPath))
        {
            return PrepareMemoryFileJob(resourceHandleAddress, normalizedPath);
        }

        if (!ObjectLocalFilePathUtility.IsLocalFilePath(normalizedPath))
        {
            return new(FileJobRoute.GamePath, normalizedPath);
        }

        ResourceType resourceType = (ResourceType)fileDescriptor->ResourceHandle->FileType;

        if (CanLoadLocalFilePath(normalizedPath, resourceType))
        {
            return new(FileJobRoute.Local, normalizedPath);
        }

        LogRejectedFileJob("local reader unavailable", resourceHandleAddress, normalizedPath);
        return new(FileJobRoute.Reject);
    }

    private FileJobPlan PrepareMemoryFileJob(nint resourceHandleAddress, string normalizedPath)
    {
        if (!_memoryResourceService.TryGetResource(normalizedPath, out ObjectMemoryResource memoryResource))
        {
            LogRejectedFileJob("memory payload unavailable", resourceHandleAddress, normalizedPath);
            return new(FileJobRoute.Reject);
        }

        if (!CanLoadMemoryResource(memoryResource))
        {
            LogRejectedFileJob("memory resource unsupported", resourceHandleAddress, normalizedPath);
            return new(FileJobRoute.Reject);
        }

        return new(FileJobRoute.Memory, normalizedPath, Memory: memoryResource);
    }

    internal byte ExecuteFileJob(ClientFileThread* fileThread, ClientFileDescriptor* descriptor, int priority, bool isSync, FileJobPlan plan)
    {
        switch (plan.Route)
        {
            case FileJobRoute.Reject:
                return CompleteRejectedFileJob(descriptor);
            case FileJobRoute.GamePath:
                return RunFileJobWithTemporaryResourcePath(fileThread, descriptor, priority, isSync, plan.Path);
            case FileJobRoute.Local:
                using (EnterActiveLocalFileJob(descriptor, plan.Path, ClassifyLocalResource((ResourceType)descriptor->ResourceHandle->FileType)))
                {
                    return ReadLocalFile(fileThread, descriptor, priority, isSync, plan.Path);
                }
            case FileJobRoute.Memory:
                byte result = _memoryResourceService.ReadResource(descriptor, plan.Memory);

                if (result != 0)
                {
                    return result;
                }

                LogRejectedFileJob("memory read failed", (nint)descriptor->ResourceHandle, plan.Path);
                return CompleteRejectedFileJob(descriptor);
            default:
                return CallFileJobOriginal(fileThread, descriptor, priority, isSync);
        }
    }

    private byte CompleteRejectedFileJob(ClientFileDescriptor* fileDescriptor)
    {
        using ObjectResourceLoadScopeToken scope = _loadScope.Suspend();
        return RejectFileJob(fileDescriptor);
    }

    internal static byte RejectFileJob(ClientFileDescriptor* fileDescriptor)
    {
        // sqpack failures complete the handle, zero would leave the descriptor pending
        fileDescriptor->ResourceHandle->FinishLoad(fileDescriptor, byte.MaxValue, 0);
        return 1;
    }

    private static bool ShouldFailClosedFileJob(ClientFileDescriptor* fileDescriptor)
    {
        if (fileDescriptor == null
            || fileDescriptor->ResourceHandle == null
            || !ObjectResourcePathEncoding.TryReadHandlePath(fileDescriptor->ResourceHandle, out string handlePath))
        {
            return false;
        }

        return ObjectResourcePathUtility.IsOwnedResourcePath(handlePath);
    }

    private byte CallFileJobOriginal(ClientFileThread* fileThread, ClientFileDescriptor* fileDescriptor, int priority, bool isSync)
    {
        using ObjectResourceLoadScopeToken scope = _loadScope.Suspend();
        return _fileJobHook!.OriginalDisposeSafe(fileThread, fileDescriptor, priority, isSync);
    }

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
            static state => state.Hook.OriginalDisposeSafe(
                (ClientFileThread*)state.FileThread,
                (ClientFileDescriptor*)state.FileDescriptor,
                state.Priority,
                state.IsSync));

    private byte ReadLocalFile(
        ClientFileThread* fileThread,
        ClientFileDescriptor* fileDescriptor,
        int priority,
        bool isSync,
        string normalizedPath)
        => ObjectResourcePathEncoding.WithNullTerminatedUtf8(
            normalizedPath,
            (
                Owner: this,
                FileThread: (nint)fileThread,
                FileDescriptor: (nint)fileDescriptor,
                Priority: priority,
                IsSync: isSync),
            static (pathPointer, pathByteCount, state) =>
            {
                var fileDescriptor = (ClientFileDescriptor*)state.FileDescriptor;
                using ObjectResourceCreateFileHook.ReadScope readScope = state.Owner._createFileHook.EnterRead(pathPointer, pathByteCount);

                if (!readScope.IsActive)
                {
                    return (byte)0;
                }

                ClientFileMode originalFileMode = fileDescriptor->FileMode;
                ClientFileInterface* originalFileInterface = fileDescriptor->FileInterface;
                Span<byte> originalFilePath = stackalloc byte[ObjectResourceCreateFileHook.PointerPayloadSize];
                Span<byte> temporaryFilePath = stackalloc byte[ObjectResourceCreateFileHook.PointerPayloadSize];
                new ReadOnlySpan<byte>(GetFilePathPointer(fileDescriptor), originalFilePath.Length).CopyTo(originalFilePath);
                fileDescriptor->FileMode = ClientFileMode.LoadUnpackedResource;
                Span<char> descriptorScratch = stackalloc char[CreateFilePathScratchSize];
                descriptorScratch.Clear();
                fixed (char* scratchPointer = descriptorScratch)
                {
                    fileDescriptor->FileInterface = (ClientFileInterface*)((byte*)scratchPointer + 1);
                    ObjectResourceCreateFileHook.WritePointerPayload(scratchPointer + CreateFilePathScratchOffset, pathPointer, pathByteCount);
                    ObjectResourceCreateFileHook.WritePointerPayload(GetFilePathPointer(fileDescriptor), pathPointer, pathByteCount);
                    new ReadOnlySpan<byte>(GetFilePathPointer(fileDescriptor), temporaryFilePath.Length).CopyTo(temporaryFilePath);

                    try
                    {
                        using ObjectResourceHandlePathScope resourcePath = new(fileDescriptor->ResourceHandle, pathPointer, pathByteCount);
                        return state.Owner._readFile!(
                            (ClientFileThread*)state.FileThread,
                            fileDescriptor,
                            state.Priority,
                            state.IsSync);
                    }
                    finally
                    {
                        if (fileDescriptor->FileInterface == (ClientFileInterface*)((byte*)scratchPointer + 1))
                        {
                            fileDescriptor->FileInterface = originalFileInterface;
                        }

                        if (fileDescriptor->FileMode == ClientFileMode.LoadUnpackedResource)
                        {
                            fileDescriptor->FileMode = originalFileMode;
                        }

                        Span<byte> currentPath = new(GetFilePathPointer(fileDescriptor), originalFilePath.Length);

                        if (currentPath.SequenceEqual(temporaryFilePath))
                        {
                            originalFilePath.CopyTo(currentPath);
                        }
                    }
                }
            });

    private nint CheckFileStateDetour(nint service, ulong crc64)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            if (TryGetActiveLocalFileJob(crc64, out ActiveLocalFileJob activeJob))
            {
                switch (activeJob.Kind)
                {
                    case ObjectLocalFileKind.Model when CanLoadLocalModels():
                        return CustomModelFileFlag;
                    case ObjectLocalFileKind.Texture when CanLoadLocalTextures():
                        return nint.Zero;
                }
            }
        }
        catch (Exception ex)
        {
            ObjectResourceLog.Write(() => _logger.LogError(ex, "object resource check file state hook failed"));
        }

        return _checkFileStateHook!.OriginalDisposeSafe(service, crc64);
    }

    private byte LoadMdlFileExternDetour(ResourceHandle* handle, nint unknown0, bool unknown1, nint state)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();

        if (state != CustomModelFileFlag)
        {
            return _loadMdlFileExternHook!.OriginalDisposeSafe(handle, unknown0, unknown1, state);
        }

        try
        {
            if (_readBindings.LoadModel == null
                || !TryGetMatchingActiveLocalFileJob(ObjectLocalFileKind.Model, handle, null, out ActiveLocalFileJob activeJob))
            {
                return 0;
            }

            using var scope = EnterScopedHandleCollectionScope(handle);
            if (!ObjectResourcePathEncoding.TryReadActualScopedHandlePath(handle, out string actualPath))
            {
                return _readBindings.LoadModel(handle, (ClientFileDescriptor*)unknown0, unknown1);
            }

            if (!string.Equals(ObjectResourcePathUtility.NormalizeTrackedPath(actualPath), activeJob.Path, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return ObjectResourcePathEncoding.WithTemporaryHandlePath(
                handle,
                actualPath,
                (Owner: this, Handle: (nint)handle, Unknown0: unknown0, Unknown1: unknown1),
                static state => state.Owner._readBindings.LoadModel!(
                    (ResourceHandle*)state.Handle,
                    (ClientFileDescriptor*)state.Unknown0,
                    state.Unknown1));
        }
        catch (Exception ex)
        {
            LogFileHookFailure(ex, "model extern load", handle);
            return 0;
        }
    }

    internal byte TextureOnLoadDetour(TextureResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            if (ObjectMemoryResourceService.IsMemoryFileDescriptor(descriptor)
             && _memoryResourceService.TryReadTextureResource(handle, descriptor, unknown0 != 0, out byte memoryResult))
            {
                return memoryResult;
            }

            byte result = _textureOnLoadHook!.OriginalDisposeSafe(handle, descriptor, unknown0);

            if (descriptor == null || descriptor->FileMode != ClientFileMode.LoadUnpackedResource
                || !TryGetMatchingActiveLocalFileJob(ObjectLocalFileKind.Texture, (ResourceHandle*)handle, descriptor, out _))
            {
                return result;
            }

            if (_readBindings.LoadTexture == null)
            {
                return 0;
            }

            result = _readBindings.LoadTexture(handle, _lodService.GetLod(handle), descriptor, unknown0 != 0);
            _readBindings.UpdateTextureCategory?.Invoke(handle);
            return result;
        }
        catch (Exception ex)
        {
            LogFileHookFailure(ex, "texture on load", (ResourceHandle*)handle);
            return 0;
        }
    }

    internal byte SoundOnLoadDetour(ResourceHandle* handle, ClientFileDescriptor* descriptor, byte unknown0)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();
        try
        {
            if (ObjectMemoryResourceService.IsMemoryFileDescriptor(descriptor))
            {
                return _memoryResourceService.TryReadSoundResource(handle, descriptor, unknown0 != 0, out byte memoryResult)
                    ? memoryResult
                    : (byte)0;
            }

            if (descriptor != null
                && descriptor->FileMode == ClientFileMode.LoadUnpackedResource
                && TryGetMatchingActiveLocalFileJob(ObjectLocalFileKind.Sound, handle, descriptor, out _))
            {
                return _readBindings.LoadSound != null
                    ? _readBindings.LoadSound(handle, descriptor, unknown0)
                    : (byte)0;
            }

            return _soundOnLoadHook!.OriginalDisposeSafe(handle, descriptor, unknown0);
        }
        catch (Exception ex)
        {
            LogFileHookFailure(ex, "sound on load", handle);
            return 0;
        }
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private bool TryEnableHooks()
        => !IsDisposing && _createFileHook.Enable() && _hooks.Enable();

    private ActiveLocalFileJobToken EnterActiveLocalFileJob(ClientFileDescriptor* descriptor, string normalizedPath, ObjectLocalFileKind kind)
    {
        if (descriptor == null
            || descriptor->ResourceHandle == null
            || kind == ObjectLocalFileKind.DirectRead
            || normalizedPath.Length == 0
            || !ObjectThreadLocalUtility.TryRead(_activeLocalFileJob, null, out ActiveLocalFileJob? previousJob))
        {
            return default(ActiveLocalFileJobToken);
        }

        ResourceHandle* handle = descriptor->ResourceHandle;
        ulong pathCrc = ObjectResourcePathEncoding.ComputeFilePathCrc64(normalizedPath);
        ulong handleCrc = ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            ? ObjectResourcePathEncoding.ComputeFilePathCrc64(handlePath)
            : 0;
        ActiveLocalFileJob activeJob = new((nint)descriptor, (nint)handle, normalizedPath, kind, pathCrc, handleCrc);
        return ObjectThreadLocalUtility.TryWrite(_activeLocalFileJob, activeJob)
            ? new ActiveLocalFileJobToken(this, previousJob)
            : default(ActiveLocalFileJobToken);
    }

    private ActiveLocalFileJobToken SuspendActiveLocalFileJob()
        => ObjectThreadLocalUtility.TryRead(_activeLocalFileJob, null, out ActiveLocalFileJob? previousJob)
         && ObjectThreadLocalUtility.TryWrite(_activeLocalFileJob, null)
            ? new ActiveLocalFileJobToken(this, previousJob)
            : default;

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
            || (descriptor != null && (nint)descriptor != job.FileDescriptor))
        {
            return false;
        }

        if (ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            && (!ObjectResolvedPath.TryCreate(ObjectScopedResourcePathUtility.Strip(handlePath), out ObjectResolvedPath actualPath)
                || actualPath.IsMemory
                || actualPath.IsLocalFile && !string.Equals(actualPath.Path, job.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        activeJob = job;
        return true;
    }

    private bool TryReadActiveLocalFileJob(out ActiveLocalFileJob activeJob)
    {
        activeJob = default;
        if (!ObjectThreadLocalUtility.TryRead(_activeLocalFileJob, null, out ActiveLocalFileJob? currentJob)
            || currentJob is not { } job
            || ((ClientFileDescriptor*)job.FileDescriptor)->ResourceHandle != (ResourceHandle*)job.ResourceHandle)
        {
            return false;
        }

        activeJob = job;
        return true;
    }

    private void RestoreActiveLocalFileJob(ActiveLocalFileJob? previousJob)
        => _ = ObjectThreadLocalUtility.TryWrite(_activeLocalFileJob, previousJob);

    private void LogRejectedFileJob(string reason, nint address, string path, long resourceScopeId = 0)
    {
        _resourceTracker.TryGetHandleScope(address, out ObjectResourceScope scope);
        ObjectResourceLog.Write(() => _logger.LogWarning("object resource file job rejected; reason={Reason}; address=0x{Address:X}; path={Path}; collection={CollectionId}; generation={ResourceScopeId}",
            reason, (ulong)address, path, scope.ResourceCollectionId, resourceScopeId != 0 ? resourceScopeId : scope.ResourceScopeId));
    }

    internal void LogFileHookFailure(Exception exception, string hook, ResourceHandle* handle)
        => ObjectResourceLog.LogHookFailure(_logger, _resourceTracker, exception, hook, (nint)handle);

    private static char* GetFilePathPointer(ClientFileDescriptor* fileDescriptor)
        => (char*)((byte*)fileDescriptor + FileDescriptorFilePathOffset);

    private ObjectResourceLoadScopeToken EnterScopedHandleCollectionScope(ResourceHandle* handle)
    {
        if (handle == null
            || !ObjectResourcePathEncoding.TryReadHandlePath(handle, out string handlePath)
            || !ObjectScopedResourcePathUtility.TryParse(handlePath, out ObjectScopedResourcePath scopedPath))
        {
            return default;
        }

        return _loadScope.EnterResourceScope(scopedPath.ResourceScopeId);
    }

    private bool CanLoadLocalModels()
        => _checkFileStateHook != null && _loadMdlFileExternHook != null && _readBindings.LoadModel != null;

    private bool CanLoadLocalTextures()
        => _checkFileStateHook != null && _textureOnLoadHook != null && _readBindings.LoadTexture != null && _readBindings.UpdateTextureCategory != null;

    private bool CanLoadLocalSounds()
        => _soundOnLoadHook != null && _readBindings.LoadSound != null;

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
            && _memoryResourceService.CanLoadMemoryResource(memoryResource)
            && CanRouteMemoryResourceKind(memoryResource.GamePath);

    private bool CanRouteMemoryResourceKind(string gamePath)
        => ObjectAssetPathRules.ClassifyResourcePath(gamePath) switch
        {
            ObjectResourcePathKind.Texture => _textureOnLoadHook != null,
            ObjectResourcePathKind.Sound => _soundOnLoadHook != null,
            _ => true,
        };

    private bool CanLoadRootedLocalFiles()
        => CanHandleCustomFileJobs() && _readFile != null;

    private bool CanHandleCustomFileJobs()
        => !IsDisposing && _fileJobHook != null;

    internal static ObjectLocalFileKind ClassifyLocalResource(ResourceType resourceType)
        => resourceType switch
        {
            ResourceType.Mdl => ObjectLocalFileKind.Model,
            ResourceType.Tex => ObjectLocalFileKind.Texture,
            ResourceType.Scd => ObjectLocalFileKind.Sound,
            _ => ObjectLocalFileKind.DirectRead,
        };

    private readonly struct ActiveLocalFileJobToken(ObjectFileReadService? owner, ActiveLocalFileJob? previousJob) : IDisposable
    {
        public void Dispose()
            => owner?.RestoreActiveLocalFileJob(previousJob);
    }
}
