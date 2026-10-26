using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.STD;
using Intoner.Utils;
using Penumbra.String;
using Penumbra.String.Functions;
using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Text;

namespace Intoner.Objects.Resources;

internal static class ObjectResourcePathEncoding
{
    public const int MinimumExternalStringCapacity = 16;
    private const int StackByteLimit = 1024;
    private const int MaxPartialReadSuffixByteCount = 18;
    internal const int MaxGamePathByteCount = 240;
    internal const int PcbPathCapacity = 80;
    internal const int MaxNativePathByteCount = 4096;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public delegate TResult TemporaryHandlePathAction<in TState, out TResult>(TState state);
    public unsafe delegate TResult NullTerminatedUtf8Action<in TState, out TResult>(byte* pathPointer, int byteCount, TState state);

    public static bool TryGetNativePathByteCount(string? path, out int byteCount)
        => TryGetPathByteCount(path, MaxNativePathByteCount, out byteCount);
    public static bool TryGetGamePathByteCount(string? path, out int byteCount)
        => TryGetPathByteCount(path, MaxGamePathByteCount, out byteCount);
    public static bool TryGetPcbPathByteCount(string? path, out int byteCount)
        => TryGetPathByteCount(path, PcbPathCapacity, out byteCount) && byteCount > 0;

    public static bool TryDecodeGamePath(ReadOnlySpan<byte> bytes, out string path)
    {
        path = string.Empty;
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out Rune rune, out int consumed) != OperationStatus.Done)
            {
                return false;
            }

            if (!Rune.IsWhiteSpace(rune))
            {
                break;
            }

            bytes = bytes[consumed..];
        }

        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeLastFromUtf8(bytes, out Rune rune, out int consumed) != OperationStatus.Done)
            {
                return false;
            }

            if (!Rune.IsWhiteSpace(rune))
            {
                break;
            }

            bytes = bytes[..^consumed];
        }

        if (bytes.IsEmpty || bytes.Length > MaxGamePathByteCount)
        {
            return false;
        }

        try
        {
            path = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryGetPathByteCount(string? path, int capacity, out int byteCount)
    {
        byteCount = 0;

        if (path is null || path.Length >= capacity || path.Contains('\0'))
        {
            return false;
        }

        try
        {
            int encodedByteCount = StrictUtf8.GetByteCount(path);

            if (encodedByteCount >= capacity)
            {
                return false;
            }

            byteCount = encodedByteCount;
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public static unsafe int ComputeResourceHash(string path, ObjectGetResourceParameters* parameters)
    {
        int pathByteCount = GetNativePathByteCount(path);
        bool isPartialRead = parameters != null && parameters->IsPartialRead;

        int hashByteCapacity = pathByteCount + (isPartialRead ? MaxPartialReadSuffixByteCount : 0);
        Span<byte> hashBytes = hashByteCapacity <= StackByteLimit
            ? stackalloc byte[hashByteCapacity + 1]
            : new byte[hashByteCapacity + 1];
        WriteNullTerminatedUtf8(path, hashBytes, pathByteCount);
        int hashByteCount = pathByteCount;

        if (isPartialRead)
        {
            hashBytes[hashByteCount++] = (byte)'.';
            _ = Utf8Formatter.TryFormat(parameters->SegmentOffset, hashBytes[hashByteCount..], out int offsetByteCount, new StandardFormat('x'));
            hashByteCount += offsetByteCount;
            hashBytes[hashByteCount++] = (byte)'.';
            _ = Utf8Formatter.TryFormat(parameters->SegmentLength, hashBytes[hashByteCount..], out int lengthByteCount, new StandardFormat('x'));
            hashByteCount += lengthByteCount;
            hashBytes[hashByteCount] = 0;
        }

        fixed (byte* pointer = hashBytes)
        {
            ByteStringFunctions.ComputeCrc32AndSize(pointer, out int hash, out _, hashByteCount);
            return hash;
        }
    }

    public static unsafe ulong ComputeFilePathCrc64(string path)
        => WithNullTerminatedUtf8(path.Replace('\\', '/').Trim(), 0, static (pointer, length, _) =>
        {
            using CiByteString bytes = CiByteString.FromByteStringUnsafe(pointer, length, isNullTerminated: true);
            return ByteStringFunctions.ComputeLowerCaseCrc64(bytes);
        });

    private static void WriteNullTerminatedUtf8(string path, Span<byte> buffer, int byteCount)
    {
        StrictUtf8.GetBytes(path.AsSpan(), buffer[..byteCount]);
        buffer[byteCount] = 0;
    }

    private static int GetNativePathByteCount(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!TryGetNativePathByteCount(path, out int pathByteCount))
        {
            throw new ArgumentException($"resource path must be valid UTF8 without embedded nulls and fit in {MaxNativePathByteCount} bytes including its terminator", nameof(path));
        }

        return pathByteCount;
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
        int pathByteCount = GetNativePathByteCount(path);
        int pathByteCapacity = Math.Max(pathByteCount, MinimumExternalStringCapacity);
        Span<byte> pathBytes = pathByteCount <= StackByteLimit
            ? stackalloc byte[pathByteCapacity + 1]
            : new byte[pathByteCapacity + 1];
        WriteNullTerminatedUtf8(path, pathBytes, pathByteCount);

        fixed (byte* pathPointer = pathBytes)
        {
            return action(pathPointer, pathByteCount, state);
        }
    }

    public static unsafe bool TryReadPcbPath(byte* path, out string resourcePath)
    {
        resourcePath = string.Empty;
        return path != null && TryReadNullTerminatedPath(path, stackalloc byte[PcbPathCapacity], out resourcePath);
    }

    public static unsafe bool TryReadNativePath(byte* path, out string resourcePath)
    {
        resourcePath = string.Empty;
        if (path == null)
        {
            return false;
        }

        byte[] rentedBytes = ArrayPool<byte>.Shared.Rent(MaxNativePathByteCount);
        try
        {
            return TryReadNullTerminatedPath(path, rentedBytes.AsSpan(0, MaxNativePathByteCount), out resourcePath);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBytes);
        }
    }

    private static unsafe bool TryReadNullTerminatedPath(byte* path, Span<byte> pathBytes, out string resourcePath)
    {
        resourcePath = string.Empty;
        int length = 0;
        while (length < pathBytes.Length)
        {
            var address = (nint)(path + length);
            if (!PtrGuard.TryGetReadableRegionSize(address, (nuint)(pathBytes.Length - length), out nuint readableSize))
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

    public static unsafe bool TryReadHandlePath(ResourceHandle* handle, out string resourcePath)
    {
        resourcePath = string.Empty;
        if (handle == null || !PtrGuard.TryReadUnaligned((nint)handle, out ResourceHandle snapshot))
        {
            return false;
        }

        StdString fileName = snapshot.FileName;
        ulong length = fileName.Length;
        if (length == 0 || length >= MaxNativePathByteCount || length > fileName.Capacity)
        {
            return false;
        }

        int byteCount = (int)length;

        if (fileName.Capacity < MinimumExternalStringCapacity)
        {
            return TryDecodeUtf8(new ReadOnlySpan<byte>(fileName.Buffer, byteCount), out resourcePath);
        }

        byte* path = fileName.BufferPtr;
        if (path == null)
        {
            return false;
        }

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

    public static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, out string value)
    {
        value = string.Empty;
        if (bytes.IsEmpty || bytes.Contains((byte)0))
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

[StructLayout(LayoutKind.Auto)]
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
    {
        if (_resourceHandle != null)
        {
            _resourceHandle->FileName = _originalFileName;
        }
    }
}
