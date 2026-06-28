using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI.Bounds;

internal static class BoundsOverlayGeometry
{
    public const int BoxCornerCount = 8;

    public static void CopyBoxCorners(ObjectBoundsSnapshot boundsSnapshot, BoundsOverlaySpace overlaySpace, Span<Vector3> corners)
    {
        if (corners.Length < BoxCornerCount)
        {
            throw new ArgumentException("bounds corner buffer is too small", nameof(corners));
        }

        if (overlaySpace == BoundsOverlaySpace.Local && boundsSnapshot.LocalBounds.HasValue)
        {
            ObjectShapeMath.CopyOrientedBoxCorners(boundsSnapshot.LocalBounds.Value, corners);
            return;
        }

        ObjectShapeMath.CopyAxisAlignedBoxCorners(boundsSnapshot.Min, boundsSnapshot.Max, corners);
    }
}

