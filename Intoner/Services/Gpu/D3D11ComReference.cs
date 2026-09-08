using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;
using ShaderResourceView = SharpDX.Direct3D11.ShaderResourceView;
using Texture2D = SharpDX.Direct3D11.Texture2D;

namespace Intoner.Services.Gpu;

internal static class D3D11ComReference
{
    public static Device RetainDevice(nint devicePointer, object owner)
        => Retain(devicePointer, owner, static pointer => new Device(pointer));

    public static Texture2D RetainTexture(nint texturePointer, object owner)
        => Retain(texturePointer, owner, static pointer => new Texture2D(pointer));

    public static ShaderResourceView RetainShaderResourceView(nint viewPointer, object owner)
        => Retain(viewPointer, owner, static pointer => new ShaderResourceView(pointer));

    public static nint RetainPointer(nint pointer, object owner)
    {
        ObjectDisposedException.ThrowIf(pointer == nint.Zero, owner);
        Marshal.AddRef(pointer);
        return pointer;
    }

    private static T Retain<T>(nint pointer, object owner, Func<nint, T> create)
    {
        RetainPointer(pointer, owner);
        try
        {
            return create(pointer);
        }
        catch
        {
            Marshal.Release(pointer);
            throw;
        }
    }

    public static void Release(ref nint pointer)
    {
        nint value = Interlocked.Exchange(ref pointer, nint.Zero);
        if (value == nint.Zero)
        {
            return;
        }

        try
        {
            Marshal.Release(value);
        }
        catch
        {
            // ignore release errors
        }
    }
}
