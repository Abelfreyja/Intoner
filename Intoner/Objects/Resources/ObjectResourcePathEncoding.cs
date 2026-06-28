using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.STD;
using Intoner.Utils;
using System.Buffers;
using System.Text;

namespace Intoner.Objects.Resources;

internal static class ObjectResourcePathEncoding
{
    public const int MinimumExternalStringCapacity = 16;
    private const int StackByteLimit = 1024;
    private const int MaxNativePathByteCount = 4096;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public delegate TResult TemporaryHandlePathAction<in TState, out TResult>(TState state);
    public unsafe delegate TResult NullTerminatedUtf8Action<in TState, out TResult>(byte* pathPointer, int byteCount, TState state);

    private static void WriteNullTerminatedUtf8(string path, Span<byte> buffer, int byteCount)
    {
        Encoding.UTF8.GetBytes(path.AsSpan(), buffer[..byteCount]);
        buffer[byteCount] = 0;
    }

    public static unsafe TResult WithTemporaryHandlePath<TState, TResult>(
        ResourceHandle* resourceHandle,
        string path,
        TState state,
        TemporaryHandlePathAction<TState, TResult> action)
    {
        if (resourceHandle == null || string.IsNullOrEmpty(path))
        {
            return action(state);
        }

        return WithNullTerminatedUtf8(
            path,
            (ResourceHandle: (nint)resourceHandle, State: state, Action: action),
            static (pathPointer, pathByteCount, call) =>
            {
                using var pathScope = new ObjectResourceHandlePathScope(
                    (ResourceHandle*)call.ResourceHandle,
                    pathPointer,
                    pathByteCount);
                return call.Action(call.State);
            });
    }

    public static unsafe TResult WithNullTerminatedUtf8<TState, TResult>(
        string path,
        TState state,
        NullTerminatedUtf8Action<TState, TResult> action)
    {
        int pathByteCount = Encoding.UTF8.GetByteCount(path);
        Span<byte> pathBytes = pathByteCount <= StackByteLimit
            ? stackalloc byte[pathByteCount + 1]
            : new byte[pathByteCount + 1];
        WriteNullTerminatedUtf8(path, pathBytes, pathByteCount);

        fixed (byte* pathPointer = pathBytes)
        {
            return action(pathPointer, pathByteCount, state);
        }
    }

    public static unsafe bool TryReadNativePath(byte* path, out string resourcePath)
    {
        resourcePath = string.Empty;
        if (path == null)
        {
            return false;
        }

        byte[] rentedBytes = ArrayPool<byte>.Shared.Rent(MaxNativePathByteCount);
        Span<byte> pathBytes = rentedBytes.AsSpan(0, MaxNativePathByteCount);
        int length = 0;
        try
        {
            while (length < MaxNativePathByteCount)
            {
                var address = (nint)(path + length);
                if (!PtrGuard.TryGetReadableRegionSize(address, (nuint)(MaxNativePathByteCount - length), out nuint readableSize))
                {
                    return false;
                }

                int readSize = (int)readableSize;
                Span<byte> chunk = pathBytes.Slice(length, readSize);
                if (!PtrGuard.TryReadUnalignedBytes(address, chunk))
                {
                    return false;
                }

                int terminatorIndex = chunk.IndexOf((byte)0);
                if (terminatorIndex >= 0)
                {
                    length += terminatorIndex;
                    return length > 0 && TryDecodeUtf8(pathBytes[..length], out resourcePath);
                }

                length += readSize;
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBytes);
        }
    }

    public static unsafe bool TryReadHandlePath(ResourceHandle* handle, out string resourcePath)
    {
        resourcePath = string.Empty;
        if (handle == null)
        {
            return false;
        }

        if (!PtrGuard.TryReadUnaligned((nint)(&handle->FileName), out StdString fileName))
        {
            return false;
        }

        ulong length = fileName.Length;
        if (length == 0 || length > MaxNativePathByteCount)
        {
            return false;
        }

        byte* path = fileName.BufferPtr;
        if (path == null)
        {
            return false;
        }

        int byteCount = (int)length;
        Span<byte> pathBytes = byteCount <= StackByteLimit
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        return PtrGuard.TryReadUnalignedBytes((nint)path, pathBytes)
            && TryDecodeUtf8(pathBytes, out resourcePath);
    }

    public static unsafe bool TryReadActualScopedHandlePath(ResourceHandle* handle, out string actualPath)
    {
        actualPath = string.Empty;
        if (!TryReadHandlePath(handle, out string handlePath)
            || !ObjectScopedResourcePathUtility.TryParse(handlePath, out ObjectScopedResourcePath scopedPath))
        {
            return false;
        }

        actualPath = scopedPath.Path;
        return actualPath.Length > 0;
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string value)
    {
        value = string.Empty;
        if (bytes.IsEmpty)
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(bytes);
            return value.Length > 0;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

internal readonly unsafe ref struct ObjectResourceHandlePathScope
{
    private readonly ResourceHandle* _resourceHandle;
    private readonly StdString _originalFileName;

    public ObjectResourceHandlePathScope(ResourceHandle* resourceHandle, byte* pathPointer, int pathByteCount)
    {
        _resourceHandle = resourceHandle;
        _originalFileName = resourceHandle->FileName;
        resourceHandle->FileName.BufferPtr = pathPointer;
        resourceHandle->FileName.Length = (ulong)pathByteCount;
        resourceHandle->FileName.Capacity = (ulong)Math.Max(pathByteCount, ObjectResourcePathEncoding.MinimumExternalStringCapacity);
    }

    public void Dispose()
        => _resourceHandle->FileName = _originalFileName;
}


