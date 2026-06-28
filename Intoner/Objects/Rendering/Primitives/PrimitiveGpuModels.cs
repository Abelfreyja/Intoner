using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Rendering.Primitives;

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PrimitiveLineCorner
{
    public const int QuadVertexCount = 6;

    public readonly Vector2 AlongSide;

    public PrimitiveLineCorner(float along, float side)
        => AlongSide = new Vector2(along, side);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PrimitiveLineInstance
{
    public readonly Vector2 LineStart;
    public readonly Vector2 LineEnd;
    public readonly float ViewDepthStart;
    public readonly float ViewDepthEnd;
    public readonly float InvClipWStart;
    public readonly float InvClipWEnd;
    public readonly float Thickness;
    public readonly uint Color;

    public PrimitiveLineInstance(
        Vector2 lineStart,
        Vector2 lineEnd,
        float viewDepthStart,
        float viewDepthEnd,
        float invClipWStart,
        float invClipWEnd,
        float thickness,
        uint color)
    {
        LineStart      = lineStart;
        LineEnd        = lineEnd;
        ViewDepthStart = viewDepthStart;
        ViewDepthEnd   = viewDepthEnd;
        InvClipWStart  = invClipWStart;
        InvClipWEnd    = invClipWEnd;
        Thickness      = thickness;
        Color          = color;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PrimitiveVertex
{
    public readonly Vector2 Position;
    public readonly float ViewDepth;
    public readonly float InvClipW;
    public readonly uint Color;
    public readonly Vector2 LineStart;
    public readonly Vector2 LineEnd;
    public readonly float Thickness;
    public readonly float LineCaps;

    public PrimitiveVertex(
        Vector2 position,
        float viewDepth,
        float invClipW,
        uint color,
        Vector2 lineStart,
        Vector2 lineEnd,
        float thickness,
        float lineCaps)
    {
        Position  = position;
        ViewDepth = viewDepth;
        InvClipW  = invClipW;
        Color     = color;
        LineStart = lineStart;
        LineEnd   = lineEnd;
        Thickness = thickness;
        LineCaps  = lineCaps;
    }
}

internal readonly record struct PrimitiveGeometryBuildResult(int LineInstanceCount, int PointVertexCount, int ScreenVertexCount)
{
    public static readonly PrimitiveGeometryBuildResult Empty = new(0, 0, 0);

    public bool IsEmpty
        => DrawVertexCount == 0;

    public bool HasWorldPrimitives
        => LineInstanceCount > 0 || PointVertexCount > 0;

    public int DrawVertexCount
        => (LineInstanceCount * PrimitiveLineCorner.QuadVertexCount) + PointVertexCount + ScreenVertexCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PrimitiveConstants
{
    public Vector4 Viewport;
    public Vector4 DepthParams;
    public Vector4 DepthTextureSize;
    public Vector4 LineParams;
    public Matrix4x4 InverseProjection;
}

