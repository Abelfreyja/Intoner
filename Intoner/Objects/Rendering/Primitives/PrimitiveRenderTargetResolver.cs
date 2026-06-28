using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using KernelTexture = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture;
using RawViewportF = SharpDX.Mathematics.Interop.RawViewportF;

namespace Intoner.Objects.Rendering.Primitives;

internal static unsafe class PrimitiveRenderTargetResolver
{
    public static bool TryResolveFinalTargetViewport(out RawViewportF viewport, out PrimitiveTextureSize finalTargetSize)
    {
        finalTargetSize = PrimitiveTextureSize.Empty;
        var finalTarget = GetRenderTargetOrNull(PrimitiveRenderTargetLayout.DeviceBackBufferOffset);
        if (!TryGetTextureSize(finalTarget, out finalTargetSize))
        {
            viewport = default;
            return false;
        }

        viewport = new RawViewportF
        {
            X = 0.5f,
            Y = 0.5f,
            Width = finalTargetSize.ActualWidth,
            Height = finalTargetSize.ActualHeight,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        return true;
    }

    public static bool IsFinalTargetBind(nint targetSlots)
        => TryGetBoundTexture(targetSlots, out KernelTexture* boundTarget)
           && boundTarget == GetRenderTargetOrNull(PrimitiveRenderTargetLayout.DeviceBackBufferOffset);

    public static bool TryResolveSceneDepthView(out nint depthViewPointer, out PrimitiveTextureSize sceneDepthSize)
    {
        sceneDepthSize = PrimitiveTextureSize.Empty;
        depthViewPointer = nint.Zero;
        if (!TryGetRenderTargetManager(out var renderTargetManager))
        {
            return false;
        }

        var depthTexture = renderTargetManager->DepthStencil;
        depthViewPointer = depthTexture != null
            ? (nint)depthTexture->D3D11ShaderResourceView
            : nint.Zero;
        return depthViewPointer != nint.Zero
            && TryGetTextureSize(depthTexture, out sceneDepthSize);
    }

    private static bool TryGetRenderTargetManager(out RenderTargetManager* renderTargetManager)
    {
        renderTargetManager = RenderTargetManager.Instance();
        return renderTargetManager != null;
    }

    private static KernelTexture* GetRenderTargetOrNull(int offset)
    {
        if (!TryGetRenderTargetManager(out var renderTargetManager))
        {
            return null;
        }

        return *(KernelTexture**)((nint)renderTargetManager + offset);
    }

    private static bool TryGetBoundTexture(nint targetSlots, out KernelTexture* texture)
    {
        texture = targetSlots != nint.Zero
            ? *(KernelTexture**)targetSlots
            : null;
        return texture != null;
    }

    private static bool TryGetTextureSize(KernelTexture* texture, out PrimitiveTextureSize size)
    {
        size = PrimitiveTextureSize.Empty;
        if (texture == null || texture->ActualWidth == 0 || texture->ActualHeight == 0)
        {
            return false;
        }

        size = new PrimitiveTextureSize(
            texture->ActualWidth,
            texture->ActualHeight,
            texture->AllocatedWidth,
            texture->AllocatedHeight);
        return true;
    }
}

