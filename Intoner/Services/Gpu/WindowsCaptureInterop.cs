using SharpDX.Direct3D11;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Intoner.Services.Gpu;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct WindowsCaptureSize(int Width, int Height)
{
    public bool IsValid
        => Width > 0 && Height > 0;
}

/// <summary> owns the native Windows Graphics Capture ABI boundary without creating managed WinRT wrappers </summary>
internal static unsafe partial class WindowsCaptureInterop
{
    private const int NoInterface = unchecked((int)0x80004002);
    private const int B8G8R8A8UIntNormalized = 87;

    private const string CaptureItemRuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private const string FramePoolRuntimeClass = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";

    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid CaptureItemInteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid CaptureSession2Id = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid ClosableId = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid Direct3DDxgiInterfaceAccessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid FramePoolStatics2Id = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid CaptureItemClosedHandlerId = new("E9C610C0-A68C-5BD9-8021-8589346EEEE2");
    private static readonly Guid Texture2DId = typeof(Texture2D).GUID;
    private static readonly ComWrappers CaptureItemClosedComWrappers = new StrategyBasedComWrappers();
    public static void InitializeApartment()
        => Marshal.ThrowExceptionForHR(RoInitialize(1));

    public static void UninitializeApartment()
        => RoUninitialize();

    public static nint CreateCaptureItem(nint target, bool isWindow)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(target, nint.Zero);

        nint interop = GetActivationFactory(CaptureItemRuntimeClass, CaptureItemInteropId);
        try
        {
            nint item = nint.Zero;
            Guid itemId = CaptureItemId;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)GetMethod(
                interop,
                isWindow ? 3 : 4);
            int result = create(interop, target, &itemId, &item);
            Marshal.ThrowExceptionForHR(result);
            return RequirePointer(item, "Windows Graphics Capture returned an empty capture item.");
        }
        finally
        {
            Release(ref interop);
        }
    }

    public static WindowsCaptureSize GetCaptureItemSize(nint item)
    {
        WindowsCaptureSize size;
        var getSize = (delegate* unmanaged[Stdcall]<nint, WindowsCaptureSize*, int>)GetMethod(item, 7);
        int result = getSize(item, &size);
        Marshal.ThrowExceptionForHR(result);
        return size;
    }

    public static IDisposable SubscribeCaptureItemClosed(nint item, Action callback)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(item, nint.Zero);
        ArgumentNullException.ThrowIfNull(callback);

        nint unknown = nint.Zero;
        nint handler = nint.Zero;
        try
        {
            unknown = CaptureItemClosedComWrappers.GetOrCreateComInterfaceForObject(
                new WindowsCaptureItemClosedHandler(callback),
                CreateComInterfaceFlags.None);
            Marshal.ThrowExceptionForHR(
                Marshal.QueryInterface(unknown, in CaptureItemClosedHandlerId, out handler));

            long token = 0;
            var addClosed = (delegate* unmanaged[Stdcall]<nint, nint, long*, int>)GetMethod(item, 8);
            int result = addClosed(item, handler, &token);
            Marshal.ThrowExceptionForHR(result);

            var subscription = new CaptureItemClosedSubscription(item, handler, token);
            handler = nint.Zero;
            return subscription;
        }
        finally
        {
            Release(ref handler);
            Release(ref unknown);
        }
    }

    public static nint CreateDirect3DDevice(nint dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(
            CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint device));
        return RequirePointer(device, "Windows Graphics Capture returned an empty Direct3D device.");
    }

    public static nint CreateFramePool(
        nint device,
        int frameBufferCount,
        WindowsCaptureSize size)
    {
        nint statics = GetActivationFactory(FramePoolRuntimeClass, FramePoolStatics2Id);
        try
        {
            nint framePool = nint.Zero;
            var create = (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                int,
                int,
                WindowsCaptureSize,
                nint*,
                int>)GetMethod(statics, 6);
            int result = create(
                statics,
                device,
                B8G8R8A8UIntNormalized,
                frameBufferCount,
                size,
                &framePool);
            Marshal.ThrowExceptionForHR(result);
            return RequirePointer(framePool, "Windows Graphics Capture returned an empty frame pool.");
        }
        finally
        {
            Release(ref statics);
        }
    }

    public static void RecreateFramePool(
        nint framePool,
        nint device,
        int frameBufferCount,
        WindowsCaptureSize size)
    {
        var recreate = (delegate* unmanaged[Stdcall]<
            nint,
            nint,
            int,
            int,
            WindowsCaptureSize,
            int>)GetMethod(framePool, 6);
        int result = recreate(
            framePool,
            device,
            B8G8R8A8UIntNormalized,
            frameBufferCount,
            size);
        Marshal.ThrowExceptionForHR(result);
    }

    public static nint CreateCaptureSession(nint framePool, nint item)
    {
        nint session = nint.Zero;
        var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)GetMethod(framePool, 10);
        int result = create(framePool, item, &session);
        Marshal.ThrowExceptionForHR(result);
        return RequirePointer(session, "Windows Graphics Capture returned an empty capture session.");
    }

    public static bool TrySetCursorCaptureEnabled(nint session, bool enabled)
    {
        nint session2 = nint.Zero;
        try
        {
            int result = Marshal.QueryInterface(session, in CaptureSession2Id, out session2);
            if (result == NoInterface)
            {
                return false;
            }

            Marshal.ThrowExceptionForHR(result);
            var setCursorCaptureEnabled = (delegate* unmanaged[Stdcall]<nint, byte, int>)GetMethod(session2, 7);
            result = setCursorCaptureEnabled(session2, enabled ? (byte)1 : (byte)0);
            Marshal.ThrowExceptionForHR(result);
            return true;
        }
        finally
        {
            Release(ref session2);
        }
    }

    public static void StartCapture(nint session)
    {
        var start = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(session, 6);
        int result = start(session);
        Marshal.ThrowExceptionForHR(result);
    }

    public static nint TryGetNextFrame(nint framePool)
    {
        nint frame = nint.Zero;
        var getNextFrame = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(framePool, 7);
        int result = getNextFrame(framePool, &frame);
        Marshal.ThrowExceptionForHR(result);
        return frame;
    }

    public static WindowsCaptureSize GetFrameSize(nint frame)
    {
        WindowsCaptureSize size;
        var getSize = (delegate* unmanaged[Stdcall]<nint, WindowsCaptureSize*, int>)GetMethod(frame, 8);
        int result = getSize(frame, &size);
        Marshal.ThrowExceptionForHR(result);
        return size;
    }

    public static Texture2D GetFrameTexture(nint frame)
    {
        nint surface = nint.Zero;
        nint access = nint.Zero;
        nint texture = nint.Zero;
        try
        {
            var getSurface = (delegate* unmanaged[Stdcall]<nint, nint*, int>)GetMethod(frame, 6);
            int result = getSurface(frame, &surface);
            Marshal.ThrowExceptionForHR(result);
            RequirePointer(surface, "Windows Graphics Capture returned an empty frame surface.");

            Guid accessId = Direct3DDxgiInterfaceAccessId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface, in accessId, out access));

            Guid textureId = Texture2DId;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)GetMethod(access, 3);
            result = getInterface(access, &textureId, &texture);
            Marshal.ThrowExceptionForHR(result);
            RequirePointer(texture, "Windows Graphics Capture returned an empty frame texture.");

            var resultTexture = new Texture2D(texture);
            texture = nint.Zero;
            return resultTexture;
        }
        finally
        {
            Release(ref texture);
            Release(ref access);
            Release(ref surface);
        }
    }

    public static void CloseAndRelease(ref nint instance)
    {
        nint value = Interlocked.Exchange(ref instance, nint.Zero);
        if (value == nint.Zero)
        {
            return;
        }

        nint closable = nint.Zero;
        try
        {
            Guid closableId = ClosableId;
            if (Marshal.QueryInterface(value, in closableId, out closable) >= 0)
            {
                var close = (delegate* unmanaged[Stdcall]<nint, int>)GetMethod(closable, 6);
                Marshal.ThrowExceptionForHR(close(closable));
            }
        }
        finally
        {
            Release(ref closable);
            Marshal.Release(value);
        }
    }

    public static void Release(ref nint instance)
    {
        nint value = Interlocked.Exchange(ref instance, nint.Zero);
        if (value != nint.Zero)
        {
            Marshal.Release(value);
        }
    }

    private static nint GetActivationFactory(string runtimeClass, Guid interfaceId)
    {
        Marshal.ThrowExceptionForHR(
            WindowsCreateString(runtimeClass, runtimeClass.Length, out nint className));
        try
        {
            nint factory = nint.Zero;
            Guid requestedInterface = interfaceId;
            int result = RoGetActivationFactory(className, &requestedInterface, &factory);
            Marshal.ThrowExceptionForHR(result);
            return RequirePointer(factory, $"Windows Runtime returned an empty {runtimeClass} activation factory.");
        }
        finally
        {
            _ = WindowsDeleteString(className);
        }
    }

    private static nint RequirePointer(nint pointer, string message)
        => pointer != nint.Zero
            ? pointer
            : throw new InvalidOperationException(message);

    private static nint GetMethod(nint instance, int slot)
    {
        ObjectDisposedException.ThrowIf(instance == nint.Zero, typeof(WindowsCaptureInterop));
        return (*(nint**)instance)[slot];
    }

    private sealed class CaptureItemClosedSubscription(
        nint item,
        nint handler,
        long token) : IDisposable
    {
        private readonly nint _item = item;
        private readonly long _token = token;
        private nint _handler = handler;

        public void Dispose()
        {
            nint handlerPointer = Interlocked.Exchange(ref _handler, nint.Zero);
            if (handlerPointer == nint.Zero)
            {
                return;
            }

            try
            {
                var removeClosed = (delegate* unmanaged[Stdcall]<nint, long, int>)GetMethod(_item, 9);
                Marshal.ThrowExceptionForHR(removeClosed(_item, _token));
            }
            finally
            {
                Marshal.Release(handlerPointer);
            }
        }
    }

    [LibraryImport("combase.dll")]
    private static partial int RoInitialize(uint initializationType);

    [LibraryImport("combase.dll")]
    private static partial void RoUninitialize();

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(
        nint activatableClassId,
        Guid* interfaceId,
        nint* factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(
        string sourceString,
        int length,
        out nint value);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint value);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);
}

/// <summary> receives the native GraphicsCaptureItem closed notification </summary>
[GeneratedComInterface]
[Guid("E9C610C0-A68C-5BD9-8021-8589346EEEE2")]
internal partial interface IWindowsCaptureItemClosedHandler
{
    /// <summary> forwards the native notification to the capture source </summary>
    [PreserveSig]
    int Invoke(nint sender, nint arguments);
}

[GeneratedComClass]
internal sealed partial class WindowsCaptureItemClosedHandler(Action callback)
    : IWindowsCaptureItemClosedHandler
{
    private const int Failure = unchecked((int)0x80004005);

    public int Invoke(nint sender, nint arguments)
    {
        try
        {
            callback();
            return 0;
        }
        catch
        {
            return Failure;
        }
    }
}
