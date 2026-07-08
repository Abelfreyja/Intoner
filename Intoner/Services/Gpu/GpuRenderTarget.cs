using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

internal sealed class GpuRenderTarget : IDisposable
{
    private readonly Texture2D _texture;
    private readonly RenderTargetView _renderTargetView;
    private readonly ShaderResourceView _shaderResourceView;
    private readonly Texture2D _depthTexture;
    private readonly DepthStencilView _depthStencilView;
    private readonly GpuLeasedResource<GpuRenderTarget> _lifetime;

    public GpuRenderTarget(Device device, int width, int height)
    {
        _texture = new Texture2D(device, new Texture2DDescription
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
        _renderTargetView = new RenderTargetView(device, _texture);
        _shaderResourceView = new ShaderResourceView(device, _texture);
        _depthTexture = new Texture2D(device, new Texture2DDescription
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
        _depthStencilView = new DepthStencilView(device, _depthTexture);
        _lifetime = new GpuLeasedResource<GpuRenderTarget>(this, static target => target.DisposeResources());
    }

    public RenderTargetView RenderTargetView => _renderTargetView;
    public ShaderResourceView ShaderResourceView => _shaderResourceView;
    public DepthStencilView DepthStencilView => _depthStencilView;

    public GpuLeasedResource<GpuRenderTarget>.Lease Acquire()
        => _lifetime.Acquire();

    public void Dispose()
        => _lifetime.Dispose();

    private void DisposeResources()
    {
        _depthStencilView.Dispose();
        _depthTexture.Dispose();
        _shaderResourceView.Dispose();
        _renderTargetView.Dispose();
        _texture.Dispose();
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
