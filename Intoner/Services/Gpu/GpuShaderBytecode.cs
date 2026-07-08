using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

internal sealed class GpuShaderBytecode
{
    private readonly Lazy<byte[]> _bytecode;

    public GpuShaderBytecode(Lazy<byte[]> bytecode)
    {
        _bytecode = bytecode;
    }

    public byte[] Value
        => _bytecode.Value;

    public VertexShader CreateVertexShader(Device device)
        => new(device, Value);

    public PixelShader CreatePixelShader(Device device)
        => new(device, Value);

    public ComputeShader CreateComputeShader(Device device)
        => new(device, Value);

    public InputLayout CreateInputLayout(Device device, InputElement[] elements)
        => new(device, ShaderSignature.GetInputSignature(Value), elements);
}
