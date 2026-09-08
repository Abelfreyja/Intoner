using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

/// <summary> owns one shader readable color render target </summary>
internal sealed class GpuColorTarget : IDisposable
{
    private readonly Texture2D _texture;

    public GpuColorTarget(Device device, int width, int height)
    {
        Texture2D? texture = null;
        RenderTargetView? renderTargetView = null;
        ShaderResourceView? shaderResourceView = null;
        try
        {
            texture = new Texture2D(device, new Texture2DDescription
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
            renderTargetView = new RenderTargetView(device, texture);
            shaderResourceView = new ShaderResourceView(device, texture);

            _texture = texture;
            RenderTargetView = renderTargetView;
            ShaderResourceView = shaderResourceView;
            Width = width;
            Height = height;

            texture = null;
            renderTargetView = null;
            shaderResourceView = null;
        }
        finally
        {
            shaderResourceView?.Dispose();
            renderTargetView?.Dispose();
            texture?.Dispose();
        }
    }

    public RenderTargetView RenderTargetView { get; }
    public ShaderResourceView ShaderResourceView { get; }
    public int Width { get; }
    public int Height { get; }

    public void Dispose()
    {
        try
        {
            ShaderResourceView.Dispose();
        }
        finally
        {
            try
            {
                RenderTargetView.Dispose();
            }
            finally
            {
                _texture.Dispose();
            }
        }
    }
}

internal sealed class GpuRenderTarget : IDisposable
{
    private readonly GpuColorTarget _colorTarget;
    private readonly Texture2D _depthTexture;
    private readonly DepthStencilView _depthStencilView;
    private readonly GpuLeasedResource<GpuRenderTarget> _lifetime;

    public GpuRenderTarget(Device device, int width, int height)
    {
        GpuColorTarget? colorTarget = null;
        Texture2D? depthTexture = null;
        DepthStencilView? depthStencilView = null;
        try
        {
            colorTarget = new GpuColorTarget(device, width, height);
            depthTexture = new Texture2D(device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.D32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.DepthStencil,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
            });
            depthStencilView = new DepthStencilView(device, depthTexture);

            _colorTarget = colorTarget;
            _depthTexture = depthTexture;
            _depthStencilView = depthStencilView;
            _lifetime = new GpuLeasedResource<GpuRenderTarget>(this, static target => target.DisposeResources());

            colorTarget = null;
            depthTexture = null;
            depthStencilView = null;
        }
        finally
        {
            depthStencilView?.Dispose();
            depthTexture?.Dispose();
            colorTarget?.Dispose();
        }
    }

    public RenderTargetView RenderTargetView => _colorTarget.RenderTargetView;
    public ShaderResourceView ShaderResourceView => _colorTarget.ShaderResourceView;
    public DepthStencilView DepthStencilView => _depthStencilView;

    public GpuLeasedResource<GpuRenderTarget>.Lease Acquire()
        => _lifetime.Acquire();

    public void Dispose()
        => _lifetime.Dispose();

    private void DisposeResources()
    {
        try
        {
            _depthStencilView.Dispose();
        }
        finally
        {
            try
            {
                _depthTexture.Dispose();
            }
            finally
            {
                _colorTarget.Dispose();
            }
        }
    }

    internal sealed class Cache : IDisposable
    {
        private const int MaxRenderTargetCount = 6;

        private static readonly TimeSpan RenderTargetRetention = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RenderTargetMinimumRetention = TimeSpan.FromSeconds(1);

        private readonly Dictionary<Key, Entry> _entries = [];

        public GpuLeasedResource<GpuRenderTarget>.Lease GetOrCreateLease(Device device, int width, int height, long now)
        {
            Key key = new(width, height);
            if (_entries.TryGetValue(key, out Entry? entry))
            {
                entry.LastAccessAtMs = now;
                return entry.Target.Acquire();
            }

            GpuRenderTarget target = new(device, width, height);
            _entries.Add(key, new Entry(target, now));
            return target.Acquire();
        }

        public void Trim(long now)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            foreach ((Key key, Entry entry) in _entries
                         .OrderBy(static pair => pair.Value.LastAccessAtMs)
                         .ToArray())
            {
                bool expired = now - entry.LastAccessAtMs >= RenderTargetRetention.TotalMilliseconds;
                bool overLimit = _entries.Count > MaxRenderTargetCount
                    && now - entry.LastAccessAtMs >= RenderTargetMinimumRetention.TotalMilliseconds;
                if (!expired && !overLimit)
                {
                    continue;
                }

                entry.Target.Dispose();
                _entries.Remove(key);
            }
        }

        public void Dispose()
            => Clear();

        public void Clear()
        {
            foreach (Entry entry in _entries.Values)
            {
                entry.Target.Dispose();
            }

            _entries.Clear();
        }

        [StructLayout(LayoutKind.Auto)]
        private readonly record struct Key(int Width, int Height);

        private sealed class Entry(GpuRenderTarget target, long lastAccessAtMs)
        {
            public GpuRenderTarget Target { get; } = target;
            public long LastAccessAtMs { get; set; } = lastAccessAtMs;
        }
    }

}
