using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Intoner.Services.Gpu;

/// <summary> reuses storage while capturing and restoring a selected set of D3D11 pipeline state </summary>
internal sealed unsafe class D3D11DrawStateSnapshot : IDisposable
{
    private const int MaximumViewportCount = 16;

    private readonly Buffer[] _vertexBuffers;
    private readonly int[] _vertexStrides;
    private readonly int[] _vertexOffsets;
    private readonly Buffer[] _vertexConstantBuffers;
    private readonly Buffer[] _pixelConstantBuffers;
    private readonly ShaderResourceView[] _pixelShaderResourceViews;
    private readonly SamplerState[] _pixelSamplers;
    private readonly RenderTargetView[] _renderTargetViews;
    private readonly RawViewportF[] _viewports = new RawViewportF[MaximumViewportCount];
    private readonly bool _captureScissorRectangles;

    private DeviceContext? _context;
    private InputLayout? _inputLayout;
    private PrimitiveTopology _primitiveTopology;
    private VertexShader? _vertexShader;
    private HullShader? _hullShader;
    private DomainShader? _domainShader;
    private GeometryShader? _geometryShader;
    private PixelShader? _pixelShader;
    private DepthStencilView? _depthStencilView;
    private RasterizerState? _rasterizerState;
    private BlendState? _blendState;
    private RawColor4 _blendFactor;
    private int _sampleMask;
    private DepthStencilState? _depthStencilState;
    private RawRectangle[]? _scissorRectangles;
    private int _stencilRef;
    private int _viewportCount;
    private long _captureId;
    private int _captured;
    private int _disposed;

    public D3D11DrawStateSnapshot(
        int pixelConstantBufferCount,
        int pixelShaderResourceViewCount,
        int pixelSamplerCount = 1,
        int vertexConstantBufferCount = 1,
        int vertexBufferCount = 1,
        int renderTargetCount = 1,
        bool captureScissorRectangles = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pixelConstantBufferCount);
        ArgumentOutOfRangeException.ThrowIfNegative(pixelShaderResourceViewCount);
        ArgumentOutOfRangeException.ThrowIfNegative(pixelSamplerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(vertexConstantBufferCount);
        ArgumentOutOfRangeException.ThrowIfNegative(vertexBufferCount);
        ArgumentOutOfRangeException.ThrowIfNegative(renderTargetCount);

        _vertexBuffers = new Buffer[vertexBufferCount];
        _vertexStrides = new int[vertexBufferCount];
        _vertexOffsets = new int[vertexBufferCount];
        _vertexConstantBuffers = new Buffer[vertexConstantBufferCount];
        _pixelConstantBuffers = new Buffer[pixelConstantBufferCount];
        _pixelShaderResourceViews = new ShaderResourceView[pixelShaderResourceViewCount];
        _pixelSamplers = new SamplerState[pixelSamplerCount];
        _renderTargetViews = new RenderTargetView[renderTargetCount];
        _captureScissorRectangles = captureScissorRectangles;
    }

    private RenderTargetView? PrimaryRenderTargetView
        => _renderTargetViews.Length > 0 ? _renderTargetViews[0] : null;

    public Scope Capture(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _captured, 1, 0) != 0)
        {
            throw new InvalidOperationException("The D3D11 draw state snapshot is already active.");
        }

        long captureId = Interlocked.Increment(ref _captureId);
        _context = context;
        try
        {
            CaptureState(context);
            return new Scope(this, captureId);
        }
        catch
        {
            ReleaseReferences();
            _context = null;
            Volatile.Write(ref _captured, 0);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Restore(_captureId);
        }
    }

    private void CaptureState(DeviceContext context)
    {
        _inputLayout = context.InputAssembler.InputLayout;
        _primitiveTopology = context.InputAssembler.PrimitiveTopology;
        if (_vertexBuffers.Length > 0)
        {
            context.InputAssembler.GetVertexBuffers(
                0,
                _vertexBuffers.Length,
                _vertexBuffers,
                _vertexStrides,
                _vertexOffsets);
        }

        _vertexShader = context.VertexShader.Get();
        if (_vertexConstantBuffers.Length > 0)
        {
            SharpDxStateAccess.GetConstantBuffers(
                context.VertexShader,
                0,
                _vertexConstantBuffers.Length,
                _vertexConstantBuffers);
        }

        _hullShader = context.HullShader.Get();
        _domainShader = context.DomainShader.Get();
        _geometryShader = context.GeometryShader.Get();
        _pixelShader = context.PixelShader.Get();
        if (_pixelConstantBuffers.Length > 0)
        {
            SharpDxStateAccess.GetConstantBuffers(
                context.PixelShader,
                0,
                _pixelConstantBuffers.Length,
                _pixelConstantBuffers);
        }

        if (_pixelShaderResourceViews.Length > 0)
        {
            SharpDxStateAccess.GetShaderResources(
                context.PixelShader,
                0,
                _pixelShaderResourceViews.Length,
                _pixelShaderResourceViews);
        }

        if (_pixelSamplers.Length > 0)
        {
            SharpDxStateAccess.GetSamplers(
                context.PixelShader,
                0,
                _pixelSamplers.Length,
                _pixelSamplers);
        }

        if (_renderTargetViews.Length > 0)
        {
            SharpDxStateAccess.GetRenderTargets(
                context.OutputMerger,
                _renderTargetViews.Length,
                _renderTargetViews,
                out _depthStencilView);
        }

        _rasterizerState = context.Rasterizer.State;
        fixed (RawViewportF* viewports = _viewports)
        {
            _viewportCount = _viewports.Length;
            SharpDxStateAccess.GetViewports(context.Rasterizer, ref _viewportCount, (nint)viewports);
        }

        _scissorRectangles = _captureScissorRectangles
            ? context.Rasterizer.GetScissorRectangles<RawRectangle>()
            : null;

        _blendState = context.OutputMerger.GetBlendState(out _blendFactor, out _sampleMask);
        _depthStencilState = context.OutputMerger.GetDepthStencilState(out _stencilRef);
    }

    private void Restore(long captureId)
    {
        if (captureId != Volatile.Read(ref _captureId)
            || Interlocked.CompareExchange(ref _captured, 0, 1) != 1)
        {
            return;
        }

        DeviceContext? context = _context;
        try
        {
            if (context != null)
            {
                RestoreState(context);
            }
        }
        finally
        {
            ReleaseReferences();
            _context = null;
        }
    }

    private void RestoreState(DeviceContext context)
    {
        context.InputAssembler.InputLayout = _inputLayout;
        context.InputAssembler.PrimitiveTopology = _primitiveTopology;
        if (_vertexBuffers.Length > 0)
        {
            context.InputAssembler.SetVertexBuffers(0, _vertexBuffers, _vertexStrides, _vertexOffsets);
        }

        context.VertexShader.Set(_vertexShader);
        if (_vertexConstantBuffers.Length > 0)
        {
            context.VertexShader.SetConstantBuffers(0, _vertexConstantBuffers.Length, _vertexConstantBuffers);
        }

        context.HullShader.Set(_hullShader);
        context.DomainShader.Set(_domainShader);
        context.GeometryShader.Set(_geometryShader);
        context.PixelShader.Set(_pixelShader);
        if (_pixelConstantBuffers.Length > 0)
        {
            context.PixelShader.SetConstantBuffers(0, _pixelConstantBuffers.Length, _pixelConstantBuffers);
        }

        if (_pixelShaderResourceViews.Length > 0)
        {
            context.PixelShader.SetShaderResources(0, _pixelShaderResourceViews.Length, _pixelShaderResourceViews);
        }

        if (_pixelSamplers.Length > 0)
        {
            context.PixelShader.SetSamplers(0, _pixelSamplers.Length, _pixelSamplers);
        }

        if (_renderTargetViews.Length > 0)
        {
            context.OutputMerger.SetTargets(_depthStencilView, _renderTargetViews);
        }

        context.OutputMerger.SetBlendState(_blendState, _blendFactor, _sampleMask);
        context.OutputMerger.SetDepthStencilState(_depthStencilState, _stencilRef);
        context.Rasterizer.State = _rasterizerState;
        fixed (RawViewportF* viewports = _viewports)
        {
            context.Rasterizer.SetViewports(viewports, _viewportCount);
        }

        if (_scissorRectangles != null)
        {
            context.Rasterizer.SetScissorRectangles(_scissorRectangles);
        }
    }

    private void ReleaseReferences()
    {
        DisposeAndClear(_vertexBuffers);
        _vertexShader?.Dispose();
        _vertexShader = null;
        DisposeAndClear(_vertexConstantBuffers);
        _hullShader?.Dispose();
        _hullShader = null;
        _domainShader?.Dispose();
        _domainShader = null;
        _geometryShader?.Dispose();
        _geometryShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        DisposeAndClear(_pixelConstantBuffers);
        DisposeAndClear(_pixelShaderResourceViews);
        DisposeAndClear(_pixelSamplers);
        DisposeAndClear(_renderTargetViews);
        _depthStencilView?.Dispose();
        _depthStencilView = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
        _blendState?.Dispose();
        _blendState = null;
        _depthStencilState?.Dispose();
        _depthStencilState = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
        _scissorRectangles = null;
        _viewportCount = 0;
    }

    private static void DisposeAndClear<T>(T[] values) where T : SharpDX.ComObject
    {
        for (int index = 0; index < values.Length; ++index)
        {
            values[index]?.Dispose();
            values[index] = null!;
        }
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly struct Scope(D3D11DrawStateSnapshot owner, long captureId) : IDisposable
    {
        public RenderTargetView? PrimaryRenderTargetView
            => owner.PrimaryRenderTargetView;

        public void Dispose()
            => owner.Restore(captureId);
    }

    // sharpdx keeps its allocation free array fill wrappers internal, typed access keeps its COM ownership intact
    private static class SharpDxStateAccess
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(VertexShaderStage.GetConstantBuffers))]
        public static extern void GetConstantBuffers(
            VertexShaderStage stage,
            int startSlot,
            int count,
            Buffer[] buffers);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(PixelShaderStage.GetConstantBuffers))]
        public static extern void GetConstantBuffers(
            PixelShaderStage stage,
            int startSlot,
            int count,
            Buffer[] buffers);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(PixelShaderStage.GetShaderResources))]
        public static extern void GetShaderResources(
            PixelShaderStage stage,
            int startSlot,
            int count,
            ShaderResourceView[] views);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(PixelShaderStage.GetSamplers))]
        public static extern void GetSamplers(
            PixelShaderStage stage,
            int startSlot,
            int count,
            SamplerState[] samplers);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(OutputMergerStage.GetRenderTargets))]
        public static extern void GetRenderTargets(
            OutputMergerStage stage,
            int count,
            RenderTargetView[] views,
            out DepthStencilView? depthStencilView);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = nameof(RasterizerStage.GetViewports))]
        public static extern void GetViewports(
            RasterizerStage stage,
            ref int count,
            nint viewports);
    }
}
