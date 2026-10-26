using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Intoner.Objects.Utils;
using Intoner.Services.Interop;
using Intoner.Utils;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Intoner.Objects.Resources;

internal sealed unsafe class ObjectResourceCreateFileHook : IDisposable
{
    internal const int PointerPayloadSize = 28;
    private const int PointerPayloadAddressOffset = 2;
    private const int PointerPayloadLengthOffset = 18;
    private const int PointerPayloadByteStride = 2;
    private const char PointerPayloadPrefix = (char)((byte)'L' | (('?' & 0x00FF) << 8));
    private static readonly nint InvalidHandleValue = new(-1);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private delegate nint CreateFileWDelegate(char* fileName, uint access, uint shareMode, nint security, uint creation, uint flags, nint template);

    private readonly Hook<CreateFileWDelegate>? _createFileHook;
    private readonly CreateFileWDelegate? _createFileOriginal;
    private readonly InteropHookCollection _hooks;
    private readonly ThreadLocal<(nint Address, int Length)> _activePayload = new(static () => default);
    private readonly DisposalState _disposeState = new();

    public ObjectResourceCreateFileHook(IGameInteropProvider gameInteropProvider, ILogger logger)
    {
        _hooks = new InteropHookCollection(logger);
        try
        {
            _createFileHook = _hooks.Add(gameInteropProvider.HookFromImport<CreateFileWDelegate>(
                null,
                "KERNEL32.dll",
                "CreateFileW",
                0,
                CreateFileWDetour));

            _createFileOriginal = _createFileHook?.Original;
        }
        catch
        {
            _hooks.Dispose();
            _activePayload.Dispose();
            throw;
        }
    }

    public bool IsDisposed
        => _hooks.IsDisposed;

    public bool Enable()
        => !_disposeState.IsDisposing && _createFileHook is not null && _hooks.Enable();

    public void Dispose()
    {
        if (!_disposeState.TryBeginDispose() && _hooks.IsDisposed)
        {
            return;
        }

        _hooks.Dispose();

        if (_hooks.IsDisposed)
        {
            _activePayload.Dispose();
        }
    }

    public ReadScope EnterRead(byte* address, int length)
    {
        if (_disposeState.IsDisposing || address == null || length <= 0
         || length >= ObjectResourcePathEncoding.MaxNativePathByteCount)
        {
            return default;
        }

        InteropHookCollection.CallbackScope operation = _hooks.EnterOperation();

        if (!operation.IsActive
         || !ObjectThreadLocalUtility.TryRead(_activePayload, default, out (nint Address, int Length) previous)
         || !ObjectThreadLocalUtility.TryWrite(_activePayload, ((nint)address, length)))
        {
            operation.Dispose();
            return default;
        }

        return new ReadScope(this, previous, operation);
    }

    public readonly struct ReadScope(
        ObjectResourceCreateFileHook owner,
        (nint Address, int Length) previous,
        InteropHookCollection.CallbackScope operation) : IDisposable
    {
        public bool IsActive => owner is not null;

        public void Dispose()
        {
            if (owner is not null)
            {
                _ = ObjectThreadLocalUtility.TryWrite(owner._activePayload, previous);
            }

            operation.Dispose();
        }
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

    private nint CreateFileWDetour(char* fileName, uint access, uint shareMode, nint security, uint creation, uint flags, nint template)
    {
        using InteropHookCollection.CallbackScope callback = _hooks.EnterCallback();

        if (!CheckPointer(fileName, out ReadOnlySpan<byte> actualName))
        {
            return _createFileOriginal!(fileName, access, shareMode, security, creation, flags, template);
        }

        Span<char> translatedName = stackalloc char[actualName.Length + 9];

        if (!TryWriteFileName(actualName, translatedName))
        {
            return InvalidHandleValue;
        }

        fixed (char* translatedPointer = translatedName)
        {
            return _createFileOriginal!(translatedPointer, access, shareMode, security, creation, flags, template);
        }
    }

    internal static bool TryWriteFileName(ReadOnlySpan<byte> actualName, Span<char> destination)
    {
        if (actualName.IsEmpty || actualName.Contains((byte)0))
        {
            return false;
        }

        int written;

        try
        {
            if (StrictUtf8.GetCharCount(actualName) >= destination.Length)
            {
                return false;
            }

            written = StrictUtf8.GetChars(actualName, destination);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        Span<char> path = destination[..written];
        path.Replace('/', '\\');
        bool hasDevicePrefix = path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\");
        bool isUnc = !hasDevicePrefix && path.StartsWith(@"\\");
        ReadOnlySpan<char> prefix = isUnc ? @"\\?\UNC\" : @"\\?\";

        if (hasDevicePrefix)
        {
            prefix = [];
        }

        int skip = isUnc ? 2 : 0;
        int length = written - skip + prefix.Length;

        if (length >= destination.Length)
        {
            return false;
        }

        path[skip..].CopyTo(destination[prefix.Length..]);
        prefix.CopyTo(destination);
        destination[length] = '\0';
        return true;
    }

    private bool CheckPointer(char* buffer, out ReadOnlySpan<byte> fileName)
    {
        fileName = default;

        if (buffer == null
         || !ObjectThreadLocalUtility.TryRead(_activePayload, default, out (nint Address, int Length) payload)
         || payload.Address == nint.Zero)
        {
            return false;
        }

        Span<byte> actualPayload = stackalloc byte[PointerPayloadSize];
        char* expectedPayload = stackalloc char[PointerPayloadSize / sizeof(char)];
        WritePointerPayload(expectedPayload, (byte*)payload.Address, payload.Length);

        if (!PtrGuard.TryReadUnalignedBytes((nint)buffer, actualPayload)
         || !actualPayload.SequenceEqual(new ReadOnlySpan<byte>(expectedPayload, PointerPayloadSize))
         || !PtrGuard.IsReadable(payload.Address, (nuint)payload.Length))
        {
            return false;
        }

        fileName = new ReadOnlySpan<byte>((void*)payload.Address, payload.Length);
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
}
