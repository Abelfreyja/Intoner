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
            framebufferSet = new EdgeGlowFramebufferSet(
                CreateFramebuffer(device, width, height),
                CreateFramebuffer(device, width, height),
                CreateFramebuffer(device, blurWidth, blurHeight),
                CreateFramebuffer(device, width, height),
                width,
                height,
                blurWidth,
                blurHeight,
                _framebufferGeneration);
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

    private static EdgeGlowFramebuffer CreateFramebuffer(Device device, int width, int height)
    {
        var texture = new Texture2D(device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });
        var renderTargetView = new RenderTargetView(device, texture);
        var shaderResourceView = new ShaderResourceView(device, texture);
        return new EdgeGlowFramebuffer(texture, renderTargetView, shaderResourceView, width, height);
    }

    private sealed class EdgeGlowFramebuffer : IDisposable
    {
        public EdgeGlowFramebuffer(Texture2D texture, RenderTargetView renderTargetView, ShaderResourceView shaderResourceView, int width, int height)
        {
            Texture = texture;
            RenderTargetView = renderTargetView;
            ShaderResourceView = shaderResourceView;
            Width = width;
            Height = height;
        }

        public Texture2D Texture { get; }
        public RenderTargetView RenderTargetView { get; }
        public ShaderResourceView ShaderResourceView { get; }
        public int Width { get; }
        public int Height { get; }

        public void Dispose()
        {
            ShaderResourceView.Dispose();
            RenderTargetView.Dispose();
            Texture.Dispose();
        }
    }

    private sealed class EdgeGlowFramebufferSet : IDisposable
    {
        public EdgeGlowFramebufferSet(
            EdgeGlowFramebuffer sharpFramebuffer,
            EdgeGlowFramebuffer bloomSourceFramebuffer,
            EdgeGlowFramebuffer blurScratchFramebuffer,
            EdgeGlowFramebuffer blurOutputFramebuffer,
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

        public EdgeGlowFramebuffer SharpFramebuffer { get; }
        public EdgeGlowFramebuffer BloomSourceFramebuffer { get; }
        public EdgeGlowFramebuffer BlurScratchFramebuffer { get; }
        public EdgeGlowFramebuffer BlurOutputFramebuffer { get; }
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

