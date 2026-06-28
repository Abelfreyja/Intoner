using System.Numerics;

namespace Intoner.Objects.Utils;

internal static class GizmoRotationMath
{
    private const float MinimumWorldRadius = 0.001f;

    internal readonly record struct Projection(
        Vector2 Center,
        float ScreenRadius,
        float WorldRadius,
        Vector3 WorldPosition,
        Matrix4x4 ViewProjection,
        Vector2 ViewportPos,
        Vector2 ViewportSize,
        Vector3? CameraViewDirection,
        Vector3? CameraRight,
        Vector3? CameraUp);

    /// <summary> projects one rotation ring point to screen space </summary>
    /// <param name="projection">the active projection input</param>
    /// <param name="axisDirection">the world space rotation axis direction</param>
    /// <param name="angle">the ring angle in radians</param>
    /// <returns>the projected screen point, or the projection center when projection fails</returns>
    public static Vector2 ProjectAxisPoint(in Projection projection, Vector3 axisDirection, float angle)
    {
        return TryProjectAxisPoint(projection, axisDirection, angle, out var screenPoint, out _)
            ? screenPoint
            : projection.Center;
    }

    /// <summary> projects one rotation ring point to screen space and reports near side visibility </summary>
    /// <param name="projection">the active projection input</param>
    /// <param name="axisDirection">the world space rotation axis direction</param>
    /// <param name="angle">the ring angle in radians</param>
    /// <param name="screenPoint">the projected screen point</param>
    /// <param name="isVisible">whether the point is on the camera facing side of the ring</param>
    /// <returns>true when the point projected successfully</returns>
    public static bool TryProjectAxisPoint(
        in Projection projection,
        Vector3 axisDirection,
        float angle,
        out Vector2 screenPoint,
        out bool isVisible)
    {
        screenPoint = projection.Center;
        isVisible = false;
        if (!ObjectMathUtility.HasLength(axisDirection)
            || projection.WorldRadius <= float.Epsilon
            || projection.ScreenRadius <= float.Epsilon)
        {
            return false;
        }

        if (!TryBuildRotationPlaneBasis(projection, axisDirection, out var planeX, out var planeY))
        {
            return false;
        }

        var worldPoint = projection.WorldPosition
                         + ((planeX * MathF.Cos(angle)) + (planeY * MathF.Sin(angle))) * projection.WorldRadius;
        if (!ObjectViewportProjectionUtility.TryProjectWorldPointToViewport(
                projection.ViewProjection,
                worldPoint,
                projection.ViewportPos,
                projection.ViewportSize,
                out screenPoint))
        {
            return false;
        }

        isVisible = IsNearCameraSide(projection, worldPoint);
        return true;
    }

    /// <summary> finds the closest ring angle under the cursor for one axis </summary>
    /// <param name="projection">the active projection input</param>
    /// <param name="axisDirection">the world space rotation axis direction</param>
    /// <param name="mousePos">the current mouse position</param>
    /// <param name="referenceAngle">the optional reference angle for continuity</param>
    /// <param name="segmentCount">the ring segment count to sample</param>
    /// <param name="angle">the resolved angle in radians</param>
    /// <returns>true when an angle was resolved</returns>
    public static bool TryFindClosestRingAngle(
        in Projection projection,
        Vector3 axisDirection,
        Vector2 mousePos,
        float? referenceAngle,
        int segmentCount,
        out float angle)
    {
        angle = 0f;
        if (!ObjectMathUtility.HasLength(axisDirection))
        {
            return false;
        }

        if (referenceAngle.HasValue)
        {
            ReadOnlySpan<float> angleWindows = stackalloc float[]
            {
                MathF.PI / 8f,
                MathF.PI / 4f,
                MathF.PI / 2f,
                MathF.PI,
                0f,
            };

            foreach (var window in angleWindows)
            {
                if (TryFindClosestRingAngle(projection, axisDirection, mousePos, referenceAngle, window, segmentCount, out angle))
                {
                    return true;
                }
            }
        }

        return TryFindClosestRingAngle(projection, axisDirection, mousePos, null, 0f, segmentCount, out angle);
    }

    /// <summary> normalizes an angle into the [0, 2pi) range </summary>
    /// <param name="angle">the input angle in radians</param>
    /// <returns>the normalized angle</returns>
    public static float NormalizeAngle(float angle)
    {
        var normalized = angle % MathF.Tau;
        if (normalized < 0f)
        {
            normalized += MathF.Tau;
        }

        return normalized;
    }

    /// <summary> resolves the signed shortest angle delta from one angle to another </summary>
    /// <param name="fromAngle">the starting angle</param>
    /// <param name="toAngle">the destination angle</param>
    /// <returns>the signed shortest delta in radians</returns>
    public static float SignedAngleDelta(float fromAngle, float toAngle)
    {
        var delta = NormalizeAngle(toAngle - fromAngle);
        if (delta > MathF.PI)
        {
            delta -= MathF.Tau;
        }

        return delta;
    }

    /// <summary> resolves the world radius that best matches the current screen radius </summary>
    /// <param name="projection">the active projection input</param>
    /// <param name="referenceWorldRadius">the fallback world radius to keep when projection samples fail</param>
    /// <returns>the resolved world radius</returns>
    public static float ResolveWorldRadius(in Projection projection, float referenceWorldRadius)
    {
        float? resolvedRadius = null;

        if (TryResolveWorldRadius(projection, projection.CameraRight, referenceWorldRadius, out var rightRadius))
        {
            resolvedRadius = rightRadius;
        }

        if (TryResolveWorldRadius(projection, projection.CameraUp, referenceWorldRadius, out var upRadius))
        {
            resolvedRadius = resolvedRadius.HasValue
                ? (resolvedRadius.Value + upRadius) * 0.5f
                : upRadius;
        }

        return resolvedRadius.HasValue && float.IsFinite(resolvedRadius.Value) && resolvedRadius.Value > 0f
            ? MathF.Max(resolvedRadius.Value, MinimumWorldRadius)
            : referenceWorldRadius;
    }

    /// <summary> checks whether a point lies inside a ring segment </summary>
    /// <param name="point">the point to test</param>
    /// <param name="center">the ring center</param>
    /// <param name="innerRadius">the inner ring radius</param>
    /// <param name="outerRadius">the outer ring radius</param>
    /// <param name="startAngle">the start angle in radians</param>
    /// <param name="endAngle">the end angle in radians</param>
    /// <returns>true when the point is inside the ring segment</returns>
    public static bool IsPointInRingSegment(Vector2 point, Vector2 center, float innerRadius, float outerRadius, float startAngle, float endAngle)
    {
        var diff = point - center;
        var distanceSquared = diff.LengthSquared();
        var innerSquared = innerRadius * innerRadius;
        var outerSquared = outerRadius * outerRadius;
        if (distanceSquared < innerSquared || distanceSquared > outerSquared)
        {
            return false;
        }

        var angle = MathF.Atan2(diff.Y, diff.X);
        var normalizedAngle = NormalizeAngle(angle);
        var start = NormalizeAngle(startAngle);
        var end = NormalizeAngle(endAngle);
        return start <= end
            ? normalizedAngle >= start && normalizedAngle <= end
            : normalizedAngle >= start || normalizedAngle <= end;
    }

    private static bool TryFindClosestRingAngle(
        in Projection projection,
        Vector3 axisDirection,
        Vector2 mousePos,
        float? referenceAngle,
        float angleWindow,
        int segmentCount,
        out float angle)
    {
        angle = 0f;
        var found = false;
        var bestScore = float.MaxValue;
        var continuityWeight = MathF.Max(projection.ScreenRadius * projection.ScreenRadius * 0.2f, 1f);

        for (var index = 0; index < segmentCount; ++index)
        {
            var angle0 = ((float)index / segmentCount) * MathF.Tau;
            var angle1 = ((float)(index + 1) / segmentCount) * MathF.Tau;
            var midAngle = NormalizeAngle((angle0 + angle1) * 0.5f);
            if (referenceAngle.HasValue && angleWindow > 0f && MathF.Abs(SignedAngleDelta(referenceAngle.Value, midAngle)) > angleWindow)
            {
                continue;
            }

            if (!TryProjectAxisPoint(projection, axisDirection, angle0, out var point0, out _)
                || !TryProjectAxisPoint(projection, axisDirection, angle1, out var point1, out _))
            {
                continue;
            }

            var segment = point1 - point0;
            var lengthSq = segment.LengthSquared();
            if (lengthSq <= float.Epsilon)
            {
                continue;
            }

            var t = Vector2.Dot(mousePos - point0, segment) / lengthSq;
            t = Math.Clamp(t, 0f, 1f);
            var projected = point0 + (segment * t);
            var distanceSq = (mousePos - projected).LengthSquared();
            var candidateAngle = NormalizeAngle(angle0 + ((angle1 - angle0) * t));
            var score = distanceSq;
            if (referenceAngle.HasValue)
            {
                var delta = SignedAngleDelta(referenceAngle.Value, candidateAngle);
                score += delta * delta * continuityWeight;
            }

            if (score < bestScore)
            {
                bestScore = score;
                angle = candidateAngle;
                found = true;
            }
        }

        return found;
    }

    private static bool TryBuildRotationPlaneBasis(
        in Projection projection,
        Vector3 axisDirection,
        out Vector3 planeX,
        out Vector3 planeY)
    {
        planeX = Vector3.Zero;
        planeY = Vector3.Zero;

        if (!ObjectMathUtility.HasLength(axisDirection))
        {
            return false;
        }

        var tangent = Vector3.Zero;
        if (projection.CameraViewDirection.HasValue)
        {
            tangent = Vector3.Cross(axisDirection, projection.CameraViewDirection.Value);
        }

        if (!ObjectMathUtility.HasLength(tangent) && projection.CameraRight.HasValue)
        {
            tangent = Vector3.Cross(axisDirection, projection.CameraRight.Value);
        }

        if (!ObjectMathUtility.HasLength(tangent) && projection.CameraUp.HasValue)
        {
            tangent = Vector3.Cross(axisDirection, projection.CameraUp.Value);
        }

        if (!ObjectMathUtility.HasLength(tangent))
        {
            tangent = Vector3.Cross(axisDirection, MathF.Abs(axisDirection.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX);
        }

        if (!ObjectMathUtility.TryNormalize(tangent, out planeX))
        {
            return false;
        }

        planeY = Vector3.Cross(axisDirection, planeX);
        if (!ObjectMathUtility.TryNormalize(planeY, out planeY))
        {
            return false;
        }

        return true;
    }

    private static bool IsNearCameraSide(in Projection projection, Vector3 worldPoint)
    {
        if (!projection.CameraViewDirection.HasValue
            || !ObjectMathUtility.TryNormalize(projection.CameraViewDirection.Value, out var cameraDirection))
        {
            return true;
        }

        var offset = worldPoint - projection.WorldPosition;
        return !ObjectMathUtility.HasLength(offset) || Vector3.Dot(offset, cameraDirection) >= -0.0001f;
    }

    private static bool TryResolveWorldRadius(
        in Projection projection,
        Vector3? worldDirection,
        float referenceWorldRadius,
        out float worldRadius)
    {
        worldRadius = 0f;
        if (!worldDirection.HasValue || !ObjectMathUtility.TryNormalize(worldDirection.Value, out var normalizedWorldDirection))
        {
            return false;
        }

        var samplePoint = projection.WorldPosition + (normalizedWorldDirection * referenceWorldRadius);
        if (!ObjectViewportProjectionUtility.TryProjectWorldPointToViewport(
                projection.ViewProjection,
                samplePoint,
                projection.ViewportPos,
                projection.ViewportSize,
                out var projectedScreenPoint))
        {
            return false;
        }

        var sampleScreenRadius = (projectedScreenPoint - projection.Center).Length();
        if (ObjectMathUtility.IsNearlyZero(sampleScreenRadius))
        {
            return false;
        }

        worldRadius = referenceWorldRadius * (projection.ScreenRadius / sampleScreenRadius);
        return true;
    }
}

