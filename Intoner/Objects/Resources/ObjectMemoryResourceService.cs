using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using ClientFileDescriptor = FFXIVClientStructs.FFXIV.Client.System.File.FileDescriptor;
using ClientFileHandle = FFXIVClientStructs.FFXIV.Client.System.File.FileHandle;
using ClientFileHandleManager = FFXIVClientStructs.FFXIV.Client.System.File.FileHandleManager;
using ClientFileInterface = FFXIVClientStructs.FFXIV.Client.System.File.FileInterface;
using ClientFileMode = FFXIVClientStructs.FFXIV.Client.System.File.FileMode;

namespace Intoner.Objects.Resources;

/// <summary>
/// Stores object owned resource bytes and provides native read helpers for memory backed resources.
/// </summary>
internal interface IObjectMemoryResourceService : IDisposable
{
    /// <summary>
    /// Registers one memory backed resource under the given owner.
    /// </summary>
    /// <param name="ownerId">the runtime owner id used for cleanup</param>
    /// <param name="gamePath">the real game path represented by the bytes</param>
    /// <param name="data">resource bytes to copy into the memory registry</param>
    /// <returns>the resolved memory path to use in object resource redirects</returns>
    ObjectResolvedPath RegisterResource(string ownerId, string gamePath, byte[] data);

    /// <summary>
    /// Removes every memory resource owned by the given owner.
    /// </summary>
    /// <param name="ownerId">the runtime owner id</param>
    void ReleaseOwner(string ownerId);

    /// <summary>
    /// Removes memory resources owned by the owner that are not present in the active path set.
    /// </summary>
    /// <param name="ownerId">the runtime owner id</param>
    /// <param name="activeMemoryPaths">memory paths still referenced by active runtime data</param>
    void RetainOwnerResources(string ownerId, IReadOnlySet<string> activeMemoryPaths);

    /// <summary>
    /// Checks whether one memory path is registered and can be handled by the current native hooks.
    /// </summary>
    /// <param name="memoryResourcePath">the memory resource path</param>
    /// <returns>true when the memory resource can be loaded</returns>
    bool CanLoadMemoryResourcePath(string memoryResourcePath);

    /// <summary>
    /// Checks whether one registered memory resource can be handled by the current native hooks.
    /// </summary>
    /// <param name="memoryResource">the registered memory resource</param>
    /// <returns>true when the memory resource can be loaded</returns>
    bool CanLoadMemoryResource(ObjectMemoryResource memoryResource);

    /// <summary>
    /// Tries to resolve one registered memory resource path.
    /// </summary>
    /// <param name="memoryResourcePath">the memory resource path</param>
    /// <param name="resource">the memory resource when found</param>
    /// <returns>true when the memory resource is currently registered</returns>
    bool TryGetResource(string memoryResourcePath, out ObjectMemoryResource resource);

    /// <summary>
    /// Reads one registered memory resource through the native file descriptor pipeline.
    /// </summary>
    /// <param name="fileDescriptor">the native file descriptor</param>
    /// <param name="resource">the resolved memory resource</param>
    /// <returns>the native file job result</returns>
    unsafe byte ReadResource(ClientFileDescriptor* fileDescriptor, ObjectMemoryResource resource);

    /// <summary>
    /// Handles a texture resource read when the descriptor is memory backed.
    /// </summary>
    /// <param name="handle">the texture resource handle</param>
    /// <param name="descriptor">the native file descriptor</param>
    /// <param name="failedToOpen">whether the descriptor open failed</param>
    /// <param name="result">the native read result when handled</param>
    /// <returns>true when the read was handled by the memory loader</returns>
    unsafe bool TryReadTextureResource(TextureResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen, out byte result);

    /// <summary>
    /// Handles a sound resource read when the descriptor is memory backed.
    /// </summary>
    /// <param name="handle">the sound resource handle</param>
    /// <param name="descriptor">the native file descriptor</param>
    /// <param name="failedToOpen">whether the descriptor open failed</param>
    /// <param name="result">the native read result when handled</param>
    /// <returns>true when the read was handled by the memory loader</returns>
    unsafe bool TryReadSoundResource(ResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen, out byte result);
}

internal sealed unsafe class ObjectMemoryResourceService : IObjectMemoryResourceService
{
    private const byte MemoryFileModeValue = 0x0F;
    private const byte FileOpenSucceeded = 1;
    private const byte FileOpenMissing = unchecked((byte)-1);
    private const byte FileOperationCancelled = 2;
    private const byte FileReadInvalid = unchecked((byte)-10);

    private enum MemoryResourceReadKind
    {
        Direct,
        Model,
        Texture,
        Sound,
    }

    private delegate byte FileDescriptorReadDelegate(
        ClientFileDescriptor* fileDescriptor,
        byte* outputBuffer,
        ulong length,
        ulong start,
        bool resetPosition);

    private delegate byte ModelResourceReadDelegate(ModelResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen);
    private delegate byte ModelResourceReadUnpackedDelegate(ModelResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen);
    private delegate byte TextureResourceReadUnpackedDelegate(TextureResourceHandle* handle, int lodLevel, ClientFileDescriptor* descriptor, bool failedToOpen);
    private delegate byte SoundResourceReadUnpackedDelegate(ResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen);
    private delegate void UpdateTextureCategoryDelegate(TextureResourceHandle* handle);

    private readonly ILogger<ObjectMemoryResourceService> _logger;
    private readonly ObjectTextureLodService _lodService;
    private readonly ObjectMemoryResourceRegistry _registry = new();
    private readonly ObjectDisposalState _disposeState = new();
    private readonly ObjectLockedOnce _enableOnce = new();
    private readonly Hook<FileDescriptorReadDelegate>? _fileDescriptorReadHook;
    private readonly Hook<ModelResourceReadDelegate>? _modelResourceReadHook;
    private readonly ModelResourceReadUnpackedDelegate? _modelResourceReadUnpacked;
    private readonly TextureResourceReadUnpackedDelegate? _textureResourceReadUnpacked;
    private readonly SoundResourceReadUnpackedDelegate? _soundResourceReadUnpacked;
    private readonly UpdateTextureCategoryDelegate? _updateTextureCategory;

    public ObjectMemoryResourceService(
        ILogger<ObjectMemoryResourceService> logger,
        ObjectTextureLodService lodService,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _lodService = lodService;

        _fileDescriptorReadHook = ObjectInteropHookUtility.CreateHookFromAddress<FileDescriptorReadDelegate>(
            _logger,
            gameInteropProvider,
            ObjectSignatures.ResourceFileDescriptorRead,
            FileDescriptorReadDetour);
        _modelResourceReadHook = ObjectInteropHookUtility.CreateHook<ModelResourceReadDelegate>(
            _logger,
            gameInteropProvider,
            sigScanner,
            ObjectSignatures.ResourceMemoryModelRead,
            ModelResourceReadDetour);
        _modelResourceReadUnpacked = ObjectInteropHookUtility.CreateDelegate<ModelResourceReadUnpackedDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceLoadMdlFileLocal);
        _textureResourceReadUnpacked = ObjectInteropHookUtility.CreateDelegate<TextureResourceReadUnpackedDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceLoadTexFileLocal);
        _soundResourceReadUnpacked = ObjectInteropHookUtility.CreateDelegate<SoundResourceReadUnpackedDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceLoadScdFileLocal);
        _updateTextureCategory = ObjectInteropHookUtility.CreateDelegate<UpdateTextureCategoryDelegate>(
            _logger,
            sigScanner,
            ObjectSignatures.ResourceUpdateTextureCategory);

    }

    public ObjectResolvedPath RegisterResource(string ownerId, string gamePath, byte[] data)
        => _registry.RegisterResource(ownerId, gamePath, data);

    public void ReleaseOwner(string ownerId)
        => _registry.ReleaseOwner(ownerId);

    public void RetainOwnerResources(string ownerId, IReadOnlySet<string> activeMemoryPaths)
        => _registry.RetainOwnerResources(ownerId, activeMemoryPaths);

    public bool CanLoadMemoryResourcePath(string memoryResourcePath)
        => TryGetResource(memoryResourcePath, out ObjectMemoryResource resource)
            && CanLoadMemoryResource(resource);

    public bool CanLoadMemoryResource(ObjectMemoryResource memoryResource)
        => !IsDisposing
            && HasLoadableResourceShape(memoryResource)
            && TryEnableHooks()
            && CanReadResourceKind(ClassifyReadKind(memoryResource.GamePath));

    public bool TryGetResource(string memoryResourcePath, out ObjectMemoryResource resource)
    {
        resource = default;
        return !IsDisposing && _registry.TryGetResource(memoryResourcePath, out resource);
    }

    public byte ReadResource(ClientFileDescriptor* fileDescriptor, ObjectMemoryResource resource)
    {
        if (IsDisposing
            || fileDescriptor == null
            || fileDescriptor->ResourceHandle == null
            || !CanLoadMemoryResource(resource))
        {
            return 0;
        }

        return ObjectResourcePathEncoding.WithNullTerminatedUtf8(
            resource.GamePath,
            (Owner: this, FileDescriptor: (nint)fileDescriptor, Data: resource.Data),
            static (pathPointer, pathByteCount, state) =>
            {
                var fileDescriptor = (ClientFileDescriptor*)state.FileDescriptor;
                ClientFileMode originalFileMode = fileDescriptor->FileMode;
                ClientFileInterface* originalFileInterface = fileDescriptor->FileInterface;
                fixed (byte* resourceDataPointer = state.Data)
                {
                    MemoryFileInterface memoryFile = new();
                    fileDescriptor->FileMode = (ClientFileMode)MemoryFileModeValue;
                    fileDescriptor->FileInterface = &memoryFile.FileInterface;

                    try
                    {
                        using var resourcePath = new ObjectResourceHandlePathScope(
                            fileDescriptor->ResourceHandle,
                            pathPointer,
                            pathByteCount);
                        return state.Owner.ReadMemoryFile(
                            fileDescriptor,
                            resourceDataPointer,
                            (ulong)state.Data.LongLength);
                    }
                    finally
                    {
                        fileDescriptor->FileMode = originalFileMode;
                        fileDescriptor->FileInterface = originalFileInterface;
                    }
                }
            });
    }

    public bool TryReadTextureResource(TextureResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen, out byte result)
    {
        result = 0;
        if (!IsMemoryFileDescriptor(descriptor))
        {
            return false;
        }

        if (_textureResourceReadUnpacked == null)
        {
            return true;
        }

        result = _textureResourceReadUnpacked(handle, _lodService.GetLod(handle), descriptor, failedToOpen);
        _updateTextureCategory?.Invoke(handle);
        return true;
    }

    public bool TryReadSoundResource(ResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen, out byte result)
    {
        result = 0;
        if (!IsMemoryFileDescriptor(descriptor))
        {
            return false;
        }

        if (_soundResourceReadUnpacked == null)
        {
            return true;
        }

        result = _soundResourceReadUnpacked(handle, descriptor, failedToOpen);
        return true;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        ObjectInteropHookUtility.DisposeHook(_modelResourceReadHook);
        ObjectInteropHookUtility.DisposeHook(_fileDescriptorReadHook);
        _registry.Clear();
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private bool TryEnableHooks()
        => _enableOnce.TryExecute(
            () =>
            {
                _fileDescriptorReadHook?.Enable();
                _modelResourceReadHook?.Enable();
                return true;
            },
            () => !IsDisposing);

    private byte ModelResourceReadDetour(ModelResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen)
    {
        if (!IsMemoryFileDescriptor(descriptor))
        {
            return _modelResourceReadHook!.Original(handle, descriptor, failedToOpen);
        }

        return _modelResourceReadUnpacked != null
            ? _modelResourceReadUnpacked(handle, descriptor, failedToOpen: false)
            : (byte)0;
    }

    private byte ReadMemoryFile(ClientFileDescriptor* fileDescriptor, byte* resourceBytes, ulong resourceLength)
    {
        ClientFileHandleManager* fileHandleManager = ClientFileHandleManager.Instance();
        if (fileHandleManager == null)
        {
            return 0;
        }

        ref ClientFileHandle fileHandle = ref fileHandleManager->GetFileHandle(fileDescriptor->FileHandleIndex);
        byte fileResult = FileOpenSucceeded;
        if (ReadFileHandleState(fileHandleManager, ref fileHandle) != 0)
        {
            fileResult = FileOperationCancelled;
        }
        else if (resourceBytes == null || resourceLength == 0)
        {
            fileResult = FileOpenMissing;
        }
        else
        {
            fileDescriptor->FileInterface->PlatformHandle = (nint)resourceBytes;
            fileDescriptor->FileInterface->CachedFileSize = resourceLength;
            fileDescriptor->FileInterface->IsFileOpen = true;
            fileResult = ReadMemoryResource(fileDescriptor->ResourceHandle, fileDescriptor, fileLoadFailed: false);
            fileDescriptor->FileInterface->IsFileOpen = false;
        }

        if (ReadFileHandleState(fileHandleManager, ref fileHandle) != 0)
        {
            fileResult = FileOperationCancelled;
        }

        ResetFileHandle(fileHandleManager, ref fileHandle, fileDescriptor, fileResult);
        fileDescriptor->ResourceHandle->FinishLoad(fileDescriptor, fileResult, 0);
        return FileOpenSucceeded;
    }

    private byte ReadMemoryResource(ResourceHandle* resourceHandle, ClientFileDescriptor* fileDescriptor, bool fileLoadFailed)
    {
        if (resourceHandle == null
            || fileDescriptor == null
            || fileDescriptor->FileInterface == null
            || InterlockedRead(ref resourceHandle->ReadState) == 3)
        {
            return FileOperationCancelled;
        }

        resourceHandle->LoadState = 4;
        if (InterlockedRead(ref resourceHandle->OtherState) == 2)
        {
            return resourceHandle->FileSize != 0
                ? FileOpenSucceeded
                : FileOperationCancelled;
        }

        ulong cachedFileSize = fileDescriptor->FileInterface->CachedFileSize;
        resourceHandle->FileSize2 = cachedFileSize <= uint.MaxValue ? (uint)cachedFileSize : uint.MaxValue;
        if (!TryComputeReadSize(fileDescriptor, cachedFileSize, out uint readSize))
        {
            return FileOperationCancelled;
        }

        resourceHandle->FileSize = readSize;
        return InterlockedRead(ref resourceHandle->OtherState) == 1
            ? resourceHandle->Reread(fileDescriptor, fileLoadFailed)
            : resourceHandle->Read(fileDescriptor, fileLoadFailed);
    }

    private byte FileDescriptorReadDetour(
        ClientFileDescriptor* fileDescriptor,
        byte* outputBuffer,
        ulong length,
        ulong start,
        bool resetPosition)
    {
        if (!IsMemoryFileDescriptor(fileDescriptor))
        {
            return _fileDescriptorReadHook!.Original(fileDescriptor, outputBuffer, length, start, resetPosition);
        }

        var memoryFile = (MemoryFileInterface*)fileDescriptor->FileInterface;
        ulong totalSize = memoryFile->FileInterface.CachedFileSize;
        if (length == 0)
        {
            length = start < totalSize ? totalSize - start : 0;
        }

        if (length == 0)
        {
            return FileOpenSucceeded;
        }

        if (outputBuffer == null
            || totalSize > int.MaxValue
            || length > int.MaxValue
            || memoryFile->FileInterface.PlatformHandle == nint.Zero)
        {
            return FileReadInvalid;
        }

        if (start != 0 || resetPosition)
        {
            memoryFile->Position = start;
        }

        ulong readStart = memoryFile->Position;
        if (readStart > totalSize || length > totalSize - readStart)
        {
            return FileReadInvalid;
        }

        ReadOnlySpan<byte> source = new((void*)memoryFile->FileInterface.PlatformHandle, (int)totalSize);
        source.Slice((int)readStart, (int)length).CopyTo(new Span<byte>(outputBuffer, (int)length));
        memoryFile->Position += length;
        return FileOpenSucceeded;
    }

    internal static bool IsMemoryFileDescriptor(ClientFileDescriptor* descriptor)
        => descriptor != null
            && descriptor->FileMode == (ClientFileMode)MemoryFileModeValue
            && descriptor->FileInterface != null;

    private bool CanReadResourceKind(MemoryResourceReadKind kind)
    {
        if (_fileDescriptorReadHook == null)
        {
            return false;
        }

        return kind switch
        {
            MemoryResourceReadKind.Model => _modelResourceReadHook != null && _modelResourceReadUnpacked != null,
            MemoryResourceReadKind.Texture => _textureResourceReadUnpacked != null,
            MemoryResourceReadKind.Sound => _soundResourceReadUnpacked != null,
            _ => true,
        };
    }

    private static MemoryResourceReadKind ClassifyReadKind(string gamePath)
        => ObjectAssetPathRules.ClassifyResourcePath(gamePath) switch
        {
            ObjectResourcePathKind.Model => MemoryResourceReadKind.Model,
            ObjectResourcePathKind.Texture or ObjectResourcePathKind.Atex => MemoryResourceReadKind.Texture,
            ObjectResourcePathKind.Sound => MemoryResourceReadKind.Sound,
            _ => MemoryResourceReadKind.Direct,
        };

    private static bool HasLoadableResourceShape(ObjectMemoryResource resource)
    {
        if (string.IsNullOrEmpty(resource.GamePath)
         || string.IsNullOrEmpty(resource.MemoryPath)
         || resource.Data is not { Length: > 0 }
         || !ObjectAssetPathRules.TryNormalizeSupportedResourcePath(resource.GamePath, out string normalizedGamePath)
         || !ObjectMemoryResourcePathUtility.TryParse(resource.MemoryPath, out ObjectMemoryResourcePath memoryPath))
        {
            return false;
        }

        return string.Equals(memoryPath.GamePath, normalizedGamePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryComputeReadSize(ClientFileDescriptor* fileDescriptor, ulong cachedFileSize, out uint readSize)
    {
        readSize = 0;
        if (fileDescriptor->StartOffset >= cachedFileSize)
        {
            return false;
        }

        ulong length = cachedFileSize - fileDescriptor->StartOffset;
        if (fileDescriptor->Length > 0)
        {
            length = Math.Min(length, fileDescriptor->Length);
        }

        if (length == 0 || length > uint.MaxValue)
        {
            return false;
        }

        readSize = (uint)length;
        return true;
    }

    private static byte ReadFileHandleState(ClientFileHandleManager* fileHandleManager, ref ClientFileHandle fileHandle)
    {
        using var managerLock = fileHandleManager->Lock.Acquire();
        return fileHandle.State2;
    }

    private static void ResetFileHandle(
        ClientFileHandleManager* fileHandleManager,
        ref ClientFileHandle fileHandle,
        ClientFileDescriptor* fileDescriptor,
        byte fileResult)
    {
        using (var managerLock = fileHandleManager->Lock.Acquire())
        {
            fileHandle.AllocatedBuffer = null;
            fileHandle.ResultLength = fileDescriptor->Length;
        }

        using (var managerLock = fileHandleManager->Lock.Acquire())
        {
            fileHandle.Reset(fileResult);
        }
    }

    private static byte InterlockedRead(ref byte value)
        => Interlocked.CompareExchange(ref value, (byte)0, (byte)0);

    [StructLayout(LayoutKind.Explicit, Size = 0x30)]
    private struct MemoryFileInterface
    {
        [FieldOffset(0x00)] public ClientFileInterface FileInterface;
        [FieldOffset(0x28)] public ulong Position;
    }
}

