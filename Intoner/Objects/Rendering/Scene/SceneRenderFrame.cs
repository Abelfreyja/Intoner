using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
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
    public static SceneRenderFrame Capture(in SceneRenderCommand command, SceneDepthViewCache sceneDepth, SceneRenderFeatures features)
    {
        SceneTextureSize depthSize = default;
        ShaderResourceView? depthView = (features & SceneRenderFeatures.SceneDepth) != SceneRenderFeatures.None
            ? sceneDepth.Get(command.Target.DepthViewAddress, out depthSize)
            : null;
        return new SceneRenderFrame(
            command.Target.Viewport,
            command.Target.Size,
            command.Projection.GetValueOrDefault(),
            command.Projection.HasValue,
            depthView,
            depthSize);
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
    public static SceneProjection? Capture(RawViewportF viewport)
    {
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
            return null;
        }

        return new SceneProjection(
            viewProjection,
            viewMatrix,
            nearPlane,
            inverseProjection,
            reverseDepth,
            forwardPositive,
            viewport);
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneTextureSize(
    uint ActualWidth,
    uint ActualHeight,
    uint AllocatedWidth,
    uint AllocatedHeight)
{
    public static readonly SceneTextureSize Empty = default;
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneRenderTarget(
    nint Address,
    SceneTextureSize Size,
    (uint Width, uint Height) ViewportSize,
    nint DepthViewAddress)
{
    public RawViewportF Viewport => new()
    {
        X = 0.5f,
        Y = 0.5f,
        Width = ViewportSize.Width,
        Height = ViewportSize.Height,
        MinDepth = 0f,
        MaxDepth = 1f,
    };
}

internal static unsafe class SceneRenderTargets
{
    private const int ColorMipOffset = 0x38;
    private const int ColorSliceOffset = 0x3C;

    public static bool TryCaptureFinal(KernelTexture* texture, out SceneRenderTarget target)
    {
        target = default;
        RenderTargetManager* manager = RenderTargetManager.Instance();
        if (manager == null || texture != manager->SwapChainBackBuffer || !TryGetSize(texture, out SceneTextureSize size)
            || texture->Width3 == 0 || texture->Height3 == 0)
        {
            return false;
        }

        target = new SceneRenderTarget((nint)texture, size, (texture->Width3, texture->Height3),
            GetDepthViewAddress(manager->DepthStencil));
        return true;
    }

    public static bool Matches(in SceneRenderTarget target, RenderCommandSetTarget* command)
    {
        if (target.Address == nint.Zero || command == null
            || command->Type != RenderCommandType.SetTarget
            || command->RenderTargetCount != 1 || command->DepthBuffer != null
            || *(ushort*)((byte*)command + ColorMipOffset) != 0
            || *(ushort*)((byte*)command + ColorSliceOffset) != 0)
        {
            return false;
        }

        KernelTexture* texture = command->RenderTargets[0].Value;
        return (nint)texture == target.Address
            && texture->Width3 == target.ViewportSize.Width
            && texture->Height3 == target.ViewportSize.Height;
    }

    public static nint GetDepthView(out SceneTextureSize size)
    {
        RenderTargetManager* manager = RenderTargetManager.Instance();
        KernelTexture* depth = manager != null ? manager->DepthStencil : null;
        nint view = GetDepthViewAddress(depth);
        if (view != nint.Zero && TryGetSize(depth, out size))
        {
            return view;
        }

        size = default;
        return nint.Zero;
    }

    private static nint GetDepthViewAddress(KernelTexture* depth)
        => depth != null ? (nint)depth->D3D11ShaderResourceView : nint.Zero;

    private static bool TryGetSize(KernelTexture* texture, out SceneTextureSize size)
    {
        size = default;
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

    public ShaderResourceView? Get(nint recordedView, out SceneTextureSize size)
    {
        nint viewPointer = SceneRenderTargets.GetDepthView(out size);
        if (viewPointer == nint.Zero || viewPointer != recordedView)
        {
            size = default;
            Clear();
            return null;
        }

        if (_viewPointer != viewPointer)
        {
            Clear();
            _view = D3D11ComReference.RetainShaderResourceView(viewPointer, this);
            _viewPointer = viewPointer;
        }

        return _view;
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
