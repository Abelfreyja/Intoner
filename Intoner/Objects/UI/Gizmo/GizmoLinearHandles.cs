using Intoner.Objects.Models;
using Intoner.Objects.Rendering;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

/// <summary> screen geometry, drawing, and picking for move and scale handles </summary>
internal static class GizmoLinearHandles
{
    public static float ResolveCenterInteractionRadius(float scale)
        => GizmoConstants.CenterInteractionRadius * scale;

    public static void FindLinearHandle(
        ReadOnlySpan<GizmoAxisVisualState> axes,
        GizmoTransformMode operation,
        GizmoTransformMode modes,
        ReadOnlySpan<GizmoAxisVisualState> translationAxes,
        Vector2 mousePos,
        float scale,
        ref GizmoHandleHit hit)
    {
        foreach (GizmoAxisVisualState axis in axes)
        {
            bool endpoint = operation == GizmoTransformMode.Scale
                ? TryGetScaleHandleHitDistance(mousePos, axis.ScreenEnd, axis.VisualScale, out float distance)
                : TryGetTranslationArrowHitDistance(mousePos, axis, out distance);
            Vector2 start = operation == GizmoTransformMode.Scale && modes.IsCombined()
                ? axis.ScreenStart + axis.ScreenDirection * GetCombinedScaleStemStart(axis, translationAxes, scale)
                : GetLinearGizmoInteractionStart(axis, scale);
            Vector2 end = GetLinearGizmoInteractionEnd(axis, operation);

            if (!endpoint && (Vector2.Dot(end - start, axis.ScreenDirection) <= 0f
                    || !TryGetSegmentHitDistance(mousePos, start, end,
                        ResolveLinearGizmoAxisLineHitRadius(operation, axis.VisualScale), out distance)))
            {
                continue;
            }

            GizmoHandleHit candidate = new(operation, axis, default, distance, endpoint);
            if (candidate.IsCloserThan(hit))
            {
                hit = candidate;
            }
        }
    }

    public static float GetCombinedScaleStemStart(GizmoAxisVisualState axis, ReadOnlySpan<GizmoAxisVisualState> translationAxes, float scale)
    {
        float gap = 5f * axis.VisualScale;
        float start = ResolveCenterInteractionRadius(scale) + gap;

        if (TryGetTranslationOverlap(axis, translationAxes, 0f, float.PositiveInfinity, out float overlap))
        {
            start = MathF.Max(start, overlap + gap);
        }

        return start;
    }

    public static bool TryGetTranslationOverlap(GizmoAxisVisualState axis, ReadOnlySpan<GizmoAxisVisualState> translationAxes,
        float start, float stop, out float overlapEnd)
    {
        overlapEnd = 0f;
        float stemHalfWidth = 0.5f * axis.VisualScale;
        Span<Vector2> polygon = stackalloc Vector2[4];

        foreach (GizmoAxisVisualState move in translationAxes)
        {
            if (!TryGetTranslationArrowVertices(move, out Vector2 tip, out Vector2 left, out Vector2 right))
            {
                continue;
            }

            polygon[0] = tip;
            polygon[1] = left;
            polygon[2] = right;
            float overlap = GetRayPolygonOverlapEnd(axis.ScreenStart, axis.ScreenDirection, polygon[..3],
                stemHalfWidth + 0.55f * move.VisualScale, start, stop);
            Vector2 normal = new(-move.ScreenDirection.Y, move.ScreenDirection.X);
            normal *= GizmoConstants.AxisLineThickness * GizmoConstants.AxisGlowThicknessMultiplier * move.VisualScale * 0.5f;
            Vector2 end = GetTrimmedGizmoEndpoint(move);
            polygon[0] = move.ScreenStart + normal;
            polygon[1] = move.ScreenStart - normal;
            polygon[2] = end - normal;
            polygon[3] = end + normal;
            overlap = MathF.Max(overlap, GetRayPolygonOverlapEnd(axis.ScreenStart, axis.ScreenDirection, polygon, stemHalfWidth, start, stop));
            overlapEnd = MathF.Max(overlapEnd, overlap);
        }

        return overlapEnd > 0f;
    }

    private static float GetRayPolygonOverlapEnd(Vector2 origin, Vector2 direction, ReadOnlySpan<Vector2> polygon, float padding,
        float enter, float exit)
    {
        for (int index = 0; index < polygon.Length; ++index)
        {
            Vector2 edge = polygon[(index + 1) % polygon.Length] - polygon[index];
            Vector2 normal = new(-edge.Y, edge.X);
            float distance = Vector2.Dot(origin - polygon[index], normal) + padding * edge.Length();
            float rate = Vector2.Dot(direction, normal);

            if (NumericsUtility.IsNearlyZero(rate))
            {
                if (distance < 0f)
                {
                    return 0f;
                }

                continue;
            }

            float crossing = -distance / rate;

            if (rate > 0f)
            {
                enter = MathF.Max(enter, crossing);
            }
            else
            {
                exit = MathF.Min(exit, crossing);
            }

            if (enter > exit)
            {
                return 0f;
            }
        }

        return exit;
    }

    public static Vector2 GetLinearGizmoInteractionStart(GizmoAxisVisualState state, float scale)
    {
        if (!NumericsUtility.TryNormalize(state.ScreenDirection, out var screenDirection))
        {
            return state.ScreenStart;
        }

        var inset = MathF.Min(
            ResolveCenterInteractionRadius(scale) + (5f * scale),
            MathF.Max(0f, state.ScreenLength - 1f));
        return inset <= 0f
            ? state.ScreenStart
            : state.ScreenStart + (screenDirection * inset);
    }

    private static Vector2 GetLinearGizmoInteractionEnd(GizmoAxisVisualState state, GizmoTransformMode mode)
    {
        if (!NumericsUtility.TryNormalize(state.ScreenDirection, out var screenDirection))
        {
            return state.ScreenEnd;
        }

        if (mode == GizmoTransformMode.Translation)
        {
            return GetTrimmedGizmoEndpoint(state);
        }

        var trimAmount = GetScaleHandleVisualHalfExtent(state.VisualScale) + (1.5f * state.VisualScale);
        trimAmount = MathF.Min(trimAmount, MathF.Max(0f, state.ScreenLength - 1f));
        return trimAmount <= 0f
            ? state.ScreenEnd
            : state.ScreenEnd - (screenDirection * trimAmount);
    }

    private static float ResolveLinearGizmoAxisLineHitRadius(GizmoTransformMode mode, float scale)
        => mode == GizmoTransformMode.Scale
            ? MathF.Max(7f * scale, GizmoConstants.AxisLineThickness * scale * 2.35f)
            : MathF.Max(6f * scale, GizmoConstants.AxisLineThickness * scale * 2.1f);

    private static bool TryGetSegmentHitDistance(
        Vector2 point,
        Vector2 start,
        Vector2 end,
        float hitRadius,
        out float distance)
    {
        distance = float.MaxValue;
        if (hitRadius <= 0f)
        {
            return false;
        }

        distance = DistanceToSegment(point, start, end);
        return distance <= hitRadius;
    }

    private static bool TryGetTranslationArrowHitDistance(Vector2 point, in GizmoAxisVisualState state, out float distance)
    {
        distance = float.MaxValue;

        if (!TryGetTranslationArrowVertices(state, out Vector2 tip, out Vector2 left, out Vector2 right))
        {
            return false;
        }

        var edgePadding = MathF.Max(3.5f * state.VisualScale, 2f);

        if (IsPointWithinTriangle(point, tip, left, right))
        {
            distance = 0f;
            return true;
        }

        var tipDistance = DistanceToSegment(point, tip, left);
        var leftDistance = DistanceToSegment(point, left, right);
        var rightDistance = DistanceToSegment(point, right, tip);
        distance = MathF.Min(tipDistance, MathF.Min(leftDistance, rightDistance));
        return distance <= edgePadding;
    }

    private static bool TryGetScaleHandleHitDistance(
        Vector2 point,
        Vector2 center,
        float scale,
        out float distance)
    {
        var halfExtent = GetScaleHandleVisualHalfExtent(scale) + MathF.Max(3.5f * scale, 2f);
        var min = center - new Vector2(halfExtent);
        var max = center + new Vector2(halfExtent);
        var clamped = Vector2.Clamp(point, min, max);
        distance = Vector2.Distance(point, clamped);
        return NumericsUtility.IsNearlyZero(distance);
    }

    private static float GetScaleHandleVisualHalfExtent(float scale)
        => GizmoConstants.ScaleHandleSize * scale * 0.5f;

    private static bool IsPointWithinTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        var denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        if (NumericsUtility.IsNearlyZero(denominator))
        {
            return false;
        }

        var alpha = (((b.Y - c.Y) * (point.X - c.X)) + ((c.X - b.X) * (point.Y - c.Y))) / denominator;
        var beta = (((c.Y - a.Y) * (point.X - c.X)) + ((a.X - c.X) * (point.Y - c.Y))) / denominator;
        var gamma = 1f - alpha - beta;
        return alpha >= 0f && beta >= 0f && gamma >= 0f;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSq = segment.LengthSquared();
        if (!NumericsUtility.HasLength(lengthSq))
        {
            return (point - start).Length();
        }

        var t = Vector2.Dot(point - start, segment) / lengthSq;
        t = Math.Clamp(t, 0f, 1f);
        var projection = start + (segment * t);
        return (point - projection).Length();
    }

    public static Vector2 GetTrimmedGizmoEndpoint(GizmoAxisVisualState state)
    {
        if (!NumericsUtility.TryNormalize(state.ScreenDirection, out var screenDirection))
        {
            return state.ScreenEnd;
        }

        var trimAmount = MathF.Min(GizmoConstants.AxisArrowLength * state.VisualScale, MathF.Max(0f, state.ScreenLength - 1f));
        return trimAmount <= 0f
            ? state.ScreenEnd
            : state.ScreenEnd - (screenDirection * trimAmount);
    }

    public static void DrawScaleStem(DrawBatch batch, in GizmoFrame frame, float uiScale, GizmoAxisVisualState state,
        ReadOnlySpan<GizmoAxisVisualState> translationAxes, Vector4 highlightColor,
        Vector4 color, float thickness, Vector4 glow = default)
    {
        float scale = state.VisualScale;
        float stop = state.ScreenLength - GizmoConstants.ScaleHandleSize * scale * 0.5f;
        float spacing = 16f * scale;
        float dashLength = 10f * scale;
        float start = frame.IsCombined
            ? GetCombinedScaleStemStart(state, translationAxes, uiScale)
            : ResolveCenterInteractionRadius(uiScale) + 2f * scale;
        float clipStart = start;
        bool hovered = frame.IsHovered(GizmoTransformMode.Scale, state);

        if (frame.IsCombined && hovered)
        {
            clipStart = ResolveCenterInteractionRadius(uiScale) + 5f * scale;
            start -= MathF.Ceiling(MathF.Max(0f, start - clipStart) / spacing) * spacing;
        }

        if (frame.IsCombined && clipStart >= stop)
        {
            return;
        }

        if (!frame.IsCombined)
        {
            batch.AddScreenLine(state.ScreenStart, state.ScreenEnd, glow,
                thickness * scale * GizmoConstants.AxisGlowThicknessMultiplier);
            batch.AddScreenLine(state.ScreenStart, state.ScreenEnd, color, thickness * scale);
        }

        if (!frame.IsCombined && !state.IsMirrored)
        {
            return;
        }

        for (float offset = start; offset < stop; offset += spacing)
        {
            float dashStart = MathF.Max(offset, clipStart);
            float dashStop = MathF.Min(offset + dashLength, stop);

            if (dashStart < dashStop)
            {
                Vector4 dashColor = frame.IsCombined ? color : ThemeColors.Color(0f, 0f, 0f, color.W);

                if (frame.IsCombined && hovered && TryGetTranslationOverlap(state, translationAxes, dashStart, dashStop, out _))
                {
                    dashColor = highlightColor with { W = color.W };
                }

                batch.AddScreenLine(state.ScreenStart + state.ScreenDirection * dashStart,
                    state.ScreenStart + state.ScreenDirection * dashStop, dashColor,
                    (frame.IsCombined ? 1f : thickness) * scale, ScreenLineCaps.None);
            }

            if (frame.IsCombined && state.IsMirrored && dashStop < stop)
            {
                float gapStart = MathF.Max(dashStop, clipStart);
                float gapStop = MathF.Min(offset + spacing, stop);

                if (gapStart < gapStop)
                {
                    batch.AddScreenLine(state.ScreenStart + state.ScreenDirection * gapStart,
                        state.ScreenStart + state.ScreenDirection * gapStop,
                        ThemeColors.Color(0f, 0f, 0f, color.W), scale, ScreenLineCaps.None);
                }
            }
        }
    }

    public static void DrawAxisArrowhead(DrawBatch batch, GizmoAxisVisualState state, Vector4 axisColor, float opacity)
    {
        if (!TryGetTranslationArrowVertices(state, out Vector2 tip, out Vector2 left, out Vector2 right))
        {
            return;
        }

        batch.AddScreenTriangle(tip, left, right, axisColor);
        float scale = state.VisualScale;
        var outlineColor = ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, MathF.Min(axisColor.W + 0.15f * opacity, opacity));
        batch.AddScreenLine(tip, left, outlineColor, 1.1f * scale);
        batch.AddScreenLine(left, right, outlineColor, 1.1f * scale);
        batch.AddScreenLine(right, tip, outlineColor, 1.1f * scale);
    }

    private static bool TryGetTranslationArrowVertices(in GizmoAxisVisualState state,
        out Vector2 tip, out Vector2 left, out Vector2 right)
    {
        tip = state.ScreenEnd;
        left = right = default;

        if (!NumericsUtility.TryNormalize(state.ScreenDirection, out Vector2 direction))
        {
            return false;
        }

        float scale = state.VisualScale;
        Vector2 normal = new(-direction.Y, direction.X);
        Vector2 basePoint = tip - direction * (GizmoConstants.AxisArrowLength * scale);
        Vector2 halfWidth = normal * (GizmoConstants.AxisArrowWidth * scale);
        left = basePoint + halfWidth;
        right = basePoint - halfWidth;
        return true;
    }

    public static void DrawScaleHandle(DrawBatch batch, Vector2 position, float scale, Vector4 axisColor, bool highlighted)
    {
        var half = new Vector2(GizmoConstants.ScaleHandleSize * scale * 0.5f);
        var fillColor = ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, axisColor.W * (highlighted ? 0.95f : 0.65f));
        batch.AddScreenRectFilled(position - half, position + half, fillColor);
        batch.AddScreenRect(position - half, position + half, ThemeColors.Color(axisColor.X, axisColor.Y, axisColor.Z, axisColor.W * 0.95f), 1f * scale);
    }
}
