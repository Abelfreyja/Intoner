using SharpDX.Direct3D11;

namespace Intoner.Services.Gpu;

internal static class GpuRenderStateUtility
{
    public static BlendState CreateAlphaBlendState(Device device)
    {
        BlendStateDescription description = BlendStateDescription.Default();
        description.RenderTarget[0].IsBlendEnabled = true;
        description.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
        description.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        description.RenderTarget[0].BlendOperation = BlendOperation.Add;
        description.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
        description.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        description.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
        description.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        return new BlendState(device, description);
    }
}
