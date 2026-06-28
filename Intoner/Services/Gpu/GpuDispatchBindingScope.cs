using SharpDX.Direct3D11;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;

namespace Intoner.Services.Gpu;

internal sealed class GpuDispatchBindingScope : IDisposable
{
    private readonly DeviceContext _context;
    private int _maxConstantBufferSlot = -1;
    private int _maxShaderResourceSlot = -1;
    private int _maxUnorderedAccessSlot = -1;

    public GpuDispatchBindingScope(DeviceContext context, ComputeShader shader)
    {
        _context = context;
        _context.ComputeShader.Set(shader);
    }

    public void SetConstantBuffer(int slot, D3D11Buffer? buffer)
    {
        _context.ComputeShader.SetConstantBuffer(slot, buffer);
        _maxConstantBufferSlot = Math.Max(_maxConstantBufferSlot, slot);
    }

    public void SetShaderResource(int slot, ShaderResourceView? view)
    {
        _context.ComputeShader.SetShaderResource(slot, view);
        _maxShaderResourceSlot = Math.Max(_maxShaderResourceSlot, slot);
    }

    public void SetUnorderedAccessView(int slot, UnorderedAccessView? view)
    {
        _context.ComputeShader.SetUnorderedAccessView(slot, view);
        _maxUnorderedAccessSlot = Math.Max(_maxUnorderedAccessSlot, slot);
    }

    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
        => _context.Dispatch(groupsX, groupsY, groupsZ);

    public void Dispose()
    {
        for (var slot = 0; slot <= _maxUnorderedAccessSlot; slot++)
        {
            _context.ComputeShader.SetUnorderedAccessView(slot, null!);
        }

        for (var slot = 0; slot <= _maxShaderResourceSlot; slot++)
        {
            _context.ComputeShader.SetShaderResource(slot, null!);
        }

        for (var slot = 0; slot <= _maxConstantBufferSlot; slot++)
        {
            _context.ComputeShader.SetConstantBuffer(slot, null!);
        }

        _context.ComputeShader.Set(null);
    }
}
