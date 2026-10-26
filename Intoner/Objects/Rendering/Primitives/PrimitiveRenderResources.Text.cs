using Intoner.Services.Gpu;
using SharpDX.Direct3D11;
using SharpDX.Mathematics.Interop;
using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed partial class PrimitiveRenderResources
{
    private const string TextShaderResourceName = "Objects.Rendering.Primitives.Shaders.SceneText.hlsl";
    private static readonly GpuShaderBytecode TextVertexShader = GpuShaderCompileService.CreateVertexShader(
        typeof(PrimitiveRenderResources), TextShaderResourceName, "native text vertex shader");
    private static readonly GpuShaderBytecode TextPixelShader = GpuShaderCompileService.CreatePixelShader(
        typeof(PrimitiveRenderResources), TextShaderResourceName, "native text pixel shader");

    private VertexShader? _textVertexShader;
    private PixelShader? _textPixelShader;
    private Buffer? _textConstants;
    private SamplerState? _textSampler;

    public bool IsTextureCompatible(PrimitiveTexture texture)
        => ActiveDevice?.NativePointer == texture.DeviceHandle;

    private void CreateTextResources(Device device)
    {
        _textVertexShader = TextVertexShader.CreateVertexShader(device);
        _textPixelShader = TextPixelShader.CreatePixelShader(device);
        _textConstants = new Buffer(device, Marshal.SizeOf<TextConstants>(), ResourceUsage.Default,
            BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _textSampler = new SamplerState(device, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaximumLod = float.MaxValue,
            ComparisonFunction = Comparison.Never,
        });
    }

    public void ApplyTextPipeline(DeviceContext context)
    {
        context.InputAssembler.InputLayout = null;
        context.VertexShader.Set(_textVertexShader);
        context.VertexShader.SetConstantBuffer(0, _textConstants);
        context.PixelShader.Set(_textPixelShader);
        context.PixelShader.SetSampler(0, _textSampler);
    }

    public void DrawText(DeviceContext context, in TextCommand command, Vector2 position, RawViewportF viewport)
    {
        position = new Vector2(MathF.Floor(position.X + 0.5f), MathF.Floor(position.Y + 0.5f));
        TextConstants constants = new()
        {
            Viewport = new Vector4(viewport.X, viewport.Y, viewport.Width, viewport.Height),
            Rectangle = new Vector4(position, command.Texture.Size.X, command.Texture.Size.Y),
        };
        context.UpdateSubresource(ref constants, _textConstants);
        context.PixelShader.SetShaderResource(0, command.Texture.View);
        context.Draw(6, 0);
    }

    private void DisposeTextResources()
    {
        _textSampler?.Dispose();
        _textSampler = null;
        _textConstants?.Dispose();
        _textConstants = null;
        _textPixelShader?.Dispose();
        _textPixelShader = null;
        _textVertexShader?.Dispose();
        _textVertexShader = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextConstants
    {
        public Vector4 Viewport;
        public Vector4 Rectangle;
    }
}
