using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Intoner.Services.Gpu;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using System.Numerics;
using System.Runtime.InteropServices;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;

namespace Intoner.Scene.Rendering;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneRenderFrame(
    RawViewportF Viewport,
    SceneTextureSize TargetSize,
    SceneProjection Projection,
    bool HasProjection,
    ShaderResourceView? SceneDepthView,
    SceneTextureSize SceneDepthSize)
{
    public static bool TryCapture(
        SceneDepthViewCache sceneDepth,
        SceneRenderFeatures features,
        out SceneRenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(sceneDepth);
        frame = default;
        if (!SceneRenderTarget.TryResolveViewport(out RawViewportF viewport, out SceneTextureSize targetSize))
        {
            return false;
        }

        bool hasProjection = SceneProjection.TryCapture(viewport, out SceneProjection projection);
        ShaderResourceView? sceneDepthView = null;
        SceneTextureSize sceneDepthSize = SceneTextureSize.Empty;
        if ((features & SceneRenderFeatures.SceneDepth) != SceneRenderFeatures.None)
        {
            _ = sceneDepth.TryGet(out sceneDepthView, out sceneDepthSize);
        }
        frame = new SceneRenderFrame(
            viewport,
            targetSize,
            projection,
            hasProjection,
            sceneDepthView,
            sceneDepthSize);
        return true;
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneProjection(
    Matrix4x4 ViewProjection,
    Matrix4x4 ViewMatrix,
    float NearPlane,
    Matrix4x4 InverseProjection,
    bool ReverseDepth,
    bool ForwardPositive,
    RawViewportF Viewport)
{
    public static bool TryCapture(RawViewportF viewport, out SceneProjection projection)
    {
        projection = default;
        if (!SceneViewportProjection.TryGetMainRenderViewProjection(
                out Matrix4x4 viewProjection,
                out Matrix4x4 viewMatrix,
                out Matrix4x4 projectionMatrix,
                out float nearPlane,
                out bool reverseDepth)
            || !Matrix4x4.Invert(projectionMatrix, out Matrix4x4 inverseProjection)
            || !SceneViewportProjection.TryResolveForwardViewDepthSign(
                projectionMatrix,
                nearPlane,
                reverseDepth,
                out bool forwardPositive))
        {
            return false;
        }

        projection = new SceneProjection(
            viewProjection,
            viewMatrix,
            nearPlane,
            inverseProjection,
            reverseDepth,
            forwardPositive,
            viewport);
        return true;
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneTextureSize(
    uint ActualWidth,
    uint ActualHeight,
    uint AllocatedWidth,
    uint AllocatedHeight)
{
    public static readonly SceneTextureSize Empty = new(0, 0, 0, 0);
}

internal static unsafe class SceneRenderTarget
{
    public static bool IsFinalTargetBind(nint targetSlots)
        => TryGetBoundTexture(targetSlots, out KernelTexture* boundTarget)
           && IsFinalTarget(boundTarget);

    public static bool IsFinalTarget(KernelTexture* texture)
        => texture != null && texture == GetFinalTargetOrNull();

    public static bool TryResolveViewport(out RawViewportF viewport, out SceneTextureSize targetSize)
    {
        targetSize = SceneTextureSize.Empty;
        KernelTexture* finalTarget = GetFinalTargetOrNull();
        if (!TryGetTextureSize(finalTarget, out targetSize))
        {
            viewport = default;
            return false;
        }

        viewport = new RawViewportF
        {
            X = 0.5f,
            Y = 0.5f,
            Width = targetSize.ActualWidth,
            Height = targetSize.ActualHeight,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        return true;
    }

    public static bool TryResolveDepthView(out nint depthViewPointer, out SceneTextureSize depthSize)
    {
        depthViewPointer = nint.Zero;
        depthSize = SceneTextureSize.Empty;
        RenderTargetManager* renderTargetManager = RenderTargetManager.Instance();
        if (renderTargetManager == null)
        {
            return false;
        }

        KernelTexture* depthTexture = renderTargetManager->DepthStencil;
        depthViewPointer = depthTexture != null
            ? (nint)depthTexture->D3D11ShaderResourceView
            : nint.Zero;
        return depthViewPointer != nint.Zero && TryGetTextureSize(depthTexture, out depthSize);
    }

    private static KernelTexture* GetFinalTargetOrNull()
    {
        RenderTargetManager* renderTargetManager = RenderTargetManager.Instance();
        return renderTargetManager != null
            ? renderTargetManager->SwapChainBackBuffer
            : null;
    }

    private static bool TryGetBoundTexture(nint targetSlots, out KernelTexture* texture)
    {
        texture = targetSlots != nint.Zero
            ? *(KernelTexture**)targetSlots
            : null;
        return texture != null;
    }

    private static bool TryGetTextureSize(KernelTexture* texture, out SceneTextureSize size)
    {
        size = SceneTextureSize.Empty;
        if (texture == null || texture->ActualWidth == 0 || texture->ActualHeight == 0)
        {
            return false;
        }

        size = new SceneTextureSize(
            texture->ActualWidth,
            texture->ActualHeight,
            texture->AllocatedWidth,
            texture->AllocatedHeight);
        return true;
    }
}

/// <summary> retains the current game scene depth view until the native resource changes </summary>
internal sealed class SceneDepthViewCache : IDisposable
{
    private ShaderResourceView? _view;
    private nint _viewPointer;

    public bool TryGet(out ShaderResourceView? view, out SceneTextureSize size)
    {
        if (!SceneRenderTarget.TryResolveDepthView(out nint viewPointer, out size))
        {
            Clear();
            view = null;
            return false;
        }

        if (_viewPointer != viewPointer)
        {
            Clear();
            _view = D3D11ComReference.RetainShaderResourceView(viewPointer, this);
            _viewPointer = viewPointer;
        }

        view = _view;
        return view != null;
    }

    public void Dispose()
        => Clear();

    private void Clear()
    {
        _view?.Dispose();
        _view = null;
        _viewPointer = nint.Zero;
    }
}
