using Dalamud.Interface;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using RawViewportF = SharpDX.Mathematics.Interop.RawViewportF;

namespace Intoner.Scene.Rendering;

internal sealed class WorldSurfaceRenderer : IDisposable
{
    private const float ViewDepthBias = 0.001f;

    private readonly ILogger<WorldSurfaceRenderer> _logger;
    private readonly WorldSurfaceResources _resources;
    private readonly D3D11DrawStateSnapshot _drawState = new(
        pixelConstantBufferCount: 1,
        pixelShaderResourceViewCount: 2,
        vertexBufferCount: 0,
        renderTargetCount: 0);

    private bool _loggedDepthFailure;
    private int _loggedDrawFailure;
    private int _loggedFrameSourceFailure;
    private int _loggedProjectionFailure;

    public WorldSurfaceRenderer(
        ILogger<WorldSurfaceRenderer> logger,
        IUiBuilder uiBuilder)
    {
        _logger = logger;
        _resources = new WorldSurfaceResources(logger, uiBuilder);
    }

    public void Draw(
        ReadOnlySpan<WorldSurfaceRenderState> surfaces,
        in SceneRenderFrame frame)
    {
        if (surfaces.IsEmpty)
        {
            return;
        }

        if (!frame.HasProjection)
        {
            if (Interlocked.Exchange(ref _loggedProjectionFailure, 1) == 0)
            {
                _logger.LogWarning("world surface draw skipped because the main render view projection was unavailable");
            }

            return;
        }

        if (!_resources.TryEnsure() || _resources.Context is not { } context)
        {
            return;
        }

        Device? device = _resources.Device;
        if (device == null)
        {
            return;
        }

        _loggedProjectionFailure = 0;
        SceneTextureSize sceneDepthSize = frame.SceneDepthSize;
        ShaderResourceView? sceneDepthView = frame.SceneDepthView;

        if (sceneDepthView != null)
        {
            _loggedDepthFailure = false;
        }

        try
        {
            using D3D11DrawStateSnapshot.Scope renderState = _drawState.Capture(context);
            _resources.ApplyPipeline(context, sceneDepthView, frame.Viewport);
            bool frameTransfersHealthy = true;
            foreach (WorldSurfaceRenderState surfaceState in surfaces)
            {
                WorldSurfaceState surface = surfaceState.Surface;
                if (surface.RequiresSceneDepth && sceneDepthView == null)
                {
                    if (!_loggedDepthFailure)
                    {
                        _loggedDepthFailure = true;
                        _logger.LogWarning("world surfaces using scene occlusion were skipped because scene depth was unavailable");
                    }

                    continue;
                }

                try
                {
                    frameTransfersHealthy &= UpdateFrameTransfer(
                        surfaceState,
                        device,
                        context,
                        out ShaderResourceView? textureView,
                        out GpuFrameDescriptor descriptor);
                    GpuFrameAlphaMode alphaMode = textureView != null
                        ? descriptor.AlphaMode
                        : GpuFrameAlphaMode.Opaque;
                    WorldSurfaceConstants constants = CreateConstants(surface, frame, sceneDepthSize, alphaMode);
                    _resources.Draw(
                        context,
                        constants,
                        textureView ?? _resources.PlaceholderTextureView);
                }
                finally
                {
                    context.PixelShader.SetShaderResource(1, null);
                }
            }

            if (frameTransfersHealthy)
            {
                _loggedFrameSourceFailure = 0;
            }

            _loggedDrawFailure = 0;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedDrawFailure, 1) == 0)
            {
                _logger.LogWarning(ex, "world surface draw callback failed");
            }

            _resources.RequestReset();
        }
    }

    public void Dispose()
    {
        _drawState.Dispose();
        _resources.Dispose();
    }

    private bool UpdateFrameTransfer(
        in WorldSurfaceRenderState renderState,
        Device device,
        DeviceContext context,
        out ShaderResourceView? view,
        out GpuFrameDescriptor descriptor)
    {
        if (renderState.FrameTransfer == null)
        {
            view = null;
            descriptor = default;
            return true;
        }

        try
        {
            _ = renderState.FrameTransfer.TryUpdate(device, context, out view, out descriptor);
            return true;
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _loggedFrameSourceFailure, 1) == 0)
            {
                _logger.LogWarning(
                    ex,
                    "world surface frame source {FrameSourceType} failed",
                    renderState.Surface.FrameSource?.GetType().Name ?? "none");
            }

            view = null;
            descriptor = default;
            return false;
        }
    }

    private static WorldSurfaceConstants CreateConstants(
        WorldSurfaceState surface,
        in SceneRenderFrame frame,
        SceneTextureSize sceneDepthSize,
        GpuFrameAlphaMode alphaMode)
        => new()
        {
            WorldViewProjection = surface.Transform * frame.Projection.ViewProjection,
            WorldView = surface.Transform * frame.Projection.ViewMatrix,
            InverseProjection = frame.Projection.InverseProjection,
            Viewport = new Vector4(frame.Viewport.X, frame.Viewport.Y, frame.Viewport.Width, frame.Viewport.Height),
            DepthParams = new Vector4(
                surface.Occlusion == WorldSurfaceOcclusion.SceneDepth ? 1f : 0f,
                ViewDepthBias,
                frame.Projection.ReverseDepth ? 1f : 0f,
                frame.Projection.ForwardPositive ? 1f : 0f),
            DepthTextureSize = new Vector4(sceneDepthSize.ActualWidth, sceneDepthSize.ActualHeight, 0f, 0f),
            SurfaceParams = new Vector4(surface.Size, surface.TwoSided ? 1f : 0f, 0f),
            Tint = surface.Tint,
            UvRect = surface.UvRect,
            FrameParams = new Vector4((float)alphaMode, 0f, 0f, 0f),
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct WorldSurfaceConstants
    {
        public Matrix4x4 WorldViewProjection;
        public Matrix4x4 WorldView;
        public Matrix4x4 InverseProjection;
        public Vector4 Viewport;
        public Vector4 DepthParams;
        public Vector4 DepthTextureSize;
        public Vector4 SurfaceParams;
        public Vector4 Tint;
        public Vector4 UvRect;
        public Vector4 FrameParams;
    }

    private sealed class WorldSurfaceResources : GpuUiDeviceResourceHost
    {
        private const string ShaderResourceName = "Objects.Rendering.Surfaces.Shaders.WorldSurface.hlsl";
        private const int CheckerSize = 16;
        private const int CheckerCellSize = 4;

        private static readonly GpuShaderBytecode VertexShaderBytecode = GpuShaderCompileService.CreateVertexShader(
            typeof(WorldSurfaceResources),
            ShaderResourceName,
            "world surface vertex shader");

        private static readonly GpuShaderBytecode PixelShaderBytecode = GpuShaderCompileService.CreatePixelShader(
            typeof(WorldSurfaceResources),
            ShaderResourceName,
            "world surface pixel shader");

        private VertexShader? _vertexShader;
        private PixelShader? _pixelShader;
        private Buffer? _constantBuffer;
        private SamplerState? _samplerState;
        private RasterizerState? _rasterizerState;
        private DepthStencilState? _depthStencilState;
        private BlendState? _blendState;
        private GpuTextureView? _placeholderTexture;

        public WorldSurfaceResources(ILogger logger, IUiBuilder uiBuilder)
            : base(logger, uiBuilder, "world surface renderer initialization failed")
        { }

        public DeviceContext? Context
            => ActiveContext;

        public Device? Device
            => ActiveDevice;

        public ShaderResourceView PlaceholderTextureView
            => _placeholderTexture!.View;

        public bool TryEnsure()
            => TryEnsureDevice(out _);

        public void RequestReset()
            => RequestDeviceReset();

        public void ApplyPipeline(
            DeviceContext context,
            ShaderResourceView? sceneDepthView,
            RawViewportF viewport)
        {
            context.InputAssembler.InputLayout = null;
            context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
            context.HullShader.Set(null);
            context.DomainShader.Set(null);
            context.GeometryShader.Set(null);
            context.VertexShader.Set(_vertexShader);
            context.VertexShader.SetConstantBuffer(0, _constantBuffer);
            context.PixelShader.Set(_pixelShader);
            context.PixelShader.SetConstantBuffer(0, _constantBuffer);
            context.PixelShader.SetShaderResource(0, sceneDepthView);
            context.PixelShader.SetSampler(0, _samplerState);
            context.OutputMerger.SetBlendState(_blendState);
            context.OutputMerger.SetDepthStencilState(_depthStencilState, 0);
            context.Rasterizer.State = _rasterizerState;
            context.Rasterizer.SetViewport(viewport);
        }

        public void Draw(
            DeviceContext context,
            in WorldSurfaceConstants constants,
            ShaderResourceView textureView)
        {
            WorldSurfaceConstants constantsCopy = constants;
            context.UpdateSubresource(ref constantsCopy, _constantBuffer);
            context.PixelShader.SetShaderResource(1, textureView);
            context.Draw(6, 0);
        }

        protected override void CreateDeviceResources(Device device, DeviceContext context)
        {
            _vertexShader = VertexShaderBytecode.CreateVertexShader(device);
            _pixelShader = PixelShaderBytecode.CreatePixelShader(device);
            _constantBuffer = new Buffer(
                device,
                Marshal.SizeOf<WorldSurfaceConstants>(),
                ResourceUsage.Default,
                BindFlags.ConstantBuffer,
                CpuAccessFlags.None,
                ResourceOptionFlags.None,
                0);
            _samplerState = new SamplerState(device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                BorderColor = default,
                MinimumLod = 0f,
                MaximumLod = float.MaxValue,
                MipLodBias = 0f,
                MaximumAnisotropy = 1,
            });
            _rasterizerState = new RasterizerState(device, new RasterizerStateDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                IsDepthClipEnabled = true,
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
            _placeholderTexture = GpuTextureView.CreateRgba(device, CheckerSize, CheckerSize, CreateCheckerPixels());
        }

        protected override void DisposeDeviceResources()
        {
            _placeholderTexture?.Dispose();
            _placeholderTexture = null;
            _blendState?.Dispose();
            _blendState = null;
            _depthStencilState?.Dispose();
            _depthStencilState = null;
            _rasterizerState?.Dispose();
            _rasterizerState = null;
            _samplerState?.Dispose();
            _samplerState = null;
            _constantBuffer?.Dispose();
            _constantBuffer = null;
            _pixelShader?.Dispose();
            _pixelShader = null;
            _vertexShader?.Dispose();
            _vertexShader = null;
        }

        private static BlendState CreateBlendState(Device device)
        {
            BlendStateDescription description = BlendStateDescription.Default();
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

        private static byte[] CreateCheckerPixels()
        {
            var pixels = new byte[CheckerSize * CheckerSize * 4];
            for (int y = 0; y < CheckerSize; ++y)
            {
                for (int x = 0; x < CheckerSize; ++x)
                {
                    bool accent = ((x / CheckerCellSize) + (y / CheckerCellSize)) % 2 == 0;
                    int offset = ((y * CheckerSize) + x) * 4;
                    pixels[offset] = accent ? (byte)154 : (byte)38;
                    pixels[offset + 1] = accent ? (byte)112 : (byte)40;
                    pixels[offset + 2] = accent ? (byte)214 : (byte)46;
                    pixels[offset + 3] = 255;
                }
            }

            return pixels;
        }
    }
}
