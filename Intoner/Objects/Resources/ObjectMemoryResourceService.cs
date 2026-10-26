using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Intoner.Objects.Assets;
using Intoner.Services.Interop;
using Intoner.Utils;
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
    /// <exception cref="ArgumentException">the owner is empty, the game path is invalid or unsupported, or the data is empty</exception>
    /// <exception cref="ObjectDisposedException">the service is shutting down or has been disposed</exception>
    ObjectResolvedPath RegisterResource(string ownerId, string gamePath, byte[] data);

    /// <summary>
    /// Removes every memory resource owned by the given owner.
    /// </summary>
    /// <param name="ownerId">the runtime owner id</param>
    void ReleaseOwner(string ownerId);

    /// <summary>
    /// Retains one existing memory resource under the given owner.
    /// </summary>
    /// <param name="ownerId">the runtime owner id</param>
    /// <param name="memoryResourcePath">the registered memory resource path</param>
    /// <param name="resource">the retained memory resource when found</param>
    /// <returns>true when the resource exists and is retained by the owner</returns>
    bool TryAcquireResource(string ownerId, string memoryResourcePath, out ObjectMemoryResource resource);

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
    /// <returns>1 after completion, or 0 when the read could not start</returns>
    unsafe byte ReadResource(ClientFileDescriptor* fileDescriptor, ObjectMemoryResource resource);

    /// <summary>
    /// Handles a texture resource read when the descriptor is memory backed.
    /// </summary>
    /// <param name="handle">the texture resource handle</param>
    /// <param name="descriptor">the native file descriptor</param>
    /// <param name="resetPosition">whether the native reader resets the descriptor's file position</param>
    /// <param name="result">the native read result when handled</param>
    /// <returns>true when the read was handled by the memory loader</returns>
    /// <remarks> returns true with a zero result for owned descriptors with a null or different resource handle; returns false for foreign descriptors </remarks>
    unsafe bool TryReadTextureResource(TextureResourceHandle* handle, ClientFileDescriptor* descriptor, bool resetPosition, out byte result);

    /// <summary>
    /// Handles a sound resource read when the descriptor is memory backed.
    /// </summary>
    /// <param name="handle">the sound resource handle</param>
    /// <param name="descriptor">the native file descriptor</param>
    /// <param name="resetPosition">whether the native reader resets the descriptor's file position</param>
    /// <param name="result">the native read result when handled</param>
    /// <returns>true when the read was handled by the memory loader</returns>
    /// <remarks> returns true with a zero result for owned descriptors with a null or different resource handle; returns false for foreign descriptors </remarks>
    unsafe bool TryReadSoundResource(ResourceHandle* handle, ClientFileDescriptor* descriptor, bool resetPosition, out byte result);
}

internal sealed unsafe class ObjectMemoryResourceService : IObjectMemoryResourceService
{
    private const byte MemoryFileModeValue = 0x10;
    private const byte FileOpenSucceeded = 1;
    private const byte FileOpenMissing = unchecked((byte)-1);
    private const byte FileOperationCancelled = 2;
    private const byte FileReadInvalid = unchecked((byte)-10);

    [ThreadStatic] private static nint _activeDescriptor;
    [ThreadStatic] private static nint _activeFileInterface;
    [ThreadStatic] private static nint _activeResourceHandle;

    private readonly ILogger<ObjectMemoryResourceService> _logger;
    private readonly ObjectTextureLodService _lodService;
    private readonly ObjectMemoryResourceRegistry _registry;
    private readonly ObjectResourceReadBindings _readBindings;
    private readonly DisposalState _disposeState = new();
    private readonly InteropHookCollection _hooks;
    private readonly Hook<ClientFileDescriptor.Delegates.Read>? _fileDescriptorReadHook;
    private readonly Hook<ResourceHandle.Delegates.Read>? _modelResourceReadHook;

    public ObjectMemoryResourceService(
        ILogger<ObjectMemoryResourceService> logger,
        ObjectTextureLodService lodService,
        ObjectMemoryResourceRegistry registry,
        ObjectResourceReadBindings readBindings,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner)
    {
        _logger = logger;
        _lodService = lodService;
        _registry = registry;
        _readBindings = readBindings;
        _hooks = new InteropHookCollection(logger);

        try
        {
            _fileDescriptorReadHook = _hooks.Add(InteropHookUtility.CreateHookFromAddress<ClientFileDescriptor.Delegates.Read>(
                logger,
                gameInteropProvider,
                IntonerSignatures.ResourceFileDescriptorRead,
                FileDescriptorReadDetour));
            _modelResourceReadHook = _hooks.Add(InteropHookUtility.CreateHook<ResourceHandle.Delegates.Read>(
                logger,
                gameInteropProvider,
                sigScanner,
                IntonerSignatures.ResourceMemoryModelRead,
                ModelResourceReadDetour));
        }
        catch
        {
            _hooks.Dispose();
            throw;
        }
    }

    public ObjectResolvedPath RegisterResource(string ownerId, string gamePath, byte[] data)
    {
        using InteropHookCollection.CallbackScope operation = _hooks.EnterOperation();
        ObjectDisposedException.ThrowIf(!operation.IsActive || IsDisposing, this);
        return _registry.RegisterResource(ownerId, gamePath, data);
    }

    public void ReleaseOwner(string ownerId)
        => _registry.ReleaseOwner(ownerId);

    public bool TryAcquireResource(string ownerId, string memoryResourcePath, out ObjectMemoryResource resource)
    {
        resource = default;
        return !IsDisposing && _registry.TryAcquireResource(ownerId, memoryResourcePath, out resource);
    }

    public bool CanLoadMemoryResourcePath(string memoryResourcePath)
        => TryGetResource(memoryResourcePath, out ObjectMemoryResource resource)
            && CanLoadMemoryResource(resource);

    public bool CanLoadMemoryResource(ObjectMemoryResource memoryResource)
        => !IsDisposing
            && HasLoadableResourceShape(memoryResource)
            && TryEnableHooks()
            && CanReadResourceKind(ObjectAssetPathRules.ClassifyResourcePath(memoryResource.GamePath));

    public bool TryGetResource(string memoryResourcePath, out ObjectMemoryResource resource)
    {
        resource = default;
        return !IsDisposing && _registry.TryGetResource(memoryResourcePath, out resource);
    }

    public byte ReadResource(ClientFileDescriptor* fileDescriptor, ObjectMemoryResource resource)
    {
        using InteropHookCollection.CallbackScope operation = _hooks.EnterOperation();

        if (!operation.IsActive
            || IsDisposing
            || fileDescriptor == null
            || fileDescriptor->ResourceHandle == null
            || !CanLoadMemoryResource(resource))
        {
            return 0;
        }

        return ObjectResourcePathEncoding.WithNullTerminatedUtf8(
            resource.GamePath,
            (FileDescriptor: (nint)fileDescriptor, Data: resource.Data),
            static (pathPointer, pathByteCount, state) =>
            {
                var fileDescriptor = (ClientFileDescriptor*)state.FileDescriptor;
                fixed (byte* resourceDataPointer = state.Data)
                {
                    MemoryFileInterface memoryFile = new();
                    using MemoryDescriptorScope descriptorScope = new((nint)fileDescriptor, (nint)(&memoryFile.FileInterface));
                    using var resourcePath = new ObjectResourceHandlePathScope(
                        fileDescriptor->ResourceHandle,
                        pathPointer,
                        pathByteCount);
                    return ReadMemoryFile(
                        fileDescriptor,
                        resourceDataPointer,
                        (ulong)state.Data.LongLength);
                }
            });
    }

    public bool TryReadTextureResource(TextureResourceHandle* handle, ClientFileDescriptor* descriptor, bool resetPosition, out byte result)
    {
        result = 0;
        if (!IsMemoryFileDescriptor(descriptor))
        {
            return false;
        }

        if (!HasMatchingResourceHandle((ResourceHandle*)handle, descriptor) || _readBindings.LoadTexture == null)
        {
            return true;
        }

        result = _readBindings.LoadTexture(handle, _lodService.GetLod(handle), descriptor, resetPosition);
        _readBindings.UpdateTextureCategory?.Invoke(handle);
        return true;
    }

    public bool TryReadSoundResource(ResourceHandle* handle, ClientFileDescriptor* descriptor, bool resetPosition, out byte result)
    {
        result = 0;
        if (!IsMemoryFileDescriptor(descriptor))
        {
            return false;
        }

        if (!HasMatchingResourceHandle(handle, descriptor) || _readBindings.LoadSound == null)
        {
            return true;
        }

        result = _readBindings.LoadSound(handle, descriptor, resetPosition ? (byte)1 : (byte)0);
        return true;
    }

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose() && _hooks.IsDisposed)
        {
            return;
        }

        _hooks.Dispose();

        if (_hooks.IsDisposed)
        {
            _registry.Clear();
        }
    }

    private bool IsDisposing
        => _disposeState.IsDisposing;

    private bool TryEnableHooks()
        => !IsDisposing && _hooks.Enable();

    private byte ModelResourceReadDetour(ResourceHandle* handle, ClientFileDescriptor* descriptor, bool failedToOpen)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();

        try
        {
            if (!IsMemoryFileDescriptor(descriptor))
            {
                return _modelResourceReadHook!.OriginalDisposeSafe(handle, descriptor, failedToOpen);
            }

            return HasMatchingResourceHandle(handle, descriptor) && _readBindings.LoadModel != null
                ? _readBindings.LoadModel(handle, descriptor, false)
                : (byte)0;
        }
        catch (Exception ex)
        {
            ObjectResourceLog.Write(() => _logger.LogError(ex, "object memory model read hook failed"));
            return 0;
        }
    }

    private static byte ReadMemoryFile(ClientFileDescriptor* fileDescriptor, byte* resourceBytes, ulong resourceLength)
    {
        ResourceHandle* resourceHandle = fileDescriptor->ResourceHandle;
        ClientFileInterface* borrowedFileInterface = fileDescriptor->FileInterface;
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
            borrowedFileInterface->PlatformHandle = (nint)resourceBytes;
            borrowedFileInterface->CachedFileSize = resourceLength;
            borrowedFileInterface->IsFileOpen = true;
            fileResult = ReadMemoryResource(resourceHandle, fileDescriptor);
            borrowedFileInterface->IsFileOpen = false;
        }

        if (ReadFileHandleState(fileHandleManager, ref fileHandle) != 0)
        {
            fileResult = FileOperationCancelled;
        }

        ResetFileHandle(fileHandleManager, ref fileHandle, fileDescriptor, fileResult);
        resourceHandle->FinishLoad(fileDescriptor, fileResult, 0);
        return FileOpenSucceeded;
    }

    private static byte ReadMemoryResource(ResourceHandle* resourceHandle, ClientFileDescriptor* fileDescriptor)
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
            ? resourceHandle->Reread(fileDescriptor, false)
            : resourceHandle->Read(fileDescriptor, false);
    }

    private byte FileDescriptorReadDetour(
        ClientFileDescriptor* fileDescriptor,
        byte* outputBuffer,
        ulong length,
        ulong start,
        bool resetPosition)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();
        using ObjectResourceLog.WriteScope logWrites = ObjectResourceLog.DeferNativeWrites();

        if (!IsMemoryFileDescriptor(fileDescriptor))
        {
            return _fileDescriptorReadHook!.OriginalDisposeSafe(fileDescriptor, outputBuffer, length, start, resetPosition);
        }

        if ((nint)fileDescriptor->ResourceHandle != _activeResourceHandle)
        {
            return FileReadInvalid;
        }

        var memoryFile = (MemoryFileInterface*)fileDescriptor->FileInterface;
        ulong totalSize = memoryFile->FileInterface.CachedFileSize;
        ulong readStart = start != 0 || resetPosition ? start : memoryFile->Position;

        if (readStart > totalSize || totalSize > int.MaxValue)
        {
            return FileReadInvalid;
        }

        if (length == 0)
        {
            length = totalSize - readStart;
        }

        if (length == 0)
        {
            memoryFile->Position = readStart;
            return FileOpenSucceeded;
        }

        if (outputBuffer == null
            || length > totalSize - readStart
            || memoryFile->FileInterface.PlatformHandle == nint.Zero)
        {
            return FileReadInvalid;
        }

        ReadOnlySpan<byte> source = new((void*)memoryFile->FileInterface.PlatformHandle, (int)totalSize);
        source.Slice((int)readStart, (int)length).CopyTo(new Span<byte>(outputBuffer, (int)length));
        memoryFile->Position = readStart + length;
        return FileOpenSucceeded;
    }

    internal static bool IsMemoryFileDescriptor(ClientFileDescriptor* descriptor)
        => (nint)descriptor == _activeDescriptor
            && descriptor != null
            && descriptor->FileMode == (ClientFileMode)MemoryFileModeValue
            && (nint)descriptor->FileInterface == _activeFileInterface
            && descriptor->FileInterface != null;

    private static bool HasMatchingResourceHandle(ResourceHandle* handle, ClientFileDescriptor* descriptor)
        => handle != null && descriptor->ResourceHandle == handle && (nint)handle == _activeResourceHandle;

    private bool CanReadResourceKind(ObjectResourcePathKind kind)
    {
        if (_fileDescriptorReadHook == null)
        {
            return false;
        }

        return kind switch
        {
            ObjectResourcePathKind.Model => _modelResourceReadHook != null && _readBindings.LoadModel != null,
            ObjectResourcePathKind.Texture => _readBindings.LoadTexture != null && _readBindings.UpdateTextureCategory != null,
            ObjectResourcePathKind.Sound => _readBindings.LoadSound != null,
            _ => true,
        };
    }

    private static bool HasLoadableResourceShape(ObjectMemoryResource resource)
    {
        if (string.IsNullOrEmpty(resource.GamePath)
         || string.IsNullOrEmpty(resource.MemoryPath)
         || resource.Data is not { Length: > 0 }
         || !ObjectResourcePathUtility.TryNormalizeSupportedGamePath(resource.GamePath, out string normalizedGamePath)
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
        using var managerLock = fileHandleManager->Lock.Acquire();
        fileHandle.AllocatedBuffer = null;
        fileHandle.ResultLength = fileDescriptor->Length;
        fileHandle.Reset(fileResult);
    }

    private static byte InterlockedRead(ref byte value)
        => Interlocked.CompareExchange(ref value, (byte)0, (byte)0);

    [StructLayout(LayoutKind.Auto)]
    private readonly struct MemoryDescriptorScope : IDisposable
    {
        private readonly nint _previousDescriptor;
        private readonly nint _previousFileInterface;
        private readonly nint _previousResourceHandle;
        private readonly ClientFileDescriptor* _descriptor;
        private readonly ClientFileMode _originalFileMode;
        private readonly ClientFileInterface* _originalFileInterface;
        private readonly ClientFileInterface* _borrowedFileInterface;

        public MemoryDescriptorScope(nint descriptor, nint fileInterface)
        {
            _previousDescriptor = _activeDescriptor;
            _previousFileInterface = _activeFileInterface;
            _previousResourceHandle = _activeResourceHandle;
            _descriptor = (ClientFileDescriptor*)descriptor;
            _originalFileMode = _descriptor->FileMode;
            _originalFileInterface = _descriptor->FileInterface;
            _borrowedFileInterface = (ClientFileInterface*)fileInterface;
            _descriptor->FileMode = (ClientFileMode)MemoryFileModeValue;
            _descriptor->FileInterface = _borrowedFileInterface;
            SetActiveDescriptor(descriptor, fileInterface, (nint)_descriptor->ResourceHandle);
        }

        public void Dispose()
        {
            if (_descriptor->FileMode == (ClientFileMode)MemoryFileModeValue)
            {
                _descriptor->FileMode = _originalFileMode;
            }

            if (_descriptor->FileInterface == _borrowedFileInterface)
            {
                _descriptor->FileInterface = _originalFileInterface;
            }

            SetActiveDescriptor(_previousDescriptor, _previousFileInterface, _previousResourceHandle);
        }

        private static void SetActiveDescriptor(nint descriptor, nint fileInterface, nint resourceHandle)
        {
            _activeDescriptor = descriptor;
            _activeFileInterface = fileInterface;
            _activeResourceHandle = resourceHandle;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x30)]
    private struct MemoryFileInterface
    {
        [FieldOffset(0x00)] public ClientFileInterface FileInterface;
        [FieldOffset(0x28)] public ulong Position;
    }
}
