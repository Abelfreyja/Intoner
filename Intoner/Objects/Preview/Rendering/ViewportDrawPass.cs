using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;

namespace Intoner.Objects.Preview.Rendering;

internal static class ViewportDrawPass
{
    private static readonly int VertexStride = ViewportMesh.VertexStride;

    public static void Render(ViewportResources.DrawContext drawContext, ViewportFrame frame)
    {
        ClearUnusedShaderStages(drawContext);
        RenderBackground(drawContext, frame);
        RenderMesh(drawContext, frame);
    }

    private static void ClearUnusedShaderStages(ViewportResources.DrawContext drawContext)
    {
        drawContext.Context.HullShader.Set(null);
        drawContext.Context.DomainShader.Set(null);
        drawContext.Context.GeometryShader.Set(null);
    }

    private static void RenderBackground(ViewportResources.DrawContext drawContext, ViewportFrame frame)
    {
        ViewportResources.FrameConstants constants = frame.FrameConstants;
        drawContext.Context.UpdateSubresource(ref constants, drawContext.FrameConstantBuffer);
        drawContext.Context.PixelShader.SetShaderResource(0, null);
        drawContext.Context.OutputMerger.SetTargets(frame.RenderTarget.DepthStencilView, frame.RenderTarget.RenderTargetView);
        drawContext.Context.OutputMerger.SetBlendState(drawContext.BlendState);
        drawContext.Context.OutputMerger.SetDepthStencilState(drawContext.BackgroundDepthStencilState, 0);
        drawContext.Context.Rasterizer.State = drawContext.RasterizerState;
        drawContext.Context.Rasterizer.SetViewport(0, 0, frame.Width, frame.Height);
        drawContext.Context.InputAssembler.InputLayout = null;
        drawContext.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        drawContext.Context.VertexShader.Set(drawContext.BackgroundVertexShader);
        drawContext.Context.VertexShader.SetConstantBuffer(0, drawContext.FrameConstantBuffer);
        drawContext.Context.PixelShader.Set(drawContext.BackgroundPixelShader);
        drawContext.Context.PixelShader.SetConstantBuffer(0, drawContext.FrameConstantBuffer);
        drawContext.Context.ClearRenderTargetView(frame.RenderTarget.RenderTargetView, new Color4(0f, 0f, 0f, 1f));
        drawContext.Context.ClearDepthStencilView(frame.RenderTarget.DepthStencilView, DepthStencilClearFlags.Depth, 1f, 0);
        drawContext.Context.Draw(3, 0);
    }

    private static void RenderMesh(ViewportResources.DrawContext drawContext, ViewportFrame frame)
    {
        drawContext.Context.InputAssembler.InputLayout = drawContext.MeshInputLayout;
        drawContext.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        drawContext.Context.InputAssembler.SetVertexBuffers(
            0,
            new VertexBufferBinding(frame.Mesh.VertexBuffer, VertexStride, 0));
        drawContext.Context.VertexShader.Set(drawContext.MeshVertexShader);
        drawContext.Context.VertexShader.SetConstantBuffer(0, drawContext.FrameConstantBuffer);
        drawContext.Context.PixelShader.Set(drawContext.MeshPixelShader);
        drawContext.Context.PixelShader.SetConstantBuffer(0, drawContext.FrameConstantBuffer);
        drawContext.Context.PixelShader.SetConstantBuffer(1, drawContext.MaterialConstantBuffer);
        drawContext.Context.PixelShader.SetSampler(0, drawContext.SamplerState);

        drawContext.Context.OutputMerger.SetDepthStencilState(drawContext.OpaqueDepthStencilState, 0);
        DrawRanges(drawContext, frame.Mesh.OpaqueRanges);
        drawContext.Context.OutputMerger.SetDepthStencilState(drawContext.TransparentDepthStencilState, 0);
        DrawRanges(drawContext, frame.Mesh.TransparentRanges);

        drawContext.Context.PixelShader.SetShaderResource(0, null);
        drawContext.Context.PixelShader.SetConstantBuffer(1, null);
    }

    private static void DrawRanges(ViewportResources.DrawContext drawContext, IReadOnlyList<ViewportMesh.DrawRange> ranges)
    {
        for (var i = 0; i < ranges.Count; i++)
        {
            ViewportMesh.DrawRange range = ranges[i];
            ViewportResources.MaterialConstants materialConstants = range.CreateConstants();
            drawContext.Context.UpdateSubresource(ref materialConstants, drawContext.MaterialConstantBuffer);
            drawContext.Context.PixelShader.SetShaderResource(0, range.DiffuseView);
            drawContext.Context.Draw(range.VertexCount, range.StartVertex);
        }
    }
}
