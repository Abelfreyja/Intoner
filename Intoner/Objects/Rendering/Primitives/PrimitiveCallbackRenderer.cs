using Dalamud.Interface;
using Intoner.Objects.Filesystem.Configuration;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RawViewportF = SharpDX.Mathematics.Interop.RawViewportF;
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
    private readonly PrimitiveRenderResources           _resources;
    private readonly PrimitiveGeometryBuilder           _geometry = new();

    private bool _loggedDepthFailure;
    private int _loggedFinalTargetFailure;
    private int _loggedProjectionFailure;
    private int _loggedUploadFailure;
    private long _lastDrawDiagnosticTimestamp;

    public PrimitiveCallbackRenderer(
        ILogger<PrimitiveCallbackRenderer> logger,
        IUiBuilder uiBuilder)
    {
        _logger    = logger;
        _resources = new PrimitiveRenderResources(logger, uiBuilder);
    }

    public bool Draw(
        ReadOnlySpan<LineCommand> lines,
        ReadOnlySpan<PointCommand> points,
        ReadOnlySpan<ScreenCommand> screens,
        PrimitiveDrawState state)
    {
        if ((lines.IsEmpty && points.IsEmpty && screens.IsEmpty) || !_resources.TryEnsure())
        {
            return false;
        }

        DeviceContext? context = _resources.Context;
        if (context == null)
        {
            return false;
        }

        if (!PrimitiveRenderTargetResolver.TryResolveFinalTargetViewport(out var viewport, out var finalTargetSize))
        {
            if (Interlocked.Exchange(ref _loggedFinalTargetFailure, 1) == 0)
            {
                _logger.LogWarning("object native primitive draw skipped because the final scene target was unavailable");
            }

            return false;
        }

        var hasWorldInput = !lines.IsEmpty || !points.IsEmpty;
        PrimitiveProjectionFrame projectionFrame = default;
        var hasWorldProjection = hasWorldInput && PrimitiveProjectionFrame.TryCaptureMainView(viewport, out projectionFrame);
        var diagnostics = new PrimitiveDrawDiagnostics(lines.Length, points.Length, screens.Length);
        if (hasWorldInput && !hasWorldProjection)
        {
            diagnostics.MarkProjectionUnavailable(lines.Length, points.Length);
            if (Interlocked.Exchange(ref _loggedProjectionFailure, 1) == 0)
            {
                _logger.LogWarning("object native primitive world draw skipped because the main render view projection was unavailable");
            }

            if (screens.IsEmpty)
            {
                LogDrawDiagnostics(
                    "missing-projection",
                    state,
                    diagnostics,
                    viewport,
                    finalTargetSize,
                    PrimitiveTextureSize.Empty,
                    projectionFrame,
                    false,
                    PrimitiveGeometryBuildResult.Empty);
                return false;
            }
        }

        _loggedFinalTargetFailure = 0;
        if (hasWorldProjection)
        {
            _loggedProjectionFailure = 0;
        }

        PrimitiveAntiAliasParameters antiAlias = PrimitiveAntiAlias.ResolveParameters(state.AntiAliasing);
        var geometry = hasWorldProjection
            ? _geometry.Build(lines, points, screens, projectionFrame, antiAlias, ref diagnostics)
            : _geometry.BuildScreens(screens, antiAlias);
        if (geometry.IsEmpty)
        {
            LogDrawDiagnostics(
                "empty",
                state,
                diagnostics,
                viewport,
                finalTargetSize,
                PrimitiveTextureSize.Empty,
                projectionFrame,
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
                    "object native primitive draw skipped because upload failed for {LineInstanceCount} line instances, {PointVertexCount} point vertices, and {ScreenVertexCount} screen vertices",
                    geometry.LineInstanceCount,
                    geometry.PointVertexCount,
                    geometry.ScreenVertexCount);
            }

            return false;
        }

        _loggedUploadFailure = 0;

        var needsSceneDepth = geometry.HasWorldPrimitives && NeedsSceneDepth(state.DepthMode);
        var sceneDepthSize = PrimitiveTextureSize.Empty;
        using ShaderResourceView? sceneDepthView = needsSceneDepth
            ? TryCreateSceneDepthView(out sceneDepthSize)
            : null;
        if (needsSceneDepth && sceneDepthView == null)
        {
            LogDrawDiagnostics(
                "missing-depth",
                state,
                diagnostics,
                viewport,
                finalTargetSize,
                sceneDepthSize,
                projectionFrame,
                hasWorldProjection,
                geometry);
            return false;
        }

        var constants = new PrimitiveConstants
        {
            Viewport = new Vector4(viewport.X, viewport.Y, viewport.Width, viewport.Height),
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
            viewport,
            finalTargetSize,
            sceneDepthSize,
            projectionFrame,
            hasWorldProjection,
            geometry);

        try
        {
            using var renderState = D3D11RenderStateSnapshot.Capture(
                context,
                pixelConstantBufferCount: 1,
                pixelShaderResourceViewCount: 1,
                vertexBufferCount: 2,
                captureScissorRectangles: true);

            if (geometry.HasWorldPrimitives)
            {
                _resources.ApplySharedPipeline(context, sceneDepthView, viewport, constants);
                _resources.DrawLines(context, geometry.LineInstanceCount);
                _resources.DrawPoints(context, geometry.PointVertexCount);
            }

            if (geometry.ScreenVertexCount > 0)
            {
                constants.DepthParams.X = ShaderDepthDisabled;
                constants.DepthTextureSize = default;
                _resources.ApplySharedPipeline(context, null, viewport, constants);
                _resources.DrawScreen(context, geometry.ScreenVertexCount);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object native primitive draw callback failed");
            _resources.Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        _resources.Dispose();
        _geometry.ResetStorage();
    }

    private ShaderResourceView? TryCreateSceneDepthView(out PrimitiveTextureSize sceneDepthSize)
    {
        if (!PrimitiveRenderTargetResolver.TryResolveSceneDepthView(out var depthViewPointer, out sceneDepthSize))
        {
            if (!_loggedDepthFailure)
            {
                _loggedDepthFailure = true;
                _logger.LogWarning("object native primitive scene-depth draw skipped because scene depth texture was unavailable");
            }

            return null;
        }

        _loggedDepthFailure = false;
        Marshal.AddRef(depthViewPointer);
        return new ShaderResourceView(depthViewPointer);
    }

    private static bool NeedsSceneDepth(DrawDepthMode depthMode)
        => depthMode is DrawDepthMode.Occluded or DrawDepthMode.InvertOccluded;

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
        RawViewportF viewport,
        PrimitiveTextureSize finalTargetSize,
        PrimitiveTextureSize sceneDepthSize,
        in PrimitiveProjectionFrame projectionFrame,
        bool hasWorldProjection,
        PrimitiveGeometryBuildResult geometry)
    {
        if (!_logger.IsEnabled(LogLevel.Debug) || !ShouldLogDrawDiagnostics())
        {
            return;
        }

        var summary = diagnostics.FormatSummary(
            state,
            viewport,
            finalTargetSize,
            sceneDepthSize,
            projectionFrame,
            hasWorldProjection,
            geometry,
            ViewDepthBias);
        _logger.LogDebug(
            "object native primitive diagnostics ({Reason}){NewLine}  {Summary}",
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

