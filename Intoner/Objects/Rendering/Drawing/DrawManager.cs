using System.Numerics;

namespace Intoner.Objects.Rendering.Drawing;

internal enum DrawPassKind
{
    Bounds           = 100,
    HousingPlacement = 110,
    GizmoSnapGrid    = 200,
    GizmoDragPath    = 210,
    Gizmo            = 220,
    Diagnostics      = 900,
}

internal sealed class DrawPass
{
    public DrawPassKind Kind     { get; private set; }
    public string       Name     { get; private set; } = string.Empty;
    public DrawLayer    Layer    { get; private set; }
    public int          Order    { get; private set; }
    public int          Sequence { get; private set; }
    public bool         Drawn    { get; private set; }
    public DrawBatch    Batch    { get; } = new();

    public void Reset(DrawPassKind kind, string name, DrawLayer layer, int order, int sequence)
    {
        Kind     = kind;
        Name     = name;
        Layer    = layer;
        Order    = order;
        Sequence = sequence;
        Drawn    = false;
        Batch.Clear();
    }

    public void MarkDrawn()
        => Drawn = true;
}

internal sealed class DrawManager
{
    private readonly IRenderer      _renderer;
    private readonly List<DrawPass> _passes     = [];
    private readonly List<DrawPass> _drawPasses = [];

    private int _activePassCount;

    public DrawManager(IRenderer renderer)
        => _renderer = renderer;

    public void BeginFrame()
    {
        _renderer.BeginFrame();

        for (int idx = 0; idx < _activePassCount; ++idx)
        {
            _passes[idx].Batch.Clear();
        }

        _activePassCount = 0;
    }

    public DrawBatch BeginPass(DrawPassKind kind, string name, DrawLayer layer)
        => BeginPass(kind, name, layer, (int)kind);

    public DrawBatch BeginPass(DrawPassKind kind, string name, DrawLayer layer, int order)
    {
        int sequence = _activePassCount;
        DrawPass pass = GetOrCreatePass();
        pass.Reset(kind, name, layer, order, sequence);
        return pass.Batch;
    }

    public bool HasPendingLayer(DrawLayer layer)
    {
        for (int idx = 0; idx < _activePassCount; ++idx)
        {
            DrawPass pass = _passes[idx];
            if (pass.Layer == layer && !pass.Drawn && !pass.Batch.IsEmpty)
            {
                return true;
            }
        }

        return false;
    }

    public void DrawLayer(Vector2 viewportPos, Vector2 viewportSize, DrawLayer layer, float alphaMultiplier = 1f)
    {
        if (!DrawContext.TryCaptureEditor(viewportPos, viewportSize, layer, alphaMultiplier, out DrawContext context))
        {
            return;
        }

        DrawLayer(context);
    }

    public void DrawLayer(in DrawContext context)
    {
        _drawPasses.Clear();
        for (int idx = 0; idx < _activePassCount; ++idx)
        {
            DrawPass pass = _passes[idx];
            if (pass.Layer == context.Layer && !pass.Drawn && !pass.Batch.IsEmpty)
            {
                _drawPasses.Add(pass);
            }
        }

        if (_drawPasses.Count == 0)
        {
            return;
        }

        _drawPasses.Sort(static (left, right) =>
        {
            int order = left.Order.CompareTo(right.Order);
            return order != 0 ? order : left.Sequence.CompareTo(right.Sequence);
        });

        foreach (DrawPass pass in _drawPasses)
        {
            _renderer.Draw(context, pass.Batch);
            pass.MarkDrawn();
        }
    }

    private DrawPass GetOrCreatePass()
    {
        if (_activePassCount < _passes.Count)
        {
            return _passes[_activePassCount++];
        }

        DrawPass pass = new();
        _passes.Add(pass);
        ++_activePassCount;
        return pass;
    }
}

