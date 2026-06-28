namespace Intoner.Objects.Rendering.Primitives;

internal readonly record struct PrimitiveTextureSize(
    uint ActualWidth,
    uint ActualHeight,
    uint AllocatedWidth,
    uint AllocatedHeight)
{
    public static readonly PrimitiveTextureSize Empty = new(0, 0, 0, 0);
}

