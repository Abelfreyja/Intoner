using Intoner.Scene;
using System.Numerics;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Utils;

internal static class ObjectShapeMath
{
    private const float MaximumSkewAngleDegrees = 89.9f;

    public const int WireCircleSegmentCount = 16;

    public static Matrix4x4 CreateRigidTransform(Vector3 position, Vector3 rotationDegrees)
    {
        var rotation = SceneTransformMath.CreateRotationQuaternion(rotationDegrees);
        return CreateRigidTransform(position, rotation);
    }

    public static Matrix4x4 CreateRigidTransform(Vector3 position, Quaternion rotation)
    {
        var transform = Matrix4x4.CreateFromQuaternion(SceneTransformMath.NormalizeQuaternion(rotation));
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
        => CopyOrientedBoxCorners(bounds.Transform, bounds.HalfExtents, corners);

    public static void CopyOrientedBoxCorners(SceneOrientedBounds bounds, Span<Vector3> corners)
        => CopyOrientedBoxCorners(bounds.Transform, bounds.HalfExtents, corners);

    public static bool TryCreateOrientedBounds(Matrix4x4 boxTransform, out SceneOrientedBounds bounds)
    {
        Vector3 axisX = new(boxTransform.M11, boxTransform.M12, boxTransform.M13);
        Vector3 axisY = new(boxTransform.M21, boxTransform.M22, boxTransform.M23);
        Vector3 axisZ = new(boxTransform.M31, boxTransform.M32, boxTransform.M33);
        float sizeX = axisX.Length();
        float sizeY = axisY.Length();
        float sizeZ = axisZ.Length();
        if (!NumericsUtility.IsFinite(boxTransform)
            || sizeX <= NumericsUtility.ScalarEpsilon
            || sizeY <= NumericsUtility.ScalarEpsilon
            || sizeZ <= NumericsUtility.ScalarEpsilon)
        {
            bounds = default;
            return false;
        }

        Matrix4x4 rigidTransform = new(
            axisX.X / sizeX, axisX.Y / sizeX, axisX.Z / sizeX, 0f,
            axisY.X / sizeY, axisY.Y / sizeY, axisY.Z / sizeY, 0f,
            axisZ.X / sizeZ, axisZ.Y / sizeZ, axisZ.Z / sizeZ, 0f,
            boxTransform.M41, boxTransform.M42, boxTransform.M43, 1f);
        bounds = new SceneOrientedBounds(
            rigidTransform,
            new Vector3(sizeX, sizeY, sizeZ) * 0.5f);
        return true;
    }

    private static void CopyOrientedBoxCorners(Matrix4x4 transform, Vector3 halfExtents, Span<Vector3> corners)
    {
        Span<Vector3> localCorners =
        [
            new Vector3(-halfExtents.X, -halfExtents.Y, -halfExtents.Z),
            new Vector3(halfExtents.X, -halfExtents.Y, -halfExtents.Z),
            new Vector3(halfExtents.X, halfExtents.Y, -halfExtents.Z),
            new Vector3(-halfExtents.X, halfExtents.Y, -halfExtents.Z),
            new Vector3(-halfExtents.X, -halfExtents.Y, halfExtents.Z),
            new Vector3(halfExtents.X, -halfExtents.Y, halfExtents.Z),
            new Vector3(halfExtents.X, halfExtents.Y, halfExtents.Z),
            new Vector3(-halfExtents.X, halfExtents.Y, halfExtents.Z),
        ];

        for (var index = 0; index < localCorners.Length; ++index)
        {
            corners[index] = Vector3.Transform(localCorners[index], transform);
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

    public static Matrix4x4 CreateForwardSkewedBoxTransform(
        SceneTransform transform,
        float length,
        Vector2 skewAngleDegrees,
        float lateralPadding = 0f)
    {
        length = MathF.Max(length, 0.01f);
        lateralPadding = MathF.Max(lateralPadding, 0f);

        Matrix4x4 worldTransform = SceneTransformMath.CreateWorldTransform(transform);
        Quaternion rotation = SceneTransformMath.CreateRotationQuaternion(transform.RotationDegrees);
        Vector3 axisX = Vector3.TransformNormal(Vector3.UnitX, worldTransform);
        Vector3 axisY = Vector3.TransformNormal(Vector3.UnitY, worldTransform);
        Vector3 axisZ = Vector3.TransformNormal(Vector3.UnitZ, worldTransform);
        Vector3 directionX = NormalizeOrFallback(axisX, Vector3.Transform(Vector3.UnitX, rotation));
        Vector3 directionY = NormalizeOrFallback(axisY, Vector3.Transform(Vector3.UnitY, rotation));

        axisX += directionX * (lateralPadding * 2f);
        axisY += directionY * (lateralPadding * 2f);

        float skewX = length * MathF.Tan(ToSkewRadians(skewAngleDegrees.Y));
        float skewY = -length * MathF.Tan(ToSkewRadians(skewAngleDegrees.X));
        Vector3 forward = (axisZ * length) + (directionX * skewX) + (directionY * skewY);
        Vector3 center = transform.Position + (forward * 0.5f);

        return new Matrix4x4(
            axisX.X, axisX.Y, axisX.Z, 0f,
            axisY.X, axisY.Y, axisY.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            center.X, center.Y, center.Z, 1f);
    }

    private static float ToSkewRadians(float angleDegrees)
        => Math.Clamp(angleDegrees, -MaximumSkewAngleDegrees, MaximumSkewAngleDegrees) * (MathF.PI / 180f);

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
        => NumericsUtility.TryNormalize(value, out Vector3 normalized) ? normalized : fallback;

    public static void CopyUnitBoxCorners(Matrix4x4 transform, Span<Vector3> corners)
    {
        corners[0] = Vector3.Transform(new Vector3(-0.5f, -0.5f, -0.5f), transform);
        corners[1] = Vector3.Transform(new Vector3(0.5f, -0.5f, -0.5f), transform);
        corners[2] = Vector3.Transform(new Vector3(0.5f, 0.5f, -0.5f), transform);
        corners[3] = Vector3.Transform(new Vector3(-0.5f, 0.5f, -0.5f), transform);
        corners[4] = Vector3.Transform(new Vector3(-0.5f, -0.5f, 0.5f), transform);
        corners[5] = Vector3.Transform(new Vector3(0.5f, -0.5f, 0.5f), transform);
        corners[6] = Vector3.Transform(new Vector3(0.5f, 0.5f, 0.5f), transform);
        corners[7] = Vector3.Transform(new Vector3(-0.5f, 0.5f, 0.5f), transform);
    }

    public static float ComputeOrientedBoundsSupportExtent(Quaternion rotation, Vector3 halfExtents, Vector3 normal)
    {
        if (!NumericsUtility.TryNormalize(normal, out var normalizedNormal))
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

