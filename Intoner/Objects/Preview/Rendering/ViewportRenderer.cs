using Dalamud.Interface;
using Intoner.Objects.Preview.Assets;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Preview.Rendering;

internal sealed class ViewportRenderer : IDisposable
{
    private readonly ILogger<ViewportRenderer> _logger;
    private readonly ViewportResources         _resources;
    private readonly ViewportMeshCache         _meshCache;
    private readonly GpuRenderTarget.Cache     _renderTargetCache = new();

    private bool _disposed;

    public ViewportRenderer(
        ILogger<ViewportRenderer> logger,
        IUiBuilder uiBuilder)
    {
        _logger    = logger;
        _resources = new ViewportResources(_logger, uiBuilder);
        _meshCache = new ViewportMeshCache(_logger);
    }

    public bool TryPrepareFrame(
        PreviewAssetState.GeometryEntry geometryEntry,
        PreviewAsset asset,
        PreviewRender.Request request,
        out ViewportFrame? frame,
        out string? error)
    {
        frame = null;
        error = null;

        if (_disposed)
        {
            error = "Preview renderer is disposed.";
            return false;
        }

        if (!_resources.TryEnsureReady(out bool resetDeviceResources))
        {
            if (resetDeviceResources)
            {
                ClearDeviceCaches();
            }

            error = "GPU preview renderer is unavailable.";
            return false;
        }

        if (resetDeviceResources)
        {
            ClearDeviceCaches();
        }

        var device = _resources.Device;
        if (device is null)
        {
            error = "GPU preview renderer is unavailable.";
            return false;
        }

        int width = Math.Clamp(request.Width, 96, 640);
        int height = Math.Clamp(request.Height, 96, 480);
        GpuLeasedResource<GpuRenderTarget>.Lease? renderTargetLease = null;
        GpuLeasedResource<ViewportMesh>.Lease? meshLease = null;
        try
        {
            long now = GetNowMilliseconds();
            renderTargetLease = _renderTargetCache.GetOrCreateLease(device, width, height, now);
            if (!TryGetOrUploadMesh(geometryEntry, asset, out meshLease, out error) || meshLease is null)
            {
                return false;
            }

            _meshCache.Trim(now);
            _renderTargetCache.Trim(now);
            frame = new ViewportFrame(
                renderTargetLease,
                meshLease,
                geometryEntry.Geometry.Bounds,
                request.ToCameraState(),
                width,
                height);
            renderTargetLease = null;
            meshLease = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "preview viewport preparation failed");
            ClearRuntimeResources();
            error = "Failed to prepare GPU preview.";
            return false;
        }
        finally
        {
            meshLease?.Dispose();
            renderTargetLease?.Dispose();
        }
    }

    public void RenderFrame(ViewportFrame frame)
    {
        if (_disposed || !_resources.TryGetDrawContext(out ViewportResources.DrawContext drawContext))
        {
            return;
        }

        try
        {
            using var state = D3D11DrawStateScope.Capture(
                drawContext.Context,
                pixelConstantBufferCount: 2,
                pixelShaderResourceViewCount: 1,
                vertexConstantBufferCount: 1,
                vertexBufferCount: 1,
                captureScissorRectangles: true);

            ViewportDrawPass.Render(drawContext, frame);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "preview viewport render failed");
            _resources.RequestReset();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearRuntimeResources();
    }

    private bool TryGetOrUploadMesh(
        PreviewAssetState.GeometryEntry geometryEntry,
        PreviewAsset asset,
        out GpuLeasedResource<ViewportMesh>.Lease? meshLease,
        out string? error)
    {
        meshLease = null;
        error = null;
        var device = _resources.Device;
        var whiteTexture = _resources.WhiteTexture;
        if (device is null || whiteTexture is null)
        {
            error = "GPU preview renderer is unavailable.";
            return false;
        }

        return _meshCache.TryGetOrUpload(device, geometryEntry, asset, whiteTexture, out meshLease, out error);
    }

    private void ClearRuntimeResources()
    {
        ClearDeviceCaches();
        _resources.Clear();
    }

    private void ClearDeviceCaches()
    {
        _meshCache.Clear();
        _renderTargetCache.Clear();
    }

    private static long GetNowMilliseconds()
        => Environment.TickCount64;
}
