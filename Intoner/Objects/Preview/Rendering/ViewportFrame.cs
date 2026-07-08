using Intoner.Objects.Assets;
using Intoner.Services.Gpu;
using System.Numerics;

namespace Intoner.Objects.Preview.Rendering;

internal sealed class ViewportFrame : IDisposable
{
    private readonly GpuLeasedResource<GpuRenderTarget>.Lease _renderTargetLease;
    private readonly GpuLeasedResource<ViewportMesh>.Lease    _meshLease;

    private bool _disposed;

    internal ViewportFrame(
        GpuLeasedResource<GpuRenderTarget>.Lease renderTargetLease,
        GpuLeasedResource<ViewportMesh>.Lease meshLease,
        ModelPreviewGeometryReader.PreviewBounds bounds,
        PreviewRender.CameraState camera,
        int width,
        int height)
    {
        _renderTargetLease = renderTargetLease;
        _meshLease         = meshLease;
        Width              = width;
        Height             = height;

        PreviewScene.Frame scene = PreviewScene.CreateFrame(bounds, camera, width, height);
        FrameConstants = new ViewportResources.FrameConstants
        {
            ViewProjection   = scene.ViewProjection,
            LightDirection   = new Vector4(scene.LightDirection, 0f),
            BackgroundTop    = new Vector4(scene.BackgroundTop, 1f),
            BackgroundBottom = new Vector4(scene.BackgroundBottom, 1f),
        };
    }

    internal GpuRenderTarget RenderTarget
        => _renderTargetLease.Resource;

    internal ViewportMesh Mesh
        => _meshLease.Resource;

    internal int Width { get; }

    internal int Height { get; }

    internal ViewportResources.FrameConstants FrameConstants { get; }

    public nint TextureHandle
        => RenderTarget.ShaderResourceView.NativePointer;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _meshLease.Dispose();
        _renderTargetLease.Dispose();
    }
}
