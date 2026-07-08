using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Intoner.Objects.Rendering.Drawing;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;
using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;
using D3D11MapFlags = SharpDX.Direct3D11.MapFlags;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.UI.Services.EdgeGlow;

internal enum EdgeGlowColorVariant
{
    Colorful,
    Sunset,
}

internal enum EdgeGlowTheme
{
    Dark,
    Light,
}

internal enum EdgeGlowMode
{
    FullBorder,
    Line,
}

internal readonly record struct EdgeGlowStyle
{
    public EdgeGlowStyle()
    {
    }

    public EdgeGlowMode Mode { get; init; } = EdgeGlowMode.FullBorder;
    public EdgeGlowColorVariant ColorVariant { get; init; } = EdgeGlowColorVariant.Colorful;
    public EdgeGlowTheme Theme { get; init; } = EdgeGlowTheme.Dark;
    public float? CornerRounding { get; init; }
    public float BorderInset { get; init; } = 0f;
    public float BorderWidth { get; init; } = 1f;
    public float Duration { get; init; }
    public float Strength { get; init; } = 1f;
    public float Brightness { get; init; } = 1.3f;
    public float Saturation { get; init; } = 1.2f;
    public float HueRange { get; init; } = 30f;
    public bool StaticColors { get; init; }
    public float StrokeOpacity { get; init; } = 0.48f;
    public float InnerOpacity { get; init; } = 0.70f;
    public float BloomOpacity { get; init; } = 0.80f;
    public float InnerShadowAlpha { get; init; } = 0.27f;
    public float RenderScale { get; init; } = 1f;
    public float HorizontalFootprintScale { get; init; } = 1f;
    public float FullBorderInnerReachScale { get; init; } = 1f;
    public float FullBorderSweepScale { get; init; } = 1f;
    public ImDrawFlags CornerFlags { get; init; } = ImDrawFlags.RoundCornersAll;
    public bool ClipToRect { get; init; }
    public float ClipPadding { get; init; }
}

internal sealed unsafe partial class EdgeGlowRenderer : GpuUiDeviceResourceHost
{
    private const string ShaderResourceName = "Objects.UI.Shaders.EdgeGlow.EdgeGlow.hlsl";
    private const int SpotCount = 9;
    private const float DefaultFullBorderDuration = 1.96f;
    private const float DefaultLineDuration = 2.4f;
    private const float HueAnimationPeriod = 12f;
    private const float BloomHueAnimationPeriod = 8f;
    private const float BloomBlurScale = 0.45f;
    private const float BloomBlurOffset = 2.25f;
    private const float MaxAdaptiveRenderDimension = 512f;
    private const float MaxAdaptiveRenderPixels = 160_000f;
    private const float MinAdaptiveRenderScale = 0.35f;
    private const float VisibilityEpsilon = 0.001f;
    private const float FullBorderInnerSpotOpacity = 0.45f;

    private static readonly GpuShaderBytecode VertexShader = CreateVertexShader("edge glow vertex shader");
    private static readonly GpuShaderBytecode LinePixelShader = CreatePixelShader("edge glow line pixel shader", "PSLineMain");
    private static readonly GpuShaderBytecode LineBloomPixelShader = CreatePixelShader("edge glow line bloom pixel shader", "PSLineBloomOnly");
    private static readonly GpuShaderBytecode FullBorderPixelShader = CreatePixelShader("edge glow full border pixel shader", "PSFullMain");
    private static readonly GpuShaderBytecode FullBorderBloomPixelShader = CreatePixelShader("edge glow full border bloom pixel shader", "PSFullBloomOnly");
    private static readonly GpuShaderBytecode DownsampleShader = CreatePixelShader("edge glow downsample shader", "PSDownsample");
    private static readonly GpuShaderBytecode UpsampleShader = CreatePixelShader("edge glow upsample shader", "PSUpsample");

    private static readonly AnimationKeyframe[] BeamTravelXFrames =
    [
        new(0.0f, 0.06f),
        new(0.1f, 0.15f),
        new(0.2f, 0.25f),
        new(0.3f, 0.35f),
        new(0.4f, 0.44f),
        new(0.5f, 0.50f),
        new(0.6f, 0.56f),
        new(0.7f, 0.65f),
        new(0.8f, 0.75f),
        new(0.9f, 0.85f),
        new(1.0f, 0.94f),
    ];

    private static readonly AnimationKeyframe[] BeamTravelWidthFrames =
    [
        new(0.0f, 0.50f),
        new(0.1f, 0.80f),
        new(0.2f, 1.10f),
        new(0.3f, 1.30f),
        new(0.4f, 1.45f),
        new(0.5f, 1.50f),
        new(0.6f, 1.45f),
        new(0.7f, 1.30f),
        new(0.8f, 1.10f),
        new(0.9f, 0.80f),
        new(1.0f, 0.50f),
    ];

    private static readonly AnimationKeyframe[] BeamEdgeFrames =
    [
        new(0.0f, 0.0f),
        new(0.125f, 0.0f),
        new(0.325f, 1.0f),
        new(0.675f, 1.0f),
        new(0.875f, 0.0f),
        new(1.0f, 0.0f),
    ];

    private static readonly AnimationKeyframe[] BeamHeightFrames =
    [
        new(0.0f, 0.80f),
        new(0.25f, 1.25f),
        new(0.55f, 0.85f),
        new(0.80f, 1.30f),
        new(1.0f, 0.80f),
    ];

    private static readonly AnimationKeyframe[] BeamSpikeFrames =
    [
        new(0.0f, 0.80f),
        new(0.25f, 1.30f),
        new(0.50f, 0.90f),
        new(0.75f, 1.40f),
        new(1.0f, 0.80f),
    ];

    private static readonly AnimationKeyframe[] BeamSpike2Frames =
    [
        new(0.0f, 1.20f),
        new(0.25f, 0.70f),
        new(0.50f, 1.40f),
        new(0.75f, 0.80f),
        new(1.0f, 1.20f),
    ];

    private readonly ILogger<EdgeGlowRenderer> _logger;
    private readonly ImGuiDrawCallbackQueue<EdgeGlowRenderJob> _renderJobs = new(ProcessRenderJob, static job => job.Dispose());

    private PixelShader? _linePixelShader;
    private PixelShader? _lineBloomPixelShader;
    private PixelShader? _fullBorderPixelShader;
    private PixelShader? _fullBorderBloomPixelShader;
    private PixelShader? _downsampleShader;
    private PixelShader? _upsampleShader;
    private GpuFullscreenQuad? _fullscreenQuad;
    private Buffer? _constantBuffer;
    private Buffer? _blurConstantBuffer;
    private SamplerState? _samplerState;
    private RasterizerState? _rasterizerState;
    private DepthStencilState? _depthStencilState;
    private BlendState? _blendState;

    public EdgeGlowRenderer(ILogger<EdgeGlowRenderer> logger, IUiBuilder uiBuilder)
        : base(logger, uiBuilder, "edge glow renderer initialization failed")
    {
        _logger = logger;
    }

    /// <summary> draws the edge glow around the last submitted item </summary>
    public void DrawAroundLastItem(in EdgeGlowStyle style)
    {
        DrawRect(
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            ImGui.GetStyle().FrameRounding,
            style);
    }

    /// <summary> draws the edge glow around an rounded rect </summary>
    public void DrawRect(Vector2 min, Vector2 max, float defaultRounding, in EdgeGlowStyle style)
    {
        if (IsDisposed)
        {
            return;
        }

        var strength = Math.Clamp(style.Strength, 0f, 1f);
        if (strength <= 0f)
        {
            return;
        }

        if (!HasVisibleOutput(style))
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var inset = MathF.Max(0f, style.BorderInset * scale);
        var drawMin = min + new Vector2(inset);
        var drawMax = max - new Vector2(inset);
        var size = drawMax - drawMin;
        if (size.X <= 2f || size.Y <= 2f)
        {
            return;
        }

        var renderScale = MathF.Min(ResolveRenderScale(size), Math.Clamp(style.RenderScale, 0.1f, 1f));
        var rounding = ResolveRounding(defaultRounding, style, size, scale);
        var outerRadii = ScaleCornerRadii(ResolveCornerRadii(rounding, style.CornerFlags), renderScale);
        var borderWidth = MathF.Max(0.5f, style.BorderWidth * scale * renderScale);
        var innerRadii = new CornerRadii(
            MathF.Max(0f, outerRadii.TopLeft - borderWidth),
            MathF.Max(0f, outerRadii.TopRight - borderWidth),
            MathF.Max(0f, outerRadii.BottomRight - borderWidth),
            MathF.Max(0f, outerRadii.BottomLeft - borderWidth));

        var renderSize = size * renderScale;
        var width = Math.Max(1, (int)MathF.Ceiling(renderSize.X));
        var height = Math.Max(1, (int)MathF.Ceiling(renderSize.Y));
        if (!TryEnsureDeviceResources() || !TryAcquireFramebufferSet(width, height, out var framebufferSet))
        {
            return;
        }

        var time = (float)ImGui.GetTime();
        var shaderScale = scale * renderScale;
        var request = CreateRenderRequest(
            width,
            height,
            outerRadii,
            innerRadii,
            time,
            strength,
            style,
            shaderScale);
        var drawList = ImGui.GetWindowDrawList();
        var clipPadding = style.ClipToRect
            ? MathF.Max(0f, style.ClipPadding * scale)
            : 0f;
        var renderJob = new EdgeGlowRenderJob
        {
            Renderer = this,
            Request = request,
            FramebufferSet = framebufferSet,
        };
        EdgeGlowStyle drawStyle = style;
        _renderJobs.QueueDraw(
            drawList,
            renderJob,
            () => DrawFramebufferSet(drawList, renderJob, drawMin, drawMax, rounding, drawStyle, clipPadding));
    }

    /// <summary> draws the edge glow around the current window border </summary>
    public void DrawWindowBorder(in EdgeGlowStyle style)
    {
        DrawRect(
            ImGui.GetWindowPos(),
            ImGui.GetWindowPos() + ImGui.GetWindowSize(),
            ImGui.GetStyle().WindowRounding,
            style);
    }

    protected override void DisposeManagedResources()
        => _renderJobs.Dispose();

    private static void DrawFramebufferSet(
        ImDrawListPtr drawList,
        EdgeGlowRenderJob renderJob,
        Vector2 drawMin,
        Vector2 drawMax,
        float rounding,
        in EdgeGlowStyle style,
        float clipPadding)
    {
        if (style.ClipToRect)
        {
            drawList.PushClipRect(drawMin - new Vector2(clipPadding), drawMax + new Vector2(clipPadding), false);
        }

        try
        {
            if (renderJob.Request.RenderBloom)
            {
                drawList.AddImageRounded(
                    new ImTextureID(renderJob.FramebufferSet.BlurOutputFramebuffer.ShaderResourceView.NativePointer),
                    drawMin,
                    drawMax,
                    Vector2.Zero,
                    Vector2.One,
                    0xFFFFFFFF,
                    rounding,
                    style.CornerFlags);
            }

            drawList.AddImageRounded(
                new ImTextureID(renderJob.FramebufferSet.SharpFramebuffer.ShaderResourceView.NativePointer),
                drawMin,
                drawMax,
                Vector2.Zero,
                Vector2.One,
                0xFFFFFFFF,
                rounding,
                style.CornerFlags);
        }
        finally
        {
            if (style.ClipToRect)
            {
                drawList.PopClipRect();
            }
        }
    }

    private static EdgeGlowRenderRequest CreateRenderRequest(
        int width,
        int height,
        in CornerRadii outerRadii,
        in CornerRadii innerRadii,
        float time,
        float strength,
        in EdgeGlowStyle style,
        float scale)
    {
        return style.Mode == EdgeGlowMode.Line
            ? CreateLineRenderRequest(width, height, outerRadii, innerRadii, time, strength, style, scale)
            : CreateFullBorderRenderRequest(width, height, outerRadii, innerRadii, time, strength, style, scale);
    }

    private static EdgeGlowRenderRequest CreateLineRenderRequest(
        int width,
        int height,
        in CornerRadii outerRadii,
        in CornerRadii innerRadii,
        float time,
        float strength,
        in EdgeGlowStyle style,
        float scale)
    {
        var resources = ResolveVariantResources(style.ColorVariant);
        var duration = ResolveDuration(style.Mode, style.Duration);
        var hueRange = MathF.Min(MathF.Abs(style.HueRange), 13f);
        var themeIsDark = style.Theme == EdgeGlowTheme.Dark;
        var horizontalFootprintScale = Math.Clamp(style.HorizontalFootprintScale, 0.25f, 6f);
        var beamX = SampleAnimation(time, duration, BeamTravelXFrames);
        var beamW = SampleAnimation(time, duration, BeamTravelWidthFrames);
        var beamEdge = SampleAnimation(time, duration, BeamEdgeFrames);
        var beamH = SampleAnimation(time, duration * 1.3f, BeamHeightFrames);
        var beamSpike = SampleAnimation(time, duration * 1.33f, BeamSpikeFrames);
        var beamSpike2 = SampleAnimation(time, duration * 1.7f, BeamSpike2Frames);
        var hueShift = style.StaticColors ? 0f : SampleHueShift(time, HueAnimationPeriod, hueRange);
        var bloomHueShift = style.StaticColors ? 0f : SampleHueShift(time, BloomHueAnimationPeriod, hueRange + 10f);
        var brightness = MathF.Max(0.1f, style.Brightness);
        var saturation = MathF.Max(0.1f, style.Saturation);
        var constants = CreateCommonConstants(
            width,
            height,
            outerRadii,
            innerRadii,
            style,
            scale,
            themeIsDark,
            NeedsColorTransform(hueShift, saturation, brightness),
            NeedsColorTransform(bloomHueShift, saturation, brightness));
        constants.BeamMotion0 = new Vector4(beamX, beamW, beamH, 1f);
        constants.BeamMotion1 = new Vector4(beamEdge, beamSpike, beamSpike2, strength);
        constants.ColorParams0 = new Vector4(
            hueShift,
            bloomHueShift,
            brightness,
            saturation);
        constants.LineParams = CreateLineParams(scale, horizontalFootprintScale);

        for (var index = 0; index < SpotCount; index++)
        {
            WriteLineSpot(ref constants, index, resources.LineSpots[index], scale, horizontalFootprintScale, isInner: false);
            WriteLineSpot(ref constants, index, resources.LineInnerSpots[index], scale, horizontalFootprintScale, isInner: true);
        }

        WriteBloomColors(ref constants, resources.LineBloomPalette);
        return new EdgeGlowRenderRequest(EdgeGlowMode.Line, constants, width, height, ShouldRenderBloom(style));
    }

    private static EdgeGlowRenderRequest CreateFullBorderRenderRequest(
        int width,
        int height,
        in CornerRadii outerRadii,
        in CornerRadii innerRadii,
        float time,
        float strength,
        in EdgeGlowStyle style,
        float scale)
    {
        var resources = ResolveVariantResources(style.ColorVariant);
        var duration = ResolveDuration(style.Mode, style.Duration);
        var hueRange = MathF.Abs(style.HueRange);
        var themeIsDark = style.Theme == EdgeGlowTheme.Dark;
        var angle01 = Wrap01(time / duration);
        var hueShift = style.StaticColors ? 0f : SampleHueShift(time, HueAnimationPeriod, hueRange);
        var fullBorderInnerReachScale = Math.Clamp(style.FullBorderInnerReachScale, 0.5f, 2.25f);
        var fullBorderSweepScale = Math.Clamp(style.FullBorderSweepScale, 0.5f, 3f);
        var brightness = MathF.Max(0.1f, style.Brightness);
        var saturation = MathF.Max(0.1f, style.Saturation);
        var constants = CreateCommonConstants(
            width,
            height,
            outerRadii,
            innerRadii,
            style,
            scale,
            themeIsDark,
            NeedsColorTransform(hueShift, saturation, brightness),
            false);
        constants.BeamMotion0 = new Vector4(angle01, 0f, 0f, 1f);
        constants.BeamMotion1 = new Vector4(0f, 0f, 0f, strength);
        constants.ColorParams0 = new Vector4(
            hueShift,
            0f,
            brightness,
            saturation);
        constants.FullBorderParams = CreateFullBorderParams(scale, fullBorderInnerReachScale, fullBorderSweepScale);

        var innerOpacity = FullBorderInnerSpotOpacity;
        for (var index = 0; index < SpotCount; index++)
        {
            WriteBorderSpot(ref constants, index, resources.BorderSpots[index], width, height, scale, isInner: false, innerOpacity);
            WriteBorderSpot(ref constants, index, resources.BorderSpots[index], width, height, scale, isInner: true, innerOpacity);
        }

        return new EdgeGlowRenderRequest(EdgeGlowMode.FullBorder, constants, width, height, ShouldRenderBloom(style));
    }

    private static GpuShaderBytecode CreateVertexShader(string shaderName, string entryPoint = "VSMain")
        => GpuShaderCompileService.CreateVertexShader(
            typeof(EdgeGlowRenderer),
            ShaderResourceName,
            shaderName,
            entryPoint);

    private static GpuShaderBytecode CreatePixelShader(string shaderName, string entryPoint)
        => GpuShaderCompileService.CreatePixelShader(
            typeof(EdgeGlowRenderer),
            ShaderResourceName,
            shaderName,
            entryPoint);

    private static EdgeGlowConstants CreateCommonConstants(
        int width,
        int height,
        in CornerRadii outerRadii,
        in CornerRadii innerRadii,
        in EdgeGlowStyle style,
        float scale,
        bool themeIsDark,
        bool applyBaseColorTransform,
        bool applyBloomColorTransform)
    {
        var borderWidthPx = MathF.Max(1f, style.BorderWidth * scale);
        return new EdgeGlowConstants
        {
            TextureSize = new Vector2(width, height),
            OuterRadii = new Vector4(outerRadii.TopLeft, outerRadii.TopRight, outerRadii.BottomRight, outerRadii.BottomLeft),
            InnerRadii = new Vector4(innerRadii.TopLeft, innerRadii.TopRight, innerRadii.BottomRight, innerRadii.BottomLeft),
            ColorParams1 = new Vector4(
                Math.Clamp(style.StrokeOpacity, 0f, 1f),
                Math.Clamp(style.InnerOpacity, 0f, 1f),
                Math.Clamp(style.BloomOpacity, 0f, 1f),
                Math.Clamp(style.InnerShadowAlpha, 0f, 1f)),
            LayoutParams0 = new Vector4(borderWidthPx, scale, 0f, 0f),
            StateParams = new Vector4(
                themeIsDark ? 1f : 0f,
                applyBaseColorTransform ? 1f : 0f,
                applyBloomColorTransform ? 1f : 0f,
                0f),
        };
    }

    private static float ResolveDuration(EdgeGlowMode mode, float styleDuration)
    {
        if (styleDuration > 0f)
        {
            return MathF.Max(0.1f, styleDuration);
        }

        return mode == EdgeGlowMode.Line
            ? DefaultLineDuration
            : DefaultFullBorderDuration;
    }

    private static Vector4 CreateLineParams(float scale, float horizontalFootprintScale)
        => new(28f * scale, 9f * scale, horizontalFootprintScale, 0f);

    private static Vector4 CreateFullBorderParams(float scale, float fullBorderInnerReachScale, float fullBorderSweepScale)
        => new(28f * scale, 9f * scale, fullBorderInnerReachScale, fullBorderSweepScale);

    private static bool NeedsColorTransform(float hueShift, float saturation, float brightness)
        => MathF.Abs(hueShift) > VisibilityEpsilon
            || MathF.Abs(saturation - 1f) > VisibilityEpsilon
            || MathF.Abs(brightness - 1f) > VisibilityEpsilon;

    private static bool HasVisibleOutput(in EdgeGlowStyle style)
        => style.StrokeOpacity > VisibilityEpsilon
            || style.InnerOpacity > VisibilityEpsilon
            || style.BloomOpacity > VisibilityEpsilon;

    private static bool ShouldRenderBloom(in EdgeGlowStyle style)
        => style.BloomOpacity > VisibilityEpsilon;

    private static void ProcessRenderJob(EdgeGlowRenderJob renderJob)
        => renderJob.Renderer.ProcessRender(renderJob);

    private void ProcessRender(EdgeGlowRenderJob renderJob)
    {
        var request = renderJob.Request;
        var framebufferSet = renderJob.FramebufferSet;
        if (ActiveContext is null
            || _fullscreenQuad is null
            || _constantBuffer is null
            || _blurConstantBuffer is null
            || _samplerState is null
            || _rasterizerState is null
            || _depthStencilState is null
            || _blendState is null
            || framebufferSet.Width != request.Width
            || framebufferSet.Height != request.Height
            || !TryResolveModeShaders(request.Mode, out var sharpShader, out var bloomShader))
        {
            return;
        }

        try
        {
            using var state = D3D11DrawStateScope.Capture(
                ActiveContext,
                pixelConstantBufferCount: 2,
                pixelShaderResourceViewCount: 1);
            ConfigureFullscreenPipeline();
            RenderEffectPass(request, sharpShader, framebufferSet.SharpFramebuffer);

            if (request.RenderBloom)
            {
                RenderEffectPass(request, bloomShader, framebufferSet.BloomSourceFramebuffer);
                RenderBlurPass(framebufferSet.BlurScratchFramebuffer, framebufferSet.BloomSourceFramebuffer.ShaderResourceView, _downsampleShader!, BloomBlurOffset);
                RenderBlurPass(framebufferSet.BlurOutputFramebuffer, framebufferSet.BlurScratchFramebuffer.ShaderResourceView, _upsampleShader!, BloomBlurOffset);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "edge glow render pass failed");
            RequestDeviceReset();
        }
    }

    private bool TryResolveModeShaders(EdgeGlowMode mode, out PixelShader sharpShader, out PixelShader bloomShader)
    {
        (sharpShader, bloomShader) = mode switch
        {
            EdgeGlowMode.Line => (_linePixelShader!, _lineBloomPixelShader!),
            EdgeGlowMode.FullBorder => (_fullBorderPixelShader!, _fullBorderBloomPixelShader!),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unsupported edge glow mode"),
        };

        return sharpShader is not null && bloomShader is not null;
    }

    private void RenderEffectPass(in EdgeGlowRenderRequest request, PixelShader shader, EdgeGlowFramebuffer framebuffer)
    {
        if (ActiveContext is null || _constantBuffer is null)
        {
            return;
        }

        var dataBox = ActiveContext.MapSubresource(_constantBuffer, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
        try
        {
            *(EdgeGlowConstants*)dataBox.DataPointer = request.Constants;
        }
        finally
        {
            ActiveContext.UnmapSubresource(_constantBuffer, 0);
        }

        ActiveContext.OutputMerger.SetTargets(framebuffer.RenderTargetView);
        ActiveContext.Rasterizer.SetViewport(0f, 0f, request.Width, request.Height);
        ActiveContext.ClearRenderTargetView(framebuffer.RenderTargetView, new SharpDX.Color4(0f, 0f, 0f, 0f));
        ActiveContext.PixelShader.Set(shader);
        ActiveContext.PixelShader.SetConstantBuffer(0, _constantBuffer);
        ActiveContext.PixelShader.SetConstantBuffer(1, null);
        ActiveContext.PixelShader.SetShaderResource(0, null);
        ActiveContext.PixelShader.SetSampler(0, null);
        ActiveContext.Draw(4, 0);
    }

    private void ConfigureFullscreenPipeline()
    {
        if (ActiveContext is null
            || _fullscreenQuad is null
            || _blendState is null
            || _depthStencilState is null
            || _rasterizerState is null)
        {
            return;
        }

        _fullscreenQuad.Apply(ActiveContext);
        ActiveContext.OutputMerger.BlendState = _blendState;
        ActiveContext.OutputMerger.SetDepthStencilState(_depthStencilState, 0);
        ActiveContext.Rasterizer.State = _rasterizerState;
    }

    private void RenderBlurPass(EdgeGlowFramebuffer framebuffer, ShaderResourceView inputView, PixelShader shader, float blurOffset)
    {
        if (ActiveContext is null
            || _blurConstantBuffer is null
            || _samplerState is null)
        {
            return;
        }

        var dataBox = ActiveContext.MapSubresource(_blurConstantBuffer, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
        try
        {
            var blurConstants = new EdgeGlowBlurConstants
            {
                HalfPixel = new Vector2(0.5f / framebuffer.Width, 0.5f / framebuffer.Height),
                Offset = blurOffset,
            };
            *(EdgeGlowBlurConstants*)dataBox.DataPointer = blurConstants;
        }
        finally
        {
            ActiveContext.UnmapSubresource(_blurConstantBuffer, 0);
        }

        ActiveContext.OutputMerger.SetTargets(framebuffer.RenderTargetView);
        ActiveContext.Rasterizer.SetViewport(0f, 0f, framebuffer.Width, framebuffer.Height);
        ActiveContext.ClearRenderTargetView(framebuffer.RenderTargetView, new SharpDX.Color4(0f, 0f, 0f, 0f));
        ActiveContext.PixelShader.Set(shader);
        ActiveContext.PixelShader.SetConstantBuffer(0, null);
        ActiveContext.PixelShader.SetConstantBuffer(1, _blurConstantBuffer);
        ActiveContext.PixelShader.SetShaderResource(0, inputView);
        ActiveContext.PixelShader.SetSampler(0, _samplerState);
        ActiveContext.Draw(4, 0);
        ActiveContext.PixelShader.SetShaderResource(0, null);
    }

    private bool TryEnsureDeviceResources()
        => TryEnsureDevice(out _);

    protected override void CreateDeviceResources(Device device, DeviceContext context)
    {
        _fullscreenQuad = new GpuFullscreenQuad(device, VertexShader);
        _linePixelShader = LinePixelShader.CreatePixelShader(device);
        _lineBloomPixelShader = LineBloomPixelShader.CreatePixelShader(device);
        _fullBorderPixelShader = FullBorderPixelShader.CreatePixelShader(device);
        _fullBorderBloomPixelShader = FullBorderBloomPixelShader.CreatePixelShader(device);
        _downsampleShader = DownsampleShader.CreatePixelShader(device);
        _upsampleShader = UpsampleShader.CreatePixelShader(device);
        _constantBuffer = new Buffer(
            device,
            Marshal.SizeOf<EdgeGlowConstants>(),
            ResourceUsage.Dynamic,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0);

        _blurConstantBuffer = new Buffer(
            device,
            Marshal.SizeOf<EdgeGlowBlurConstants>(),
            ResourceUsage.Dynamic,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0);

        var samplerDescription = new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never,
            MaximumLod = float.MaxValue,
        };
        _samplerState = new SamplerState(device, samplerDescription);

        _rasterizerState = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = false,
            IsScissorEnabled = false,
            IsFrontCounterClockwise = false,
            IsMultisampleEnabled = false,
        });

        _depthStencilState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.Always,
            IsStencilEnabled = false,
        });

        var blendStateDescription = BlendStateDescription.Default();
        blendStateDescription.RenderTarget[0].IsBlendEnabled = false;
        _blendState = new BlendState(device, blendStateDescription);
    }

    protected override void DisposeDeviceResources()
    {
        DisposeFramebufferPool();
        _samplerState?.Dispose();
        _samplerState = null;
        _blendState?.Dispose();
        _blendState = null;
        _depthStencilState?.Dispose();
        _depthStencilState = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
        _blurConstantBuffer?.Dispose();
        _blurConstantBuffer = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _fullscreenQuad?.Dispose();
        _fullscreenQuad = null;
        _upsampleShader?.Dispose();
        _upsampleShader = null;
        _downsampleShader?.Dispose();
        _downsampleShader = null;
        _fullBorderBloomPixelShader?.Dispose();
        _fullBorderBloomPixelShader = null;
        _fullBorderPixelShader?.Dispose();
        _fullBorderPixelShader = null;
        _lineBloomPixelShader?.Dispose();
        _lineBloomPixelShader = null;
        _linePixelShader?.Dispose();
        _linePixelShader = null;
    }

    private static unsafe void WriteLineSpot(
        ref EdgeGlowConstants constants,
        int spotIndex,
        GradientEllipse spot,
        float scale,
        float horizontalFootprintScale,
        bool isInner)
    {
        var baseIndex = spotIndex * 4;
        var offsetX = spot.Offset.X * scale * horizontalFootprintScale;
        var offsetY = spot.Offset.Y * scale;
        var sizeX = spot.Size.X * scale * horizontalFootprintScale;
        var sizeY = spot.Size.Y * scale;
        WriteSpot(
            ref constants,
            baseIndex,
            offsetX,
            offsetY,
            sizeX,
            sizeY,
            spot.Color,
            isInner);
    }

    private static unsafe void WriteBorderSpot(
        ref EdgeGlowConstants constants,
        int spotIndex,
        BorderGradientSpot spot,
        int width,
        int height,
        float scale,
        bool isInner,
        float innerOpacity)
    {
        var baseIndex = spotIndex * 4;
        var centerX = width * spot.Position.X;
        var centerY = height * spot.Position.Y;
        var size = isInner
            ? spot.Size * 0.9f * scale
            : spot.Size * scale;
        var color = isInner
            ? WithAlpha(spot.Color, innerOpacity)
            : spot.Color;
        WriteSpot(
            ref constants,
            baseIndex,
            centerX,
            centerY,
            size.X,
            size.Y,
            color,
            isInner);
    }

    private static unsafe void WriteSpot(
        ref EdgeGlowConstants constants,
        int baseIndex,
        float x,
        float y,
        float width,
        float height,
        Vector4 color,
        bool isInner)
    {
        if (isInner)
        {
            constants.InnerSpots[baseIndex] = x;
            constants.InnerSpots[baseIndex + 1] = y;
            constants.InnerSpots[baseIndex + 2] = width;
            constants.InnerSpots[baseIndex + 3] = height;

            constants.InnerColors[baseIndex] = color.X;
            constants.InnerColors[baseIndex + 1] = color.Y;
            constants.InnerColors[baseIndex + 2] = color.Z;
            constants.InnerColors[baseIndex + 3] = color.W;
            return;
        }

        constants.StrokeSpots[baseIndex] = x;
        constants.StrokeSpots[baseIndex + 1] = y;
        constants.StrokeSpots[baseIndex + 2] = width;
        constants.StrokeSpots[baseIndex + 3] = height;

        constants.StrokeColors[baseIndex] = color.X;
        constants.StrokeColors[baseIndex + 1] = color.Y;
        constants.StrokeColors[baseIndex + 2] = color.Z;
        constants.StrokeColors[baseIndex + 3] = color.W;
    }

    private static unsafe void WriteBloomColors(ref EdgeGlowConstants constants, BloomPalette bloomPalette)
    {
        WriteBloomColor(ref constants, 0, bloomPalette.Primary);
        WriteBloomColor(ref constants, 1, bloomPalette.Primary);
        WriteBloomColor(ref constants, 2, bloomPalette.Secondary);
        WriteBloomColor(ref constants, 3, WithAlpha(bloomPalette.Secondary, 0.49f));

        for (var index = 0; index < bloomPalette.Spikes.Length; index++)
        {
            var pair = bloomPalette.Spikes[index];
            WriteBloomColor(ref constants, 4 + index, pair.Color1);
            WriteBloomColor(ref constants, 9 + index, pair.Color2);
        }
    }

    private static unsafe void WriteBloomColor(ref EdgeGlowConstants constants, int colorIndex, Vector4 color)
    {
        var baseIndex = colorIndex * 4;
        constants.BloomColors[baseIndex] = color.X;
        constants.BloomColors[baseIndex + 1] = color.Y;
        constants.BloomColors[baseIndex + 2] = color.Z;
        constants.BloomColors[baseIndex + 3] = color.W;
    }

    private static Vector4 WithAlpha(Vector4 color, float alpha)
        => new(color.X, color.Y, color.Z, alpha);

    private static float SampleAnimation(float time, float period, AnimationKeyframe[] keyframes)
        => SampleKeyframes(keyframes, Wrap01(time / MathF.Max(0.01f, period)));

    private static float SampleKeyframes(AnimationKeyframe[] keyframes, float t)
    {
        for (var index = 1; index < keyframes.Length; index++)
        {
            if (t <= keyframes[index].Time)
            {
                var previous = keyframes[index - 1];
                var next = keyframes[index];
                var range = MathF.Max(0.0001f, next.Time - previous.Time);
                var amount = Math.Clamp((t - previous.Time) / range, 0f, 1f);
                return previous.Value + ((next.Value - previous.Value) * amount);
            }
        }

        return keyframes[^1].Value;
    }

    private static float SampleHueShift(float time, float period, float hueRange)
        => -MathF.Cos(Wrap01(time / MathF.Max(0.01f, period)) * MathF.Tau) * hueRange;

    private static Vector4 Color(float r, float g, float b, float a = 1f)
        => new(r / 255f, g / 255f, b / 255f, a);

    private static float ResolveRenderScale(Vector2 size)
    {
        var largestDimension = MathF.Max(size.X, size.Y);
        var pixelCount = MathF.Max(1f, size.X * size.Y);
        var dimensionScale = largestDimension > MaxAdaptiveRenderDimension
            ? MaxAdaptiveRenderDimension / largestDimension
            : 1f;
        var pixelScale = pixelCount > MaxAdaptiveRenderPixels
            ? MathF.Sqrt(MaxAdaptiveRenderPixels / pixelCount)
            : 1f;
        var renderScale = MathF.Min(1f, MathF.Min(dimensionScale, pixelScale));
        return renderScale < 1f
            ? Math.Clamp(renderScale, MinAdaptiveRenderScale, 1f)
            : 1f;
    }

    private static float ResolveRounding(float defaultRounding, in EdgeGlowStyle style, Vector2 size, float scale)
    {
        var rounding = style.CornerRounding.HasValue
            ? style.CornerRounding.Value * scale
            : defaultRounding;
        return Math.Clamp(rounding, 0f, MathF.Min(size.X, size.Y) * 0.5f);
    }

    private static CornerRadii ScaleCornerRadii(in CornerRadii radii, float scale)
    {
        return new CornerRadii(
            radii.TopLeft * scale,
            radii.TopRight * scale,
            radii.BottomRight * scale,
            radii.BottomLeft * scale);
    }

    private static CornerRadii ResolveCornerRadii(float rounding, ImDrawFlags cornerFlags)
    {
        return new CornerRadii(
            HasCorner(cornerFlags, ImDrawFlags.RoundCornersTopLeft) ? rounding : 0f,
            HasCorner(cornerFlags, ImDrawFlags.RoundCornersTopRight) ? rounding : 0f,
            HasCorner(cornerFlags, ImDrawFlags.RoundCornersBottomRight) ? rounding : 0f,
            HasCorner(cornerFlags, ImDrawFlags.RoundCornersBottomLeft) ? rounding : 0f);
    }

    private static bool HasCorner(ImDrawFlags flags, ImDrawFlags corner)
        => (flags & corner) != ImDrawFlags.None;

    private static float Wrap01(float value)
    {
        var wrapped = value - MathF.Floor(value);
        return wrapped < 0f ? wrapped + 1f : wrapped;
    }

    private readonly record struct AnimationKeyframe(float Time, float Value);

    private readonly record struct GradientEllipse(Vector2 Offset, Vector2 Size, Vector4 Color);

    private readonly record struct BorderGradientSpot(Vector2 Position, Vector2 Size, Vector4 Color);

    private readonly record struct BloomColorPair(Vector4 Color1, Vector4 Color2);

    private readonly record struct BloomPalette(Vector4 Primary, Vector4 Secondary, BloomColorPair[] Spikes);

    private readonly record struct BeamVariantResources(
        GradientEllipse[] LineSpots,
        GradientEllipse[] LineInnerSpots,
        BloomPalette LineBloomPalette,
        BorderGradientSpot[] BorderSpots);

    private readonly record struct CornerRadii(float TopLeft, float TopRight, float BottomRight, float BottomLeft);

    private readonly record struct EdgeGlowRenderRequest(EdgeGlowMode Mode, EdgeGlowConstants Constants, int Width, int Height, bool RenderBloom);

    private sealed class EdgeGlowRenderJob : IDisposable
    {
        private bool _disposed;

        public required EdgeGlowRenderer Renderer { get; init; }
        public required EdgeGlowRenderRequest Request { get; init; }
        public required EdgeGlowFramebufferSet FramebufferSet { get; init; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Renderer.ReleaseFramebufferSet(FramebufferSet);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct EdgeGlowConstants
    {
        public Vector2 TextureSize;
        private Vector2 _padding0;
        public Vector4 OuterRadii;
        public Vector4 InnerRadii;
        public Vector4 BeamMotion0;
        public Vector4 BeamMotion1;
        public Vector4 ColorParams0;
        public Vector4 ColorParams1;
        public Vector4 LayoutParams0;
        public Vector4 StateParams;
        public Vector4 LineParams;
        public Vector4 FullBorderParams;
        public fixed float StrokeSpots[SpotCount * 4];
        public fixed float StrokeColors[SpotCount * 4];
        public fixed float InnerSpots[SpotCount * 4];
        public fixed float InnerColors[SpotCount * 4];
        public fixed float BloomColors[14 * 4];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EdgeGlowBlurConstants
    {
        public Vector2 HalfPixel;
        public float Offset;
        private float _padding0;
    }

}
