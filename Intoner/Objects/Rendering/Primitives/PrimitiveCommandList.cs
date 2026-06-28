using System.Runtime.InteropServices;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed class PrimitiveCommandList
{
    private readonly List<LineCommand>   _lines   = [];
    private readonly List<PointCommand>  _points  = [];
    private readonly List<ScreenCommand> _screens = [];

    public ReadOnlySpan<LineCommand> Lines
        => CollectionsMarshal.AsSpan(_lines);

    public ReadOnlySpan<PointCommand> Points
        => CollectionsMarshal.AsSpan(_points);

    public ReadOnlySpan<ScreenCommand> Screens
        => CollectionsMarshal.AsSpan(_screens);

    public int LineCount
        => _lines.Count;

    public int PointCount
        => _points.Count;

    public int ScreenCount
        => _screens.Count;

    public int Count
        => LineCount + PointCount + ScreenCount;

    public bool IsEmpty
        => Count == 0;

    public void Add(LineCommand command)
        => _lines.Add(command);

    public void Add(PointCommand command)
        => _points.Add(command);

    public void Add(ScreenCommand command)
        => _screens.Add(command);

    public void Clear()
    {
        _lines.Clear();
        _points.Clear();
        _screens.Clear();
    }

    public void CopyFrom(PrimitiveCommandList source)
    {
        if (ReferenceEquals(this, source))
        {
            return;
        }

        CopyFrom(source.Lines, source.Points, source.Screens);
    }

    public void CopyFrom(
        ReadOnlySpan<LineCommand> lines,
        ReadOnlySpan<PointCommand> points,
        ReadOnlySpan<ScreenCommand> screens)
    {
        Store(lines, _lines);
        Store(points, _points);
        Store(screens, _screens);
    }

    private static void Store<T>(ReadOnlySpan<T> source, List<T> target)
    {
        target.Clear();
        target.EnsureCapacity(source.Length);
        for (var index = 0; index < source.Length; ++index)
        {
            target.Add(source[index]);
        }
    }
}

