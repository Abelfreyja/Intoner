using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class ObjectNativeSurfaceRaycaster
{
    public static unsafe bool TryRaycastSurface(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out ObjectSurfaceHit hit,
        float maxDistance = 1000000f)
    {
        hit = ObjectSurfaceHit.Empty;
        if (!ObjectMathUtility.TryNormalize(rayDirection, out Vector3 normalizedDirection)
            || !BGCollisionModule.RaycastMaterialFilter(rayOrigin, normalizedDirection, out RaycastHit raycastHit, maxDistance))
        {
            return false;
        }

        hit = new ObjectSurfaceHit(
            raycastHit.Point,
            ObjectRaycastMath.ResolveSurfaceNormal(raycastHit, normalizedDirection),
            raycastHit.Material,
            (nint)raycastHit.Object,
            raycastHit.Distance,
            ObjectSurfaceHitSource.Native);
        return true;
    }
}
