using Intoner.Objects.Utils;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Rendering.Drawing;

internal sealed class DrawBatch
{
    private readonly List<LinePrimitive>   _lines            = [];
    private readonly List<PointPrimitive>  _points           = [];
    private readonly List<ScreenPrimitive> _screenPrimitives = [];

    public ReadOnlySpan<LinePrimitive> Lines
        => CollectionsMarshal.AsSpan(_lines);

    public ReadOnlySpan<PointPrimitive> Points
        => CollectionsMarshal.AsSpan(_points);

    public ReadOnlySpan<ScreenPrimitive> ScreenPrimitives
        => CollectionsMarshal.AsSpan(_screenPrimitives);

    public bool IsEmpty
        => _lines.Count == 0 && _points.Count == 0 && _screenPrimitives.Count == 0;

    public void AddLine(Vector3 start, Vector3 end, Vector4 color, float thickness)
    {
        if (!HasVisibleColor(color) || thickness <= 0f)
        {
            return;
        }

        _lines.Add(new LinePrimitive(start, end, color, thickness));
    }

    public void AddPoint(Vector3 position, Vector4 color, float radius, int segments = 32)
    {
        if (!HasVisibleColor(color) || radius <= 0f)
        {
            return;
        }

        _points.Add(new PointPrimitive(position, color, radius, Math.Max(3, segments)));
    }

    public void AddScreenLine(Vector2 start, Vector2 end, Vector4 color, float thickness)
        => AddScreenLine(start, end, color, thickness, ScreenLineCaps.Both);

    public void AddScreenLine(Vector2 start, Vector2 end, Vector4 color, float thickness, ScreenLineCaps caps)
    {
        if (!HasVisibleColor(color)
            || thickness <= 0f
            || !ObjectMathUtility.IsFinite(start)
            || !ObjectMathUtility.IsFinite(end))
        {
            return;
        }

        AddScreenLineUnchecked(start, end, color, thickness, caps, start, end);
    }

    public void AddScreenJoinedLine(
        Vector2 previous,
        Vector2 start,
        Vector2 end,
        Vector2 next,
        Vector4 color,
        float thickness,
        ScreenLineCaps caps)
    {
        if (!HasVisibleColor(color)
            || thickness <= 0f
            || !ObjectMathUtility.IsFinite(previous)
            || !ObjectMathUtility.IsFinite(start)
            || !ObjectMathUtility.IsFinite(end)
            || !ObjectMathUtility.IsFinite(next))
        {
            return;
        }

        AddScreenLineUnchecked(start, end, color, thickness, caps, previous, next);
    }

    private void AddScreenLineUnchecked(
        Vector2 start,
        Vector2 end,
        Vector4 color,
        float thickness,
        ScreenLineCaps caps,
        Vector2 previous,
        Vector2 next)
        => _screenPrimitives.Add(new ScreenPrimitive(
            ScreenPrimitiveKind.Line,
            start,
            end,
            default,
            color,
            thickness,
            caps,
            previous,
            next));

    public void AddScreenTriangle(Vector2 first, Vector2 second, Vector2 third, Vector4 color)
    {
        if (!HasVisibleColor(color)
            || !ObjectMathUtility.IsFinite(first)
            || !ObjectMathUtility.IsFinite(second)
            || !ObjectMathUtility.IsFinite(third))
        {
            return;
        }

        _screenPrimitives.Add(new ScreenPrimitive(ScreenPrimitiveKind.Triangle, first, second, third, color, 0f, ScreenLineCaps.None, default, default));
    }

    public void AddScreenRectFilled(Vector2 min, Vector2 max, Vector4 color)
    {
        AddScreenTriangle(min, new Vector2(max.X, min.Y), max, color);
        AddScreenTriangle(min, max, new Vector2(min.X, max.Y), color);
    }

    public void AddScreenRect(Vector2 min, Vector2 max, Vector4 color, float thickness)
    {
        AddScreenLine(min, new Vector2(max.X, min.Y), color, thickness);
        AddScreenLine(new Vector2(max.X, min.Y), max, color, thickness);
        AddScreenLine(max, new Vector2(min.X, max.Y), color, thickness);
        AddScreenLine(new Vector2(min.X, max.Y), min, color, thickness);
    }

    public void AddScreenCircleFilled(Vector2 center, float radius, Vector4 color, int segments = 32)
    {
        if (!HasVisibleColor(color)
            || radius <= 0f
            || !ObjectMathUtility.IsFinite(center))
        {
            return;
        }

        var clampedSegments = Math.Clamp(segments, 8, 96);
        var previous = ResolveCirclePoint(center, radius, 0, clampedSegments);
        for (var index = 1; index <= clampedSegments; ++index)
        {
            var next = ResolveCirclePoint(center, radius, index, clampedSegments);
            AddScreenTriangle(center, previous, next, color);
            previous = next;
        }
    }

    public void AddScreenCircle(Vector2 center, float radius, Vector4 color, float thickness, int segments = 32)
    {
        if (!HasVisibleColor(color)
            || radius <= 0f
            || thickness <= 0f
            || !ObjectMathUtility.IsFinite(center))
        {
            return;
        }

        var clampedSegments = Math.Clamp(segments, 8, 128);
        var previous = ResolveCirclePoint(center, radius, -1, clampedSegments);
        var start = ResolveCirclePoint(center, radius, 0, clampedSegments);
        var end = ResolveCirclePoint(center, radius, 1, clampedSegments);
        for (var index = 0; index < clampedSegments; ++index)
        {
            var next = ResolveCirclePoint(center, radius, index + 2, clampedSegments);
            AddScreenJoinedLine(
                previous,
                start,
                end,
                next,
                color,
                thickness,
                ScreenLineCaps.None);
            previous = start;
            start = end;
            end = next;
        }
    }

    public void Clear()
    {
        _lines.Clear();
        _points.Clear();
        _screenPrimitives.Clear();
    }

    public void AddPolyline(ReadOnlySpan<Vector3> points, bool closed, Vector4 color, float thickness)
    {
        if (points.Length < 2)
        {
            return;
        }

        for (var index = 1; index < points.Length; ++index)
        {
            AddLine(points[index - 1], points[index], color, thickness);
        }

        if (closed && points.Length > 2)
        {
            AddLine(points[^1], points[0], color, thickness);
        }
    }

    private static bool HasVisibleColor(Vector4 color)
        => ObjectMathUtility.IsFinite(color) && color.W > 0f;

    private static Vector2 ResolveCirclePoint(Vector2 center, float radius, int index, int segmentCount)
    {
        var angle = MathF.Tau * index / segmentCount;
        return center + new Vector2(radius * MathF.Cos(angle), radius * MathF.Sin(angle));
    }
}

internal readonly record struct LinePrimitive(
    Vector3 Start,
    Vector3 End,
    Vector4 Color,
    float Thickness);

internal readonly record struct PointPrimitive(
    Vector3 Position,
    Vector4 Color,
    float Radius,
    int Segments);

internal readonly record struct ScreenPrimitive(
    ScreenPrimitiveKind Kind,
    Vector2 First,
    Vector2 Second,
    Vector2 Third,
    Vector4 Color,
    float Thickness,
    ScreenLineCaps Caps,
    Vector2 Previous,
    Vector2 Next);

