using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Rendering.Drawing;

internal static class ShapeBuilder
{
    private static readonly (int Start, int End)[] BoxEdges =
    [
        (0, 1),
        (1, 2),
        (2, 3),
        (3, 0),
        (4, 5),
        (5, 6),
        (6, 7),
        (7, 4),
        (0, 4),
        (1, 5),
        (2, 6),
        (3, 7),
    ];

    public static void AddBox(DrawBatch batch, ReadOnlySpan<Vector3> corners, Vector4 color, float thickness)
    {
        if (corners.Length < 8)
        {
            return;
        }

        foreach (var (start, end) in BoxEdges)
        {
            batch.AddLine(corners[start], corners[end], color, thickness);
        }
    }

    public static void AddSphere(DrawBatch batch, Matrix4x4 transform, float radius, Vector4 color, float thickness)
    {
        Span<Vector3> xyRing = stackalloc Vector3[ObjectShapeMath.WireCircleSegmentCount];
        Span<Vector3> xzRing = stackalloc Vector3[ObjectShapeMath.WireCircleSegmentCount];
        Span<Vector3> yzRing = stackalloc Vector3[ObjectShapeMath.WireCircleSegmentCount];
        ObjectShapeMath.CopyCirclePoints(transform, radius, Vector3.UnitX, Vector3.UnitY, xyRing);
        ObjectShapeMath.CopyCirclePoints(transform, radius, Vector3.UnitX, Vector3.UnitZ, xzRing);
        ObjectShapeMath.CopyCirclePoints(transform, radius, Vector3.UnitY, Vector3.UnitZ, yzRing);

        batch.AddPolyline(xyRing, closed: true, color, thickness);
        batch.AddPolyline(xzRing, closed: true, color, thickness);
        batch.AddPolyline(yzRing, closed: true, color, thickness);
    }

    public static void AddCone(DrawBatch batch, Matrix4x4 transform, float length, float angleDegrees, Vector4 color, float thickness)
    {
        Span<Vector3> basePoints = stackalloc Vector3[ObjectShapeMath.WireCircleSegmentCount];
        ObjectShapeMath.CopyConeBasePoints(transform, length, angleDegrees, basePoints);
        batch.AddPolyline(basePoints, closed: true, color, thickness);

        var apex = transform.Translation;
        batch.AddLine(apex, basePoints[0], color, thickness);
        batch.AddLine(apex, basePoints[basePoints.Length / 4], color, thickness);
        batch.AddLine(apex, basePoints[basePoints.Length / 2], color, thickness);
        batch.AddLine(apex, basePoints[(basePoints.Length * 3) / 4], color, thickness);
    }

    public static void AddSquarePyramid(DrawBatch batch, Matrix4x4 transform, float length, float angleDegrees, Vector4 color, float thickness)
    {
        Span<Vector3> baseCorners = stackalloc Vector3[4];
        ObjectShapeMath.CopySquarePyramidBaseCorners(transform, length, angleDegrees, baseCorners);
        batch.AddPolyline(baseCorners, closed: true, color, thickness);

        var apex = transform.Translation;
        foreach (var baseCorner in baseCorners)
        {
            batch.AddLine(apex, baseCorner, color, thickness);
        }
    }
}

