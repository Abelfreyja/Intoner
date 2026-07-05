using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class ObjectRaycastMath
{
    public const float MinimumHitDistance = 0.001f;

    public static Vector3 ResolveSurfaceNormal(in RaycastHit raycastHit, Vector3 rayDirection)
    {
        Vector3 triangleNormal = ComputeTriangleNormal(raycastHit.V1, raycastHit.V2, raycastHit.V3);
        if (ObjectMathUtility.HasLength(triangleNormal))
        {
            return OrientSurfaceNormal(triangleNormal, rayDirection);
        }

        return ObjectMathUtility.TryNormalize(raycastHit.Normal, out Vector3 normalizedNormal)
            ? OrientSurfaceNormal(normalizedNormal, rayDirection)
            : Vector3.Zero;
    }

    /// <summary> intersects a ray against one plane </summary>
    /// <param name="rayOrigin">the ray origin in world space</param>
    /// <param name="rayDirection">the ray direction in world space</param>
    /// <param name="planePoint">any point on the plane</param>
    /// <param name="planeNormal">the plane normal</param>
    /// <param name="intersectionPoint">the resolved hit point when an intersection exists in front of the ray</param>
    /// <returns>true when the ray intersects the plane in front of the origin</returns>
    public static bool TryIntersectRayPlane(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 planePoint,
        Vector3 planeNormal,
        out Vector3 intersectionPoint)
    {
        intersectionPoint = default;
        if (!ObjectMathUtility.HasLength(rayDirection) || !ObjectMathUtility.HasLength(planeNormal))
        {
            return false;
        }

        float denominator = Vector3.Dot(rayDirection, planeNormal);
        if (ObjectMathUtility.IsNearlyZero(denominator))
        {
            return false;
        }

        float distance = Vector3.Dot(planePoint - rayOrigin, planeNormal) / denominator;
        if (distance < 0f)
        {
            return false;
        }

        intersectionPoint = rayOrigin + (rayDirection * distance);
        return true;
    }

    public static Vector3 OrientSurfaceNormal(Vector3 normal, Vector3 rayDirection)
    {
        if (!ObjectMathUtility.TryNormalize(normal, out Vector3 orientedNormal))
        {
            return Vector3.Zero;
        }

        return Vector3.Dot(orientedNormal, rayDirection) > 0f
            ? -orientedNormal
            : orientedNormal;
    }

    public static float ResolveMaxDistance(float maxDistance)
        => float.IsFinite(maxDistance) && maxDistance > MinimumHitDistance
            ? maxDistance
            : float.PositiveInfinity;

    private static Vector3 ComputeTriangleNormal(Vector3 v1, Vector3 v2, Vector3 v3)
    {
        Vector3 edge1 = v2 - v1;
        Vector3 edge2 = v3 - v1;
        Vector3 normal = Vector3.Cross(edge1, edge2);
        return ObjectMathUtility.TryNormalize(normal, out Vector3 normalizedNormal)
            ? normalizedNormal
            : Vector3.Zero;
    }
}
