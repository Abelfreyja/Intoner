using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
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

internal sealed unsafe partial class EdgeGlowRenderer : IDisposable
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

    private static readonly Lazy<byte[]> VertexShaderBytecode = CreateVertexShaderBytecodeLazy("edge glow vertex shader");
    private static readonly Lazy<byte[]> LinePixelShaderBytecode = CreatePixelShaderBytecodeLazy("edge glow line pixel shader", "PSLineMain");
    private static readonly Lazy<byte[]> LineBloomPixelShaderBytecode = CreatePixelShaderBytecodeLazy("edge glow line bloom pixel shader", "PSLineBloomOnly");
    private static readonly Lazy<byte[]> FullBorderPixelShaderBytecode = CreatePixelShaderBytecodeLazy("edge glow full border pixel shader", "PSFullMain");
    private static readonly Lazy<byte[]> FullBorderBloomPixelShaderBytecode = CreatePixelShaderBytecodeLazy("edge glow full border bloom pixel shader", "PSFullBloomOnly");
    private static readonly Lazy<byte[]> DownsampleShaderBytecode = CreatePixelShaderBytecodeLazy("edge glow downsample shader", "PSDownsample");
    private static readonly Lazy<byte[]> UpsampleShaderBytecode = CreatePixelShaderBytecodeLazy("edge glow upsample shader", "PSUpsample");

    private static readonly ImDrawCallback RenderCallback = ProcessRenderCallback;
    private static readonly ImDrawCallback ReleaseCallback = ProcessReleaseCallback;

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
    private readonly IUiBuilder _uiBuilder;

    private Device? _device;
    private DeviceContext? _context;
    private VertexShader? _vertexShader;
    private PixelShader? _linePixelShader;
    private PixelShader? _lineBloomPixelShader;
    private PixelShader? _fullBorderPixelShader;
    private PixelShader? _fullBorderBloomPixelShader;
    private PixelShader? _downsampleShader;
    private PixelShader? _upsampleShader;
    private InputLayout? _inputLayout;
    private Buffer? _vertexBuffer;
    private Buffer? _constantBuffer;
    private Buffer? _blurConstantBuffer;
    private SamplerState? _samplerState;
    private RasterizerState? _rasterizerState;
    private DepthStencilState? _depthStencilState;
    private BlendState? _blendState;
    private bool _disposed;
    private bool _loggedInitializationFailure;

    public EdgeGlowRenderer(ILogger<EdgeGlowRenderer> logger, IUiBuilder uiBuilder)
    {
        _logger = logger;
        _uiBuilder = uiBuilder;
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
        if (_disposed)
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
        var handle = GCHandle.Alloc(renderJob);
        var handlePtr = (void*)GCHandle.ToIntPtr(handle);

        drawList.AddCallback(RenderCallback, handlePtr);
        if (style.ClipToRect)
        {
            drawList.PushClipRect(drawMin - new Vector2(clipPadding), drawMax + new Vector2(clipPadding), false);
        }

        if (request.RenderBloom)
        {
            drawList.AddImageRounded(
                new ImTextureID(framebufferSet.BlurOutputFramebuffer.ShaderResourceView.NativePointer),
                drawMin,
                drawMax,
                Vector2.Zero,
                Vector2.One,
                0xFFFFFFFF,
                rounding,
                style.CornerFlags);
        }

        drawList.AddImageRounded(
            new ImTextureID(framebufferSet.SharpFramebuffer.ShaderResourceView.NativePointer),
            drawMin,
            drawMax,
            Vector2.Zero,
            Vector2.One,
            0xFFFFFFFF,
            rounding,
            style.CornerFlags);

        if (style.ClipToRect)
        {
            drawList.PopClipRect();
        }

        drawList.AddCallback(ReleaseCallback, handlePtr);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeRuntimeResources();
    }

    private static unsafe void ProcessRenderCallback(ImDrawList* _, ImDrawCmd* cmd)
    {
        var handle = GCHandle.FromIntPtr((nint)cmd->UserCallbackData);
        if (handle.Target is EdgeGlowRenderJob renderJob)
        {
            renderJob.Renderer.ProcessRender(renderJob);
        }
    }

    private static unsafe void ProcessReleaseCallback(ImDrawList* _, ImDrawCmd* cmd)
    {
        var handle = GCHandle.FromIntPtr((nint)cmd->UserCallbackData);
        try
        {
            if (handle.Target is EdgeGlowRenderJob renderJob)
            {
                renderJob.Renderer.ReleaseFramebufferSet(renderJob.FramebufferSet);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private EdgeGlowRenderRequest CreateRenderRequest(
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

    private EdgeGlowRenderRequest CreateLineRenderRequest(
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

    private EdgeGlowRenderRequest CreateFullBorderRenderRequest(
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

    private static Lazy<byte[]> CreateVertexShaderBytecodeLazy(string shaderName, string entryPoint = "VSMain")
        => new(() => GpuShaderCompileService.CreateVertexShaderBytecode(
            typeof(EdgeGlowRenderer),
            ShaderResourceName,
            shaderName,
            entryPoint));

    private static Lazy<byte[]> CreatePixelShaderBytecodeLazy(string shaderName, string entryPoint)
        => new(() => GpuShaderCompileService.CreatePixelShaderBytecode(
            typeof(EdgeGlowRenderer),
            ShaderResourceName,
            shaderName,
            entryPoint));

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

    private void ProcessRender(EdgeGlowRenderJob renderJob)
    {
        var request = renderJob.Request;
        var framebufferSet = renderJob.FramebufferSet;
        if (_context is null
            || _vertexShader is null
            || _inputLayout is null
            || _vertexBuffer is null
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
            using var state = D3D11RenderStateSnapshot.Capture(
                _context,
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
            DisposeRuntimeResources();
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
        if (_context is null || _constantBuffer is null)
        {
            return;
        }

        var dataBox = _context.MapSubresource(_constantBuffer, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
        try
        {
            *(EdgeGlowConstants*)dataBox.DataPointer = request.Constants;
        }
        finally
        {
            _context.UnmapSubresource(_constantBuffer, 0);
        }

        _context.OutputMerger.SetTargets(framebuffer.RenderTargetView);
        _context.Rasterizer.SetViewport(0f, 0f, request.Width, request.Height);
        _context.ClearRenderTargetView(framebuffer.RenderTargetView, new SharpDX.Color4(0f, 0f, 0f, 0f));
        _context.PixelShader.Set(shader);
        _context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        _context.PixelShader.SetConstantBuffer(1, null);
        _context.PixelShader.SetShaderResource(0, null);
        _context.PixelShader.SetSampler(0, null);
        _context.Draw(4, 0);
    }

    private void ConfigureFullscreenPipeline()
    {
        if (_context is null
            || _inputLayout is null
            || _vertexBuffer is null
            || _vertexShader is null
            || _blendState is null
            || _depthStencilState is null
            || _rasterizerState is null)
        {
            return;
        }

        _context.InputAssembler.InputLayout = _inputLayout;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vertexBuffer, Marshal.SizeOf<FullscreenVertex>(), 0));
        _context.VertexShader.Set(_vertexShader);
        _context.OutputMerger.BlendState = _blendState;
        _context.OutputMerger.SetDepthStencilState(_depthStencilState, 0);
        _context.Rasterizer.State = _rasterizerState;
    }

    private void RenderBlurPass(EdgeGlowFramebuffer framebuffer, ShaderResourceView inputView, PixelShader shader, float blurOffset)
    {
        if (_context is null
            || _blurConstantBuffer is null
            || _samplerState is null)
        {
            return;
        }

        var dataBox = _context.MapSubresource(_blurConstantBuffer, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
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
            _context.UnmapSubresource(_blurConstantBuffer, 0);
        }

        _context.OutputMerger.SetTargets(framebuffer.RenderTargetView);
        _context.Rasterizer.SetViewport(0f, 0f, framebuffer.Width, framebuffer.Height);
        _context.ClearRenderTargetView(framebuffer.RenderTargetView, new SharpDX.Color4(0f, 0f, 0f, 0f));
        _context.PixelShader.Set(shader);
        _context.PixelShader.SetConstantBuffer(0, null);
        _context.PixelShader.SetConstantBuffer(1, _blurConstantBuffer);
        _context.PixelShader.SetShaderResource(0, inputView);
        _context.PixelShader.SetSampler(0, _samplerState);
        _context.Draw(4, 0);
        _context.PixelShader.SetShaderResource(0, null);
    }

    private bool TryEnsureDeviceResources()
    {
        if (_device is not null && _context is not null)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var deviceHandle = _uiBuilder.DeviceHandle;
        if (deviceHandle == nint.Zero)
        {
            return false;
        }

        Device? device = null;
        DeviceContext? context = null;
        try
        {
            Marshal.AddRef(deviceHandle);
            device = new Device(deviceHandle);
            context = device.ImmediateContext;

            _vertexShader = new VertexShader(device, VertexShaderBytecode.Value);
            _linePixelShader = new PixelShader(device, LinePixelShaderBytecode.Value);
            _lineBloomPixelShader = new PixelShader(device, LineBloomPixelShaderBytecode.Value);
            _fullBorderPixelShader = new PixelShader(device, FullBorderPixelShaderBytecode.Value);
            _fullBorderBloomPixelShader = new PixelShader(device, FullBorderBloomPixelShaderBytecode.Value);
            _downsampleShader = new PixelShader(device, DownsampleShaderBytecode.Value);
            _upsampleShader = new PixelShader(device, UpsampleShaderBytecode.Value);
            _inputLayout = new InputLayout(
                device,
                ShaderSignature.GetInputSignature(VertexShaderBytecode.Value),
                [
                    new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0),
                    new InputElement("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                ]);

            FullscreenVertex[] vertices =
            [
                new(new Vector2(-1f, 1f), new Vector2(0f, 0f)),
                new(new Vector2(1f, 1f), new Vector2(1f, 0f)),
                new(new Vector2(-1f, -1f), new Vector2(0f, 1f)),
                new(new Vector2(1f, -1f), new Vector2(1f, 1f)),
            ];

            _vertexBuffer = Buffer.Create(device, BindFlags.VertexBuffer, vertices);
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

            _device = device;
            _context = context;
            _loggedInitializationFailure = false;
            return true;
        }
        catch (Exception ex)
        {
            context?.Dispose();
            device?.Dispose();
            DisposeRuntimeResources();

            if (!_loggedInitializationFailure)
            {
                _loggedInitializationFailure = true;
                _logger.LogWarning(ex, "edge glow renderer initialization failed");
            }

            return false;
        }
    }

    private void DisposeRuntimeResources()
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
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
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
        _vertexShader?.Dispose();
        _vertexShader = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
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
        => (flags & corner) != 0;

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

    private sealed class EdgeGlowRenderJob
    {
        public required EdgeGlowRenderer Renderer { get; init; }
        public required EdgeGlowRenderRequest Request { get; init; }
        public required EdgeGlowFramebufferSet FramebufferSet { get; init; }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FullscreenVertex
    {
        public Vector2 Position;
        public Vector2 Uv;

        public FullscreenVertex(Vector2 position, Vector2 uv)
        {
            Position = position;
            Uv = uv;
        }
    }

}

