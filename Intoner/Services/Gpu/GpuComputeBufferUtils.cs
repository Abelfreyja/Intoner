using System.Runtime.InteropServices;
using SharpDX.Direct3D11;
using D3D11Buffer = SharpDX.Direct3D11.Buffer;

namespace Intoner.Services.Gpu;

internal sealed class GpuStructuredWorkspaceBuffer<T> : IDisposable
    where T : unmanaged
{
    private const int MinCapacity = 64;

    private readonly BindFlags _bindFlags;
    private readonly int _stride;
    private D3D11Buffer? _buffer;
    private ShaderResourceView? _shaderResourceView;
    private UnorderedAccessView? _unorderedAccessView;
    private nint _devicePointer;

    public GpuStructuredWorkspaceBuffer(BindFlags bindFlags)
    {
        if ((bindFlags & (BindFlags.ShaderResource | BindFlags.UnorderedAccess)) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bindFlags));
        }

        _bindFlags = bindFlags;
        _stride = Marshal.SizeOf<T>();
    }

    public int Capacity { get; private set; }

    public D3D11Buffer Buffer
        => _buffer ?? throw new InvalidOperationException("GPU workspace buffer is not initialized.");

    public ShaderResourceView? ShaderResourceView => _shaderResourceView;

    public UnorderedAccessView? UnorderedAccessView => _unorderedAccessView;

    public void EnsureCapacity(Device device, int requiredElementCount)
    {
        if (requiredElementCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredElementCount));
        }

        if (_buffer != null
            && _devicePointer == device.NativePointer
            && requiredElementCount <= Capacity)
        {
            return;
        }

        DisposeResources();

        var capacity = ComputeCapacity(requiredElementCount);
        var description = new BufferDescription
        {
            SizeInBytes = checked(capacity * _stride),
            Usage = ResourceUsage.Default,
            BindFlags = _bindFlags,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = _stride,
        };

        _buffer = new D3D11Buffer(device, description);
        if ((_bindFlags & BindFlags.ShaderResource) != 0)
        {
            _shaderResourceView = new ShaderResourceView(device, _buffer);
        }

        if ((_bindFlags & BindFlags.UnorderedAccess) != 0)
        {
            _unorderedAccessView = new UnorderedAccessView(device, _buffer);
        }

        _devicePointer = device.NativePointer;
        Capacity = capacity;
    }

    public void Upload(DeviceContext context, T[] source, int elementCount)
    {
        if (_buffer == null)
        {
            throw new InvalidOperationException("GPU workspace buffer is not initialized.");
        }

        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (elementCount < 0 || elementCount > source.Length || elementCount > Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(elementCount));
        }

        if (elementCount == 0)
        {
            return;
        }

        var sourceHandle = default(GCHandle);
        var sourcePinned = false;
        try
        {
            sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
            sourcePinned = true;
            var sizeInBytes = checked(elementCount * _stride);
            if (_buffer.Description.SizeInBytes < sizeInBytes)
            {
                throw new InvalidOperationException("GPU workspace buffer is smaller than upload payload.");
            }

            var updateRegion = new ResourceRegion
            {
                Left = 0,
                Top = 0,
                Front = 0,
                Right = sizeInBytes,
                Bottom = 1,
                Back = 1,
            };
            context.UpdateSubresource(_buffer, 0, updateRegion, sourceHandle.AddrOfPinnedObject(), 0, 0);
        }
        finally
        {
            if (sourcePinned)
            {
                sourceHandle.Free();
            }
        }
    }

    public void Dispose()
        => DisposeResources();

    private static int ComputeCapacity(int requiredElementCount)
    {
        var capacity = MinCapacity;
        while (capacity < requiredElementCount && capacity <= int.MaxValue / 2)
        {
            capacity <<= 1;
        }

        return capacity < requiredElementCount ? requiredElementCount : capacity;
    }

    private void DisposeResources()
    {
        _unorderedAccessView?.Dispose();
        _unorderedAccessView = null;
        _shaderResourceView?.Dispose();
        _shaderResourceView = null;
        _buffer?.Dispose();
        _buffer = null;
        _devicePointer = nint.Zero;
        Capacity = 0;
    }
}

internal static class GpuComputeBufferUtils
{
    public static D3D11Buffer CreateStructuredReadBuffer<T>(Device device, T[] source, out GCHandle sourceHandle)
        where T : unmanaged
    {
        sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        var stride = Marshal.SizeOf<T>();
        var description = CreateStructuredBufferDescription(source.Length, stride, BindFlags.ShaderResource);
        return new D3D11Buffer(device, sourceHandle.AddrOfPinnedObject(), description);
    }

    public static D3D11Buffer CreateStructuredReadWriteBuffer<T>(Device device, int elementCount, bool includeShaderResource = false)
        where T : unmanaged
    {
        var stride = Marshal.SizeOf<T>();
        var bindFlags = BindFlags.UnorderedAccess;
        if (includeShaderResource)
        {
            bindFlags |= BindFlags.ShaderResource;
        }

        return new D3D11Buffer(device, CreateStructuredBufferDescription(elementCount, stride, bindFlags));
    }

    public static PooledStructuredBuffer RentAndUploadStructuredReadBuffer<T>(
        GpuResourcePoolService resourcePool,
        Device device,
        DeviceContext context,
        T[] source)
        where T : unmanaged
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var stride = Marshal.SizeOf<T>();
        var pooledBuffer = resourcePool.RentStructuredBuffer(device, source.Length, stride, BindFlags.ShaderResource);
        UploadStructuredBufferData(context, pooledBuffer.Buffer, source, stride);
        return pooledBuffer;
    }

    public static PooledStructuredBuffer RentStructuredReadWriteBuffer<T>(
        GpuResourcePoolService resourcePool,
        Device device,
        int elementCount,
        bool includeShaderResource = false)
        where T : unmanaged
    {
        var stride = Marshal.SizeOf<T>();
        var bindFlags = BindFlags.UnorderedAccess;
        if (includeShaderResource)
        {
            bindFlags |= BindFlags.ShaderResource;
        }

        return resourcePool.RentStructuredBuffer(device, elementCount, stride, bindFlags);
    }

    public static D3D11Buffer CreateConstantBuffer<T>(Device device, T[] constants, out GCHandle constantsHandle)
        where T : unmanaged
    {
        constantsHandle = GCHandle.Alloc(constants, GCHandleType.Pinned);
        var sizeInBytes = checked(constants.Length * Marshal.SizeOf<T>());
        if ((sizeInBytes & 15) != 0)
        {
            constantsHandle.Free();
            throw new InvalidOperationException($"GPU constant buffer size must be 16-byte aligned, got {sizeInBytes} bytes.");
        }

        var description = new BufferDescription
        {
            SizeInBytes = sizeInBytes,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None,
            StructureByteStride = 0,
        };

        return new D3D11Buffer(device, constantsHandle.AddrOfPinnedObject(), description);
    }

    public static float[] ReadBackFloats(
        Device device,
        DeviceContext context,
        D3D11Buffer sourceBuffer,
        int elementCount,
        GpuResourcePoolService? resourcePool = null)
        => ReadBackArray(
            device,
            context,
            sourceBuffer,
            elementCount,
            sizeof(float),
            resourcePool,
            "GPU readback source buffer is smaller than requested float count.",
            mappedData =>
            {
                var output = new float[elementCount];
                Marshal.Copy(mappedData, output, 0, output.Length);
                return output;
            });

    public static void CopyBufferData(
        DeviceContext context,
        D3D11Buffer sourceBuffer,
        D3D11Buffer destinationBuffer,
        int copySizeInBytes)
    {
        if (copySizeInBytes <= 0)
        {
            return;
        }

        var sourceSizeInBytes = sourceBuffer.Description.SizeInBytes;
        var destinationSizeInBytes = destinationBuffer.Description.SizeInBytes;
        if (sourceSizeInBytes < copySizeInBytes || destinationSizeInBytes < copySizeInBytes)
        {
            throw new InvalidOperationException("GPU buffer copy exceeds source or destination size.");
        }

        if (sourceSizeInBytes == copySizeInBytes && destinationSizeInBytes == copySizeInBytes)
        {
            context.CopyResource(sourceBuffer, destinationBuffer);
            return;
        }

        var copyRegion = new ResourceRegion
        {
            Left = 0,
            Top = 0,
            Front = 0,
            Right = copySizeInBytes,
            Bottom = 1,
            Back = 1,
        };
        context.CopySubresourceRegion(sourceBuffer, 0, copyRegion, destinationBuffer, 0, 0, 0, 0);
    }

    public static uint[] ReadBackUInts(
        Device device,
        DeviceContext context,
        D3D11Buffer sourceBuffer,
        int elementCount,
        GpuResourcePoolService? resourcePool = null)
        => ReadBackArray(
            device,
            context,
            sourceBuffer,
            elementCount,
            sizeof(uint),
            resourcePool,
            "GPU readback source buffer is smaller than requested uint count.",
            mappedData =>
            {
                var signed = new int[elementCount];
                Marshal.Copy(mappedData, signed, 0, signed.Length);
                var output = new uint[elementCount];
                for (var i = 0; i < signed.Length; i++)
                {
                    output[i] = unchecked((uint)signed[i]);
                }

                return output;
            });

    private static BufferDescription CreateStructuredBufferDescription(int elementCount, int stride, BindFlags bindFlags)
    {
        return new BufferDescription
        {
            SizeInBytes = checked(elementCount * stride),
            Usage = ResourceUsage.Default,
            BindFlags = bindFlags,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = stride,
        };
    }

    private static T[] ReadBackArray<T>(
        Device device,
        DeviceContext context,
        D3D11Buffer sourceBuffer,
        int elementCount,
        int elementSizeInBytes,
        GpuResourcePoolService? resourcePool,
        string sizeErrorMessage,
        Func<nint, T[]> reader)
    {
        if (elementCount <= 0)
        {
            return [];
        }

        var sizeInBytes = checked(elementCount * elementSizeInBytes);
        var sourceSizeInBytes = sourceBuffer.Description.SizeInBytes;
        if (sourceSizeInBytes < sizeInBytes)
        {
            throw new InvalidOperationException(sizeErrorMessage);
        }

        var pool = resourcePool ?? GpuResourcePoolService.Shared;
        var readbackBuffer = pool.RentReadbackBufferFromRing(device, sizeInBytes);
        CopyBufferToReadback(context, sourceBuffer, readbackBuffer, sizeInBytes, sourceSizeInBytes);
        var mapped = context.MapSubresource(readbackBuffer, 0, MapMode.Read, MapFlags.None);
        try
        {
            return reader(mapped.DataPointer);
        }
        finally
        {
            context.UnmapSubresource(readbackBuffer, 0);
        }
    }

    private static void CopyBufferToReadback(
        DeviceContext context,
        D3D11Buffer sourceBuffer,
        D3D11Buffer readbackBuffer,
        int copySizeInBytes,
        int sourceSizeInBytes)
    {
        if (sourceSizeInBytes == copySizeInBytes)
        {
            context.CopyResource(sourceBuffer, readbackBuffer);
            return;
        }

        var copyRegion = new ResourceRegion
        {
            Left = 0,
            Top = 0,
            Front = 0,
            Right = copySizeInBytes,
            Bottom = 1,
            Back = 1,
        };
        context.CopySubresourceRegion(sourceBuffer, 0, copyRegion, readbackBuffer, 0, 0, 0, 0);
    }

    private static void UploadStructuredBufferData<T>(
        DeviceContext context,
        D3D11Buffer destinationBuffer,
        T[] source,
        int stride)
        where T : unmanaged
    {
        if (source.Length == 0)
        {
            return;
        }

        var sourceHandle = default(GCHandle);
        var sourcePinned = false;
        try
        {
            sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
            sourcePinned = true;
            var sizeInBytes = checked(source.Length * stride);
            if (destinationBuffer.Description.SizeInBytes < sizeInBytes)
            {
                throw new InvalidOperationException("GPU structured buffer is smaller than upload payload.");
            }

            var updateRegion = new ResourceRegion
            {
                Left = 0,
                Top = 0,
                Front = 0,
                Right = sizeInBytes,
                Bottom = 1,
                Back = 1,
            };
            context.UpdateSubresource(destinationBuffer, 0, updateRegion, sourceHandle.AddrOfPinnedObject(), 0, 0);
        }
        finally
        {
            if (sourcePinned)
            {
                sourceHandle.Free();
            }
        }
    }
}
