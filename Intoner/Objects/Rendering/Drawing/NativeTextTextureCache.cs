using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Intoner.Objects.Rendering.Primitives;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.Rendering.Drawing;

/// <summary> rasterizes labels on the UI thread </summary>
internal sealed class NativeTextTextureCache : IDisposable
{
    private const int Capacity = 128;
    private const long PixelBudget = 4 * 1024 * 1024;
    private readonly ITextureProvider _textures;
    private readonly IUiBuilder _uiBuilder;
    private readonly ILogger<NativeTextTextureCache> _logger;
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Lock _lock = new();
    private long _use;
    private int _invalidate;
    private nint _deviceHandle;
    private long _pixels;
    private bool _loggedFailure;
    private bool _disposed;

    public NativeTextTextureCache(ITextureProvider textures, IUiBuilder uiBuilder, ILogger<NativeTextTextureCache> logger)
    {
        _textures = textures;
        _uiBuilder = uiBuilder;
        _logger = logger;
        _uiBuilder.DefaultFontChanged += Invalidate;
    }

    /// <summary> acquires a texture on the UI thread </summary>
    public bool TryAcquire(in TextPrimitive text, float alpha, [NotNullWhen(true)] out PrimitiveTexture? texture)
    {
        lock (_lock)
        {
            texture = null;
            if (_disposed)
            {
                return false;
            }

            try
            {
                texture = Get(text, alpha);
                texture.Retain();
                return true;
            }
            catch (Exception ex)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    _logger.LogWarning(ex, "native text rasterization failed; geometry remains available");
                }

                texture = null;
                return false;
            }
        }
    }

    private unsafe PrimitiveTexture Get(in TextPrimitive text, float alpha)
    {
        if (Interlocked.Exchange(ref _invalidate, 0) != 0 || _deviceHandle != _uiBuilder.DeviceHandle)
        {
            Clear();
            _deviceHandle = _uiBuilder.DeviceHandle;
        }

        Key key = new(text.Text, (nint)text.Font.Handle, text.FontSize, text.Style, alpha);
        if (_entries.TryGetValue(key, out Entry? entry))
        {
            entry.LastUse = ++_use;
            return entry.Texture;
        }

        PrimitiveTexture texture = Create(text, alpha);
        long pixels = (long)texture.Size.X * (long)texture.Size.Y;
        while (_entries.Count > 0 && (_entries.Count >= Capacity || _pixels + pixels > PixelBudget))
        {
            Key oldest = default;
            long lastUse = long.MaxValue;
            foreach ((Key candidate, Entry value) in _entries)
            {
                if (value.LastUse < lastUse)
                {
                    oldest = candidate;
                    lastUse = value.LastUse;
                }
            }

            PrimitiveTexture removed = _entries[oldest].Texture;
            _pixels -= (long)removed.Size.X * (long)removed.Size.Y;
            removed.Release();
            _entries.Remove(oldest);
        }

        _entries.Add(key, new Entry(texture, ++_use));
        _pixels += pixels;
        return texture;
    }

    private PrimitiveTexture Create(in TextPrimitive text, float alpha)
    {
        Vector2 textSize = text.Measure();
        float fringe = MathF.Ceiling(MathF.Max(1f, text.Style.BorderThickness));
        Vector2 offset = new(MathF.Ceiling(text.Style.Padding.X + fringe), MathF.Ceiling(text.Style.Padding.Y + fringe));
        Vector2 size = textSize + offset * 2f + new Vector2(0f, text.Style.ExtraHeight);
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y)
            || size.X > 16384f || size.Y > 16384f
            || MathF.Ceiling(size.X) * MathF.Ceiling(size.Y) > PixelBudget)
        {
            throw new InvalidOperationException("native text exceeds the texture size limit");
        }

        using IDrawListTextureWrap texture = _textures.CreateDrawListTexture("Intoner native text");
        texture.Size = size;
        using BufferBackedImDrawData data = BufferBackedImDrawData.Create();
        text.Style.Draw(data.ListPtr, offset, textSize, text.Font, text.FontSize, text.Text, alpha);
        texture.Draw(data.ListPtr, Vector2.Zero, Vector2.One);
        return new PrimitiveTexture(
            D3D11ComReference.RetainShaderResourceView((nint)texture.Handle.Handle, this), texture.Size, offset);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _uiBuilder.DefaultFontChanged -= Invalidate;
            Clear();
        }
    }

    private void Invalidate()
        => Interlocked.Exchange(ref _invalidate, 1);

    private void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Texture.Release();
        }

        _entries.Clear();
        _pixels = 0;
    }

    private readonly record struct Key(string Text, nint Font, float Size, TextStyle Style, float Alpha);

    private sealed class Entry(PrimitiveTexture texture, long lastUse)
    {
        public PrimitiveTexture Texture { get; } = texture;
        public long LastUse { get; set; } = lastUse;
    }
}
