using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Intoner.Objects.Utils;
using Penumbra.String.Classes;
using System.Runtime.InteropServices;
using System.Text;
using Intoner.Utils;

namespace Intoner.Objects.Resources;

internal sealed unsafe class ObjectResourceCreateFileHook : IDisposable
{
    private const int PointerPayloadSize = 28;
    private const int PointerPayloadAddressOffset = 2;
    private const int PointerPayloadLengthOffset = 18;
    private const int PointerPayloadByteStride = 2;
    private const int FileNameStorageSize = Utf8GamePath.MaxGamePathLength;
    private const char PointerPayloadPrefix = (char)((byte)'L' | (('?' & 0x00FF) << 8));
    private static readonly nint InvalidHandleValue = new(-1);

    private delegate nint CreateFileWDelegate(char* fileName, uint access, uint shareMode, nint security, uint creation, uint flags, nint template);

    private readonly Hook<CreateFileWDelegate> _createFileHook;
    private readonly ThreadLocal<nint> _fileNameStorage = new(SetupStorage, true);
    private readonly ObjectDisposalState _disposeState = new();
    private readonly ObjectLockedOnce _enableOnce = new();

    public ObjectResourceCreateFileHook(IGameInteropProvider gameInteropProvider)
    {
        _createFileHook = gameInteropProvider.HookFromImport<CreateFileWDelegate>(
            null,
            "KERNEL32.dll",
            "CreateFileW",
            0,
            CreateFileWDetour);
    }

    public bool Enable()
        => _enableOnce.TryExecute(
            () =>
            {
                _createFileHook.Enable();
                return true;
            },
            () => !_disposeState.IsDisposing);

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose())
        {
            return;
        }

        ObjectInteropHookUtility.DisposeHook(_createFileHook);
        foreach (var pointer in _fileNameStorage.Values)
        {
            Marshal.FreeHGlobal(pointer);
        }

        _fileNameStorage.Dispose();
    }

    public static void WritePointerPayload(char* buffer, byte* address, int length)
    {
        buffer[0] = PointerPayloadPrefix;

        byte* ptr = (byte*)buffer;
        WritePayloadValue(ptr, PointerPayloadAddressOffset, (ulong)address, sizeof(ulong));
        WritePayloadValue(ptr, PointerPayloadLengthOffset, (uint)length, sizeof(uint));

        ptr[PointerPayloadSize - 2] = 0;
        ptr[PointerPayloadSize - 1] = 0;
    }

    private static nint SetupStorage()
    {
        var pointer = (char*)Marshal.AllocHGlobal(2 * FileNameStorageSize);
        pointer[0] = '\\';
        pointer[1] = '\\';
        pointer[2] = '?';
        pointer[3] = '\\';
        pointer[4] = '\0';
        return (nint)pointer;
    }

    private nint CreateFileWDetour(char* fileName, uint access, uint shareMode, nint security, uint creation, uint flags, nint template)
    {
        if (CheckPointer(fileName, out var actualName))
        {
            return TryWriteFileName(actualName, out char* translatedPointer)
                ? _createFileHook.OriginalDisposeSafe(translatedPointer, access, shareMode, security, creation, flags, template)
                : InvalidHandleValue;
        }

        return _createFileHook.OriginalDisposeSafe(fileName, access, shareMode, security, creation, flags, template);
    }

    private bool TryWriteFileName(ReadOnlySpan<byte> actualName, out char* fileName)
    {
        fileName = null;
        if (_disposeState.IsDisposing
            || !ObjectThreadLocalUtility.TryRead(_fileNameStorage, nint.Zero, out var fileNameStorage)
            || fileNameStorage == nint.Zero)
        {
            return false;
        }

        Span<char> span = new((char*)fileNameStorage + 4, FileNameStorageSize - 4);
        if (Encoding.UTF8.GetCharCount(actualName) >= span.Length)
        {
            return false;
        }

        int written = Encoding.UTF8.GetChars(actualName, span);
        for (int i = 0; i < written; ++i)
        {
            if (span[i] == '/')
            {
                span[i] = '\\';
            }
        }

        span[written] = '\0';
        fileName = (char*)fileNameStorage;
        return true;
    }

    private static bool CheckPointer(char* buffer, out ReadOnlySpan<byte> fileName)
    {
        if (buffer == null || buffer[0] != PointerPayloadPrefix)
        {
            fileName = ReadOnlySpan<byte>.Empty;
            return false;
        }

        byte* ptr = (byte*)buffer;
        ulong address = ReadPayloadValue(ptr, PointerPayloadAddressOffset, sizeof(ulong));
        ulong length = ReadPayloadValue(ptr, PointerPayloadLengthOffset, sizeof(uint));

        if (address == 0
            || length == 0
            || length > int.MaxValue
            || !PtrGuard.IsReadable((nint)address, (nuint)length))
        {
            fileName = ReadOnlySpan<byte>.Empty;
            return false;
        }

        fileName = new ReadOnlySpan<byte>((void*)address, (int)length);
        return true;
    }

    private static void WritePayloadValue(byte* ptr, int offset, ulong value, int byteCount)
    {
        for (int i = 0; i < byteCount; ++i)
        {
            int index = offset + (i * PointerPayloadByteStride);
            ptr[index] = (byte)(value >> (i * 8));
            ptr[index + 1] = 0xFF;
        }
    }

    private static ulong ReadPayloadValue(byte* ptr, int offset, int byteCount)
    {
        ulong value = 0;
        for (int i = 0; i < byteCount; ++i)
        {
            value |= (ulong)ptr[offset + (i * PointerPayloadByteStride)] << (i * 8);
        }

        return value;
    }
}

