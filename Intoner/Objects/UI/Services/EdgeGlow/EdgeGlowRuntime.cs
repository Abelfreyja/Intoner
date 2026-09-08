using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Diagnostics.CodeAnalysis;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.UI.Services.EdgeGlow;

internal sealed unsafe partial class EdgeGlowRenderer
{
    private readonly List<EdgeGlowFramebufferSet> _availableFramebufferSets = [];
    private int _framebufferGeneration;

    private bool TryAcquireFramebufferSet(int width, int height, [NotNullWhen(true)] out EdgeGlowFramebufferSet? framebufferSet)
    {
        framebufferSet = null;
        Device? device = ActiveDevice;
        if (device is null)
        {
            return false;
        }

        var blurWidth = Math.Max(1, (int)MathF.Round(width * BloomBlurScale));
        var blurHeight = Math.Max(1, (int)MathF.Round(height * BloomBlurScale));

        for (var index = _availableFramebufferSets.Count - 1; index >= 0; index--)
        {
            var candidate = _availableFramebufferSets[index];
            if (candidate.Width != width
                || candidate.Height != height
                || candidate.BlurWidth != blurWidth
                || candidate.BlurHeight != blurHeight)
            {
                continue;
            }

            _availableFramebufferSets.RemoveAt(index);
            framebufferSet = candidate;
            return true;
        }

        try
        {
            framebufferSet = CreateFramebufferSet(device, width, height, blurWidth, blurHeight);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "edge glow framebuffer allocation failed");
            ClearDeviceResources();
            return false;
        }
    }

    private void ReleaseFramebufferSet(EdgeGlowFramebufferSet framebufferSet)
    {
        if (IsDisposed || framebufferSet.Generation != _framebufferGeneration || ActiveDevice is null || ActiveContext is null)
        {
            framebufferSet.Dispose();
            return;
        }

        _availableFramebufferSets.Add(framebufferSet);
    }

    private void DisposeFramebufferPool()
    {
        _framebufferGeneration++;
        foreach (var framebufferSet in _availableFramebufferSets)
        {
            framebufferSet.Dispose();
        }

        _availableFramebufferSets.Clear();
    }

    private EdgeGlowFramebufferSet CreateFramebufferSet(
        Device device,
        int width,
        int height,
        int blurWidth,
        int blurHeight)
    {
        GpuColorTarget? sharp = null;
        GpuColorTarget? bloomSource = null;
        GpuColorTarget? blurScratch = null;
        GpuColorTarget? blurOutput = null;
        try
        {
            sharp = new GpuColorTarget(device, width, height);
            bloomSource = new GpuColorTarget(device, width, height);
            blurScratch = new GpuColorTarget(device, blurWidth, blurHeight);
            blurOutput = new GpuColorTarget(device, width, height);
            EdgeGlowFramebufferSet result = new(
                sharp,
                bloomSource,
                blurScratch,
                blurOutput,
                width,
                height,
                blurWidth,
                blurHeight,
                _framebufferGeneration);
            sharp = null;
            bloomSource = null;
            blurScratch = null;
            blurOutput = null;
            return result;
        }
        finally
        {
            blurOutput?.Dispose();
            blurScratch?.Dispose();
            bloomSource?.Dispose();
            sharp?.Dispose();
        }
    }

    private sealed class EdgeGlowFramebufferSet : IDisposable
    {
        public EdgeGlowFramebufferSet(
            GpuColorTarget sharpFramebuffer,
            GpuColorTarget bloomSourceFramebuffer,
            GpuColorTarget blurScratchFramebuffer,
            GpuColorTarget blurOutputFramebuffer,
            int width,
            int height,
            int blurWidth,
            int blurHeight,
            int generation)
        {
            SharpFramebuffer = sharpFramebuffer;
            BloomSourceFramebuffer = bloomSourceFramebuffer;
            BlurScratchFramebuffer = blurScratchFramebuffer;
            BlurOutputFramebuffer = blurOutputFramebuffer;
            Width = width;
            Height = height;
            BlurWidth = blurWidth;
            BlurHeight = blurHeight;
            Generation = generation;
        }

        public GpuColorTarget SharpFramebuffer { get; }
        public GpuColorTarget BloomSourceFramebuffer { get; }
        public GpuColorTarget BlurScratchFramebuffer { get; }
        public GpuColorTarget BlurOutputFramebuffer { get; }
        public int Width { get; }
        public int Height { get; }
        public int BlurWidth { get; }
        public int BlurHeight { get; }
        public int Generation { get; }

        public void Dispose()
        {
            BlurOutputFramebuffer.Dispose();
            BlurScratchFramebuffer.Dispose();
            BloomSourceFramebuffer.Dispose();
            SharpFramebuffer.Dispose();
        }
    }
}

