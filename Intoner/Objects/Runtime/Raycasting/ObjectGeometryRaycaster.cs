using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class ObjectGeometryRaycaster
{
    private const float TriangleEpsilon = 0.000001f;

    public static bool TryRaycastNormalized(
        ObjectSelectionGeometry geometry,
        Matrix4x4 worldTransform,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out ObjectSurfaceHit hit)
    {
        hit = ObjectSurfaceHit.Empty;
        if (!MayIntersectBounds(geometry, worldTransform, rayOrigin, rayDirection, maxDistance))
        {
            return false;
        }

        Vector3[] positions = geometry.Positions;
        int[] indices = geometry.Indices;
        float closestDistance = maxDistance;
        Vector3 hitNormal = Vector3.Zero;
        for (int index = 0; index + 2 < indices.Length; index += 3)
        {
            if (!TryGetWorldPosition(positions, indices[index], worldTransform, out Vector3 v0)
                || !TryGetWorldPosition(positions, indices[index + 1], worldTransform, out Vector3 v1)
                || !TryGetWorldPosition(positions, indices[index + 2], worldTransform, out Vector3 v2)
                || !TryRaycastTriangle(rayOrigin, rayDirection, v0, v1, v2, out float distance, out Vector3 normal)
                || distance >= closestDistance)
            {
                continue;
            }

            closestDistance = distance;
            hitNormal = normal;
        }

        if (!ObjectMathUtility.HasLength(hitNormal))
        {
            return false;
        }

        Vector3 orientedNormal = ObjectRaycastMath.OrientSurfaceNormal(hitNormal, rayDirection);
        if (!ObjectMathUtility.HasLength(orientedNormal))
        {
            return false;
        }

        hit = new ObjectSurfaceHit(rayOrigin + (rayDirection * closestDistance), orientedNormal, Distance: closestDistance);
        return true;
    }

    private static bool MayIntersectBounds(
        ObjectSelectionGeometry geometry,
        Matrix4x4 worldTransform,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance)
    {
        float radius = geometry.BoundsRadius * ResolveMaxScale(worldTransform);
        if (radius <= 0f)
        {
            return true;
        }

        Vector3 center = Vector3.Transform(geometry.BoundsCenter, worldTransform);
        Vector3 toCenter = center - rayOrigin;
        float projection = Vector3.Dot(toCenter, rayDirection);
        float closestDistanceSquared = Vector3.DistanceSquared(toCenter, rayDirection * projection);
        float radiusSquared = radius * radius;
        if (closestDistanceSquared > radiusSquared)
        {
            return false;
        }

        float halfChord = MathF.Sqrt(MathF.Max(0f, radiusSquared - closestDistanceSquared));
        float nearDistance = projection - halfChord;
        float farDistance = projection + halfChord;
        return farDistance > ObjectRaycastMath.MinimumHitDistance && nearDistance < maxDistance;
    }

    private static float ResolveMaxScale(Matrix4x4 transform)
    {
        float x = new Vector3(transform.M11, transform.M12, transform.M13).Length();
        float y = new Vector3(transform.M21, transform.M22, transform.M23).Length();
        float z = new Vector3(transform.M31, transform.M32, transform.M33).Length();
        return MathF.Max(x, MathF.Max(y, z));
    }

    private static bool TryGetWorldPosition(Vector3[] positions, int index, Matrix4x4 worldTransform, out Vector3 position)
    {
        if ((uint)index >= (uint)positions.Length)
        {
            position = Vector3.Zero;
            return false;
        }

        position = Vector3.Transform(positions[index], worldTransform);
        return true;
    }

    private static bool TryRaycastTriangle(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        out float distance,
        out Vector3 normal)
    {
        distance = 0f;
        normal = Vector3.Zero;

        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 p = Vector3.Cross(rayDirection, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < TriangleEpsilon)
        {
            return false;
        }

        float inverseDeterminant = 1f / determinant;
        Vector3 originToVertex = rayOrigin - v0;
        float u = Vector3.Dot(originToVertex, p) * inverseDeterminant;
        if (u is < 0f or > 1f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(originToVertex, edge1);
        float v = Vector3.Dot(rayDirection, q) * inverseDeterminant;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        distance = Vector3.Dot(edge2, q) * inverseDeterminant;
        return distance > ObjectRaycastMath.MinimumHitDistance
               && ObjectMathUtility.TryNormalize(Vector3.Cross(edge1, edge2), out normal);
    }
}
