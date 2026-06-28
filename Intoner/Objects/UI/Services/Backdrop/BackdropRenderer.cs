using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Intoner.Services.Gpu;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;
using D3D11MapFlags = SharpDX.Direct3D11.MapFlags;
using Device = SharpDX.Direct3D11.Device;
using KernelDevice = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Intoner.Objects.UI.Services.Backdrop;

// adapted from https://github.com/itsRythem/ImGui-Blur
internal sealed unsafe class BackdropRenderer : IDisposable
{
    internal const string ShaderResourceName = "Objects.UI.Shaders.Window.ObjectWindowBlur.hlsl";
    private const int BlurIterations = 3;
    private const float BlurOffset = 1.90f;
    private const float BlurNoise = 0.01f;
    private const float BlurScale = 0.42f;

    private static readonly Lazy<byte[]> VertexShaderBytecode = new(
        () => GpuShaderCompileService.CreateVertexShaderBytecode(
            typeof(BackdropRenderer),
            ShaderResourceName,
            "object window blur vertex shader"));

    private static readonly Lazy<byte[]> DownsampleShaderBytecode = new(
        () => GpuShaderCompileService.CreatePixelShaderBytecode(
            typeof(BackdropRenderer),
            ShaderResourceName,
            "object window blur downsample shader",
            "PSDownsample"));

    private static readonly Lazy<byte[]> UpsampleShaderBytecode = new(
        () => GpuShaderCompileService.CreatePixelShaderBytecode(
            typeof(BackdropRenderer),
            ShaderResourceName,
            "object window blur upsample shader",
            "PSUpsample"));

    private static readonly Lazy<byte[]> CompositeShaderBytecode = new(
        () => GpuShaderCompileService.CreatePixelShaderBytecode(
            typeof(BackdropRenderer),
            ShaderResourceName,
            "object window blur composite shader",
            "PSComposite"));

    private static readonly ImDrawCallback BlurProcessCallback = ProcessBlurCallback;

    private readonly ILogger<BackdropRenderer> _logger;
    private readonly IUiBuilder _uiBuilder;
    private readonly BackdropEffectRegistry _effects;
    private readonly List<BackdropFramebuffer> _blurFramebuffers = [];
    private BackdropBlurConstants _blurConstants;

    private Device? _device;
    private DeviceContext? _context;
    private VertexShader? _vertexShader;
    private PixelShader? _downsampleShader;
    private PixelShader? _upsampleShader;
    private PixelShader? _compositeShader;
    private InputLayout? _inputLayout;
    private Buffer? _vertexBuffer;
    private Buffer? _blurConstantBuffer;
    private Buffer? _surfaceConstantBuffer;
    private SamplerState? _mirrorSampler;
    private RasterizerState? _rasterizerState;
    private RasterizerState? _scissorRasterizerState;
    private DepthStencilState? _depthStencilState;
    private BlendState? _blendState;
    private Texture2D? _capturedSourceTexture;
    private ShaderResourceView? _capturedSourceShaderResourceView;
    private Format _capturedSourceFormat = Format.Unknown;
    private Texture2D? _gameSourceCopyTexture;
    private ShaderResourceView? _gameSourceShaderResourceView;
    private Format _gameSourceFormat = Format.Unknown;
    private BackdropFramebuffer? _compositedSourceFramebuffer;
    private BackdropFramebuffer? _outputFramebuffer;
    private int _frameWidth;
    private int _frameHeight;
    private int _capturedSourceWidth;
    private int _capturedSourceHeight;
    private int _gameSourceWidth;
    private int _gameSourceHeight;
    private int _preparedFrame = -1;
    private int _processedFrame = -1;
    private int _queuedFrame = -1;
    private bool _disposed;
    private bool _loggedInitializationFailure;
    private bool _loggedGameBackBufferFailure;
    private bool _pendingRuntimeReset;

    public BackdropRenderer(
        ILogger<BackdropRenderer> logger,
        IUiBuilder uiBuilder,
        BackdropEffectRegistrationService effectRegistrations)
    {
        _logger = logger;
        _uiBuilder = uiBuilder;
        _effects = effectRegistrations.CreateRegistry(this);
    }

    /// <summary> registers a lazy backdrop effect factory and resets any cached effect instance for that type </summary>
    public BackdropRenderer RegisterEffect<T>(Func<BackdropRenderer, T> factory)
        where T : BackdropEffectBase
    {
        _effects.Register(factory);
        return this;
    }

    /// <summary> resolves a registered backdrop effect by type </summary>
    public T GetEffect<T>()
        where T : BackdropEffectBase
        => _effects.Get<T>();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeRuntimeResources();
    }

    private static unsafe void ProcessBlurCallback(ImDrawList* _, ImDrawCmd* cmd)
    {
        var handle = GCHandle.FromIntPtr((nint)cmd->UserCallbackData);
        try
        {
            if (handle.Target is BackdropRenderer renderer)
            {
                renderer.ProcessBackdropFrame();
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private bool TryPrepareFrame()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (_pendingRuntimeReset)
        {
            DisposeRuntimeResources();
            _pendingRuntimeReset = false;
        }

        var frame = ImGui.GetFrameCount();
        if (_preparedFrame == frame)
        {
            return HasPreparedFramebuffers();
        }

        if (!TryEnsureDeviceResources())
        {
            return false;
        }

        if (!TryUpdateFrameSizeFromViewport())
        {
            return false;
        }

        EnsureFramebuffers();
        var prepared = HasPreparedFramebuffers();
        _preparedFrame = prepared ? frame : -1;
        if (!prepared)
        {
            _processedFrame = -1;
        }

        return prepared;
    }

    internal bool CanDrawRegion(ImDrawListPtr drawList, Vector2 min, Vector2 max)
        => !_disposed
            && !drawList.IsNull
            && max.X > min.X
            && max.Y > min.Y
            && IsMainViewportWindow();

    private bool HasPreparedFramebuffers()
        => _compositedSourceFramebuffer?.ShaderResourceView is not null
            && _outputFramebuffer?.ShaderResourceView is not null;

    /// <summary> prepares a framebuffer region that shaders can consume </summary>
    internal bool TryPrepareBackdropRegion(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float rounding,
        ImDrawFlags roundingFlags,
        out BackdropRegion region)
    {
        region = default;
        if (!CanDrawRegion(drawList, min, max) || !TryPrepareFrame())
        {
            return false;
        }

        QueueProcessCallback(drawList);
        if (!TryCreateBackdropRegion(min, max, rounding, roundingFlags, out region))
        {
            return false;
        }

        return true;
    }

    /// <summary> resolves blur draw info for a prepared backdrop region </summary>
    internal bool TryCreateBlurDraw(in BackdropRegion region, out TextureDrawInfo draw)
    {
        draw = default;
        if (_outputFramebuffer?.ShaderResourceView is null)
        {
            return false;
        }

        draw = new TextureDrawInfo(
            _outputFramebuffer.ShaderResourceView.NativePointer,
            region.UvMin,
            region.UvMax);
        return true;
    }

    /// <summary> ensures an effect framebuffer exists at full viewport size </summary>
    internal bool TryEnsureEffectFramebuffer(ref BackdropFramebuffer? framebuffer)
    {
        if (_device is null || !TryPrepareFrame())
        {
            return false;
        }

        if (framebuffer is not null
            && framebuffer.Width == _frameWidth
            && framebuffer.Height == _frameHeight)
        {
            return true;
        }

        framebuffer?.Dispose();
        framebuffer = CreateFramebuffer(_device, _frameWidth, _frameHeight);
        return true;
    }

    internal bool TryEnsureEffectShader(Lazy<byte[]> shaderBytecode, ref PixelShader? shader)
    {
        if (_device is null && !TryPrepareFrame())
        {
            return false;
        }

        if (_device is null)
        {
            return false;
        }

        if (shader is not null)
        {
            return true;
        }

        try
        {
            shader = new PixelShader(_device, shaderBytecode.Value);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object window backdrop effect shader initialization failed");
            _pendingRuntimeReset = true;
            return false;
        }
    }

    /// <summary> renders an effect shader over the captured backdrop </summary>
    internal bool TryRenderEffect(
        PixelShader shader,
        BackdropFramebuffer framebuffer,
        in BackdropSurfaceConstants surfaceConstants,
        Buffer? effectConstantBuffer,
        int scissorMinX,
        int scissorMinY,
        int scissorMaxX,
        int scissorMaxY,
        ref int clearedFrame,
        string failureLogMessage)
    {
        if (_context is null
            || _vertexShader is null
            || _inputLayout is null
            || _vertexBuffer is null
            || _surfaceConstantBuffer is null
            || _mirrorSampler is null
            || _rasterizerState is null
            || _scissorRasterizerState is null
            || _depthStencilState is null
            || _blendState is null
            || _compositedSourceFramebuffer?.ShaderResourceView is null
            || _outputFramebuffer?.ShaderResourceView is null)
        {
            return false;
        }

        ProcessBackdropFrame();
        if (_processedFrame != ImGui.GetFrameCount())
        {
            return false;
        }

        try
        {
            using var state = D3D11RenderStateSnapshot.Capture(
                _context,
                pixelConstantBufferCount: 1,
                pixelShaderResourceViewCount: 2,
                captureScissorRectangles: true);
            ApplyFullscreenPipeline();
            if (clearedFrame != ImGui.GetFrameCount())
            {
                _context.ClearRenderTargetView(framebuffer.RenderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0f, 0f, 0f, 0f));
                clearedFrame = ImGui.GetFrameCount();
            }

            RenderEffectPass(framebuffer, shader, surfaceConstants, effectConstantBuffer, scissorMinX, scissorMinY, scissorMaxX, scissorMaxY);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureLogMessage);
            _pendingRuntimeReset = true;
            return false;
        }
    }

    internal BackdropSurfaceConstants CreateSurfaceConstants(in BackdropRegion region)
        => new()
        {
            BackdropSize = new Vector4(_frameWidth, _frameHeight, 0f, 0f),
            RegionRect = new Vector4(region.RegionMin.X, region.RegionMin.Y, region.RegionSize.X, region.RegionSize.Y),
            CornerRadii = region.CornerRadii,
        };

    private void QueueProcessCallback(ImDrawListPtr drawList)
    {
        var frame = ImGui.GetFrameCount();
        if (_queuedFrame == frame)
        {
            return;
        }

        var handle = GCHandle.Alloc(this);
        drawList.AddCallback(BlurProcessCallback, (void*)GCHandle.ToIntPtr(handle));
        _queuedFrame = frame;
    }

    internal bool TryEnsureEffectConstantBuffer<TConstants>(ref Buffer? constantBuffer)
        where TConstants : unmanaged
    {
        if (_device is null && !TryPrepareFrame())
        {
            return false;
        }

        if (_device is null)
        {
            return false;
        }

        var requiredSize = Marshal.SizeOf<TConstants>();
        if (constantBuffer is not null && constantBuffer.Description.SizeInBytes == requiredSize)
        {
            return true;
        }

        constantBuffer?.Dispose();
        constantBuffer = new Buffer(
            _device,
            requiredSize,
            ResourceUsage.Dynamic,
            BindFlags.ConstantBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0);
        return true;
    }

    internal bool TryUpdateConstantBuffer<TConstants>(Buffer constantBuffer, in TConstants constants)
        where TConstants : unmanaged
    {
        if (_context is null)
        {
            return false;
        }

        var dataBox = _context.MapSubresource(constantBuffer, 0, MapMode.WriteDiscard, D3D11MapFlags.None);
        try
        {
            *(TConstants*)dataBox.DataPointer = constants;
            return true;
        }
        finally
        {
            _context.UnmapSubresource(constantBuffer, 0);
        }
    }
    internal void QueueEffectCallback(ImDrawListPtr drawList, ImDrawCallback callback, object callbackData)
    {
        var handle = GCHandle.Alloc(callbackData);
        drawList.AddCallback(callback, (void*)GCHandle.ToIntPtr(handle));
    }

    private bool TryEnsureDeviceResources()
    {
        if (_device is not null && _context is not null)
        {
            return true;
        }

        var deviceHandle = _uiBuilder.DeviceHandle;
        if (deviceHandle == nint.Zero)
        {
            return false;
        }

        Device? device = null;
        DeviceContext? context = null;
        try
        {
            Marshal.AddRef(deviceHandle);
            device = new Device(deviceHandle);
            context = device.ImmediateContext;

            _vertexShader = new VertexShader(device, VertexShaderBytecode.Value);
            _downsampleShader = new PixelShader(device, DownsampleShaderBytecode.Value);
            _upsampleShader = new PixelShader(device, UpsampleShaderBytecode.Value);
            _compositeShader = new PixelShader(device, CompositeShaderBytecode.Value);
            _inputLayout = new InputLayout(
                device,
                SharpDX.D3DCompiler.ShaderSignature.GetInputSignature(VertexShaderBytecode.Value),
                [
                    new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0),
                    new InputElement("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                ]);

            FullscreenVertex[] vertices =
            [
                new(new Vector2(-1f, 1f), new Vector2(0f, 0f)),
                new(new Vector2(1f, 1f), new Vector2(1f, 0f)),
                new(new Vector2(-1f, -1f), new Vector2(0f, 1f)),
                new(new Vector2(1f, -1f), new Vector2(1f, 1f)),
            ];

            _vertexBuffer = Buffer.Create(device, BindFlags.VertexBuffer, vertices);
            _blurConstantBuffer = new Buffer(
                device,
                Marshal.SizeOf<BackdropBlurConstants>(),
                ResourceUsage.Dynamic,
                BindFlags.ConstantBuffer,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                0);
            _surfaceConstantBuffer = new Buffer(
                device,
                Marshal.SizeOf<BackdropSurfaceConstants>(),
                ResourceUsage.Dynamic,
                BindFlags.ConstantBuffer,
                CpuAccessFlags.Write,
                ResourceOptionFlags.None,
                0);

            var samplerDescription = new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                MaximumLod = float.MaxValue,
            };
            _mirrorSampler = new SamplerState(device, samplerDescription);

            _rasterizerState = new RasterizerState(device, new RasterizerStateDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                IsDepthClipEnabled = false,
                IsScissorEnabled = false,
                IsFrontCounterClockwise = false,
                IsMultisampleEnabled = false,
            });
            _scissorRasterizerState = new RasterizerState(device, new RasterizerStateDescription
            {
                CullMode = CullMode.None,
                FillMode = FillMode.Solid,
                IsDepthClipEnabled = false,
                IsScissorEnabled = true,
                IsFrontCounterClockwise = false,
                IsMultisampleEnabled = false,
            });

            _depthStencilState = new DepthStencilState(device, new DepthStencilStateDescription
            {
                IsDepthEnabled = false,
                DepthWriteMask = DepthWriteMask.Zero,
                DepthComparison = Comparison.Always,
                IsStencilEnabled = false,
            });

            var blendStateDescription = BlendStateDescription.Default();
            blendStateDescription.RenderTarget[0].IsBlendEnabled = false;
            _blendState = new BlendState(device, blendStateDescription);

            _device = device;
            _context = context;
            return true;
        }
        catch (Exception ex)
        {
            context?.Dispose();
            device?.Dispose();
            DisposeRuntimeResources();

            if (!_loggedInitializationFailure)
            {
                _loggedInitializationFailure = true;
                _logger.LogWarning(ex, "object window blur renderer initialization failed");
            }

            return false;
        }
    }

    private static bool TryGetFrameSizeFromViewport(out int width, out int height)
    {
        var viewport = ImGui.GetMainViewport();
        if (viewport.IsNull)
        {
            width = 0;
            height = 0;
            return false;
        }

        var framebufferScale = ImGui.GetIO().DisplayFramebufferScale;
        width = Math.Max(1, (int)MathF.Round(viewport.Size.X * framebufferScale.X));
        height = Math.Max(1, (int)MathF.Round(viewport.Size.Y * framebufferScale.Y));
        return true;
    }

    private bool TryUpdateFrameSizeFromViewport()
    {
        if (!TryGetFrameSizeFromViewport(out var width, out var height))
        {
            return false;
        }

        _frameWidth = width;
        _frameHeight = height;
        return true;
    }

    private void EnsureFramebuffers()
    {
        if (_device is null)
        {
            return;
        }

        var scaledWidth = Math.Max(1, (int)MathF.Round(_frameWidth * BlurScale));
        var scaledHeight = Math.Max(1, (int)MathF.Round(_frameHeight * BlurScale));
        if (_outputFramebuffer is not null
            && _outputFramebuffer.Width == scaledWidth
            && _outputFramebuffer.Height == scaledHeight
            && _compositedSourceFramebuffer is not null
            && _compositedSourceFramebuffer.Width == _frameWidth
            && _compositedSourceFramebuffer.Height == _frameHeight
            && _blurFramebuffers.Count == BlurIterations + 1)
        {
            return;
        }

        DisposeFramebuffers();
        for (var index = 0; index <= BlurIterations; index++)
        {
            var divisor = 1 << index;
            _blurFramebuffers.Add(CreateFramebuffer(_device, Math.Max(1, scaledWidth / divisor), Math.Max(1, scaledHeight / divisor)));
        }

        _compositedSourceFramebuffer = CreateFramebuffer(_device, _frameWidth, _frameHeight);
        _outputFramebuffer = CreateFramebuffer(_device, scaledWidth, scaledHeight);
    }

    private void ProcessBackdropFrame()
    {
        var frame = ImGui.GetFrameCount();
        if (_processedFrame == frame)
        {
            return;
        }

        if (_preparedFrame != frame
            || _context is null
            || _vertexShader is null
            || _downsampleShader is null
            || _upsampleShader is null
            || _compositeShader is null
            || _inputLayout is null
            || _vertexBuffer is null
            || _surfaceConstantBuffer is null
            || _mirrorSampler is null
            || _rasterizerState is null
            || _depthStencilState is null
            || _blendState is null
            || _compositedSourceFramebuffer?.ShaderResourceView is null
            || _outputFramebuffer?.ShaderResourceView is null
            || _blurFramebuffers.Count == 0)
        {
            return;
        }

        try
        {
            using var state = D3D11RenderStateSnapshot.Capture(
                _context,
                pixelConstantBufferCount: 1,
                pixelShaderResourceViewCount: 2,
                captureScissorRectangles: true);
            if (!TryCaptureGameBackBuffer()
                || _gameSourceShaderResourceView is null
                || !TryCaptureCurrentRenderTarget(state.PrimaryRenderTargetView)
                || _capturedSourceShaderResourceView is null
                )
            {
                return;
            }

            ApplyFullscreenPipeline();

            RenderCompositePass(_compositedSourceFramebuffer, _gameSourceShaderResourceView, _capturedSourceShaderResourceView);
            RenderShaderPass(_blurFramebuffers[0], _compositedSourceFramebuffer.ShaderResourceView, _downsampleShader);
            for (var index = 0; index < BlurIterations; index++)
            {
                RenderShaderPass(_blurFramebuffers[index + 1], _blurFramebuffers[index].ShaderResourceView, _downsampleShader);
            }

            for (var index = BlurIterations; index > 0; index--)
            {
                RenderShaderPass(_blurFramebuffers[index - 1], _blurFramebuffers[index].ShaderResourceView, _upsampleShader);
            }

            RenderShaderPass(_outputFramebuffer, _blurFramebuffers[0].ShaderResourceView, _upsampleShader);
            _processedFrame = frame;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "object window blur frame processing failed");
            _processedFrame = -1;
            _pendingRuntimeReset = true;
        }
    }

    private bool TryCreateBackdropRegion(
        Vector2 min,
        Vector2 max,
        float rounding,
        ImDrawFlags roundingFlags,
        out BackdropRegion region)
    {
        var viewport = ImGui.GetMainViewport();
        if (viewport.IsNull)
        {
            region = default;
            return false;
        }

        var framebufferScale = ImGui.GetIO().DisplayFramebufferScale;
        var viewportPos = viewport.Pos;
        var pixelMin = new Vector2(
            MathF.Max(0f, MathF.Floor((min.X - viewportPos.X) * framebufferScale.X)),
            MathF.Max(0f, MathF.Floor((min.Y - viewportPos.Y) * framebufferScale.Y)));
        var pixelMax = new Vector2(
            MathF.Min(_frameWidth, MathF.Ceiling((max.X - viewportPos.X) * framebufferScale.X)),
            MathF.Min(_frameHeight, MathF.Ceiling((max.Y - viewportPos.Y) * framebufferScale.Y)));
        var regionSize = pixelMax - pixelMin;
        if (regionSize.X < 1f || regionSize.Y < 1f)
        {
            region = default;
            return false;
        }

        var scaledRounding = Math.Clamp(
            rounding * MathF.Max(framebufferScale.X, framebufferScale.Y),
            0f,
            MathF.Min(regionSize.X, regionSize.Y) * 0.5f);
        var cornerRadii = ResolveCornerRadii(scaledRounding, roundingFlags);
        region = new BackdropRegion
        {
            RegionMin = pixelMin,
            RegionSize = regionSize,
            CornerRadii = cornerRadii,
            UvMin = new Vector2(pixelMin.X / _frameWidth, pixelMin.Y / _frameHeight),
            UvMax = new Vector2(pixelMax.X / _frameWidth, pixelMax.Y / _frameHeight),
            ScissorMinX = (int)pixelMin.X,
            ScissorMinY = (int)pixelMin.Y,
            ScissorMaxX = Math.Max((int)pixelMin.X + 1, (int)pixelMax.X),
            ScissorMaxY = Math.Max((int)pixelMin.Y + 1, (int)pixelMax.Y),
        };
        return true;
    }

    private bool TryCaptureCurrentRenderTarget(RenderTargetView? renderTargetView)
    {
        if (_context is null || renderTargetView is null)
        {
            return false;
        }

        using var sourceResource = renderTargetView.Resource;
        using var sourceTexture = sourceResource.QueryInterface<Texture2D>();
        var sourceDescription = sourceTexture.Description;
        if (sourceDescription.Width <= 0 || sourceDescription.Height <= 0)
        {
            return false;
        }

        var sourceFormat = ResolveCaptureFormat(renderTargetView.Description.Format, sourceDescription.Format);
        if (!TryEnsureCapturedSourceTexture(sourceDescription.Width, sourceDescription.Height, sourceFormat))
        {
            return false;
        }

        if (sourceDescription.SampleDescription.Count > 1)
        {
            _context.ResolveSubresource(sourceTexture, 0, _capturedSourceTexture, 0, sourceFormat);
        }
        else
        {
            _context.CopyResource(sourceTexture, _capturedSourceTexture);
        }

        return true;
    }

    private bool TryCaptureGameBackBuffer()
    {
        if (_context is null)
        {
            return false;
        }

        var device = KernelDevice.Instance();
        if (device is null
            || device->SwapChain is null
            || device->SwapChain->BackBuffer is null
            || device->SwapChain->BackBuffer->D3D11Texture2D is null)
        {
            if (!_loggedGameBackBufferFailure)
            {
                _loggedGameBackBufferFailure = true;
                _logger.LogWarning("object window blur game backbuffer is unavailable");
            }

            return false;
        }

        _loggedGameBackBufferFailure = false;
        var nativePointer = (nint)device->SwapChain->BackBuffer->D3D11Texture2D;
        if (nativePointer == nint.Zero)
        {
            return false;
        }

        Texture2D? backBufferTexture = null;
        Marshal.AddRef(nativePointer);
        try
        {
            backBufferTexture = new Texture2D(nativePointer);
            var backBufferDescription = backBufferTexture.Description;
            if (backBufferDescription.Width <= 0 || backBufferDescription.Height <= 0)
            {
                return false;
            }

            if (!TryEnsureGameSourceTexture(backBufferDescription.Width, backBufferDescription.Height, backBufferDescription.Format))
            {
                return false;
            }

            if (backBufferDescription.SampleDescription.Count > 1)
            {
                _context.ResolveSubresource(backBufferTexture, 0, _gameSourceCopyTexture, 0, backBufferDescription.Format);
            }
            else
            {
                _context.CopyResource(backBufferTexture, _gameSourceCopyTexture);
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!_loggedGameBackBufferFailure)
            {
                _loggedGameBackBufferFailure = true;
                _logger.LogWarning(ex, "object window blur game backbuffer capture failed");
            }

            return false;
        }
        finally
        {
            backBufferTexture?.Dispose();
        }
    }

    private bool TryEnsureCapturedSourceTexture(int width, int height, Format format)
    {
        if (_device is null)
        {
            return false;
        }

        if (_capturedSourceTexture is not null
            && _capturedSourceShaderResourceView is not null
            && _capturedSourceWidth == width
            && _capturedSourceHeight == height
            && _capturedSourceFormat == format)
        {
            return true;
        }

        DisposeCapturedSourceTexture();

        _capturedSourceTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });
        _capturedSourceShaderResourceView = new ShaderResourceView(_device, _capturedSourceTexture);
        _capturedSourceWidth = width;
        _capturedSourceHeight = height;
        _capturedSourceFormat = format;
        return true;
    }

    private bool TryEnsureGameSourceTexture(int width, int height, Format format)
    {
        if (_device is null)
        {
            return false;
        }

        if (_gameSourceCopyTexture is not null
            && _gameSourceShaderResourceView is not null
            && _gameSourceWidth == width
            && _gameSourceHeight == height
            && _gameSourceFormat == format)
        {
            return true;
        }

        DisposeGameSourceTexture();

        _gameSourceCopyTexture = new Texture2D(_device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });
        _gameSourceShaderResourceView = new ShaderResourceView(_device, _gameSourceCopyTexture);
        _gameSourceWidth = width;
        _gameSourceHeight = height;
        _gameSourceFormat = format;
        return true;
    }

    private static Format ResolveCaptureFormat(Format renderTargetFormat, Format sourceFormat)
        => renderTargetFormat != Format.Unknown
            ? renderTargetFormat
            : sourceFormat;

    private void ApplyFullscreenPipeline()
    {
        if (_context is null
            || _inputLayout is null
            || _vertexBuffer is null
            || _vertexShader is null
            || _blendState is null
            || _depthStencilState is null
            || _rasterizerState is null)
        {
            return;
        }

        _context.InputAssembler.InputLayout = _inputLayout;
        _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        _context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vertexBuffer, Marshal.SizeOf<FullscreenVertex>(), 0));
        _context.VertexShader.Set(_vertexShader);
        _context.OutputMerger.BlendState = _blendState;
        _context.OutputMerger.SetDepthStencilState(_depthStencilState, 0);
        _context.Rasterizer.State = _rasterizerState;
    }

    private void RenderShaderPass(BackdropFramebuffer framebuffer, ShaderResourceView inputView, PixelShader shader)
    {
        if (_context is null || _blurConstantBuffer is null || _mirrorSampler is null)
        {
            return;
        }

        _blurConstants.BlurParams = new Vector4(0.5f / framebuffer.Width, 0.5f / framebuffer.Height, BlurOffset, BlurNoise);
        if (!TryUpdateConstantBuffer(_blurConstantBuffer, _blurConstants))
        {
            return;
        }

        _context.OutputMerger.SetTargets(framebuffer.RenderTargetView);
        _context.Rasterizer.SetViewport(0f, 0f, framebuffer.Width, framebuffer.Height);
        _context.PixelShader.Set(shader);
        _context.PixelShader.SetConstantBuffer(0, _blurConstantBuffer);
        _context.PixelShader.SetShaderResource(0, inputView);
        _context.PixelShader.SetSampler(0, _mirrorSampler);
        _context.Draw(4, 0);
        _context.PixelShader.SetConstantBuffer(0, null);
        _context.PixelShader.SetShaderResource(0, null);
    }

    private void RenderCompositePass(BackdropFramebuffer framebuffer, ShaderResourceView gameView, ShaderResourceView overlayView)
    {
        if (_context is null || _compositeShader is null || _mirrorSampler is null)
        {
            return;
        }

        _context.OutputMerger.SetTargets(framebuffer.RenderTargetView);
        _context.Rasterizer.SetViewport(0f, 0f, framebuffer.Width, framebuffer.Height);
        _context.PixelShader.Set(_compositeShader);
        _context.PixelShader.SetShaderResource(0, gameView);
        _context.PixelShader.SetShaderResource(1, overlayView);
        _context.PixelShader.SetSampler(0, _mirrorSampler);
        _context.Draw(4, 0);
        _context.PixelShader.SetShaderResource(0, null);
        _context.PixelShader.SetShaderResource(1, null);
    }

    private void RenderEffectPass(
        BackdropFramebuffer framebuffer,
        PixelShader shader,
        in BackdropSurfaceConstants surfaceConstants,
        Buffer? effectConstantBuffer,
        int scissorMinX,
        int scissorMinY,
        int scissorMaxX,
        int scissorMaxY)
    {
        if (_context is null
            || _surfaceConstantBuffer is null
            || _mirrorSampler is null
            || _scissorRasterizerState is null
            || _compositedSourceFramebuffer?.ShaderResourceView is null
            || _outputFramebuffer?.ShaderResourceView is null
            || framebuffer.RenderTargetView is null)
        {
            return;
        }

        if (!TryUpdateConstantBuffer(_surfaceConstantBuffer, surfaceConstants))
        {
            return;
        }

        _context.OutputMerger.SetTargets(framebuffer.RenderTargetView);
        _context.Rasterizer.State = _scissorRasterizerState;
        _context.Rasterizer.SetViewport(0f, 0f, _frameWidth, _frameHeight);
        _context.Rasterizer.SetScissorRectangle(scissorMinX, scissorMinY, scissorMaxX, scissorMaxY);
        _context.PixelShader.Set(shader);
        _context.PixelShader.SetConstantBuffer(0, _surfaceConstantBuffer);
        _context.PixelShader.SetConstantBuffer(1, effectConstantBuffer);
        _context.PixelShader.SetShaderResource(0, _compositedSourceFramebuffer.ShaderResourceView);
        _context.PixelShader.SetShaderResource(1, _outputFramebuffer.ShaderResourceView);
        _context.PixelShader.SetSampler(0, _mirrorSampler);
        _context.Draw(4, 0);
        _context.PixelShader.SetConstantBuffer(1, null);
        _context.PixelShader.SetShaderResource(0, null);
        _context.PixelShader.SetShaderResource(1, null);
    }

    private static Vector4 ResolveCornerRadii(float rounding, ImDrawFlags cornerFlags)
    {
        return new Vector4(
            HasCorner(cornerFlags, ImDrawFlags.RoundCornersTopLeft) ? rounding : 0f,
            HasCorner(cornerFlags, ImDrawFlags.RoundCornersTopRight) ? rounding : 0f,
            HasCorner(cornerFlags, ImDrawFlags.RoundCornersBottomRight) ? rounding : 0f,
            HasCorner(cornerFlags, ImDrawFlags.RoundCornersBottomLeft) ? rounding : 0f);
    }

    private static bool HasCorner(ImDrawFlags flags, ImDrawFlags corner)
        => (flags & corner) != 0;

    private static BackdropFramebuffer CreateFramebuffer(Device device, int width, int height)
    {
        var texture = new Texture2D(device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
        });

        var renderTargetView = new RenderTargetView(device, texture);
        var shaderResourceView = new ShaderResourceView(device, texture);
        return new BackdropFramebuffer(texture, renderTargetView, shaderResourceView, width, height);
    }

    private bool IsMainViewportWindow()
    {
        var currentViewport = ImGui.GetWindowViewport();
        var mainViewport = ImGui.GetMainViewport();
        return !currentViewport.IsNull
            && !mainViewport.IsNull
            && currentViewport.ID == mainViewport.ID;
    }

    private void DisposeFramebuffers()
    {
        foreach (var framebuffer in _blurFramebuffers)
        {
            framebuffer.Dispose();
        }

        _blurFramebuffers.Clear();
        _compositedSourceFramebuffer?.Dispose();
        _compositedSourceFramebuffer = null;
        _outputFramebuffer?.Dispose();
        _outputFramebuffer = null;
    }

    private void DisposeRuntimeResources()
    {
        _effects.DisposeResources();
        DisposeFramebuffers();
        DisposeCapturedSourceTexture();
        DisposeGameSourceTexture();
        _blendState?.Dispose();
        _blendState = null;
        _depthStencilState?.Dispose();
        _depthStencilState = null;
        _scissorRasterizerState?.Dispose();
        _scissorRasterizerState = null;
        _rasterizerState?.Dispose();
        _rasterizerState = null;
        _mirrorSampler?.Dispose();
        _mirrorSampler = null;
        _blurConstantBuffer?.Dispose();
        _blurConstantBuffer = null;
        _surfaceConstantBuffer?.Dispose();
        _surfaceConstantBuffer = null;
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
        _compositeShader?.Dispose();
        _compositeShader = null;
        _upsampleShader?.Dispose();
        _upsampleShader = null;
        _downsampleShader?.Dispose();
        _downsampleShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _frameWidth = 0;
        _frameHeight = 0;
        _preparedFrame = -1;
        _processedFrame = -1;
        _queuedFrame = -1;
    }

    private void DisposeCapturedSourceTexture()
    {
        _capturedSourceShaderResourceView?.Dispose();
        _capturedSourceShaderResourceView = null;
        _capturedSourceTexture?.Dispose();
        _capturedSourceTexture = null;
        _capturedSourceWidth = 0;
        _capturedSourceHeight = 0;
        _capturedSourceFormat = Format.Unknown;
    }

    private void DisposeGameSourceTexture()
    {
        _gameSourceShaderResourceView?.Dispose();
        _gameSourceShaderResourceView = null;
        _gameSourceCopyTexture?.Dispose();
        _gameSourceCopyTexture = null;
        _gameSourceWidth = 0;
        _gameSourceHeight = 0;
        _gameSourceFormat = Format.Unknown;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FullscreenVertex
    {
        public Vector2 Position;
        public Vector2 Uv;

        public FullscreenVertex(Vector2 position, Vector2 uv)
        {
            Position = position;
            Uv = uv;
        }
    }

#pragma warning disable S4487 // gpu constant buffer fields are read by native shader upload
    [StructLayout(LayoutKind.Sequential)]
    internal struct BackdropBlurConstants
    {
        public Vector4 BlurParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BackdropSurfaceConstants
    {
        public Vector4 BackdropSize;
        public Vector4 RegionRect;
        public Vector4 CornerRadii;
    }
#pragma warning restore S4487

    internal sealed class BackdropFramebuffer : IDisposable
    {
        public BackdropFramebuffer(Texture2D texture, RenderTargetView renderTargetView, ShaderResourceView shaderResourceView, int width, int height)
        {
            Texture = texture;
            RenderTargetView = renderTargetView;
            ShaderResourceView = shaderResourceView;
            Width = width;
            Height = height;
        }

        public Texture2D Texture { get; }
        public RenderTargetView RenderTargetView { get; }
        public ShaderResourceView ShaderResourceView { get; }
        public int Width { get; }
        public int Height { get; }

        public void Dispose()
        {
            ShaderResourceView.Dispose();
            RenderTargetView.Dispose();
            Texture.Dispose();
        }
    }

    internal readonly struct TextureDrawInfo
    {
        public TextureDrawInfo(nint textureHandle, Vector2 uvMin, Vector2 uvMax)
        {
            TextureHandle = textureHandle;
            UvMin         = uvMin;
            UvMax         = uvMax;
        }

        public nint TextureHandle { get; }
        public Vector2 UvMin { get; }
        public Vector2 UvMax { get; }
    }

    internal struct BackdropRegion
    {
        public Vector2 RegionMin;
        public Vector2 RegionSize;
        public Vector4 CornerRadii;
        public Vector2 UvMin;
        public Vector2 UvMax;
        public int ScissorMinX;
        public int ScissorMinY;
        public int ScissorMaxX;
        public int ScissorMaxY;
    }

}

