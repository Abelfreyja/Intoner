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
        ScenePointerRay ray,
        float maxDistance,
        out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        if (!SceneRaycastMath.MayIntersectSphere(ray, target.BoundsCenter, target.BoundsRadius, maxDistance))
        {
            return false;
        }

        Vector3 localOrigin = Vector3.Transform(ray.Origin, target.InverseWorldTransform);
        Vector3 localDirection = Vector3.TransformNormal(ray.Direction, target.InverseWorldTransform);
        if (!NumericsUtility.HasLength(localDirection))
        {
            return false;
        }

        if (!TryRaycastLocal(
                target.Geometry,
                new(localOrigin, localDirection),
                maxDistance,
                out float closestDistance,
                out Vector3 localHitNormal))
        {
            return false;
        }

        Vector3 worldHitNormal = Vector3.TransformNormal(localHitNormal, target.NormalTransform);
        Vector3 orientedNormal = SceneRaycastMath.OrientSurfaceNormal(worldHitNormal, ray.Direction);
        if (!NumericsUtility.HasLength(orientedNormal))
        {
            return false;
        }

        hit = new SceneSurfaceHit(ray.Origin + (ray.Direction * closestDistance), orientedNormal, Distance: closestDistance);
        return true;
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

    private static bool TryRaycastLocal(
        SceneSelectionGeometry geometry,
        ScenePointerRay ray,
        float maxDistance,
        out float closestDistance,
        out Vector3 localHitNormal)
    {
        if (geometry.TryGetAcceleration(out SceneGeometryAcceleration? acceleration))
        {
            if (!acceleration.TryRaycast(
                    geometry,
                    ray,
                    maxDistance,
                    out int closestTriangle,
                    out closestDistance))
            {
                localHitNormal = Vector3.Zero;
                return false;
            }

            return TryResolveTriangleNormal(geometry, closestTriangle, out localHitNormal);
        }

        return TryRaycastLinear(
            geometry,
            ray,
            maxDistance,
            out closestDistance,
            out localHitNormal);
    }

    private static bool TryRaycastLinear(
        SceneSelectionGeometry geometry,
        ScenePointerRay ray,
        float maxDistance,
        out float closestDistance,
        out Vector3 localHitNormal)
    {
        closestDistance = maxDistance;
        localHitNormal = Vector3.Zero;
        for (int triangleIndex = 0; triangleIndex < geometry.Indices.Length / 3; triangleIndex++)
        {
            if (!TryRaycastTriangleDistance(
                    geometry,
                    triangleIndex,
                    ray,
                    out float distance)
                || distance >= closestDistance
                || !TryResolveTriangleNormal(geometry, triangleIndex, out Vector3 normal))
            {
                continue;
            }

            closestDistance = distance;
            localHitNormal = normal;
        }

        return NumericsUtility.HasLength(localHitNormal);
    }

    internal static bool TryRaycastTriangleDistance(
        SceneSelectionGeometry geometry,
        int triangleIndex,
        ScenePointerRay ray,
        out float distance)
    {
        distance = 0f;
        if (!geometry.TryGetTriangle(triangleIndex, out var triangle))
        {
            return false;
        }

        Vector3 edge1 = triangle.B - triangle.A;
        Vector3 edge2 = triangle.C - triangle.A;
        Vector3 p = Vector3.Cross(ray.Direction, edge2);
        float determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) < TriangleEpsilon)
        {
            return false;
        }

        float inverseDeterminant = 1f / determinant;
        Vector3 originToVertex = ray.Origin - triangle.A;
        float u = Vector3.Dot(originToVertex, p) * inverseDeterminant;
        if (u is < 0f or > 1f)
        {
            return false;
        }

        Vector3 q = Vector3.Cross(originToVertex, edge1);
        float v = Vector3.Dot(ray.Direction, q) * inverseDeterminant;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        distance = Vector3.Dot(edge2, q) * inverseDeterminant;
        return distance > SceneRaycastMath.MinimumHitDistance
               && NumericsUtility.HasLength(Vector3.Cross(edge1, edge2));
    }

    private static bool TryResolveTriangleNormal(
        SceneSelectionGeometry geometry,
        int triangleIndex,
        out Vector3 normal)
    {
        if (!geometry.TryGetTriangle(triangleIndex, out var triangle))
        {
            normal = Vector3.Zero;
            return false;
        }

        return NumericsUtility.TryNormalize(Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A), out normal);
    }
}
