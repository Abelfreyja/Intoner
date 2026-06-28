using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed class PrimitiveGeometryBuilder
{
    private const float ProjectionWMin = 0.000001f;
    private const float ClipEpsilon = 0.000001f;
    private const float DegenerateLineLengthSquared = 0.0001f;

    private PrimitiveLineInstance[] _lineInstances  = [];
    private PrimitiveVertex[]       _pointVertices  = [];
    private PrimitiveVertex[]       _screenVertices = [];

    public PrimitiveLineInstance[] LineInstances
        => _lineInstances;

    public PrimitiveVertex[] PointVertices
        => _pointVertices;

    public PrimitiveVertex[] ScreenVertices
        => _screenVertices;

    public PrimitiveGeometryBuildResult Build(
        ReadOnlySpan<LineCommand> lines,
        ReadOnlySpan<PointCommand> points,
        ReadOnlySpan<ScreenCommand> screens,
        in PrimitiveProjectionFrame frame,
        PrimitiveAntiAliasParameters antiAlias,
        ref PrimitiveDrawDiagnostics diagnostics)
    {
        var lineInstanceCount = 0;
        foreach (LineCommand line in lines)
        {
            AppendLineInstance(line, frame, antiAlias, ref lineInstanceCount, ref diagnostics);
        }

        var pointVertexCount = 0;
        foreach (PointCommand point in points)
        {
            AppendPointVertices(point, frame, ref pointVertexCount, ref diagnostics);
        }

        var screenVertexCount = BuildScreenVertices(screens, antiAlias);

        return new PrimitiveGeometryBuildResult(lineInstanceCount, pointVertexCount, screenVertexCount);
    }

    public PrimitiveGeometryBuildResult BuildScreens(ReadOnlySpan<ScreenCommand> screens, PrimitiveAntiAliasParameters antiAlias)
        => new(0, 0, BuildScreenVertices(screens, antiAlias));

    public void ResetStorage()
    {
        _lineInstances  = [];
        _pointVertices  = [];
        _screenVertices = [];
    }

    private void AppendLineInstance(
        LineCommand line,
        in PrimitiveProjectionFrame frame,
        PrimitiveAntiAliasParameters antiAlias,
        ref int lineInstanceCount,
        ref PrimitiveDrawDiagnostics diagnostics)
    {
        if (!ObjectMathUtility.IsFinite(line.Start)
            || !ObjectMathUtility.IsFinite(line.End))
        {
            diagnostics.InvalidLines++;
            return;
        }

        var clippedStart = line.Start;
        var clippedEnd = line.End;
        if (!ObjectViewportProjectionUtility.TryClipWorldLineToNearPlane(frame.ViewMatrix, frame.NearPlane, ref clippedStart, ref clippedEnd))
        {
            diagnostics.NearPlaneRejectedLines++;
            return;
        }

        if (!TryProjectPoint(frame, clippedStart, out var projectedStart)
            || !TryProjectPoint(frame, clippedEnd, out var projectedEnd))
        {
            diagnostics.ProjectionRejectedLines++;
            return;
        }

        if (!TryClipLineToViewport(frame, line.Thickness, antiAlias, ref projectedStart, ref projectedEnd, out var clippedToViewport))
        {
            diagnostics.ViewportRejectedLines++;
            return;
        }

        if (clippedToViewport)
        {
            diagnostics.ViewportClippedLines++;
        }

        diagnostics.ProjectedLines++;
        if (AppendScreenLineInstance(projectedStart, projectedEnd, line.Thickness, line.Color, ref lineInstanceCount))
        {
            diagnostics.ProjectedLineQuads++;
            diagnostics.Track(projectedStart, line.Thickness * 0.5f);
            diagnostics.Track(projectedEnd, line.Thickness * 0.5f);
        }
        else
        {
            diagnostics.DegenerateLines++;
        }
    }

    private static bool TryClipLineToViewport(
        in PrimitiveProjectionFrame frame,
        float thickness,
        PrimitiveAntiAliasParameters antiAlias,
        ref PrimitiveProjectedPoint start,
        ref PrimitiveProjectedPoint end,
        out bool clipped)
    {
        clipped = false;
        var padding = MathF.Max(thickness * 0.5f + antiAlias.GeometryPadding + 1f, 2f);
        var min = new Vector2(frame.Viewport.X - padding, frame.Viewport.Y - padding);
        var max = new Vector2(
            frame.Viewport.X + frame.Viewport.Width + padding,
            frame.Viewport.Y + frame.Viewport.Height + padding);
        if (!TryClipScreenLine(start.Screen, end.Screen, min, max, out var startT, out var endT))
        {
            return false;
        }

        clipped = startT > 0f || endT < 1f;
        if (clipped)
        {
            var originalStart = start;
            var originalEnd = end;
            start = Interpolate(originalStart, originalEnd, startT);
            end = Interpolate(originalStart, originalEnd, endT);
        }

        return true;
    }

    private static bool TryClipScreenLine(Vector2 start, Vector2 end, Vector2 min, Vector2 max, out float startT, out float endT)
    {
        startT = 0f;
        endT = 1f;
        var delta = end - start;

        return TryClipScreenLineEdge(-delta.X, start.X - min.X, ref startT, ref endT)
            && TryClipScreenLineEdge(delta.X, max.X - start.X, ref startT, ref endT)
            && TryClipScreenLineEdge(-delta.Y, start.Y - min.Y, ref startT, ref endT)
            && TryClipScreenLineEdge(delta.Y, max.Y - start.Y, ref startT, ref endT);
    }

    private static bool TryClipScreenLineEdge(float direction, float distance, ref float startT, ref float endT)
    {
        if (MathF.Abs(direction) < ClipEpsilon)
        {
            return distance >= 0f;
        }

        var t = distance / direction;
        if (direction < 0f)
        {
            if (t > endT)
            {
                return false;
            }

            startT = Math.Max(startT, t);
        }
        else
        {
            if (t < startT)
            {
                return false;
            }

            endT = Math.Min(endT, t);
        }

        return true;
    }

    private bool AppendScreenLineInstance(
        PrimitiveProjectedPoint screenStart,
        PrimitiveProjectedPoint screenEnd,
        float thickness,
        uint color,
        ref int lineInstanceCount)
    {
        if (!IsRenderableScreenLine(screenStart.Screen, screenEnd.Screen))
        {
            return false;
        }

        EnsureCapacity(ref _lineInstances, lineInstanceCount + 1);
        _lineInstances[lineInstanceCount++] = new PrimitiveLineInstance(
            screenStart.Screen,
            screenEnd.Screen,
            screenStart.ViewDepth,
            screenEnd.ViewDepth,
            screenStart.InvClipW,
            screenEnd.InvClipW,
            thickness,
            color);
        return true;
    }

    private void AppendPointVertices(
        PointCommand point,
        in PrimitiveProjectionFrame frame,
        ref int pointVertexCount,
        ref PrimitiveDrawDiagnostics diagnostics)
    {
        if (!ObjectMathUtility.IsFinite(point.Position))
        {
            diagnostics.InvalidPoints++;
            return;
        }

        if (!TryProjectPoint(frame, point.Position, out var center))
        {
            diagnostics.ProjectionRejectedPoints++;
            return;
        }

        diagnostics.ProjectedPoints++;
        diagnostics.Track(center, point.Radius);
        var clampedSegments = Math.Clamp(point.Segments, 8, 64);
        var previous = Vector2.Zero;
        for (var index = 0; index <= clampedSegments; ++index)
        {
            var angle = MathF.Tau * index / clampedSegments;
            var next = center.Screen + new Vector2(
                point.Radius * MathF.Cos(angle),
                point.Radius * MathF.Sin(angle));

            if (index > 0)
            {
                AddPointTriangle(
                    CreatePointVertex(center, center.Screen, point.Color),
                    CreatePointVertex(center, previous, point.Color),
                    CreatePointVertex(center, next, point.Color),
                    ref pointVertexCount);
            }

            previous = next;
        }
    }

    private static bool TryProjectPoint(
        in PrimitiveProjectionFrame frame,
        Vector3 worldPoint,
        out PrimitiveProjectedPoint point)
    {
        point = default;
        var projected = Vector4.Transform(new Vector4(worldPoint, 1f), frame.ViewProjection);
        if (!float.IsFinite(projected.W) || projected.W <= ProjectionWMin)
        {
            return false;
        }

        var invClipW = 1f / projected.W;
        var ndc = new Vector3(projected.X * invClipW, projected.Y * invClipW, projected.Z * invClipW);
        var viewPosition = Vector4.Transform(new Vector4(worldPoint, 1f), frame.ViewMatrix);
        var screen = new Vector2(
            frame.Viewport.X + ((ndc.X + 1f) * frame.Viewport.Width * 0.5f),
            frame.Viewport.Y + ((1f - ndc.Y) * frame.Viewport.Height * 0.5f));
        if (!ObjectMathUtility.IsFinite(screen)
            || !float.IsFinite(ndc.Z)
            || !float.IsFinite(viewPosition.Z)
            || !float.IsFinite(invClipW))
        {
            return false;
        }

        point = new PrimitiveProjectedPoint(screen, ndc.Z, viewPosition.Z, invClipW);
        return true;
    }

    private static PrimitiveProjectedPoint Interpolate(PrimitiveProjectedPoint start, PrimitiveProjectedPoint end, float amount)
        => new(
            Vector2.Lerp(start.Screen, end.Screen, amount),
            InterpolatePerspective(start.Depth, end.Depth, start.InvClipW, end.InvClipW, amount),
            InterpolatePerspective(start.ViewDepth, end.ViewDepth, start.InvClipW, end.InvClipW, amount),
            start.InvClipW + ((end.InvClipW - start.InvClipW) * amount));

    private static float InterpolatePerspective(float start, float end, float startInvClipW, float endInvClipW, float amount)
    {
        var invClipW = startInvClipW + ((endInvClipW - startInvClipW) * amount);
        if (MathF.Abs(invClipW) < ProjectionWMin)
        {
            return start + ((end - start) * amount);
        }

        var weightedStart = start * startInvClipW;
        var weightedEnd = end * endInvClipW;
        return (weightedStart + ((weightedEnd - weightedStart) * amount)) / invClipW;
    }

    private static bool IsRenderableScreenLine(Vector2 start, Vector2 end)
    {
        var line = end - start;
        return ObjectMathUtility.IsFinite(line)
            && line.LengthSquared() > DegenerateLineLengthSquared;
    }

    private void AddPointTriangle(
        PrimitiveVertex first,
        PrimitiveVertex second,
        PrimitiveVertex third,
        ref int pointVertexCount)
    {
        EnsureCapacity(ref _pointVertices, pointVertexCount + 3);
        _pointVertices[pointVertexCount++] = first;
        _pointVertices[pointVertexCount++] = second;
        _pointVertices[pointVertexCount++] = third;
    }

    private static PrimitiveVertex CreatePointVertex(PrimitiveProjectedPoint point, Vector2 screen, uint color)
        => new(screen, point.ViewDepth, point.InvClipW, color, screen, screen, 0f, 0f);

    private int BuildScreenVertices(ReadOnlySpan<ScreenCommand> screens, PrimitiveAntiAliasParameters antiAlias)
    {
        var screenVertexCount = 0;
        foreach (ScreenCommand screen in screens)
        {
            AppendScreenPrimitive(screen, antiAlias, ref screenVertexCount);
        }

        return screenVertexCount;
    }

    private void AppendScreenPrimitive(ScreenCommand command, PrimitiveAntiAliasParameters antiAlias, ref int screenVertexCount)
    {
        switch (command.Kind)
        {
            case ScreenPrimitiveKind.Triangle:
                AddScreenTriangle(command.First, command.Second, command.Third, command.Color, ref screenVertexCount);
                break;
            case ScreenPrimitiveKind.Line:
                AddScreenLine(
                    command.Previous,
                    command.First,
                    command.Second,
                    command.Next,
                    command.Thickness,
                    command.Color,
                    command.Caps,
                    antiAlias,
                    ref screenVertexCount);
                break;
        }
    }

    private void AddScreenLine(
        Vector2 previous,
        Vector2 start,
        Vector2 end,
        Vector2 next,
        float thickness,
        uint color,
        ScreenLineCaps caps,
        PrimitiveAntiAliasParameters antiAlias,
        ref int screenVertexCount)
    {
        if (thickness <= 0f || !IsRenderableScreenLine(start, end))
        {
            return;
        }

        var line = end - start;
        var lengthSquared = line.LengthSquared();
        var invLength = 1f / MathF.Sqrt(lengthSquared);
        var direction = line * invLength;
        var normal = new Vector2(-direction.Y, direction.X);
        var sideOffset = normal * ((thickness * 0.5f) + antiAlias.GeometryPadding);
        var hasStartCap = ScreenLineCapsUtility.Has(caps, ScreenLineCaps.Start);
        var hasEndCap = ScreenLineCapsUtility.Has(caps, ScreenLineCaps.End);
        var startSideOffset = hasStartCap
            ? sideOffset
            : ResolveScreenLineJoinOffset(previous, start, end, sideOffset);
        var endSideOffset = hasEndCap
            ? sideOffset
            : ResolveScreenLineJoinOffset(start, end, next, sideOffset);
        var startCapOffset = hasStartCap
            ? direction * antiAlias.GeometryPadding
            : Vector2.Zero;
        var endCapOffset = hasEndCap
            ? direction * antiAlias.GeometryPadding
            : Vector2.Zero;
        var first = start - startCapOffset + startSideOffset;
        var second = end + endCapOffset + endSideOffset;
        var third = end + endCapOffset - endSideOffset;
        var fourth = start - startCapOffset - startSideOffset;

        AddScreenLineTriangle(first, second, third, start, end, thickness, color, caps, ref screenVertexCount);
        AddScreenLineTriangle(first, third, fourth, start, end, thickness, color, caps, ref screenVertexCount);
    }

    private static Vector2 ResolveScreenLineJoinOffset(Vector2 previous, Vector2 current, Vector2 next, Vector2 fallback)
    {
        if (!ObjectMathUtility.TryNormalize(current - previous, out var previousDirection)
            || !ObjectMathUtility.TryNormalize(next - current, out var nextDirection)
            || !ObjectMathUtility.TryNormalize(previousDirection + nextDirection, out var tangent))
        {
            return fallback;
        }

        if (!ObjectMathUtility.TryNormalize(fallback, out var fallbackNormal))
        {
            return fallback;
        }

        var miter = new Vector2(-tangent.Y, tangent.X);
        var denominator = Vector2.Dot(miter, fallbackNormal);
        if (MathF.Abs(denominator) < 0.0001f)
        {
            return fallback;
        }

        var fallbackLength = fallback.Length();
        var length = fallbackLength / denominator;
        var maxLength = fallbackLength * 4f;
        if (!float.IsFinite(length) || MathF.Abs(length) > maxLength)
        {
            return fallback;
        }

        var offset = miter * length;
        return Vector2.Dot(offset, fallbackNormal) < 0f
            ? -offset
            : offset;
    }

    private void AddScreenLineTriangle(
        Vector2 first,
        Vector2 second,
        Vector2 third,
        Vector2 lineStart,
        Vector2 lineEnd,
        float thickness,
        uint color,
        ScreenLineCaps caps,
        ref int screenVertexCount)
    {
        if (!IsRenderableScreenTriangle(first, second, third))
        {
            return;
        }

        EnsureCapacity(ref _screenVertices, screenVertexCount + 3);
        _screenVertices[screenVertexCount++] = CreateScreenLineVertex(first, lineStart, lineEnd, thickness, color, caps);
        _screenVertices[screenVertexCount++] = CreateScreenLineVertex(second, lineStart, lineEnd, thickness, color, caps);
        _screenVertices[screenVertexCount++] = CreateScreenLineVertex(third, lineStart, lineEnd, thickness, color, caps);
    }

    private void AddScreenTriangle(Vector2 first, Vector2 second, Vector2 third, uint color, ref int screenVertexCount)
    {
        if (!IsRenderableScreenTriangle(first, second, third))
        {
            return;
        }

        EnsureCapacity(ref _screenVertices, screenVertexCount + 3);
        _screenVertices[screenVertexCount++] = CreateScreenVertex(first, color);
        _screenVertices[screenVertexCount++] = CreateScreenVertex(second, color);
        _screenVertices[screenVertexCount++] = CreateScreenVertex(third, color);
    }

    private static PrimitiveVertex CreateScreenVertex(Vector2 screen, uint color)
        => new(screen, 0f, 1f, color, screen, screen, 0f, 0f);

    private static PrimitiveVertex CreateScreenLineVertex(Vector2 screen, Vector2 lineStart, Vector2 lineEnd, float thickness, uint color, ScreenLineCaps caps)
        => new(screen, 0f, 1f, color, lineStart, lineEnd, thickness, (float)caps);

    private static bool IsRenderableScreenTriangle(Vector2 first, Vector2 second, Vector2 third)
    {
        if (!ObjectMathUtility.IsFinite(first)
            || !ObjectMathUtility.IsFinite(second)
            || !ObjectMathUtility.IsFinite(third))
        {
            return false;
        }

        var edgeA = second - first;
        var edgeB = third - first;
        var area = MathF.Abs((edgeA.X * edgeB.Y) - (edgeA.Y * edgeB.X));
        return area > DegenerateLineLengthSquared;
    }

    private static void EnsureCapacity<T>(ref T[] values, int requiredCapacity)
    {
        if (values.Length >= requiredCapacity)
        {
            return;
        }

        var nextCapacity = Math.Max(requiredCapacity, Math.Max(values.Length * 2, 256));
        Array.Resize(ref values, nextCapacity);
    }
}

