using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class ObjectNativeSurfaceRaycaster
{
    public static unsafe bool TryRaycastSurface(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out SceneSurfaceHit hit,
        float maxDistance = 1000000f)
    {
        hit = SceneSurfaceHit.Empty;
        if (!NumericsUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection)
            || !BGCollisionModule.RaycastMaterialFilter(rayOrigin, normalizedDirection, out RaycastHit raycastHit, maxDistance))
        {
            return false;
        }

        hit = new SceneSurfaceHit(
            raycastHit.Point,
            SceneRaycastMath.ResolveSurfaceNormal(
                raycastHit.V1,
                raycastHit.V2,
                raycastHit.V3,
                raycastHit.Normal,
                normalizedDirection),
            raycastHit.Material,
            (nint)raycastHit.Object,
            raycastHit.Distance,
            SceneSurfaceHitSource.Native);
        return true;
    }
}
