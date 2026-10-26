using System.Runtime.InteropServices;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed class PrimitiveCommandList
{
    private readonly List<LineCommand>   _lines   = [];
    private readonly List<PointCommand>  _points  = [];
    private readonly List<ScreenCommand> _screens = [];
    private readonly List<TextCommand> _texts = [];

    public ReadOnlySpan<TextCommand> Texts
        => CollectionsMarshal.AsSpan(_texts);

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

    public int TextCount
        => _texts.Count;

    public int Count
        => LineCount + PointCount + ScreenCount + TextCount;

    public bool IsEmpty
        => Count == 0;

    public void Add(LineCommand command)
        => _lines.Add(command);

    public void Add(PointCommand command)
        => _points.Add(command);

    public void Add(ScreenCommand command)
        => _screens.Add(command);

    public void Add(TextCommand command)
    {
        _texts.Add(command);
        command.Texture.Retain();
    }

    public void Clear()
    {
        _lines.Clear();
        _points.Clear();
        _screens.Clear();
        ClearTexts();
    }

    public void CopyFrom(PrimitiveCommandList source)
    {
        if (ReferenceEquals(this, source))
        {
            return;
        }

        CopyFrom(source.Lines, source.Points, source.Screens);
        foreach (TextCommand text in source.Texts)
        {
            Add(text);
        }
    }

    public void CopyFrom(
        ReadOnlySpan<LineCommand> lines,
        ReadOnlySpan<PointCommand> points,
        ReadOnlySpan<ScreenCommand> screens)
    {
        ClearTexts();
        Store(lines, _lines);
        Store(points, _points);
        Store(screens, _screens);
    }

    private void ClearTexts()
    {
        foreach (TextCommand text in _texts)
        {
            text.Texture.Release();
        }

        _texts.Clear();
    }

    private static void Store<T>(ReadOnlySpan<T> source, List<T> target)
    {
        target.Clear();
        target.AddRange(source);
    }
}
