using Dalamud.Interface;
using Intoner.Logging;
using Intoner.Scene.Rendering;
using Intoner.Services.Configuration;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;
using System.Diagnostics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed unsafe class PrimitiveCallbackRenderer : IDisposable
{
    private const float ShaderDepthDisabled = 0f;
    private const float ShaderDepthOccluded = 1f;
    private const float ShaderDepthInvertOccluded = 2f;
    private const float ViewDepthBias = 0.035f;

    private static readonly TimeSpan DrawDiagnosticInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<PrimitiveCallbackRenderer> _logger;
    private readonly PrimitiveRenderResources _resources;
    private readonly PrimitiveGeometryBuilder _geometry = new();
    private readonly D3D11DrawStateSnapshot _drawState = new(
        pixelConstantBufferCount: 1,
        pixelShaderResourceViewCount: 1,
        pixelSamplerCount: 1,
        vertexBufferCount: 2,
        renderTargetCount: 0);

    private bool _loggedDepthFailure;
    private int _loggedProjectionFailure;
    private int _loggedUploadFailure;
    private long _lastDrawDiagnosticTimestamp;

    public PrimitiveCallbackRenderer(
        ILogger<PrimitiveCallbackRenderer> logger,
        IUiBuilder uiBuilder)
    {
        _logger = logger;
        _resources = new PrimitiveRenderResources(logger, uiBuilder);
    }

    public bool Draw(
        ReadOnlySpan<LineCommand> lines,
        ReadOnlySpan<PointCommand> points,
        ReadOnlySpan<ScreenCommand> screens,
        ReadOnlySpan<TextCommand> texts,
        PrimitiveDrawState state,
        in SceneRenderFrame frame)
    {
        if (lines.IsEmpty && points.IsEmpty && screens.IsEmpty && texts.IsEmpty)
        {
            return false;
        }

        var hasWorldInput = !lines.IsEmpty || !points.IsEmpty;
        SceneProjection projectionFrame = frame.Projection;
        var hasWorldProjection = hasWorldInput && frame.HasProjection;
        var diagnostics = new PrimitiveDrawDiagnostics(lines.Length, points.Length, screens.Length);
        if (hasWorldInput && !hasWorldProjection)
        {
            diagnostics.MarkProjectionUnavailable(lines.Length, points.Length);
            if (Interlocked.Exchange(ref _loggedProjectionFailure, 1) == 0)
            {
                _logger.LogWarning("native primitive world draw skipped because the main render view projection was unavailable");
            }

            if (screens.IsEmpty && texts.IsEmpty)
            {
                LogDrawDiagnostics(
                    "missing-projection",
                    state,
                    diagnostics,
                    frame,
                    SceneTextureSize.Empty,
                    false,
                    PrimitiveGeometryBuildResult.Empty);
                return false;
            }
        }

        if (hasWorldProjection)
        {
            _loggedProjectionFailure = 0;
        }

        SceneTextureSize sceneDepthSize = frame.SceneDepthSize;
        ShaderResourceView? sceneDepthView = frame.SceneDepthView;
        var canDrawWorld = hasWorldProjection;
        var needsSceneDepth = canDrawWorld && state.RequiresSceneDepth;
        if (needsSceneDepth && sceneDepthView == null)
        {
            canDrawWorld = false;
            if (!_loggedDepthFailure)
            {
                _loggedDepthFailure = true;
                _logger.LogWarning("native primitive scene depth draw skipped because scene depth was unavailable");
            }

            if (screens.IsEmpty && texts.IsEmpty)
            {
                LogDrawDiagnostics(
                    "missing-depth",
                    state,
                    diagnostics,
                    frame,
                    sceneDepthSize,
                    hasWorldProjection,
                    PrimitiveGeometryBuildResult.Empty);
                return false;
            }
        }
        else
        {
            _loggedDepthFailure = false;
        }

        if (!_resources.TryEnsure() || _resources.Context is not { } context)
        {
            return false;
        }

        PrimitiveAntiAliasParameters antiAlias = PrimitiveAntiAlias.ResolveParameters(state.AntiAliasing);
        SceneProjection? screenProjection = frame.HasProjection ? projectionFrame : null;
        var geometry = canDrawWorld
            ? _geometry.Build(lines, points, screens, projectionFrame, antiAlias, ref diagnostics)
            : _geometry.BuildScreens(screens, antiAlias, screenProjection);
        if (geometry.IsEmpty && texts.IsEmpty)
        {
            LogDrawDiagnostics(
                "empty",
                state,
                diagnostics,
                frame,
                SceneTextureSize.Empty,
                hasWorldProjection,
                geometry);
            return false;
        }

        if (!_resources.TryUploadLineInstances(context, _geometry.LineInstances, geometry.LineInstanceCount)
            || !_resources.TryUploadPointVertices(context, _geometry.PointVertices, geometry.PointVertexCount)
            || !_resources.TryUploadScreenVertices(context, _geometry.ScreenVertices, geometry.ScreenVertexCount))
        {
            if (Interlocked.Exchange(ref _loggedUploadFailure, 1) == 0)
            {
                _logger.LogWarning(
                    "native primitive draw skipped because upload failed for {LineInstanceCount} line instances, {PointVertexCount} point vertices, and {ScreenVertexCount} screen vertices",
                    geometry.LineInstanceCount,
                    geometry.PointVertexCount,
                    geometry.ScreenVertexCount);
            }

            return false;
        }

        _loggedUploadFailure = 0;

        var constants = new PrimitiveConstants
        {
            Viewport = new Vector4(frame.Viewport.X, frame.Viewport.Y, frame.Viewport.Width, frame.Viewport.Height),
            DepthParams = new Vector4(
                ToShaderDepthMode(state.DepthMode),
                ViewDepthBias,
                projectionFrame.ReverseDepth ? 1f : 0f,
                projectionFrame.ForwardPositive ? 1f : 0f),
            DepthTextureSize = new Vector4(
                sceneDepthSize.ActualWidth,
                sceneDepthSize.ActualHeight,
                0f,
                0f),
            LineParams = new Vector4(
                antiAlias.GeometryPadding,
                antiAlias.TransitionWidth,
                antiAlias.CapOverlap,
                0f),
            InverseProjection = projectionFrame.InverseProjection,
        };

        LogDrawDiagnostics(
            "draw",
            state,
            diagnostics,
            frame,
            sceneDepthSize,
            hasWorldProjection,
            geometry);

        try
        {
            using D3D11DrawStateSnapshot.Scope renderState = _drawState.Capture(context);

            if (geometry.HasWorldPrimitives)
            {
                _resources.ApplySharedPipeline(context, sceneDepthView, frame.Viewport, constants);
                _resources.DrawLines(context, geometry.LineInstanceCount);
                _resources.DrawPoints(context, geometry.PointVertexCount);
            }

            if (geometry.ScreenVertexCount > 0)
            {
                constants.DepthParams.X = ShaderDepthDisabled;
                constants.DepthTextureSize = default;
                _resources.ApplySharedPipeline(context, null, frame.Viewport, constants);
                _resources.DrawScreen(context, geometry.ScreenVertexCount);
            }

            bool drewText = false;
            foreach (TextCommand text in texts)
            {
                if (!_resources.IsTextureCompatible(text.Texture))
                {
                    continue;
                }

                Vector2 offset = default;
                if (text.Anchor is { } anchor
                    && (!frame.HasProjection || !anchor.TryResolveOffset(projectionFrame, out offset)))
                {
                    continue;
                }

                if (!drewText)
                {
                    _resources.ApplySharedPipeline(context, null, frame.Viewport, constants);
                    _resources.ApplyTextPipeline(context);
                }

                _resources.DrawText(context, text, text.Position - text.Texture.Offset + offset, frame.Viewport);
                drewText = true;
            }

            return !geometry.IsEmpty || drewText;
        }
        catch (Exception ex)
        {
            _resources.RequestReset();
            DiagnosticLog.Write(() => _logger.LogWarning(ex, "native primitive draw callback failed"));
            return false;
        }
    }

    public void Dispose()
    {
        _drawState.Dispose();
        _resources.Dispose();
        _geometry.ResetStorage();
    }

    private static float ToShaderDepthMode(DrawDepthMode depthMode)
        => depthMode switch
        {
            DrawDepthMode.Occluded => ShaderDepthOccluded,
            DrawDepthMode.InvertOccluded => ShaderDepthInvertOccluded,
            _ => ShaderDepthDisabled,
        };

    private void LogDrawDiagnostics(
        string reason,
        PrimitiveDrawState state,
        in PrimitiveDrawDiagnostics diagnostics,
        in SceneRenderFrame frame,
        SceneTextureSize sceneDepthSize,
        bool hasWorldProjection,
        PrimitiveGeometryBuildResult geometry)
    {
        if (!_logger.IsEnabled(LogLevel.Debug) || !ShouldLogDrawDiagnostics())
        {
            return;
        }

        var summary = diagnostics.FormatSummary(
            state,
            frame.Viewport,
            frame.TargetSize,
            sceneDepthSize,
            frame.Projection,
            hasWorldProjection,
            geometry,
            ViewDepthBias);
        _logger.LogDebug(
            "native primitive diagnostics ({Reason}){NewLine}  {Summary}",
            reason,
            Environment.NewLine,
            summary);
    }

    private bool ShouldLogDrawDiagnostics()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastDrawDiagnosticTimestamp == 0
            || Stopwatch.GetElapsedTime(_lastDrawDiagnosticTimestamp) >= DrawDiagnosticInterval)
        {
            _lastDrawDiagnosticTimestamp = now;
            return true;
        }

        return false;
    }
}
