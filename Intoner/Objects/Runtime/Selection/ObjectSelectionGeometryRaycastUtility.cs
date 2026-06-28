using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class ObjectSelectionGeometryRaycastUtility
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
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);
        if (!MayIntersectBounds(geometry, worldTransform, rayOrigin, rayDirection, maxDistance))
        {
            return false;
        }

        var positions = geometry.Positions;
        var indices = geometry.Indices;
        var closestDistance = maxDistance;
        var hitNormal = Vector3.Zero;
        for (var index = 0; index + 2 < indices.Length; index += 3)
        {
            if (!TryGetWorldPosition(positions, indices[index], worldTransform, out var v0)
                || !TryGetWorldPosition(positions, indices[index + 1], worldTransform, out var v1)
                || !TryGetWorldPosition(positions, indices[index + 2], worldTransform, out var v2)
                || !TryRaycastTriangle(rayOrigin, rayDirection, v0, v1, v2, out var distance, out var normal)
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

        var orientedNormal = ObjectSurfaceRaycastUtility.OrientSurfaceNormal(hitNormal, rayDirection);
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
        var radius = geometry.BoundsRadius * ResolveMaxScale(worldTransform);
        if (radius <= 0f)
        {
            return true;
        }

        var center = Vector3.Transform(geometry.BoundsCenter, worldTransform);
        var toCenter = center - rayOrigin;
        var projection = Vector3.Dot(toCenter, rayDirection);
        var closestDistanceSquared = Vector3.DistanceSquared(toCenter, rayDirection * projection);
        var radiusSquared = radius * radius;
        if (closestDistanceSquared > radiusSquared)
        {
            return false;
        }

        var halfChord = MathF.Sqrt(MathF.Max(0f, radiusSquared - closestDistanceSquared));
        var nearDistance = projection - halfChord;
        var farDistance = projection + halfChord;
        return farDistance > ObjectSurfaceRaycastUtility.MinimumHitDistance && nearDistance < maxDistance;
    }

    private static float ResolveMaxScale(Matrix4x4 transform)
    {
        var x = new Vector3(transform.M11, transform.M12, transform.M13).Length();
        var y = new Vector3(transform.M21, transform.M22, transform.M23).Length();
        var z = new Vector3(transform.M31, transform.M32, transform.M33).Length();
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

        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var p = Vector3.Cross(rayDirection, edge2);
        var determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < TriangleEpsilon)
        {
            return false;
        }

        var inverseDeterminant = 1f / determinant;
        var originToVertex = rayOrigin - v0;
        var u = Vector3.Dot(originToVertex, p) * inverseDeterminant;
        if (u is < 0f or > 1f)
        {
            return false;
        }

        var q = Vector3.Cross(originToVertex, edge1);
        var v = Vector3.Dot(rayDirection, q) * inverseDeterminant;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        distance = Vector3.Dot(edge2, q) * inverseDeterminant;
        if (distance <= ObjectSurfaceRaycastUtility.MinimumHitDistance)
        {
            return false;
        }

        return ObjectMathUtility.TryNormalize(Vector3.Cross(edge1, edge2), out normal);
    }
}

