using SharpDX.Direct3D11;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;

namespace Intoner.Services.Gpu;

internal readonly record struct StructuredBufferPoolKey(
    nint DevicePointer,
    int ElementCount,
    int ElementStride,
    BindFlags BindFlags);

internal readonly record struct Texture2DPoolKey(
    nint DevicePointer,
    int Width,
    int Height,
    int MipLevels,
    int ArraySize,
    SharpDX.DXGI.Format Format,
    ResourceUsage Usage,
    BindFlags BindFlags,
    CpuAccessFlags CpuAccessFlags,
    ResourceOptionFlags OptionFlags,
    int SampleCount,
    int SampleQuality);

internal readonly record struct ReadbackRingKey(nint DevicePointer, int SizeInBytes, int RingSize);

public sealed class PooledStructuredBuffer : IDisposable
{
    internal StructuredBufferPoolKey Key { get; }
    public D3D11Buffer Buffer { get; }
    public ShaderResourceView? ShaderResourceView { get; }
    public UnorderedAccessView? UnorderedAccessView { get; }

    internal PooledStructuredBuffer(Device device, int elementCount, int elementStride, BindFlags bindFlags)
    {
        if (elementCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        }

        if (elementStride <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(elementStride));
        }

        Key = new StructuredBufferPoolKey(device.NativePointer, elementCount, elementStride, bindFlags);
        Buffer = new D3D11Buffer(device, CreateStructuredBufferDescription(elementCount, elementStride, bindFlags));

        if ((bindFlags & BindFlags.ShaderResource) != 0)
        {
            ShaderResourceView = new ShaderResourceView(device, Buffer);
        }

        if ((bindFlags & BindFlags.UnorderedAccess) != 0)
        {
            UnorderedAccessView = new UnorderedAccessView(device, Buffer);
        }
    }

    public void Dispose()
    {
        UnorderedAccessView?.Dispose();
        ShaderResourceView?.Dispose();
        Buffer.Dispose();
    }

    private static BufferDescription CreateStructuredBufferDescription(int elementCount, int elementStride, BindFlags bindFlags)
    {
        var sizeInBytes = checked(elementCount * elementStride);
        return new BufferDescription
        {
            SizeInBytes = sizeInBytes,
            Usage = ResourceUsage.Default,
            BindFlags = bindFlags,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = elementStride,
        };
    }
}

public sealed class PooledTexture2D : IDisposable
{
    internal Texture2DPoolKey Key { get; }
    public Texture2D Texture { get; }

    internal PooledTexture2D(Device device, in Texture2DDescription description)
    {
        Key = GpuResourcePoolService.CreateTexture2DPoolKey(device.NativePointer, description);
        Texture = new Texture2D(device, description);
    }

    public void Dispose()
        => Texture.Dispose();
}

internal sealed class ReadbackBufferRing : IDisposable
{
    private readonly D3D11Buffer[] _buffers;
    private int _nextIndex;

    public ReadbackBufferRing(Device device, int sizeInBytes, int ringSize)
    {
        var description = new BufferDescription
        {
            SizeInBytes = sizeInBytes,
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        };

        _buffers = new D3D11Buffer[ringSize];
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i] = new D3D11Buffer(device, description);
        }
    }

    public D3D11Buffer Rent()
    {
        var buffer = _buffers[_nextIndex];
        _nextIndex = (_nextIndex + 1) % _buffers.Length;
        return buffer;
    }

    public void Dispose()
    {
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i].Dispose();
        }
    }
}

public sealed class GpuResourcePoolService : IDisposable
{
    private const int MaxItemsPerBucket = 4;
    private const int DefaultReadbackRingSize = 3;

    public static GpuResourcePoolService Shared { get; } = new();

    private readonly Lock _sync = new();
    private readonly Dictionary<StructuredBufferPoolKey, Stack<PooledStructuredBuffer>> _structuredBuffers = new();
    private readonly Dictionary<Texture2DPoolKey, Stack<PooledTexture2D>> _textures = new();
    private readonly Dictionary<ReadbackRingKey, ReadbackBufferRing> _readbackRings = new();

    public GpuResourcePoolService()
    { }

    public PooledStructuredBuffer RentStructuredBuffer(Device device, int pixelCount, BindFlags bindFlags)
        => RentStructuredBuffer(device, pixelCount, sizeof(uint), bindFlags);

    public PooledStructuredBuffer RentStructuredBuffer(Device device, int elementCount, int elementStride, BindFlags bindFlags)
    {
        var key = new StructuredBufferPoolKey(device.NativePointer, elementCount, elementStride, bindFlags);
        lock (_sync)
        {
            if (_structuredBuffers.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                return stack.Pop();
            }
        }

        return new PooledStructuredBuffer(device, elementCount, elementStride, bindFlags);
    }

    public void ReturnStructuredBuffer(PooledStructuredBuffer? buffer)
    {
        if (buffer is null)
        {
            return;
        }

        lock (_sync)
        {
            if (!_structuredBuffers.TryGetValue(buffer.Key, out var stack))
            {
                stack = new Stack<PooledStructuredBuffer>(MaxItemsPerBucket);
                _structuredBuffers[buffer.Key] = stack;
            }

            if (stack.Count < MaxItemsPerBucket)
            {
                stack.Push(buffer);
                return;
            }
        }

        buffer.Dispose();
    }

    public PooledTexture2D RentTexture2D(Device device, in Texture2DDescription description)
    {
        var key = CreateTexture2DPoolKey(device.NativePointer, description);
        lock (_sync)
        {
            if (_textures.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                return stack.Pop();
            }
        }

        return new PooledTexture2D(device, description);
    }

    public void ReturnTexture2D(PooledTexture2D? texture)
    {
        if (texture is null)
        {
            return;
        }

        lock (_sync)
        {
            if (!_textures.TryGetValue(texture.Key, out var stack))
            {
                stack = new Stack<PooledTexture2D>(MaxItemsPerBucket);
                _textures[texture.Key] = stack;
            }

            if (stack.Count < MaxItemsPerBucket)
            {
                stack.Push(texture);
                return;
            }
        }

        texture.Dispose();
    }

    public D3D11Buffer RentReadbackBufferFromRing(Device device, int sizeInBytes, int ringSize = DefaultReadbackRingSize)
    {
        ringSize = Math.Max(1, ringSize);
        var key = new ReadbackRingKey(device.NativePointer, sizeInBytes, ringSize);
        lock (_sync)
        {
            if (!_readbackRings.TryGetValue(key, out var ring))
            {
                ring = new ReadbackBufferRing(device, sizeInBytes, ringSize);
                _readbackRings[key] = ring;
            }

            return ring.Rent();
        }
    }

    public void InvalidateDeviceResources(nint devicePointer)
    {
        if (devicePointer == nint.Zero)
        {
            return;
        }

        List<PooledStructuredBuffer>? structuredToDispose = null;
        List<PooledTexture2D>? texturesToDispose = null;
        List<ReadbackBufferRing>? ringsToDispose = null;
        lock (_sync)
        {
            foreach (var pair in _structuredBuffers.Where(kvp => kvp.Key.DevicePointer == devicePointer).ToArray())
            {
                _structuredBuffers.Remove(pair.Key);
                structuredToDispose ??= [];
                structuredToDispose.AddRange(pair.Value);
            }

            foreach (var pair in _textures.Where(kvp => kvp.Key.DevicePointer == devicePointer).ToArray())
            {
                _textures.Remove(pair.Key);
                texturesToDispose ??= [];
                texturesToDispose.AddRange(pair.Value);
            }

            foreach (var pair in _readbackRings.Where(kvp => kvp.Key.DevicePointer == devicePointer).ToArray())
            {
                _readbackRings.Remove(pair.Key);
                ringsToDispose ??= [];
                ringsToDispose.Add(pair.Value);
            }
        }

        if (structuredToDispose is not null)
        {
            for (var i = 0; i < structuredToDispose.Count; i++)
            {
                structuredToDispose[i].Dispose();
            }
        }

        if (texturesToDispose is not null)
        {
            for (var i = 0; i < texturesToDispose.Count; i++)
            {
                texturesToDispose[i].Dispose();
            }
        }

        if (ringsToDispose is not null)
        {
            for (var i = 0; i < ringsToDispose.Count; i++)
            {
                ringsToDispose[i].Dispose();
            }
        }
    }

    public void Dispose()
    {
        List<PooledStructuredBuffer> structuredToDispose;
        List<PooledTexture2D> texturesToDispose;
        List<ReadbackBufferRing> ringsToDispose;
        lock (_sync)
        {
            structuredToDispose = _structuredBuffers.Values.SelectMany(static stack => stack).ToList();
            texturesToDispose = _textures.Values.SelectMany(static stack => stack).ToList();
            ringsToDispose = _readbackRings.Values.ToList();
            _structuredBuffers.Clear();
            _textures.Clear();
            _readbackRings.Clear();
        }

        for (var i = 0; i < structuredToDispose.Count; i++)
        {
            structuredToDispose[i].Dispose();
        }

        for (var i = 0; i < texturesToDispose.Count; i++)
        {
            texturesToDispose[i].Dispose();
        }

        for (var i = 0; i < ringsToDispose.Count; i++)
        {
            ringsToDispose[i].Dispose();
        }
    }

    internal static Texture2DPoolKey CreateTexture2DPoolKey(nint devicePointer, in Texture2DDescription description)
        => new(
            devicePointer,
            description.Width,
            description.Height,
            description.MipLevels,
            description.ArraySize,
            description.Format,
            description.Usage,
            description.BindFlags,
            description.CpuAccessFlags,
            description.OptionFlags,
            description.SampleDescription.Count,
            description.SampleDescription.Quality);
}
