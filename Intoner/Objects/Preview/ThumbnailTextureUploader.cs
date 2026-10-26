using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Intoner.Logging;
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
                RawImageSpecification.Rgba32(pendingTexture.Request.Width, pendingTexture.Request.Height),
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
            renderState.NextAssetRetryAtMs = pendingTexture.NextAssetRetryAtMs;
            renderState.Error = null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(logger, LogLevel.Warning, ex, "Failed to create thumbnail preview texture");
            PreviewCachePolicy.RecordRenderFailure(renderState, request, now, "Failed to create preview texture.");
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
