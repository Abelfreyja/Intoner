using Intoner.Objects.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Scene;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneGeometryRaycastTarget(
    SceneSelectionGeometry Geometry,
    Matrix4x4 InverseWorldTransform,
    Matrix4x4 NormalTransform,
    Vector3 BoundsCenter,
    float BoundsRadius);

internal static class SceneGeometryRaycaster
{
    private const float TriangleEpsilon = 0.000001f;

    public static bool TryCreateTarget(
        SceneSelectionGeometry geometry,
        Matrix4x4 worldTransform,
        out SceneGeometryRaycastTarget target)
    {
        if (!NumericsUtility.IsFinite(worldTransform)
            || !Matrix4x4.Invert(worldTransform, out Matrix4x4 inverseWorldTransform)
            || !NumericsUtility.IsFinite(inverseWorldTransform))
        {
            target = default;
            return false;
        }

        target = new SceneGeometryRaycastTarget(
            geometry,
            inverseWorldTransform,
            Matrix4x4.Transpose(inverseWorldTransform),
            Vector3.Transform(geometry.BoundsCenter, worldTransform),
            geometry.BoundsRadius * ResolveBoundingScale(worldTransform));
        return true;
    }

    public static bool TryRaycastNormalized(
        in SceneGeometryRaycastTarget target,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        if (!MayIntersectBounds(target, rayOrigin, rayDirection, maxDistance))
        {
            return false;
        }

        Vector3 localOrigin = Vector3.Transform(rayOrigin, target.InverseWorldTransform);
        Vector3 localDirection = Vector3.TransformNormal(rayDirection, target.InverseWorldTransform);
        if (!NumericsUtility.HasLength(localDirection))
        {
            return false;
        }

        if (!TryRaycastLocal(
                target.Geometry,
                localOrigin,
                localDirection,
                maxDistance,
                out float closestDistance,
                out Vector3 localHitNormal))
        {
            return false;
        }

        Vector3 worldHitNormal = Vector3.TransformNormal(localHitNormal, target.NormalTransform);
        Vector3 orientedNormal = SceneRaycastMath.OrientSurfaceNormal(worldHitNormal, rayDirection);
        if (!NumericsUtility.HasLength(orientedNormal))
        {
            return false;
        }

        hit = new SceneSurfaceHit(rayOrigin + (rayDirection * closestDistance), orientedNormal, Distance: closestDistance);
        return true;
    }

    private static bool MayIntersectBounds(
        in SceneGeometryRaycastTarget target,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance)
    {
        float radius = target.BoundsRadius;
        if (radius <= 0f)
        {
            return true;
        }

        Vector3 toCenter = target.BoundsCenter - rayOrigin;
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
        return farDistance > SceneRaycastMath.MinimumHitDistance && nearDistance < maxDistance;
    }

    private static float ResolveBoundingScale(Matrix4x4 transform)
    {
        var x = new Vector3(transform.M11, transform.M12, transform.M13);
        var y = new Vector3(transform.M21, transform.M22, transform.M23);
        var z = new Vector3(transform.M31, transform.M32, transform.M33);
        float xy = MathF.Abs(Vector3.Dot(x, y));
        float xz = MathF.Abs(Vector3.Dot(x, z));
        float yz = MathF.Abs(Vector3.Dot(y, z));
        float scaleSquared = MathF.Max(
            x.LengthSquared() + xy + xz,
            MathF.Max(
                y.LengthSquared() + xy + yz,
                z.LengthSquared() + xz + yz));
        return MathF.Sqrt(scaleSquared);
    }

    private static bool TryGetPosition(Vector3[] positions, int index, out Vector3 position)
    {
        if ((uint)index >= (uint)positions.Length)
        {
            position = Vector3.Zero;
            return false;
        }

        position = positions[index];
        return true;
    }

    private static bool TryRaycastLocal(
        SceneSelectionGeometry geometry,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out float closestDistance,
        out Vector3 localHitNormal)
    {
        if (geometry.TryGetAcceleration(out SceneGeometryAcceleration? acceleration))
        {
            if (!acceleration.TryRaycast(
                    geometry,
                    rayOrigin,
                    rayDirection,
                    maxDistance,
                    out int closestTriangle,
                    out closestDistance))
            {
                localHitNormal = Vector3.Zero;
                return false;
            }

            return TryResolveTriangleNormal(geometry.Positions, geometry.Indices, closestTriangle, out localHitNormal);
        }

        return TryRaycastLinear(
            geometry.Positions,
            geometry.Indices,
            rayOrigin,
            rayDirection,
            maxDistance,
            out closestDistance,
            out localHitNormal);
    }

    private static bool TryRaycastLinear(
        Vector3[] positions,
        int[] indices,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out float closestDistance,
        out Vector3 localHitNormal)
    {
        closestDistance = maxDistance;
        localHitNormal = Vector3.Zero;
        for (int triangleIndex = 0; triangleIndex < indices.Length / 3; triangleIndex++)
        {
            if (!TryRaycastTriangleDistance(
                    positions,
                    indices,
                    triangleIndex,
                    rayOrigin,
                    rayDirection,
                    out float distance)
                || distance >= closestDistance
                || !TryResolveTriangleNormal(positions, indices, triangleIndex, out Vector3 normal))
            {
                continue;
            }

            closestDistance = distance;
            localHitNormal = normal;
        }

        return NumericsUtility.HasLength(localHitNormal);
    }

    internal static bool TryRaycastTriangleDistance(
        Vector3[] positions,
        int[] indices,
        int triangleIndex,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out float distance)
    {
        distance = 0f;
        int indexOffset = triangleIndex * 3;
        if (indexOffset < 0
            || indexOffset + 2 >= indices.Length
            || !TryGetPosition(positions, indices[indexOffset], out Vector3 v0)
            || !TryGetPosition(positions, indices[indexOffset + 1], out Vector3 v1)
            || !TryGetPosition(positions, indices[indexOffset + 2], out Vector3 v2))
        {
            return false;
        }

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
        return distance > SceneRaycastMath.MinimumHitDistance
               && NumericsUtility.HasLength(Vector3.Cross(edge1, edge2));
    }

    private static bool TryResolveTriangleNormal(
        Vector3[] positions,
        int[] indices,
        int triangleIndex,
        out Vector3 normal)
    {
        int indexOffset = triangleIndex * 3;
        if (indexOffset < 0
            || indexOffset + 2 >= indices.Length
            || !TryGetPosition(positions, indices[indexOffset], out Vector3 v0)
            || !TryGetPosition(positions, indices[indexOffset + 1], out Vector3 v1)
            || !TryGetPosition(positions, indices[indexOffset + 2], out Vector3 v2))
        {
            normal = Vector3.Zero;
            return false;
        }

        return NumericsUtility.TryNormalize(Vector3.Cross(v1 - v0, v2 - v0), out normal);
    }
}
