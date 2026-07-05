using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Runtime;

internal sealed unsafe class NativePlacementCollisionQuery
{
    private const int PlacementCollisionLayerMask = 1;

    private readonly NativeRaycastDelegate?     _raycast;
    private readonly NativeSweepSphereDelegate? _sweepSphere;

    public NativePlacementCollisionQuery(
        ILogger<NativePlacementCollisionQuery> logger,
        ISigScanner sigScanner)
    {
        _raycast = ObjectInteropHookUtility.CreateDelegate<NativeRaycastDelegate>(
            logger,
            sigScanner,
            ObjectSignatures.NativeHousingPlacementRaycast);
        _sweepSphere = ObjectInteropHookUtility.CreateDelegate<NativeSweepSphereDelegate>(
            logger,
            sigScanner,
            ObjectSignatures.NativeHousingPlacementSweepSphere);
    }

    public bool TryRaycast(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out ObjectSurfaceHit hit)
    {
        hit = ObjectSurfaceHit.Empty;
        if (_raycast is null
            || !ObjectMathUtility.TryNormalize(direction, out Vector3 normalizedDirection)
            || !ObjectCollisionSceneQuery.HasScene())
        {
            return false;
        }

        NativePlacementRay ray = new()
        {
            Origin = origin,
            Direction = normalizedDirection,
        };

        Vector3 hitPoint = default;
        Vector3 hitNormal = default;
        ulong material = 0;
        Collider* collider = null;
        float resolvedMaxDistance = ObjectRaycastMath.ResolveMaxDistance(maxDistance);
        if (_raycast(&ray, resolvedMaxDistance, &hitPoint, &hitNormal, &material, &collider) == 0)
        {
            return false;
        }

        hit = CreateHit(origin, normalizedDirection, hitPoint, hitNormal, material, collider);
        return true;
    }

    public bool TryRaycastMaterialMask(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        ulong materialMask,
        out ObjectSurfaceHit hit)
    {
        hit = ObjectSurfaceHit.Empty;
        if (materialMask == 0
            || !ObjectMathUtility.TryNormalize(direction, out Vector3 normalizedDirection)
            || !ObjectCollisionSceneQuery.TryResolveModule(out BGCollisionModule* collisionModule))
        {
            return false;
        }

        RaycastHit raycastHit = new();
        RaycastMaterialFilter materialFilter = new()
        {
            Mask = materialMask,
            Value = 0,
        };
        int* materialFilterPointer = (int*)&materialFilter;

        Vector3 rayOrigin = origin;
        Vector3 rayDirection = normalizedDirection;
        float resolvedMaxDistance = ObjectRaycastMath.ResolveMaxDistance(maxDistance);
        if (!collisionModule->RaycastMaterialFilter(
                &raycastHit,
                &rayOrigin,
                &rayDirection,
                resolvedMaxDistance,
                PlacementCollisionLayerMask,
                materialFilterPointer))
        {
            return false;
        }

        hit = CreateHit(origin, normalizedDirection, raycastHit);
        return true;
    }

    public bool TrySweepSphere(
        Vector3 origin,
        Vector3 direction,
        float radius,
        float maxDistance,
        out ObjectSurfaceHit hit,
        out bool surfaceHit)
    {
        hit = ObjectSurfaceHit.Empty;
        surfaceHit = false;
        if (_sweepSphere is null
            || !ObjectMathUtility.TryNormalize(direction, out Vector3 normalizedDirection)
            || !float.IsFinite(radius)
            || radius <= ObjectMathUtility.ScalarEpsilon
            || !ObjectCollisionSceneQuery.HasScene())
        {
            return false;
        }

        NativePlacementSphere sphere = new()
        {
            Origin = origin,
            Radius = radius,
        };

        Vector3 hitPoint = default;
        Vector3 hitNormal = default;
        ulong material = 0;
        Collider* collider = null;
        float resolvedMaxDistance = ObjectRaycastMath.ResolveMaxDistance(maxDistance);
        surfaceHit = _sweepSphere(&sphere, &normalizedDirection, resolvedMaxDistance, &hitPoint, &hitNormal, &material, &collider) != 0;

        hit = CreateHit(origin, normalizedDirection, hitPoint, hitNormal, material, collider);
        return true;
    }

    private static ObjectSurfaceHit CreateHit(
        Vector3 origin,
        Vector3 direction,
        in RaycastHit raycastHit)
        => new(
            raycastHit.Point,
            ObjectRaycastMath.ResolveSurfaceNormal(raycastHit, direction),
            raycastHit.Material,
            (nint)raycastHit.Object,
            raycastHit.Distance > ObjectRaycastMath.MinimumHitDistance
                ? raycastHit.Distance
                : Vector3.Distance(origin, raycastHit.Point),
            ObjectSurfaceHitSource.Native);

    private static ObjectSurfaceHit CreateHit(
        Vector3 origin,
        Vector3 direction,
        Vector3 hitPoint,
        Vector3 hitNormal,
        ulong material,
        Collider* collider)
        => new(
            hitPoint,
            ObjectRaycastMath.OrientSurfaceNormal(hitNormal, direction),
            material,
            (nint)collider,
            Vector3.Distance(origin, hitPoint),
            ObjectSurfaceHitSource.Native);

    private delegate byte NativeRaycastDelegate(
        NativePlacementRay* ray,
        float maxDistance,
        Vector3* hitPoint,
        Vector3* hitNormal,
        ulong* material,
        Collider** collider);

    private delegate byte NativeSweepSphereDelegate(
        NativePlacementSphere* sphere,
        Vector3* direction,
        float maxDistance,
        Vector3* hitPoint,
        Vector3* hitNormal,
        ulong* material,
        Collider** collider);

    [StructLayout(LayoutKind.Explicit, Size = 0x1C)]
    private struct NativePlacementRay
    {
        [FieldOffset(0x00)] public Vector3 Origin;
        [FieldOffset(0x10)] public Vector3 Direction;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x10)]
    private struct NativePlacementSphere
    {
        [FieldOffset(0x00)] public Vector3 Origin;
        [FieldOffset(0x0C)] public float Radius;
    }
}

