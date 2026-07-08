using System.Numerics;
using System.Runtime.InteropServices;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

internal sealed class GpuFullscreenQuad : IDisposable
{
    private static readonly FullscreenVertex[] Vertices =
    [
        new(new Vector2(-1f, 1f), new Vector2(0f, 0f)),
        new(new Vector2(1f, 1f), new Vector2(1f, 0f)),
        new(new Vector2(-1f, -1f), new Vector2(0f, 1f)),
        new(new Vector2(1f, -1f), new Vector2(1f, 1f)),
    ];

    private readonly VertexShader _vertexShader;
    private readonly InputLayout  _inputLayout;
    private readonly Buffer       _vertexBuffer;

    public GpuFullscreenQuad(Device device, GpuShaderBytecode vertexShaderBytecode)
    {
        _vertexShader = vertexShaderBytecode.CreateVertexShader(device);
        _inputLayout = vertexShaderBytecode.CreateInputLayout(
            device,
            [
                new InputElement("POSITION", 0, Format.R32G32_Float, 0, 0),
                new InputElement("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            ]);
        _vertexBuffer = Buffer.Create(device, BindFlags.VertexBuffer, Vertices);
    }

    public void Apply(DeviceContext context)
    {
        context.InputAssembler.InputLayout       = _inputLayout;
        context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vertexBuffer, Marshal.SizeOf<FullscreenVertex>(), 0));
        context.VertexShader.Set(_vertexShader);
    }

    public void Dispose()
    {
        _vertexBuffer.Dispose();
        _inputLayout.Dispose();
        _vertexShader.Dispose();
    }

#pragma warning disable S4487 // gpu vertex fields are read by native vertex buffer upload
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FullscreenVertex
    {
        public readonly Vector2 Position;
        public readonly Vector2 Uv;

        public FullscreenVertex(Vector2 position, Vector2 uv)
        {
            Position = position;
            Uv       = uv;
        }
    }
#pragma warning restore S4487
}
