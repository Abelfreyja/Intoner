using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class ObjectMathUtility
{
    public const float DirectionEpsilonSquared = 0.0001f;
    public const float ScalarEpsilon = 0.00001f;
    public const float ChangeEpsilonSquared = 0.000001f;

    public static bool HasLength(float value)
        => MathF.Abs(value) > ScalarEpsilon;

    public static bool HasLength(Vector2 value)
        => value.LengthSquared() > DirectionEpsilonSquared;

    public static bool HasLength(Vector3 value)
        => value.LengthSquared() > DirectionEpsilonSquared;

    public static bool HasLength(Quaternion value)
        => value.LengthSquared() > DirectionEpsilonSquared;

    public static bool HasMeaningfulChange(Vector3 nextValue, Vector3 lastValue)
        => Vector3.DistanceSquared(nextValue, lastValue) >= ChangeEpsilonSquared;

    public static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y)
           && float.IsFinite(value.Z);

    public static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y);

    public static bool IsFinite(Vector4 value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y)
           && float.IsFinite(value.Z)
           && float.IsFinite(value.W);

    public static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X)
           && float.IsFinite(value.Y)
           && float.IsFinite(value.Z)
           && float.IsFinite(value.W);

    public static bool IsNearlyEqual(float left, float right, float epsilon = ScalarEpsilon)
        => MathF.Abs(left - right) <= epsilon;

    public static bool IsNearlyEqual(Vector3 left, Vector3 right, float epsilon = ScalarEpsilon)
        => IsNearlyEqual(left.X, right.X, epsilon)
           && IsNearlyEqual(left.Y, right.Y, epsilon)
           && IsNearlyEqual(left.Z, right.Z, epsilon);

    public static bool IsNearlyEqual(Vector4 left, Vector4 right, float epsilon = ScalarEpsilon)
        => IsNearlyEqual(left.X, right.X, epsilon)
           && IsNearlyEqual(left.Y, right.Y, epsilon)
           && IsNearlyEqual(left.Z, right.Z, epsilon)
           && IsNearlyEqual(left.W, right.W, epsilon);

    public static bool IsNearlyZero(float value)
        => MathF.Abs(value) <= ScalarEpsilon;

    public static bool IsNearlyZero(float value, float epsilon)
        => MathF.Abs(value) <= epsilon;

    public static Vector3 Abs(Vector3 value)
        => new(MathF.Abs(value.X), MathF.Abs(value.Y), MathF.Abs(value.Z));

    public static bool TryNormalize(Vector2 value, out Vector2 normalizedValue)
    {
        normalizedValue = default;
        if (!HasLength(value))
        {
            return false;
        }

        normalizedValue = Vector2.Normalize(value);
        return true;
    }

    public static bool TryNormalize(Vector3 value, out Vector3 normalizedValue)
    {
        normalizedValue = default;
        if (!HasLength(value))
        {
            return false;
        }

        normalizedValue = Vector3.Normalize(value);
        return true;
    }
}

