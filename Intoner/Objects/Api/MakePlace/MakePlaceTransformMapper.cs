using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Api;

internal static class MakePlaceTransformMapper
{
    public static bool TryToObjectTransform(
        MakePlaceTransformDocument? document,
        float layoutScale,
        ObjectHousingPlotBasis? plotBasis,
        out ObjectTransform transform)
    {
        transform = new ObjectTransform();
        if (!TryReadMakePlaceLocalTransform(document, layoutScale, out Vector3 localPosition, out float localYawRadians))
        {
            return false;
        }

        transform = HousingPlotTransformMapper.ToWorldTransform(new HousingPlotLocalTransform(localPosition, localYawRadians), plotBasis);
        if (!ObjectMathUtility.IsFinite(transform.Position))
        {
            return false;
        }

        return true;
    }

    public static MakePlaceTransformDocument ToMakePlaceTransform(
        ObjectTransform transform,
        float layoutScale,
        ObjectHousingPlotBasis? plotBasis)
    {
        HousingPlotLocalTransform localTransform = HousingPlotTransformMapper.ToLocalTransform(transform, plotBasis);

        float safeScale = ResolveSafeScale(layoutScale);
        Quaternion rotation = Quaternion.CreateFromYawPitchRoll(0f, 0f, -localTransform.YawRadians);
        return new MakePlaceTransformDocument
        {
            Location =
            [
                CleanZero(localTransform.Position.X * safeScale),
                CleanZero(localTransform.Position.Z * safeScale),
                CleanZero(localTransform.Position.Y * safeScale),
            ],
            Rotation =
            [
                CleanZero(rotation.X),
                CleanZero(rotation.Y),
                CleanZero(rotation.Z),
                CleanZero(rotation.W),
            ],
            Scale = [1f, 1f, 1f],
        };
    }

    private static bool TryReadMakePlaceLocalTransform(
        MakePlaceTransformDocument? document,
        float layoutScale,
        out Vector3 localPosition,
        out float localYawRadians)
    {
        localPosition = default;
        localYawRadians = 0f;
        if (document?.Location is null
            || document.Rotation is null
            || document.Location.Count < 3
            || document.Rotation.Count < 4)
        {
            return false;
        }

        List<float> location = document.Location;
        List<float> rotation = document.Rotation;
        if (!ObjectTransformMath.TryNormalizeQuaternion(new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3]), out Quaternion quaternion))
        {
            return false;
        }

        float safeScale = ResolveSafeScale(layoutScale);
        localPosition = new Vector3(
            location[0] / safeScale,
            location[2] / safeScale,
            location[1] / safeScale);
        if (!ObjectMathUtility.IsFinite(localPosition))
        {
            return false;
        }

        localYawRadians = -ComputeZAngle(quaternion);
        return true;
    }

    private static float ResolveSafeScale(float layoutScale)
        => MathF.Abs(layoutScale) < 0.0001f ? 1f : layoutScale;

    private static float CleanZero(float value)
        => MathF.Abs(value) < 0.001f ? 0f : value;

    private static float ComputeZAngle(Quaternion rotation)
    {
        float sinyCosp = 2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y));
        float cosyCosp = 1f - (2f * ((rotation.Y * rotation.Y) + (rotation.Z * rotation.Z)));
        return MathF.Atan2(sinyCosp, cosyCosp);
    }
}
