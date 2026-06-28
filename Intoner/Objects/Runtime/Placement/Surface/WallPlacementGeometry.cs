using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;
using OrientedBounds = FFXIVClientStructs.FFXIV.Common.Math.OrientedBounds;

namespace Intoner.Objects.Runtime;

internal readonly record struct WallPlacementProbe(Vector3 Origin, Vector3 Direction, float ExpectedDistance);

internal static class WallPlacementGeometry
{
    public const float NormalDotThreshold = 0.9848077f;

    private const float WallNormalYThreshold = 0.5f;

    public static bool TryResolveProbe(
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot boundsSnapshot,
        out WallPlacementProbe probe)
    {
        Quaternion rotation = ObjectTransformMath.CreateRotationQuaternion(snapshot.Transform.RotationDegrees);
        Vector3 direction = Vector3.Transform(-Vector3.UnitZ, rotation);
        if (!ObjectMathUtility.TryNormalize(direction, out direction))
        {
            probe = default;
            return false;
        }

        Vector3 probeOrigin;
        float expectedDistance;
        if (boundsSnapshot.LocalBounds is { } localBounds)
        {
            probeOrigin = localBounds.Transform.Translation;
            expectedDistance = ObjectShapeMath.ComputeOrientedBoundsSupportExtent(rotation, localBounds.HalfExtents, direction);
        }
        else
        {
            probeOrigin = (boundsSnapshot.Min + boundsSnapshot.Max) * 0.5f;
            Vector3 halfExtents = (boundsSnapshot.Max - boundsSnapshot.Min) * 0.5f;
            expectedDistance = Vector3.Dot(halfExtents, Vector3.Abs(direction));
        }

        probe = new WallPlacementProbe(probeOrigin, direction, expectedDistance);
        return true;
    }

    public static bool IsWallSurfaceNormal(Vector3 normal)
        => ObjectMathUtility.TryNormalize(normal, out Vector3 normalizedNormal)
           && MathF.Abs(normalizedNormal.Y) <= WallNormalYThreshold;

    public static bool ContainsWallSurfacePoint(
        ObjectBoundsSnapshot boundsSnapshot,
        Vector3 worldPoint,
        Vector3 expectedNormal,
        float tolerance)
        => boundsSnapshot.LocalBounds is { } localBounds
            ? ContainsOrientedWallSurfacePoint(localBounds, worldPoint, expectedNormal, tolerance)
            : ContainsAxisAlignedWallSurfacePoint(boundsSnapshot, worldPoint, expectedNormal, tolerance);

    private static bool ContainsOrientedWallSurfacePoint(
        OrientedBounds bounds,
        Vector3 worldPoint,
        Vector3 expectedNormal,
        float tolerance)
    {
        if (!Matrix4x4.Invert(bounds.Transform, out Matrix4x4 inverseTransform))
        {
            return false;
        }

        Vector3 localPoint = Vector3.Transform(worldPoint, inverseTransform);
        Vector3 localNormal = Vector3.TransformNormal(expectedNormal, inverseTransform);
        return ObjectMathUtility.TryNormalize(localNormal, out localNormal)
               && (ContainsLocalWallSurfacePoint(localPoint, localNormal, bounds.HalfExtents, Vector3.UnitX, tolerance)
                   || ContainsLocalWallSurfacePoint(localPoint, localNormal, bounds.HalfExtents, Vector3.UnitZ, tolerance));
    }

    private static bool ContainsAxisAlignedWallSurfacePoint(
        ObjectBoundsSnapshot bounds,
        Vector3 worldPoint,
        Vector3 expectedNormal,
        float tolerance)
    {
        if (!ObjectMathUtility.TryNormalize(expectedNormal, out Vector3 normal))
        {
            return false;
        }

        Vector3 center = (bounds.Min + bounds.Max) * 0.5f;
        Vector3 halfExtents = (bounds.Max - bounds.Min) * 0.5f;
        Vector3 localPoint = worldPoint - center;
        return ContainsLocalWallSurfacePoint(localPoint, normal, halfExtents, Vector3.UnitX, tolerance)
               || ContainsLocalWallSurfacePoint(localPoint, normal, halfExtents, Vector3.UnitZ, tolerance);
    }

    private static bool ContainsLocalWallSurfacePoint(
        Vector3 localPoint,
        Vector3 localNormal,
        Vector3 halfExtents,
        Vector3 faceAxis,
        float tolerance)
    {
        float normalComponent = Vector3.Dot(localNormal, faceAxis);
        if (MathF.Abs(normalComponent) < NormalDotThreshold)
        {
            return false;
        }

        float faceSign = MathF.Sign(normalComponent);
        Vector3 absPoint = Vector3.Abs(localPoint);
        if (faceAxis.X > 0.5f)
        {
            return MathF.Abs(localPoint.X - (halfExtents.X * faceSign)) <= tolerance
                   && absPoint.Y <= halfExtents.Y + tolerance
                   && absPoint.Z <= halfExtents.Z + tolerance;
        }

        return MathF.Abs(localPoint.Z - (halfExtents.Z * faceSign)) <= tolerance
               && absPoint.X <= halfExtents.X + tolerance
               && absPoint.Y <= halfExtents.Y + tolerance;
    }
}

