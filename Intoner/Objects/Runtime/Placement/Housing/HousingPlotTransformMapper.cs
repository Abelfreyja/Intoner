using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Runtime;

internal static class HousingPlotTransformMapper
{
    private const float RadiansToDegrees = 180f / MathF.PI;
    private const float DegreesToRadians = MathF.PI / 180f;

    public static ObjectTransform ToWorldTransform(HousingPlotLocalTransform localTransform, ObjectHousingPlotBasis? plotBasis)
    {
        Vector3 worldPosition = localTransform.Position;
        float worldYawRadians = localTransform.YawRadians;
        if (plotBasis is { } basis)
        {
            worldPosition = Vector3.Transform(localTransform.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -basis.RotationRadians)) + basis.Origin;
            worldYawRadians -= basis.RotationRadians;
        }

        return new ObjectTransform
        {
            Position = worldPosition,
            RotationDegrees = ObjectTransformMath.WrapRotationDegrees(new Vector3(0f, worldYawRadians * RadiansToDegrees, 0f)),
            Scale = Vector3.One,
        };
    }

    public static HousingPlotLocalTransform ToLocalTransform(ObjectTransform transform, ObjectHousingPlotBasis? plotBasis)
    {
        Vector3 localPosition = transform.Position;
        float localYawRadians = transform.RotationDegrees.Y * DegreesToRadians;
        if (plotBasis is { } basis)
        {
            localPosition = Vector3.Transform(transform.Position - basis.Origin, Quaternion.CreateFromAxisAngle(Vector3.UnitY, basis.RotationRadians));
            localYawRadians += basis.RotationRadians;
        }

        return new HousingPlotLocalTransform(localPosition, localYawRadians);
    }
}
