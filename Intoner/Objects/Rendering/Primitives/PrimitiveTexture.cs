using SharpDX.Direct3D11;
using System.Numerics;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed class PrimitiveTexture(ShaderResourceView view, Vector2 size, Vector2 offset)
{
    private int _references = 1;

    public ShaderResourceView View { get; } = view;
    public Vector2 Size { get; } = size;
    public Vector2 Offset { get; } = offset;
    public nint DeviceHandle { get; } = view.Device.NativePointer;

    public void Retain()
        => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            View.Dispose();
        }
    }
}
