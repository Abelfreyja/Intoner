using Dalamud.Interface.Textures.TextureWraps;
using Intoner.Objects.Preview.Assets;

namespace Intoner.Objects.Preview;

internal static class PreviewCacheState
{
    internal sealed class Asset(PreviewAsset previewAsset)
    {
        public object SyncRoot { get; } = new();

        public PreviewAsset PreviewAsset { get; set; } = previewAsset;
        public bool LoadQueued { get; set; }
        public bool LoadRunning { get; set; }
        public bool HasLoadedGeometry { get; set; }
        public PreviewAssetState.GeometryEntry? GeometryEntry { get; set; }
        public string? Error { get; set; }
        public int LoadFailureCount { get; set; }
        public long NextLoadRetryAtMs { get; set; }
        public long LastDetailAccessAtMs { get; set; }
        public long LastThumbnailAccessAtMs { get; set; }
        public Render ThumbnailRenderState { get; } = new();
    }

    internal sealed class Render
    {
        public IDalamudTextureWrap? Texture { get; set; }
        public long TextureByteCount { get; set; }
        public PendingThumbnailTexture? PendingTexture { get; set; }
        public PreviewRender.Request LastRenderedRequest { get; set; }
        public bool HasRenderedRequest { get; set; }
        public bool LastRenderSucceeded { get; set; }
        public PreviewRender.Request RequestedRequest { get; set; }
        public bool HasRequestedRequest { get; set; }
        public PreviewRender.Request ActiveRenderRequest { get; set; }
        public bool HasActiveRenderRequest { get; set; }
        public CancellationTokenSource? RenderCancellation { get; set; }
        public bool RenderQueued { get; set; }
        public bool RenderRunning { get; set; }
        public long LastAccessAtMs { get; set; }
        public string? Error { get; set; }
        public int RenderFailureCount { get; set; }
        public long NextRenderRetryAtMs { get; set; }

        public bool HasActiveResources()
            => Texture is not null
                || PendingTexture is not null
                || RenderQueued
                || RenderRunning;

        public bool HasRetainedTexture()
            => Texture is not null || PendingTexture is not null;

        public long GetRetainedTextureByteCount()
            => TextureByteCount + (PendingTexture?.ByteCount ?? 0);

        public void ReleaseTexture()
        {
            Texture?.Dispose();
            Texture = null;
            TextureByteCount = 0;
        }

        public void ReleaseRetainedTextures()
        {
            ReleaseTexture();
            PendingTexture = null;
        }

        public void DisposeResources()
        {
            RenderCancellation?.Cancel();
            RenderCancellation?.Dispose();
            RenderCancellation = null;
            ReleaseRetainedTextures();
        }
    }

    internal sealed class PendingThumbnailTexture(
        PreviewRender.Request request,
        byte[] pixels,
        int width,
        int height,
        string textureName)
    {
        public PreviewRender.Request Request { get; } = request;
        public byte[] Pixels { get; } = pixels;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public string TextureName { get; } = textureName;
        public long ByteCount { get; } = pixels.LongLength;
    }
}
