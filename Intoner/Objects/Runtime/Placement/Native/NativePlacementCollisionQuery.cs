using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Interop;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Interop;
using Microsoft.Extensions.Logging;
using System.Numerics;
using NativeRay = FFXIVClientStructs.FFXIV.Client.Graphics.Ray;
using SphereBounds = FFXIVClientStructs.FFXIV.Common.Math.SphereBounds;

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
        _raycast = InteropHookUtility.CreateDelegate<NativeRaycastDelegate>(
            logger,
            sigScanner,
            IntonerSignatures.NativeHousingPlacementRaycast);
        _sweepSphere = InteropHookUtility.CreateDelegate<NativeSweepSphereDelegate>(
            logger,
            sigScanner,
            IntonerSignatures.NativeHousingPlacementSweepSphere);
    }

    public bool TryRaycast(
        Vector3 origin,
        Vector3 direction,
        float maxDistance,
        out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        if (_raycast is null
            || !NumericsUtility.TryNormalize(direction, out Vector3 normalizedDirection)
            || !ObjectCollisionSceneQuery.HasScene())
        {
            return false;
        }

        NativeRay ray = new()
        {
            Origin = origin,
            Direction = normalizedDirection,
        };

        Vector3 hitPoint = default;
        Vector3 hitNormal = default;
        ulong material = 0;
        Collider* collider = null;
        float resolvedMaxDistance = SceneRaycastMath.ResolveMaxDistance(maxDistance);
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
        out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        if (materialMask == 0
            || !NumericsUtility.TryNormalize(direction, out Vector3 normalizedDirection)
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
        float resolvedMaxDistance = SceneRaycastMath.ResolveMaxDistance(maxDistance);
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
        out SceneSurfaceHit hit,
        out bool surfaceHit)
    {
        hit = SceneSurfaceHit.Empty;
        surfaceHit = false;
        if (_sweepSphere is null
            || !NumericsUtility.TryNormalize(direction, out Vector3 normalizedDirection)
            || !float.IsFinite(radius)
            || radius <= NumericsUtility.ScalarEpsilon
            || !ObjectCollisionSceneQuery.HasScene())
        {
            return false;
        }

        SphereBounds sphere = new()
        {
            CenterPoint = origin,
            Radius = radius,
        };

        Vector3 hitPoint = default;
        Vector3 hitNormal = default;
        ulong material = 0;
        Collider* collider = null;
        float resolvedMaxDistance = SceneRaycastMath.ResolveMaxDistance(maxDistance);
        surfaceHit = _sweepSphere(&sphere, &normalizedDirection, resolvedMaxDistance, &hitPoint, &hitNormal, &material, &collider) != 0;

        hit = CreateHit(origin, normalizedDirection, hitPoint, hitNormal, material, collider);
        return true;
    }

    private static SceneSurfaceHit CreateHit(
        Vector3 origin,
        Vector3 direction,
        in RaycastHit raycastHit)
        => new(
            raycastHit.Point,
            SceneRaycastMath.ResolveSurfaceNormal(
                raycastHit.V1,
                raycastHit.V2,
                raycastHit.V3,
                raycastHit.Normal,
                direction),
            raycastHit.Material,
            (nint)raycastHit.Object,
            raycastHit.Distance > SceneRaycastMath.MinimumHitDistance
                ? raycastHit.Distance
                : Vector3.Distance(origin, raycastHit.Point),
            SceneSurfaceHitSource.Native);

    private static SceneSurfaceHit CreateHit(
        Vector3 origin,
        Vector3 direction,
        Vector3 hitPoint,
        Vector3 hitNormal,
        ulong material,
        Collider* collider)
        => new(
            hitPoint,
            SceneRaycastMath.OrientSurfaceNormal(hitNormal, direction),
            material,
            (nint)collider,
            Vector3.Distance(origin, hitPoint),
            SceneSurfaceHitSource.Native);

    private delegate byte NativeRaycastDelegate(
        NativeRay* ray,
        float maxDistance,
        Vector3* hitPoint,
        Vector3* hitNormal,
        ulong* material,
        Collider** collider);

    private delegate byte NativeSweepSphereDelegate(
        SphereBounds* sphere,
        Vector3* direction,
        float maxDistance,
        Vector3* hitPoint,
        Vector3* hitNormal,
        ulong* material,
        Collider** collider);
}
