using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Intoner.Services.Gpu;

internal sealed class D3D11RenderStateSnapshot : IDisposable
{
    private readonly DeviceContext _context;
    private readonly InputLayout? _inputLayout;
    private readonly PrimitiveTopology _primitiveTopology;
    private readonly Buffer[] _vertexBuffers;
    private readonly int[] _vertexStrides;
    private readonly int[] _vertexOffsets;
    private readonly VertexShader? _vertexShader;
    private readonly Buffer[] _vertexConstantBuffers;
    private readonly HullShader? _hullShader;
    private readonly DomainShader? _domainShader;
    private readonly GeometryShader? _geometryShader;
    private readonly PixelShader? _pixelShader;
    private readonly Buffer[] _pixelConstantBuffers;
    private readonly ShaderResourceView[] _pixelShaderResourceViews;
    private readonly SamplerState? _pixelSampler;
    private readonly RenderTargetView[] _renderTargetViews;
    private readonly DepthStencilView? _depthStencilView;
    private readonly RasterizerState? _rasterizerState;
    private readonly SharpDX.Mathematics.Interop.RawViewportF[] _viewports;
    private readonly SharpDX.Mathematics.Interop.RawRectangle[] _scissorRectangles;
    private readonly BlendState? _blendState;
    private readonly SharpDX.Mathematics.Interop.RawColor4 _blendFactor;
    private readonly int _sampleMask;
    private readonly DepthStencilState? _depthStencilState;
    private readonly int _stencilRef;

    private D3D11RenderStateSnapshot(
        DeviceContext context,
        InputLayout? inputLayout,
        PrimitiveTopology primitiveTopology,
        Buffer[] vertexBuffers,
        int[] vertexStrides,
        int[] vertexOffsets,
        VertexShader? vertexShader,
        Buffer[] vertexConstantBuffers,
        HullShader? hullShader,
        DomainShader? domainShader,
        GeometryShader? geometryShader,
        PixelShader? pixelShader,
        Buffer[] pixelConstantBuffers,
        ShaderResourceView[] pixelShaderResourceViews,
        SamplerState? pixelSampler,
        RenderTargetView[] renderTargetViews,
        DepthStencilView? depthStencilView,
        RasterizerState? rasterizerState,
        SharpDX.Mathematics.Interop.RawViewportF[] viewports,
        SharpDX.Mathematics.Interop.RawRectangle[] scissorRectangles,
        BlendState? blendState,
        SharpDX.Mathematics.Interop.RawColor4 blendFactor,
        int sampleMask,
        DepthStencilState? depthStencilState,
        int stencilRef)
    {
        _context = context;
        _inputLayout = inputLayout;
        _primitiveTopology = primitiveTopology;
        _vertexBuffers = vertexBuffers;
        _vertexStrides = vertexStrides;
        _vertexOffsets = vertexOffsets;
        _vertexShader = vertexShader;
        _vertexConstantBuffers = vertexConstantBuffers;
        _hullShader = hullShader;
        _domainShader = domainShader;
        _geometryShader = geometryShader;
        _pixelShader = pixelShader;
        _pixelConstantBuffers = pixelConstantBuffers;
        _pixelShaderResourceViews = pixelShaderResourceViews;
        _pixelSampler = pixelSampler;
        _renderTargetViews = renderTargetViews;
        _depthStencilView = depthStencilView;
        _rasterizerState = rasterizerState;
        _viewports = viewports;
        _scissorRectangles = scissorRectangles;
        _blendState = blendState;
        _blendFactor = blendFactor;
        _sampleMask = sampleMask;
        _depthStencilState = depthStencilState;
        _stencilRef = stencilRef;
    }

    public RenderTargetView? PrimaryRenderTargetView
        => _renderTargetViews.Length > 0 ? _renderTargetViews[0] : null;

    public static D3D11RenderStateSnapshot Capture(
        DeviceContext context,
        int pixelConstantBufferCount,
        int pixelShaderResourceViewCount,
        int vertexConstantBufferCount = 1,
        int vertexBufferCount = 1,
        bool captureScissorRectangles = false)
    {
        vertexBufferCount = Math.Max(vertexBufferCount, 1);
        var inputLayout = context.InputAssembler.InputLayout;
        var primitiveTopology = context.InputAssembler.PrimitiveTopology;
        var vertexBuffers = new Buffer[vertexBufferCount];
        var vertexStrides = new int[vertexBufferCount];
        var vertexOffsets = new int[vertexBufferCount];
        context.InputAssembler.GetVertexBuffers(0, vertexBufferCount, vertexBuffers, vertexStrides, vertexOffsets);

        var vertexShader = context.VertexShader.Get();
        var vertexConstantBuffers = context.VertexShader.GetConstantBuffers(0, vertexConstantBufferCount);
        var hullShader = context.HullShader.Get();
        var domainShader = context.DomainShader.Get();
        var geometryShader = context.GeometryShader.Get();
        var pixelShader = context.PixelShader.Get();
        var pixelConstantBuffers = context.PixelShader.GetConstantBuffers(0, pixelConstantBufferCount);
        var pixelShaderResourceViews = context.PixelShader.GetShaderResources(0, pixelShaderResourceViewCount);
        var pixelSampler = context.PixelShader.GetSamplers(0, 1)[0];
        var renderTargetViews = context.OutputMerger.GetRenderTargets(1, out var depthStencilView);
        var rasterizerState = context.Rasterizer.State;
        var viewports = context.Rasterizer.GetViewports<SharpDX.Mathematics.Interop.RawViewportF>();
        var scissorRectangles = captureScissorRectangles
            ? context.Rasterizer.GetScissorRectangles<SharpDX.Mathematics.Interop.RawRectangle>()
            : [];
        var blendState = context.OutputMerger.GetBlendState(out var blendFactor, out var sampleMask);
        var depthStencilState = context.OutputMerger.GetDepthStencilState(out var stencilRef);

        return new D3D11RenderStateSnapshot(
            context,
            inputLayout,
            primitiveTopology,
            vertexBuffers,
            vertexStrides,
            vertexOffsets,
            vertexShader,
            vertexConstantBuffers,
            hullShader,
            domainShader,
            geometryShader,
            pixelShader,
            pixelConstantBuffers,
            pixelShaderResourceViews,
            pixelSampler,
            renderTargetViews,
            depthStencilView,
            rasterizerState,
            viewports,
            scissorRectangles,
            blendState,
            blendFactor,
            sampleMask,
            depthStencilState,
            stencilRef);
    }

    public void Dispose()
    {
        _context.InputAssembler.InputLayout = _inputLayout;
        _context.InputAssembler.PrimitiveTopology = _primitiveTopology;
        _context.InputAssembler.SetVertexBuffers(0, _vertexBuffers, _vertexStrides, _vertexOffsets);
        _context.VertexShader.Set(_vertexShader);
        _context.VertexShader.SetConstantBuffers(0, _vertexConstantBuffers);
        _context.HullShader.Set(_hullShader);
        _context.DomainShader.Set(_domainShader);
        _context.GeometryShader.Set(_geometryShader);
        _context.PixelShader.Set(_pixelShader);
        _context.PixelShader.SetConstantBuffers(0, _pixelConstantBuffers);
        _context.PixelShader.SetShaderResources(0, _pixelShaderResourceViews);
        _context.PixelShader.SetSampler(0, _pixelSampler);
        _context.OutputMerger.SetTargets(_depthStencilView, _renderTargetViews);
        _context.OutputMerger.SetBlendState(_blendState, _blendFactor, _sampleMask);
        _context.OutputMerger.SetDepthStencilState(_depthStencilState, _stencilRef);
        _context.Rasterizer.State = _rasterizerState;
        if (_viewports.Length > 0)
        {
            _context.Rasterizer.SetViewports(_viewports);
        }

        if (_scissorRectangles.Length > 0)
        {
            _context.Rasterizer.SetScissorRectangles(_scissorRectangles);
        }

        DisposeArray(_vertexBuffers);
        _vertexShader?.Dispose();
        DisposeArray(_vertexConstantBuffers);
        _hullShader?.Dispose();
        _domainShader?.Dispose();
        _geometryShader?.Dispose();
        _pixelShader?.Dispose();
        DisposeArray(_pixelConstantBuffers);
        DisposeArray(_pixelShaderResourceViews);
        _pixelSampler?.Dispose();
        DisposeArray(_renderTargetViews);
        _depthStencilView?.Dispose();
        _rasterizerState?.Dispose();
        _blendState?.Dispose();
        _depthStencilState?.Dispose();
        _inputLayout?.Dispose();
    }

    private static void DisposeArray<T>(IEnumerable<T> values) where T : SharpDX.ComObject
    {
        foreach (var value in values)
        {
            value?.Dispose();
        }
    }
}
