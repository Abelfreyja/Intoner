using Intoner.Services.Gpu;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Services.Backdrop;

internal readonly struct SplashScreenBannerRequest
{
    public SplashScreenBannerRequest(BackdropRenderer.BackdropRegion region, SplashScreenBannerEffect.Style style)
    {
        Region = region;
        TimeParams = style.PackTimeParams();
        ColorParams = style.PackColorParams();
        FlowParams = style.PackFlowParams();
    }

    public BackdropRenderer.BackdropRegion Region { get; }
    public Vector4 TimeParams { get; }
    public Vector4 ColorParams { get; }
    public Vector4 FlowParams { get; }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct SplashScreenBannerConstants
{
    public SplashScreenBannerConstants(Vector4 timeParams, Vector4 colorParams, Vector4 flowParams)
    {
        TimeParams = timeParams;
        ColorParams = colorParams;
        FlowParams = flowParams;
    }

    public Vector4 TimeParams { get; }
    public Vector4 ColorParams { get; }
    public Vector4 FlowParams { get; }
}

/// <summary> draws the splash screen banner shader </summary>
internal sealed class SplashScreenBannerEffect
    : ShaderBackdropEffectBase<SplashScreenBannerEffect.Style, SplashScreenBannerRequest, SplashScreenBannerConstants>
{
    private const string ShaderResourceName = "Objects.UI.Shaders.Window.ObjectSplashScreenBanner.hlsl";

    private static readonly Lazy<byte[]> SplashScreenBannerShaderBytecode = new(
        () => GpuShaderCompileService.CreatePixelShaderBytecode(
            typeof(BackdropRenderer),
            ShaderResourceName,
            "object splash screen banner shader",
            "PSSplashScreenBanner"));

    internal readonly struct Style
    {
        public Style()
        {
            TimeSeconds = 0f;
            Rate1 = 1.9f;
            Rate2 = 0.6f;
            LoopCycle = 85f;
            Color1 = 0.45f;
            Color2 = 1.0f;
            Cycle1 = 1.3f;
            Cycle2 = 0.5f;
            Nudge = 0.01f;
            DepthX = 0.66f;
            DepthY = 0.46f;
            Opacity = 0.92f;
        }

        public float TimeSeconds { get; init; }
        public float Rate1 { get; init; }
        public float Rate2 { get; init; }
        public float LoopCycle { get; init; }
        public float Color1 { get; init; }
        public float Color2 { get; init; }
        public float Cycle1 { get; init; }
        public float Cycle2 { get; init; }
        public float Nudge { get; init; }
        public float DepthX { get; init; }
        public float DepthY { get; init; }
        public float Opacity { get; init; }

        public readonly Vector4 PackTimeParams()
            => new(TimeSeconds, Rate1, Rate2, LoopCycle);

        public readonly Vector4 PackColorParams()
            => new(Color1, Color2, Cycle1, Cycle2);

        public readonly Vector4 PackFlowParams()
            => new(Nudge, DepthX, DepthY, Opacity);
    }

    public SplashScreenBannerEffect(BackdropRenderer renderer)
        : base(renderer)
    { }

    protected override Lazy<byte[]> ShaderBytecode => SplashScreenBannerShaderBytecode;
    protected override string FailureLogMessage => "splash screen banner shader processing failed";

    protected override SplashScreenBannerRequest CreateRequest(in BackdropRenderer.BackdropRegion region, Style style)
        => new(region, style);

    protected override BackdropRenderer.BackdropRegion GetRegion(in SplashScreenBannerRequest request)
        => request.Region;

    protected override SplashScreenBannerConstants CreateEffectConstants(in SplashScreenBannerRequest request)
        => new(request.TimeParams, request.ColorParams, request.FlowParams);
}

