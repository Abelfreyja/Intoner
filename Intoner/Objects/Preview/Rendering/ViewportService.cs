using Dalamud.Bindings.ImGui;
using Intoner.Objects.Preview.Assets;
using Intoner.Objects.Rendering.Drawing;
using System.Numerics;

namespace Intoner.Objects.Preview.Rendering;

internal sealed class ViewportService : IDisposable
{
    private readonly PreviewService                    _previewService;
    private readonly ViewportRenderer                  _renderer;
    private readonly ImGuiDrawCallbackQueue<RenderJob> _renderJobs = new(ProcessRenderJob, static job => job.Dispose());

    private bool _disposed;

    public ViewportService(
        PreviewService previewService,
        ViewportRenderer renderer)
    {
        _previewService = previewService;
        _renderer       = renderer;
    }

    public bool TryDrawPreview(
        ImDrawListPtr drawList,
        PreviewAsset? asset,
        PreviewRender.Request request,
        Vector2 imageMin,
        Vector2 imageMax,
        float rounding,
        out PreviewRender.Result result)
    {
        result = new PreviewRender.Result(null, false, null);
        if (_disposed || drawList.IsNull)
        {
            result = new PreviewRender.Result(null, false, "GPU preview renderer is unavailable.");
            return false;
        }

        if (asset is null || !asset.IsValid)
        {
            result = new PreviewRender.Result(null, false, "Select an asset to preview.");
            return false;
        }

        int width = Math.Max(1, (int)MathF.Round(imageMax.X - imageMin.X));
        int height = Math.Max(1, (int)MathF.Round(imageMax.Y - imageMin.Y));
        PreviewRender.Request viewportRequest = request with
        {
            Width = width,
            Height = height,
            Mode = PreviewRender.Mode.Detail,
        };

        result = _previewService.AcquirePreviewGeometry(
            asset,
            PreviewRender.Mode.Detail,
            out PreviewAssetState.GeometryLease geometryLease);
        if (!geometryLease.HasEntry || geometryLease.Entry is not { } geometryEntry)
        {
            return false;
        }

        using (geometryLease)
        {
            if (!_renderer.TryPrepareFrame(
                    geometryEntry,
                    asset,
                    viewportRequest,
                    out ViewportFrame? frame,
                    out string? error))
            {
                result = new PreviewRender.Result(null, false, error);
                return false;
            }

            if (frame is null)
            {
                result = new PreviewRender.Result(null, false, "GPU preview renderer is unavailable.");
                return false;
            }

            QueueRenderImage(drawList, _renderer, frame, imageMin, imageMax, rounding);
            result = new PreviewRender.Result(null, false, null);
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderJobs.Dispose();
        _renderer.Dispose();
    }

    private void QueueRenderImage(
        ImDrawListPtr drawList,
        ViewportRenderer renderer,
        ViewportFrame frame,
        Vector2 imageMin,
        Vector2 imageMax,
        float rounding)
    {
        RenderJob renderJob = new(renderer, frame);
        _renderJobs.QueueDraw(
            drawList,
            renderJob,
            () =>
            {
                drawList.AddImageRounded(
                    new ImTextureID(frame.TextureHandle),
                    imageMin,
                    imageMax,
                    Vector2.Zero,
                    Vector2.One,
                    0xFFFFFFFF,
                    rounding);
            });
    }

    private static void ProcessRenderJob(RenderJob renderJob)
        => renderJob.Renderer.RenderFrame(renderJob.Frame);

    private sealed class RenderJob(
        ViewportRenderer renderer,
        ViewportFrame frame) : IDisposable
    {
        private bool _disposed;

        public ViewportRenderer Renderer { get; } = renderer;
        public ViewportFrame Frame { get; } = frame;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Frame.Dispose();
        }
    }
}
