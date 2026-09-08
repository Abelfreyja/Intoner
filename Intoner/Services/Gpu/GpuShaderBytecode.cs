using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

internal sealed class GpuShaderBytecode
{
    private static readonly ConcurrentExclusiveSchedulerPair CompilationScheduler = new(
        TaskScheduler.Default,
        maxConcurrencyLevel: 1);
    private static readonly TaskFactory CompilationTasks = new(
        CancellationToken.None,
        TaskCreationOptions.DenyChildAttach,
        TaskContinuationOptions.None,
        CompilationScheduler.ExclusiveScheduler);

    private readonly Task<byte[]> _bytecode;

    public GpuShaderBytecode(Func<byte[]> compile)
    {
        ArgumentNullException.ThrowIfNull(compile);
        _bytecode = CompilationTasks.StartNew(compile);
    }

    public byte[] Value
        => _bytecode.GetAwaiter().GetResult();

    public bool IsCompilationComplete
        => _bytecode.IsCompleted;

    public VertexShader CreateVertexShader(Device device)
        => new(device, Value);

    public PixelShader CreatePixelShader(Device device)
        => new(device, Value);

    public ComputeShader CreateComputeShader(Device device)
        => new(device, Value);

    public InputLayout CreateInputLayout(Device device, InputElement[] elements)
        => new(device, ShaderSignature.GetInputSignature(Value), elements);
}
