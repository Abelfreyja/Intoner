using Intoner.Objects.Api;
using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class ObjectMakePlaceUtility
{
    public static bool TryConvertTransform(ObjectMakePlaceTransformDocument? transformDocument, float layoutScale, out ObjectTransform transform)
    {
        transform = new ObjectTransform();

        if (transformDocument?.Location is null
            || transformDocument.Rotation is null
            || transformDocument.Location.Count < 3
            || transformDocument.Rotation.Count < 4)
        {
            return false;
        }

        var safeScale = MathF.Abs(layoutScale) < 0.0001f ? 1f : layoutScale;
        var location = transformDocument.Location;
        var rotation = transformDocument.Rotation;
        if (!ObjectTransformMath.TryNormalizeQuaternion(new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]), out var quaternion))
        {
            return false;
        }

        var position = new Vector3(
            location[0] / safeScale,
            location[2] / safeScale,
            location[1] / safeScale);
        if (!ObjectMathUtility.IsFinite(position))
        {
            return false;
        }

        var yawDegrees = -ComputeZAngle(quaternion) * (180f / MathF.PI);
        transform = new ObjectTransform
        {
            Position = position,
            RotationDegrees = ObjectTransformMath.WrapRotationDegrees(new Vector3(0f, yawDegrees, 0f)),
            Scale = Vector3.One,
        };

        return true;
    }

    private static float ComputeZAngle(Quaternion rotation)
    {
        var sinyCosp = 2f * (rotation.W * rotation.Z + rotation.X * rotation.Y);
        var cosyCosp = 1f - (2f * (rotation.Y * rotation.Y + rotation.Z * rotation.Z));
        return MathF.Atan2(sinyCosp, cosyCosp);
    }
}

