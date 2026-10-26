using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Scene;

internal static class SceneTransformMath
{
    public const float MinimumScale = 0.01f;

    private const float GimbalToleranceDegrees = 0.05f;

    /// <summary> scales an item and its distance from a shared pivot without changing its orientation </summary>
    public static SceneTransform ApplyUniformScale(SceneTransform transform, Vector3 pivot, float factor, bool scaleSize = true,
        Vector3 translation = default)
    {
        if (factor.Equals(1f) && translation == Vector3.Zero)
            return transform;

        return transform with
        {
            Position = pivot + (transform.Position - pivot) * factor + translation,
            Scale = scaleSize ? transform.Scale * factor : transform.Scale,
        };
    }

    /// <summary> gets the smallest common factor that preserves the minimum size on every axis </summary>
    public static float MinimumUniformScaleFactor(Vector3 scale)
        => MinimumScale / MathF.Min(scale.X, MathF.Min(scale.Y, scale.Z));

    public static Vector3 WithAxisScale(Vector3 startScale, int axis, float value, bool uniform)
    {
        if (uniform)
        {
            return startScale * MathF.Max(MinimumUniformScaleFactor(startScale), value / startScale[axis]);
        }

        startScale[axis] = MathF.Max(MinimumScale, value);
        return startScale;
    }

    public static Quaternion ResolveRotationDelta(Quaternion start, Quaternion target)
        => NormalizeQuaternion(NormalizeQuaternion(target) * Quaternion.Inverse(NormalizeQuaternion(start)));

    public static SceneTransform ApplyRigidRotation(SceneTransform transform, Vector3 pivot, Quaternion delta,
        Vector3 referenceRotationDegrees)
        => ApplyRigidRotation(transform, pivot, transform.Position - pivot,
            CreateRotationQuaternion(transform.RotationDegrees), delta, referenceRotationDegrees);

    /// <summary> rotates a captured offset and orientation around the current selection pivot </summary>
    public static SceneTransform ApplyRigidRotation(SceneTransform transform, Vector3 pivot, Vector3 pivotOffset,
        Quaternion startRotation, Quaternion delta, Vector3 referenceRotationDegrees)
    {
        Quaternion normalized = NormalizeQuaternion(delta);
        return transform with
        {
            Position = pivot + Vector3.Transform(pivotOffset, normalized),
            RotationDegrees = ToRotationDegrees(normalized * startRotation, referenceRotationDegrees),
        };
    }

    public static Matrix4x4 CreateWorldTransform(SceneTransform transform)
        => CreateWorldTransform(
            transform.Position,
            CreateRotationQuaternion(transform.RotationDegrees),
            transform.Scale);

    public static Matrix4x4 CreateWorldTransform(
        Vector3 translation,
        Quaternion rotation,
        Vector3 scale)
    {
        Matrix4x4 worldTransform =
            Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(NormalizeQuaternion(rotation));
        worldTransform.Translation = translation;
        return worldTransform;
    }

    public static Quaternion CreateRotationQuaternion(Vector3 rotationDegrees)
    {
        var radians = rotationDegrees * (MathF.PI / 180f);
        return NormalizeQuaternion(Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z));
    }

    public static Quaternion NormalizeQuaternion(Quaternion rotation)
        => NumericsUtility.TryNormalize(rotation, out var normalizedRotation)
            ? normalizedRotation
            : Quaternion.Identity;

    public static Quaternion AlignLocalAxisToDirection(Quaternion rotation, Vector3 localAxis, Vector3 direction)
    {
        if (!NumericsUtility.HasLength(rotation) || !NumericsUtility.HasLength(localAxis) || !NumericsUtility.HasLength(direction))
        {
            return rotation;
        }

        rotation = NormalizeQuaternion(rotation);
        var currentAxis = Vector3.Transform(localAxis, rotation);
        if (!NumericsUtility.TryNormalize(direction, out var normalizedDirection)
            || !NumericsUtility.TryNormalize(currentAxis, out currentAxis))
        {
            return rotation;
        }

        var delta = CreateShortestArcQuaternion(currentAxis, normalizedDirection);
        return NormalizeQuaternion(delta * rotation);
    }

    public static Vector3 ToRotationDegrees(Quaternion rotation)
    {
        rotation = NormalizeQuaternion(rotation);

        var yaw = MathF.Atan2(
            2f * ((rotation.W * rotation.Y) + (rotation.Z * rotation.X)),
            1f - (2f * ((rotation.X * rotation.X) + (rotation.Y * rotation.Y))));

        var pitchSin = 2f * ((rotation.W * rotation.X) - (rotation.Y * rotation.Z));
        pitchSin = Math.Clamp(pitchSin, -1f, 1f);
        var pitch = MathF.Asin(pitchSin);

        var roll = MathF.Atan2(
            2f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1f - (2f * ((rotation.X * rotation.X) + (rotation.Z * rotation.Z))));

        return WrapRotationDegrees(new Vector3(
            pitch * (180f / MathF.PI),
            yaw * (180f / MathF.PI),
            roll * (180f / MathF.PI)));
    }

    public static Vector3 ToRotationDegrees(Quaternion rotation, Vector3 referenceDegrees)
    {
        rotation = NormalizeQuaternion(rotation);
        var wrappedReference = WrapRotationDegrees(referenceDegrees);
        if (TryResolvePreferredGimbalRotation(rotation, wrappedReference, out var resolvedGimbalRotation))
        {
            return resolvedGimbalRotation;
        }

        var primaryRotation = ToRotationDegrees(rotation);
        var alternateRotation = WrapRotationDegrees(new Vector3(
            180f - primaryRotation.X,
            primaryRotation.Y + 180f,
            primaryRotation.Z + 180f));

        var preferredPrimary = ResolvePreferredGimbalRotation(primaryRotation, wrappedReference);
        var preferredAlternate = ResolvePreferredGimbalRotation(alternateRotation, wrappedReference);
        return ResolveWrappedRotationDistanceSquared(preferredPrimary, wrappedReference) <= ResolveWrappedRotationDistanceSquared(preferredAlternate, wrappedReference)
            ? preferredPrimary
            : preferredAlternate;
    }

    public static Vector3 WrapRotationDegrees(Vector3 rotationDegrees)
        => new(
            WrapSignedDegrees180(rotationDegrees.X),
            WrapSignedDegrees180(rotationDegrees.Y),
            WrapSignedDegrees180(rotationDegrees.Z));

    public static float WrapSignedDegrees180(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }

        value %= 360f;
        if (value > 180f)
        {
            value -= 360f;
        }
        else if (value < -180f)
        {
            value += 360f;
        }

        return value;
    }

    private static float ResolveWrappedRotationDistanceSquared(Vector3 rotationDegrees, Vector3 referenceDegrees)
    {
        var delta = new Vector3(
            WrapSignedDegrees180(rotationDegrees.X - referenceDegrees.X),
            WrapSignedDegrees180(rotationDegrees.Y - referenceDegrees.Y),
            WrapSignedDegrees180(rotationDegrees.Z - referenceDegrees.Z));
        return delta.LengthSquared();
    }

    private static Vector3 ResolvePreferredGimbalRotation(Vector3 rotationDegrees, Vector3 referenceDegrees)
    {
        var wrappedRotation = WrapRotationDegrees(rotationDegrees);
        if (MathF.Abs(MathF.Abs(wrappedRotation.X) - 90f) > GimbalToleranceDegrees)
        {
            return wrappedRotation;
        }

        var snappedPitch = ResolveSnappedGimbalPitchDegrees(wrappedRotation.X);
        var preserveRoll = wrappedRotation;
        var preserveYaw = wrappedRotation;
        preserveRoll.X = snappedPitch;
        preserveYaw.X = snappedPitch;
        if (wrappedRotation.X >= 0f)
        {
            var yawMinusRoll = WrapSignedDegrees180(wrappedRotation.Y - wrappedRotation.Z);
            preserveRoll.Z = WrapSignedDegrees180(referenceDegrees.Z);
            preserveRoll.Y = WrapSignedDegrees180(yawMinusRoll + preserveRoll.Z);
            preserveYaw.Y = WrapSignedDegrees180(referenceDegrees.Y);
            preserveYaw.Z = WrapSignedDegrees180(preserveYaw.Y - yawMinusRoll);
        }
        else
        {
            var yawPlusRoll = WrapSignedDegrees180(wrappedRotation.Y + wrappedRotation.Z);
            preserveRoll.Z = WrapSignedDegrees180(referenceDegrees.Z);
            preserveRoll.Y = WrapSignedDegrees180(yawPlusRoll - preserveRoll.Z);
            preserveYaw.Y = WrapSignedDegrees180(referenceDegrees.Y);
            preserveYaw.Z = WrapSignedDegrees180(yawPlusRoll - preserveYaw.Y);
        }

        return ResolveWrappedRotationDistanceSquared(preserveRoll, referenceDegrees) <= ResolveWrappedRotationDistanceSquared(preserveYaw, referenceDegrees)
            ? preserveRoll
            : preserveYaw;
    }

    private static bool TryResolvePreferredGimbalRotation(Quaternion rotation, Vector3 referenceDegrees, out Vector3 resolvedRotation)
    {
        resolvedRotation = default;

        var primaryRotation = ToRotationDegrees(rotation);
        if (MathF.Abs(MathF.Abs(primaryRotation.X) - 90f) > GimbalToleranceDegrees)
        {
            return false;
        }

        var invariantDegrees = primaryRotation.X >= 0f
            ? ResolvePositivePitchGimbalInvariantDegrees(rotation)
            : ResolveNegativePitchGimbalInvariantDegrees(rotation);
        var snappedPitch = ResolveSnappedGimbalPitchDegrees(primaryRotation.X);
        var preserveRoll = primaryRotation;
        var preserveYaw = primaryRotation;
        preserveRoll.X = snappedPitch;
        preserveYaw.X = snappedPitch;
        if (primaryRotation.X >= 0f)
        {
            preserveRoll.Z = WrapSignedDegrees180(referenceDegrees.Z);
            preserveRoll.Y = WrapSignedDegrees180(invariantDegrees + preserveRoll.Z);
            preserveYaw.Y = WrapSignedDegrees180(referenceDegrees.Y);
            preserveYaw.Z = WrapSignedDegrees180(preserveYaw.Y - invariantDegrees);
        }
        else
        {
            preserveRoll.Z = WrapSignedDegrees180(referenceDegrees.Z);
            preserveRoll.Y = WrapSignedDegrees180(invariantDegrees - preserveRoll.Z);
            preserveYaw.Y = WrapSignedDegrees180(referenceDegrees.Y);
            preserveYaw.Z = WrapSignedDegrees180(invariantDegrees - preserveYaw.Y);
        }

        resolvedRotation = ResolveWrappedRotationDistanceSquared(preserveRoll, referenceDegrees) <= ResolveWrappedRotationDistanceSquared(preserveYaw, referenceDegrees)
            ? preserveRoll
            : preserveYaw;
        return true;
    }

    private static float ResolveSnappedGimbalPitchDegrees(float pitchDegrees)
        => pitchDegrees >= 0f ? 90f : -90f;

    private static float ResolvePositivePitchGimbalInvariantDegrees(Quaternion rotation)
    {
        var angle = MathF.Atan2(rotation.Y - rotation.Z, rotation.W + rotation.X);
        return WrapSignedDegrees180(angle * (360f / MathF.PI));
    }

    private static float ResolveNegativePitchGimbalInvariantDegrees(Quaternion rotation)
    {
        var angle = MathF.Atan2(rotation.Y + rotation.Z, rotation.W - rotation.X);
        return WrapSignedDegrees180(angle * (360f / MathF.PI));
    }

    private static Quaternion CreateShortestArcQuaternion(Vector3 from, Vector3 to)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot >= 0.9999f)
        {
            return Quaternion.Identity;
        }

        if (dot <= -0.9999f)
        {
            var axis = Vector3.Cross(from, Vector3.UnitX);
            if (!NumericsUtility.HasLength(axis))
            {
                axis = Vector3.Cross(from, Vector3.UnitZ);
            }

            axis = !NumericsUtility.TryNormalize(axis, out var normalizedAxis)
                ? Vector3.UnitY
                : normalizedAxis;
            return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
        }

        var cross = Vector3.Cross(from, to);
        var quaternion = new Quaternion(cross, 1f + dot);
        return NormalizeQuaternion(quaternion);
    }
}
