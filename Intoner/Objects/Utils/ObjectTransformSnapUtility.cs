using System;
using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class ObjectTransformSnapUtility
{
    public static Vector3 SnapPosition(Vector3 position, float step, in ObjectSnapBasis basis)
        => SnapBasisPosition(position, step, basis, snapX: true, snapY: true, snapZ: true);

    public static Vector3 SnapPositionAxis(Vector3 position, int axisIndex, float step, in ObjectSnapBasis basis)
    {
        if ((uint)axisIndex > 2)
        {
            return position;
        }

        return axisIndex switch
        {
            0 => SnapBasisPosition(position, step, basis, snapX: true, snapY: false, snapZ: false),
            1 => SnapBasisPosition(position, step, basis, snapX: false, snapY: true, snapZ: false),
            2 => SnapBasisPosition(position, step, basis, snapX: false, snapY: false, snapZ: true),
            _ => position,
        };
    }

    public static Vector3 SnapPositionAxes(Vector3 position, bool snapX, bool snapY, bool snapZ, float step, in ObjectSnapBasis basis)
        => !snapX && !snapY && !snapZ
            ? position
            : SnapBasisPosition(position, step, basis, snapX, snapY, snapZ);

    public static float SnapAngleDegrees(float degrees, float stepDegrees)
        => SnapValue(degrees, stepDegrees);

    public static Vector3 SnapRotationDegrees(Vector3 rotationDegrees, float stepDegrees)
        => SnapVector(rotationDegrees, stepDegrees);

    public static Vector3 SnapScale(Vector3 scale, float step, float minScale = 0.01f)
        => new(
            MathF.Max(minScale, SnapValue(scale.X, step)),
            MathF.Max(minScale, SnapValue(scale.Y, step)),
            MathF.Max(minScale, SnapValue(scale.Z, step)));

    public static Vector3 SnapVector(Vector3 value, float step)
        => new(
            SnapValue(value.X, step),
            SnapValue(value.Y, step),
            SnapValue(value.Z, step));

    public static float SnapValue(float value, float step)
    {
        if (step <= float.Epsilon)
        {
            return value;
        }

        return MathF.Round(value / step, MidpointRounding.AwayFromZero) * step;
    }

    private static Vector3 SnapBasisPosition(Vector3 position, float step, in ObjectSnapBasis basis, bool snapX, bool snapY, bool snapZ)
    {
        var localPosition = ToBasisLocalPosition(position, basis, out var rotation);
        var snappedLocalPosition = new Vector3(
            snapX ? SnapValue(localPosition.X, step) : localPosition.X,
            snapY ? SnapValue(localPosition.Y, step) : localPosition.Y,
            snapZ ? SnapValue(localPosition.Z, step) : localPosition.Z);
        return FromBasisLocalPosition(snappedLocalPosition, basis.Origin, rotation);
    }

    private static Vector3 ToBasisLocalPosition(Vector3 position, in ObjectSnapBasis basis, out Quaternion rotation)
    {
        rotation = ObjectTransformMath.NormalizeQuaternion(basis.Rotation);
        if (rotation == Quaternion.Identity)
        {
            return position - basis.Origin;
        }

        var inverseRotation = Quaternion.Inverse(rotation);
        return Vector3.Transform(position - basis.Origin, inverseRotation);
    }

    private static Vector3 FromBasisLocalPosition(Vector3 localPosition, Vector3 origin, Quaternion rotation)
        => rotation == Quaternion.Identity
            ? localPosition + origin
            : origin + Vector3.Transform(localPosition, rotation);
}

