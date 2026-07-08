using Dalamud.Interface;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.Preview.Rendering;

internal sealed class ViewportResources : GpuUiDeviceResourceHost
{
    private const string ShaderResourceName = "Objects.Preview.Rendering.Shaders.Viewport.hlsl";

    private static readonly GpuShaderBytecode MeshVertexShader = GpuShaderCompileService.CreateVertexShader(
        typeof(ViewportResources),
        ShaderResourceName,
        "preview viewport mesh vertex shader",
        "VSMeshMain");

    private static readonly GpuShaderBytecode BackgroundVertexShader = GpuShaderCompileService.CreateVertexShader(
        typeof(ViewportResources),
        ShaderResourceName,
        "preview viewport background vertex shader",
        "VSBackgroundMain");

    private static readonly GpuShaderBytecode MeshPixelShader = GpuShaderCompileService.CreatePixelShader(
        typeof(ViewportResources),
        ShaderResourceName,
        "preview viewport mesh pixel shader",
        "PSMeshMain");

    private static readonly GpuShaderBytecode BackgroundPixelShader = GpuShaderCompileService.CreatePixelShader(
        typeof(ViewportResources),
        ShaderResourceName,
        "preview viewport background pixel shader",
        "PSBackgroundMain");

    private VertexShader? _meshVertexShader;
    private VertexShader? _backgroundVertexShader;
    private PixelShader? _meshPixelShader;
    private PixelShader? _backgroundPixelShader;
    private InputLayout? _meshInputLayout;
    private Buffer? _frameConstantBuffer;
    private Buffer? _materialConstantBuffer;
    private RasterizerState? _rasterizerState;
    private DepthStencilState? _backgroundDepthStencilState;
    private DepthStencilState? _opaqueDepthStencilState;
    private DepthStencilState? _transparentDepthStencilState;
    private BlendState? _blendState;
    private SamplerState? _samplerState;
    private GpuTextureView? _whiteTexture;

    public ViewportResources(ILogger logger, IUiBuilder uiBuilder)
        : base(logger, uiBuilder, "preview viewport renderer initialization failed")
    { }

    public Device? Device
        => ActiveDevice;

    public GpuTextureView? WhiteTexture
        => _whiteTexture;

    public bool TryEnsureReady(out bool resetDeviceResources)
        => TryEnsureDevice(out resetDeviceResources);

    public bool TryGetDrawContext(out ViewportResources.DrawContext drawContext)
    {
        drawContext = default;
        if (ActiveContext is null
         || _meshVertexShader is null
         || _backgroundVertexShader is null
         || _meshPixelShader is null
         || _backgroundPixelShader is null
         || _meshInputLayout is null
         || _frameConstantBuffer is null
         || _materialConstantBuffer is null
         || _rasterizerState is null
         || _backgroundDepthStencilState is null
         || _opaqueDepthStencilState is null
         || _transparentDepthStencilState is null
         || _blendState is null
         || _samplerState is null)
        {
            return false;
        }

        drawContext = new ViewportResources.DrawContext(
            ActiveContext,
            _meshVertexShader,
            _backgroundVertexShader,
            _meshPixelShader,
            _backgroundPixelShader,
            _meshInputLayout,
            _frameConstantBuffer,
            _materialConstantBuffer,
            _rasterizerState,
            _backgroundDepthStencilState,
            _opaqueDepthStencilState,
            _transparentDepthStencilState,
            _blendState,
            _samplerState);
        return true;
    }

    public void RequestReset()
        => RequestDeviceReset();

    public void Clear()
        => ClearDeviceResources();

    protected override void CreateDeviceResources(Device device, DeviceContext context)
    {
        _meshVertexShader = MeshVertexShader.CreateVertexShader(device);
        _backgroundVertexShader = BackgroundVertexShader.CreateVertexShader(device);
        _meshPixelShader = MeshPixelShader.CreatePixelShader(device);
        _backgroundPixelShader = BackgroundPixelShader.CreatePixelShader(device);
        _meshInputLayout = MeshVertexShader.CreateInputLayout(
            device,
            [
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("NORMAL", 0, Format.R32G32B32_Float, InputElement.AppendAligned, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, InputElement.AppendAligned, 0),
            ]);
        _frameConstantBuffer = new Buffer(
            device,
            Marshal.SizeOf<FrameConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
        _materialConstantBuffer = new Buffer(
            device,
            Marshal.SizeOf<MaterialConstants>(),
            ResourceUsage.Default,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.None,
            ResourceOptionFlags.None,
            0);
        _rasterizerState = new RasterizerState(device, new RasterizerStateDescription
        {
            CullMode = CullMode.None,
            FillMode = FillMode.Solid,
            IsDepthClipEnabled = true,
            IsFrontCounterClockwise = false,
            IsMultisampleEnabled = false,
            IsScissorEnabled = false,
        });
        _backgroundDepthStencilState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.Always,
            IsStencilEnabled = false,
        });
        _opaqueDepthStencilState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = false,
        });
        _transparentDepthStencilState = new DepthStencilState(device, new DepthStencilStateDescription
        {
            IsDepthEnabled = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthComparison = Comparison.LessEqual,
            IsStencilEnabled = false,
        });
        _blendState = CreateBlendState(device);
        _samplerState = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunction = Comparison.Never,
            MinimumLod = 0,
            MaximumLod = float.MaxValue,
        });
        _whiteTexture = GpuTextureView.CreateSolidRgba(device, 255, 255, 255, 255);
    }

    protected override void DisposeDeviceResources()
    {
        _whiteTexture?.Dispose();
        _whiteTexture = null;
        _samplerState?.Dispose();
        _samplerState = null;
        _blendState?.Dispose();
        _blendState = null;
        _transparentDepthStencilState?.Dispose();
        _transparentDepthStencilState = null;
        _opaqueDepthStencilState?.Dispose();
        _opaqueDepthStencilState = null;
        _backgroundDepthStencilState?.Dispose();
        _backgroundDepthStencilState = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
        _materialConstantBuffer?.Dispose();
        _materialConstantBuffer = null;
        _frameConstantBuffer?.Dispose();
        _frameConstantBuffer = null;
        _meshInputLayout?.Dispose();
        _meshInputLayout = null;
        _backgroundPixelShader?.Dispose();
        _backgroundPixelShader = null;
        _meshPixelShader?.Dispose();
        _meshPixelShader = null;
        _backgroundVertexShader?.Dispose();
        _backgroundVertexShader = null;
        _meshVertexShader?.Dispose();
        _meshVertexShader = null;
    }

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

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct DrawContext(
        DeviceContext Context,
        VertexShader MeshVertexShader,
        VertexShader BackgroundVertexShader,
        PixelShader MeshPixelShader,
        PixelShader BackgroundPixelShader,
        InputLayout MeshInputLayout,
        Buffer FrameConstantBuffer,
        Buffer MaterialConstantBuffer,
        RasterizerState RasterizerState,
        DepthStencilState BackgroundDepthStencilState,
        DepthStencilState OpaqueDepthStencilState,
        DepthStencilState TransparentDepthStencilState,
        BlendState BlendState,
        SamplerState SamplerState);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameConstants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 LightDirection;
        public Vector4 BackgroundTop;
        public Vector4 BackgroundBottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MaterialConstants
    {
        public Vector4 UntexturedDiffuseColor;
        public Vector4 MaterialParams;
    }
}
