using System.Numerics;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Utils;

internal static class ObjectShapeMath
{
    public const int WireCircleSegmentCount = 16;

    public static Matrix4x4 CreateRigidTransform(Vector3 position, Vector3 rotationDegrees)
    {
        var rotation = ObjectTransformMath.CreateRotationQuaternion(rotationDegrees);
        return CreateRigidTransform(position, rotation);
    }

    public static Matrix4x4 CreateRigidTransform(Vector3 position, Quaternion rotation)
    {
        var transform = Matrix4x4.CreateFromQuaternion(ObjectTransformMath.NormalizeQuaternion(rotation));
        transform.Translation = position;
        return transform;
    }

    public static void CopyAxisAlignedBoxCorners(Vector3 min, Vector3 max, Span<Vector3> corners)
    {
        corners[0] = new Vector3(min.X, min.Y, min.Z);
        corners[1] = new Vector3(max.X, min.Y, min.Z);
        corners[2] = new Vector3(max.X, max.Y, min.Z);
        corners[3] = new Vector3(min.X, max.Y, min.Z);
        corners[4] = new Vector3(min.X, min.Y, max.Z);
        corners[5] = new Vector3(max.X, min.Y, max.Z);
        corners[6] = new Vector3(max.X, max.Y, max.Z);
        corners[7] = new Vector3(min.X, max.Y, max.Z);
    }

    public static void CopyOrientedBoxCorners(OrientedBounds bounds, Span<Vector3> corners)
    {
        Span<Vector3> localCorners =
        [
            new Vector3(-bounds.HalfExtents.X, -bounds.HalfExtents.Y, -bounds.HalfExtents.Z),
            new Vector3(bounds.HalfExtents.X, -bounds.HalfExtents.Y, -bounds.HalfExtents.Z),
            new Vector3(bounds.HalfExtents.X, bounds.HalfExtents.Y, -bounds.HalfExtents.Z),
            new Vector3(-bounds.HalfExtents.X, bounds.HalfExtents.Y, -bounds.HalfExtents.Z),
            new Vector3(-bounds.HalfExtents.X, -bounds.HalfExtents.Y, bounds.HalfExtents.Z),
            new Vector3(bounds.HalfExtents.X, -bounds.HalfExtents.Y, bounds.HalfExtents.Z),
            new Vector3(bounds.HalfExtents.X, bounds.HalfExtents.Y, bounds.HalfExtents.Z),
            new Vector3(-bounds.HalfExtents.X, bounds.HalfExtents.Y, bounds.HalfExtents.Z),
        ];

        for (var index = 0; index < localCorners.Length; ++index)
        {
            corners[index] = Vector3.Transform(localCorners[index], bounds.Transform);
        }
    }

    public static void CopyCirclePoints(Matrix4x4 transform, float radius, Vector3 localAxisA, Vector3 localAxisB, Span<Vector3> points)
    {
        for (var index = 0; index < points.Length; ++index)
        {
            var angle = (index / (float)points.Length) * MathF.Tau;
            var localPoint = ((localAxisA * MathF.Cos(angle)) + (localAxisB * MathF.Sin(angle))) * radius;
            points[index] = Vector3.Transform(localPoint, transform);
        }
    }

    public static void CopyConeBasePoints(Matrix4x4 transform, float length, float angleDegrees, Span<Vector3> points)
    {
        var radius = length * MathF.Tan(Math.Clamp(angleDegrees, 0f, 179f) * (MathF.PI / 360f));
        for (var index = 0; index < points.Length; ++index)
        {
            var angle = (index / (float)points.Length) * MathF.Tau;
            var localPoint = new Vector3(
                MathF.Cos(angle) * radius,
                MathF.Sin(angle) * radius,
                length);
            points[index] = Vector3.Transform(localPoint, transform);
        }
    }

    public static void CopySquarePyramidBaseCorners(Matrix4x4 transform, float length, float angleDegrees, Span<Vector3> corners)
    {
        var halfExtent = length * MathF.Tan(Math.Clamp(angleDegrees, 0f, 179f) * (MathF.PI / 360f));
        corners[0] = Vector3.Transform(new Vector3(-halfExtent, -halfExtent, length), transform);
        corners[1] = Vector3.Transform(new Vector3(halfExtent, -halfExtent, length), transform);
        corners[2] = Vector3.Transform(new Vector3(halfExtent, halfExtent, length), transform);
        corners[3] = Vector3.Transform(new Vector3(-halfExtent, halfExtent, length), transform);
    }

    public static float ComputeOrientedBoundsSupportExtent(Quaternion rotation, Vector3 halfExtents, Vector3 normal)
    {
        if (!ObjectMathUtility.TryNormalize(normal, out var normalizedNormal))
        {
            return 0f;
        }

        var axisX = Vector3.Transform(Vector3.UnitX, rotation);
        var axisY = Vector3.Transform(Vector3.UnitY, rotation);
        var axisZ = Vector3.Transform(Vector3.UnitZ, rotation);
        return (MathF.Abs(Vector3.Dot(normalizedNormal, axisX)) * halfExtents.X)
             + (MathF.Abs(Vector3.Dot(normalizedNormal, axisY)) * halfExtents.Y)
             + (MathF.Abs(Vector3.Dot(normalizedNormal, axisZ)) * halfExtents.Z);
    }
}

