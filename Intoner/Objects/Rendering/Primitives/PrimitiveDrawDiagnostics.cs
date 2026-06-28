using System.Globalization;
using System.Numerics;
using Intoner.Objects.Filesystem.Configuration;
using RawViewportF = SharpDX.Mathematics.Interop.RawViewportF;

namespace Intoner.Objects.Rendering.Primitives;

internal readonly record struct PrimitiveProjectedPoint(
    Vector2 Screen,
    float Depth,
    float ViewDepth,
    float InvClipW);

internal struct PrimitiveDrawDiagnostics
{
    private bool _hasRange;
    private Vector2 _screenMin;
    private Vector2 _screenMax;
    private float _projectedDepthMin;
    private float _projectedDepthMax;
    private float _viewDepthMin;
    private float _viewDepthMax;

    public PrimitiveDrawDiagnostics(int inputLines, int inputPoints, int inputScreens)
    {
        InputLines          = inputLines;
        InputPoints         = inputPoints;
        InputScreens        = inputScreens;
        _hasRange          = false;
        _screenMin         = default;
        _screenMax         = default;
        _projectedDepthMin = 0f;
        _projectedDepthMax = 0f;
        _viewDepthMin      = 0f;
        _viewDepthMax      = 0f;
    }

    public int InputLines { get; }
    public int InputPoints { get; }
    public int InputScreens { get; }
    public int InvalidLines { get; set; }
    public int InvalidPoints { get; set; }
    public int ProjectedLines { get; set; }
    public int ProjectedPoints { get; set; }
    public int ProjectedLineQuads { get; set; }
    public int NearPlaneRejectedLines { get; set; }
    public int ProjectionRejectedLines { get; set; }
    public int ProjectionRejectedPoints { get; set; }
    public int ViewportClippedLines { get; set; }
    public int ViewportRejectedLines { get; set; }
    public int DegenerateLines { get; set; }

    public void MarkProjectionUnavailable(int lineCount, int pointCount)
    {
        ProjectionRejectedLines  += lineCount;
        ProjectionRejectedPoints += pointCount;
    }

    public void Track(PrimitiveProjectedPoint point, float screenPadding)
    {
        var padding = Math.Max(0f, screenPadding);
        var min = point.Screen - new Vector2(padding);
        var max = point.Screen + new Vector2(padding);
        if (!_hasRange)
        {
            _screenMin         = min;
            _screenMax         = max;
            _projectedDepthMin = point.Depth;
            _projectedDepthMax = point.Depth;
            _viewDepthMin      = point.ViewDepth;
            _viewDepthMax      = point.ViewDepth;
            _hasRange          = true;
            return;
        }

        _screenMin         = Vector2.Min(_screenMin, min);
        _screenMax         = Vector2.Max(_screenMax, max);
        _projectedDepthMin = Math.Min(_projectedDepthMin, point.Depth);
        _projectedDepthMax = Math.Max(_projectedDepthMax, point.Depth);
        _viewDepthMin      = Math.Min(_viewDepthMin, point.ViewDepth);
        _viewDepthMax      = Math.Max(_viewDepthMax, point.ViewDepth);
    }

    private string FormatScreenBounds()
        => _hasRange
            ? $"{Format(_screenMin.X)},{Format(_screenMin.Y)}->{Format(_screenMax.X)},{Format(_screenMax.Y)}"
            : "empty";

    private string FormatProjectedDepthRange()
        => _hasRange
            ? $"{Format(_projectedDepthMin)}->{Format(_projectedDepthMax)}"
            : "empty";

    private string FormatViewDepthRange()
        => _hasRange
            ? $"{Format(_viewDepthMin)}->{Format(_viewDepthMax)}"
            : "empty";

    public string FormatSummary(
        PrimitiveDrawState state,
        RawViewportF viewport,
        PrimitiveTextureSize finalTargetSize,
        PrimitiveTextureSize sceneDepthSize,
        in PrimitiveProjectionFrame projectionFrame,
        bool hasWorldProjection,
        PrimitiveGeometryBuildResult geometry,
        float viewDepthBias)
        => string.Join(
            Environment.NewLine + "  ",
            FormatState(state),
            FormatInput(),
            FormatProjected(),
            FormatOutput(geometry),
            FormatRejects(),
            $"target: viewport={FormatViewport(viewport)}, final={FormatTexture(finalTargetSize)}, depth={FormatTexture(sceneDepthSize)}",
            FormatProjection(projectionFrame, hasWorldProjection, viewDepthBias));

    private static string Format(float value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatBool(bool value)
        => value ? "true" : "false";

    private static string FormatTexture(PrimitiveTextureSize size)
        => size.ActualWidth == 0 || size.ActualHeight == 0
            ? "empty"
            : $"{size.ActualWidth}x{size.ActualHeight}/{size.AllocatedWidth}x{size.AllocatedHeight}";

    private static string FormatViewport(RawViewportF viewport)
        => $"{Format(viewport.X)},{Format(viewport.Y)} {Format(viewport.Width)}x{Format(viewport.Height)}";

    private static string FormatState(PrimitiveDrawState state)
        => $"state: occlusion={state.DepthMode}, antiAliasing={Format(RenderingConfiguration.AntiAliasingToPixels(state.AntiAliasing))}px";

    private string FormatInput()
        => $"input: lines={InputLines}, points={InputPoints}, screen={InputScreens}";

    private string FormatProjected()
        => $"project: lines={ProjectedLines}, points={ProjectedPoints}, lineQuads={ProjectedLineQuads}";

    private static string FormatOutput(PrimitiveGeometryBuildResult geometry)
        => "output: " + string.Join(
            ", ",
            $"lineInstances={geometry.LineInstanceCount}",
            $"pointVertices={geometry.PointVertexCount}",
            $"screenVertices={geometry.ScreenVertexCount}",
            $"drawVertices={geometry.DrawVertexCount}");

    private string FormatRejects()
        => "reject: " + string.Join(
            ", ",
            $"invalidLines={InvalidLines}",
            $"invalidPoints={InvalidPoints}",
            $"nearLines={NearPlaneRejectedLines}",
            $"projectionLines={ProjectionRejectedLines}",
            $"projectionPoints={ProjectionRejectedPoints}",
            $"viewportClipped={ViewportClippedLines}",
            $"viewportRejected={ViewportRejectedLines}",
            $"degenerateLines={DegenerateLines}");

    private string FormatProjection(
        in PrimitiveProjectionFrame projectionFrame,
        bool hasWorldProjection,
        float viewDepthBias)
        => hasWorldProjection
            ? "projection: " + string.Join(
                ", ",
                $"near={Format(projectionFrame.NearPlane)}",
                $"reverse={FormatBool(projectionFrame.ReverseDepth)}",
                $"forwardPositive={FormatBool(projectionFrame.ForwardPositive)}",
                $"bias={Format(viewDepthBias)}",
                $"screen={FormatScreenBounds()}",
                $"ndc={FormatProjectedDepthRange()}",
                $"view={FormatViewDepthRange()}")
            : "projection: none";
}

