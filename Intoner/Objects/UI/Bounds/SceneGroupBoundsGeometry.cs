using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI.Bounds;

internal static class SceneGroupBoundsGeometry
{
    public static SceneItemBoundsSnapshot? Create(SceneGroupSnapshot group, IReadOnlyList<SceneItemBoundsSnapshot> members)
    {
        if (members.Count == 0)
            return null;

        Quaternion rotation = SceneTransformMath.CreateRotationQuaternion(group.Transform.RotationDegrees);
        Quaternion inverse = Quaternion.Conjugate(rotation);
        Vector3 origin = members[0].Min;
        Vector3 worldMin = new(float.PositiveInfinity);
        Vector3 worldMax = new(float.NegativeInfinity);
        Vector3 localMin = worldMin;
        Vector3 localMax = worldMax;
        SceneBoundsCategory category = SceneBoundsCategory.None;
        Span<Vector3> corners = stackalloc Vector3[BoundsOverlayGeometry.BoxCornerCount];
        foreach (SceneItemBoundsSnapshot member in members)
        {
            category |= member.Category;
            BoundsOverlayGeometry.CopyBoxCorners(member, BoundsOverlaySpace.Local, corners);
            foreach (Vector3 corner in corners)
            {
                worldMin = Vector3.Min(worldMin, corner);
                worldMax = Vector3.Max(worldMax, corner);
                Vector3 local = Vector3.Transform(corner - origin, inverse);
                localMin = Vector3.Min(localMin, local);
                localMax = Vector3.Max(localMax, local);
            }
        }

        Vector3 center = Vector3.Transform((localMin + localMax) * 0.5f, rotation) + origin;
        SceneOrientedBounds oriented = new(Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(center),
            (localMax - localMin) * 0.5f);
        return new SceneItemBoundsSnapshot(group.Id, category, worldMin, worldMax, oriented, false);
    }
}
