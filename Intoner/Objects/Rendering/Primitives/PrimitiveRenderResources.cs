using Dalamud.Interface;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using RawViewportF = SharpDX.Mathematics.Interop.RawViewportF;
using SharpDXUtilities = SharpDX.Utilities;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed class PrimitiveRenderResources : GpuUiDeviceResourceHost
{
    private const string ShaderResourceName = "Objects.Rendering.Primitives.Shaders.ObjectPrimitives.hlsl";

    private static readonly PrimitiveLineCorner[] LineCorners =
    [
        new(0f, 1f),
        new(1f, 1f),
        new(1f, -1f),
        new(0f, 1f),
        new(1f, -1f),
        new(0f, -1f),
    ];

    private static readonly GpuShaderBytecode LineVertexShader = GpuShaderCompileService.CreateVertexShader(
        typeof(PrimitiveRenderResources),
        ShaderResourceName,
        "object native line primitive vertex shader",
        "VSLineMain");

    private static readonly GpuShaderBytecode PointVertexShader = GpuShaderCompileService.CreateVertexShader(
        typeof(PrimitiveRenderResources),
        ShaderResourceName,
        "object native point primitive vertex shader",
        "VSPointMain");

    private static readonly GpuShaderBytecode PixelShader = GpuShaderCompileService.CreatePixelShader(
        typeof(PrimitiveRenderResources),
        ShaderResourceName,
        "object native primitive pixel shader");

    private static readonly int LineCornerStride = Marshal.SizeOf<PrimitiveLineCorner>();
    private static readonly int LineInstanceStride = Marshal.SizeOf<PrimitiveLineInstance>();
    private static readonly int PrimitiveVertexStride = Marshal.SizeOf<PrimitiveVertex>();

    private VertexShader? _lineVertexShader;
    private VertexShader? _pointVertexShader;
    private PixelShader? _pixelShader;
    private InputLayout? _lineInputLayout;
    private InputLayout? _pointInputLayout;
    private Buffer? _lineCornerBuffer;
    private Buffer? _lineInstanceBuffer;
    private Buffer? _pointVertexBuffer;
    private Buffer? _screenVertexBuffer;
    private Buffer? _constantBuffer;
    private RasterizerState? _rasterizerState;
    private DepthStencilState? _depthStencilState;
    private BlendState? _blendState;
    private int _lineInstanceCapacity;
    private int _pointVertexCapacity;
    private int _screenVertexCapacity;

    public PrimitiveRenderResources(
        ILogger logger,
        IUiBuilder uiBuilder)
        : base(logger, uiBuilder, "object native primitive renderer initialization failed")
    { }

    public DeviceContext? Context
        => ActiveContext;

    public bool TryEnsure()
        => TryEnsureDevice(out _);

    protected override void CreateDeviceResources(Device device, DeviceContext context)
    {
        _lineVertexShader = LineVertexShader.CreateVertexShader(device);
        _pointVertexShader = PointVertexShader.CreateVertexShader(device);
        _pixelShader = PixelShader.CreatePixelShader(device);
        _lineInputLayout = CreateLineInputLayout(device);
        _pointInputLayout = CreatePointInputLayout(device);
        _lineCornerBuffer = Buffer.Create(device, BindFlags.VertexBuffer, LineCorners);
        _constantBuffer = new Buffer(
            device,
            Marshal.SizeOf<PrimitiveConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
        _rasterizerState = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = false,
            IsFrontCounterClockwise = false,
            IsMultisampleEnabled = false,
            IsScissorEnabled = false,
        });
        _depthStencilState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.Always,
            IsStencilEnabled = false,
        });
        _blendState = CreateBlendState(device);
    }

    public bool TryUploadLineInstances(DeviceContext context, PrimitiveLineInstance[] instances, int instanceCount)
        => TryUploadDynamicBuffer(context, instances, instanceCount, LineInstanceStride, ref _lineInstanceBuffer, ref _lineInstanceCapacity);

    public bool TryUploadPointVertices(DeviceContext context, PrimitiveVertex[] vertices, int vertexCount)
        => TryUploadDynamicBuffer(context, vertices, vertexCount, PrimitiveVertexStride, ref _pointVertexBuffer, ref _pointVertexCapacity);

    public bool TryUploadScreenVertices(DeviceContext context, PrimitiveVertex[] vertices, int vertexCount)
        => TryUploadDynamicBuffer(context, vertices, vertexCount, PrimitiveVertexStride, ref _screenVertexBuffer, ref _screenVertexCapacity);

    public void ApplySharedPipeline(
        DeviceContext context,
        ShaderResourceView? depthView,
        RawViewportF viewport,
        in PrimitiveConstants constants)
    {
        var constantsCopy = constants;
        context.UpdateSubresource(ref constantsCopy, _constantBuffer);
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        context.HullShader.Set(null);
        context.DomainShader.Set(null);
        context.GeometryShader.Set(null);
        context.PixelShader.Set(_pixelShader);
        context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        context.PixelShader.SetShaderResource(0, depthView);
        context.OutputMerger.SetBlendState(_blendState);
        context.OutputMerger.SetDepthStencilState(_depthStencilState, 0);
        context.Rasterizer.State = _rasterizerState;
        context.Rasterizer.SetViewport(viewport);
    }

    public void DrawLines(DeviceContext context, int instanceCount)
    {
        if (instanceCount == 0)
        {
            return;
        }

        context.InputAssembler.InputLayout = _lineInputLayout;
        context.InputAssembler.SetVertexBuffers(
            0,
            new VertexBufferBinding(_lineCornerBuffer, LineCornerStride, 0),
            new VertexBufferBinding(_lineInstanceBuffer, LineInstanceStride, 0));
        context.VertexShader.Set(_lineVertexShader);
        context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        context.DrawInstanced(PrimitiveLineCorner.QuadVertexCount, instanceCount, 0, 0);
    }

    public void DrawPoints(DeviceContext context, int vertexCount)
        => DrawVertices(context, _pointVertexBuffer, vertexCount);

    public void DrawScreen(DeviceContext context, int vertexCount)
        => DrawVertices(context, _screenVertexBuffer, vertexCount);

    private void DrawVertices(DeviceContext context, Buffer? vertexBuffer, int vertexCount)
    {
        if (vertexCount == 0 || vertexBuffer == null)
        {
            return;
        }

        context.InputAssembler.InputLayout = _pointInputLayout;
        context.InputAssembler.SetVertexBuffers(
            0,
            new VertexBufferBinding(vertexBuffer, PrimitiveVertexStride, 0));
        context.VertexShader.Set(_pointVertexShader);
        context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        context.Draw(vertexCount, 0);
    }

    private static InputLayout CreateLineInputLayout(Device device)
        => LineVertexShader.CreateInputLayout(
            device,
            [
                new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 0, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 1, Format.R32G32_Float, InputElement.AppendAligned, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 2, Format.R32_Float, InputElement.AppendAligned, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 3, Format.R32_Float, InputElement.AppendAligned, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 4, Format.R32_Float, InputElement.AppendAligned, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 5, Format.R32_Float, InputElement.AppendAligned, 1, InputClassification.PerInstanceData, 1),
                new InputElement("TEXCOORD", 6, Format.R32_Float, InputElement.AppendAligned, 1, InputClassification.PerInstanceData, 1),
                new InputElement("COLOR", 0, Format.R8G8B8A8_UNorm, InputElement.AppendAligned, 1, InputClassification.PerInstanceData, 1),
            ]);

    private static InputLayout CreatePointInputLayout(Device device)
        => PointVertexShader.CreateInputLayout(
            device,
            [
                new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElement("TEXCOORD", 0, Format.R32_Float, InputElement.AppendAligned, 0),
                new InputElement("TEXCOORD", 1, Format.R32_Float, InputElement.AppendAligned, 0),
                new InputElement("COLOR", 0, Format.R8G8B8A8_UNorm, InputElement.AppendAligned, 0),
                new InputElement("TEXCOORD", 2, Format.R32G32_Float, InputElement.AppendAligned, 0),
                new InputElement("TEXCOORD", 3, Format.R32G32_Float, InputElement.AppendAligned, 0),
                new InputElement("TEXCOORD", 4, Format.R32_Float, InputElement.AppendAligned, 0),
                new InputElement("TEXCOORD", 5, Format.R32_Float, InputElement.AppendAligned, 0),
            ]);

    private static BlendState CreateBlendState(Device device)
    {
        var description = BlendStateDescription.Default();
        description.RenderTarget[0].IsBlendEnabled = true;
        description.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
        description.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        description.RenderTarget[0].BlendOperation = BlendOperation.Add;
        description.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
        description.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        description.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
        description.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        return new BlendState(device, description);
    }

    private bool TryUploadDynamicBuffer<T>(
        DeviceContext context,
        T[] values,
        int valueCount,
        int valueStride,
        ref Buffer? buffer,
        ref int capacity) where T : unmanaged
    {
        if (valueCount == 0)
        {
            return true;
        }

        if (ActiveDevice == null || !TryEnsureDynamicVertexBuffer(valueCount, valueStride, ref buffer, ref capacity))
        {
            return false;
        }

        var targetBuffer = buffer;
        if (targetBuffer == null)
        {
            return false;
        }

        var mapped = context.MapSubresource(targetBuffer, 0, MapMode.WriteDiscard, MapFlags.None);
        try
        {
            SharpDXUtilities.Write(mapped.DataPointer, values, 0, valueCount);
            return true;
        }
        finally
        {
            context.UnmapSubresource(targetBuffer, 0);
        }
    }

    private bool TryEnsureDynamicVertexBuffer(int valueCount, int valueStride, ref Buffer? buffer, ref int capacity)
    {
        Device? device = ActiveDevice;
        if (device == null)
        {
            return false;
        }

        if (buffer != null && capacity >= valueCount)
        {
            return true;
        }

        buffer?.Dispose();
        capacity = Math.Max(valueCount, Math.Max(capacity * 2, 256));
        buffer = new Buffer(
            device,
            new BufferDescription(
                valueStride * capacity,
                ResourceUsage.Dynamic,
                BindFlags.VertexBuffer,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                0));
        return true;
    }

    protected override void DisposeDeviceResources()
    {
        _blendState?.Dispose();
        _blendState = null;
        _depthStencilState?.Dispose();
        _depthStencilState = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _pointVertexBuffer?.Dispose();
        _pointVertexBuffer = null;
        _screenVertexBuffer?.Dispose();
        _screenVertexBuffer = null;
        _lineInstanceBuffer?.Dispose();
        _lineInstanceBuffer = null;
        _lineCornerBuffer?.Dispose();
        _lineCornerBuffer = null;
        _pointInputLayout?.Dispose();
        _pointInputLayout = null;
        _lineInputLayout?.Dispose();
        _lineInputLayout = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _pointVertexShader?.Dispose();
        _pointVertexShader = null;
        _lineVertexShader?.Dispose();
        _lineVertexShader = null;
        _pointVertexCapacity = 0;
        _lineInstanceCapacity = 0;
        _screenVertexCapacity = 0;
    }
}

