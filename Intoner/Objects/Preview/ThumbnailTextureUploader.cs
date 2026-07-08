using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Preview;

internal sealed class ThumbnailTextureUploader(
    ILogger logger,
    ITextureProvider textureProvider)
{
    private readonly UploadBudget _uploadBudget = new();

    public void ApplyPendingTexture(
        PreviewCacheState.Render renderState,
        PreviewRender.Request request,
        long now)
    {
        if (renderState.PendingTexture is null)
        {
            return;
        }

        if (PreviewCachePolicy.IsThumbnailWorkExpired(renderState.LastAccessAtMs, now))
        {
            renderState.PendingTexture = null;
            return;
        }

        if (renderState.PendingTexture.Request != request)
        {
            renderState.PendingTexture = null;
            return;
        }

        if (!_uploadBudget.TryConsume(now))
        {
            return;
        }

        try
        {
            PreviewCacheState.PendingThumbnailTexture pendingTexture = renderState.PendingTexture;
            var nextTexture = textureProvider.CreateFromRaw(
                RawImageSpecification.Rgba32(pendingTexture.Width, pendingTexture.Height),
                pendingTexture.Pixels,
                pendingTexture.TextureName);
            renderState.ReleaseTexture();
            renderState.Texture = nextTexture;
            renderState.TextureByteCount = pendingTexture.ByteCount;
            renderState.LastRenderedRequest = request;
            renderState.HasRenderedRequest = true;
            renderState.LastRenderSucceeded = true;
            renderState.RenderFailureCount = 0;
            renderState.NextRenderRetryAtMs = 0;
            renderState.Error = null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create thumbnail preview texture");
            if (renderState.Texture is null)
            {
                renderState.LastRenderedRequest = request;
                renderState.HasRenderedRequest = true;
                renderState.LastRenderSucceeded = false;
                renderState.RenderFailureCount++;
                renderState.NextRenderRetryAtMs = now + PreviewCachePolicy.GetRenderRetryDelayMilliseconds(renderState.RenderFailureCount);
                renderState.Error = "Failed to create preview texture.";
            }
        }
        finally
        {
            renderState.PendingTexture = null;
        }
    }

    private sealed class UploadBudget
    {
        private static readonly TimeSpan BudgetWindow = TimeSpan.FromMilliseconds(16);

        private const int MaxUploadsPerWindow = 2;

        private readonly Lock _lock = new();

        private long _nextResetAtMs;
        private int _remainingUploads;

        public bool TryConsume(long now)
        {
            lock (_lock)
            {
                if (now >= _nextResetAtMs)
                {
                    _nextResetAtMs = now + (long)BudgetWindow.TotalMilliseconds;
                    _remainingUploads = MaxUploadsPerWindow;
                }

                if (_remainingUploads <= 0)
                {
                    return false;
                }

                _remainingUploads--;
                return true;
            }
        }
    }
}
