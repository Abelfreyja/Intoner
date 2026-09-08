using SharpDX.Direct3D11;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

internal enum GpuFrameAlphaMode
{
    Opaque = 0,
    Straight = 1,
    Premultiplied = 2,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GpuFrameDescriptor(
    int Width,
    int Height,
    GpuFrameAlphaMode AlphaMode)
{
    public bool IsValid
        => Width > 0
           && Height > 0
           && Enum.IsDefined(AlphaMode);
}

/// <summary> provides leased GPU frames through device independent shared resources </summary>
internal interface IGpuFrameSource
{
    /// <summary> acquires the latest complete frame when it is newer than the supplied frame id </summary>
    /// <param name="afterFrameId">the last frame already consumed, or zero when no frame has been consumed</param>
    /// <param name="frame">the acquired frame, which the caller must dispose</param>
    /// <returns>true when a newer frame is available</returns>
    bool TryAcquireLatest(long afterFrameId, [NotNullWhen(true)] out GpuFrame.Lease? frame);
}

/// <summary> describes one shared texture while retaining its producer owned storage </summary>
internal sealed class GpuFrame : IDisposable
{
    private static long _nextId;

    private readonly GpuLeasedResource<GpuFrame> _lifetime;

    public GpuFrame(
        nint sharedHandle,
        long resourceSetId,
        long resourceId,
        GpuFrameDescriptor descriptor,
        IDisposable resourceOwner)
    {
        if (sharedHandle == nint.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sharedHandle));
        }

        if (!descriptor.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor));
        }

        ArgumentNullException.ThrowIfNull(resourceOwner);
        Id = Interlocked.Increment(ref _nextId);
        SharedHandle = sharedHandle;
        ResourceSetId = resourceSetId;
        ResourceId = resourceId;
        Descriptor = descriptor;
        _lifetime = new GpuLeasedResource<GpuFrame>(
            this,
            _ => resourceOwner.Dispose());
    }

    public long Id { get; }
    public nint SharedHandle { get; }
    public long ResourceSetId { get; }
    public long ResourceId { get; }
    public GpuFrameDescriptor Descriptor { get; }

    public bool TryAcquire([NotNullWhen(true)] out Lease? lease)
    {
        if (!_lifetime.TryAcquire(out GpuLeasedResource<GpuFrame>.Lease? resourceLease))
        {
            lease = null;
            return false;
        }

        lease = new Lease(resourceLease);
        return true;
    }

    public void Dispose()
        => _lifetime.Dispose();

    internal sealed class Lease : IDisposable
    {
        private GpuLeasedResource<GpuFrame>.Lease? _resourceLease;

        internal Lease(GpuLeasedResource<GpuFrame>.Lease resourceLease)
        {
            _resourceLease = resourceLease;
        }

        public long Id
            => Resource.Id;

        public nint SharedHandle
            => Resource.SharedHandle;

        public long ResourceSetId
            => Resource.ResourceSetId;

        public long ResourceId
            => Resource.ResourceId;

        public GpuFrameDescriptor Descriptor
            => Resource.Descriptor;

        public void Dispose()
            => Interlocked.Exchange(ref _resourceLease, null)?.Dispose();

        private GpuFrame Resource
            => _resourceLease?.Resource
               ?? throw new ObjectDisposedException(nameof(Lease));
    }
}

/// <summary> copies shared frames into a reusable texture owned by the render device </summary>
internal sealed class GpuFrameTransfer : IDisposable
{
    private readonly Lock _stateLock = new();
    private readonly Dictionary<long, Texture2D> _sharedFrames = [];
    private readonly Queue<PendingFrame> _pendingFrames = [];
    private readonly Stack<Query> _availableCompletionQueries = [];

    private GpuTextureView? _texture;
    private Device? _sharedDevice;
    private GpuFrameDescriptor _descriptor;
    private Texture2DDescription _textureDescription;
    private long _frameId;
    private long _resourceSetId;
    private nint _devicePointer;
    private nint _pendingDevicePointer;
    private bool _disposed;

    public bool TryUpdate(
        IGpuFrameSource? source,
        Device device,
        DeviceContext context,
        [NotNullWhen(true)] out ShaderResourceView? view,
        out GpuFrameDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);

        lock (_stateLock)
        {
            if (_disposed)
            {
                view = null;
                descriptor = default;
                return false;
            }

            try
            {
                EnsureDevice(device);
                RetireCompletedFrames(context);
                if (source != null
                    && source.TryAcquireLatest(_frameId, out GpuFrame.Lease? frame)
                    && frame is { } acquiredFrame)
                {
                    CopyLatestFrame(device, context, acquiredFrame);
                }

                view = _texture?.View;
                descriptor = _descriptor;
                return view != null;
            }
            catch
            {
                ResetTransfer();
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ResetTransfer();
        }
    }

    private void EnsureDevice(Device device)
    {
        nint devicePointer = device.NativePointer;
        if ((_devicePointer != nint.Zero && _devicePointer != devicePointer)
            || (_pendingDevicePointer != nint.Zero && _pendingDevicePointer != devicePointer))
        {
            ResetTransfer();
        }
    }

    private void CopyLatestFrame(
        Device device,
        DeviceContext context,
        GpuFrame.Lease frame)
    {
        bool retirementQueued = false;
        try
        {
            EnsureSharedResourceSet(device, frame.ResourceSetId);
            Texture2D sharedFrame = GetSharedFrame(frame);
            Texture2DDescription sourceDescription = sharedFrame.Description;
            EnsureTexture(device, sourceDescription);
            context.CopyResource(sharedFrame, _texture!.Texture);

            Query? completion = AcquireCompletionQuery(device);
            try
            {
                context.End(completion);
                _pendingFrames.Enqueue(new PendingFrame(completion, frame));
                _pendingDevicePointer = device.NativePointer;
                completion = null;
                retirementQueued = true;
            }
            finally
            {
                completion?.Dispose();
            }

            _descriptor = frame.Descriptor;
            _frameId = frame.Id;
        }
        finally
        {
            if (!retirementQueued)
            {
                frame.Dispose();
            }
        }
    }

    private void RetireCompletedFrames(DeviceContext context)
    {
        while (_pendingFrames.TryPeek(out PendingFrame pending)
               && context.IsDataAvailable(
                   pending.Completion,
                   AsynchronousFlags.DoNotFlush))
        {
            PendingFrame completed = _pendingFrames.Dequeue();
            completed.Frame.Dispose();
            _availableCompletionQueries.Push(completed.Completion);
        }

        if (_pendingFrames.Count == 0)
        {
            _pendingDevicePointer = nint.Zero;
        }
    }

    private void ResetTransfer()
    {
        DisposePendingFrames();
        DisposeCompletionQueries();
        ResetResources();
    }

    private void EnsureSharedResourceSet(Device device, long resourceSetId)
    {
        if (_sharedDevice != null
            && _devicePointer == device.NativePointer
            && _resourceSetId == resourceSetId)
        {
            return;
        }

        DisposeSharedResources();
        _sharedDevice = D3D11ComReference.RetainDevice(device.NativePointer, this);
        _devicePointer = device.NativePointer;
        _resourceSetId = resourceSetId;
    }

    private Texture2D GetSharedFrame(GpuFrame.Lease frame)
    {
        if (_sharedFrames.TryGetValue(frame.ResourceId, out Texture2D? sharedFrame))
        {
            return sharedFrame;
        }

        ObjectDisposedException.ThrowIf(_sharedDevice == null, this);
        Texture2D? texture = null;
        try
        {
            texture = _sharedDevice.OpenSharedResource<Texture2D>(frame.SharedHandle);
            _sharedFrames.Add(frame.ResourceId, texture);
            Texture2D result = texture;
            texture = null;
            return result;
        }
        finally
        {
            texture?.Dispose();
        }
    }

    private void EnsureTexture(Device device, in Texture2DDescription source)
    {
        if (_texture != null
            && _textureDescription.Width == source.Width
            && _textureDescription.Height == source.Height
            && _textureDescription.MipLevels == source.MipLevels
            && _textureDescription.ArraySize == source.ArraySize
            && _textureDescription.Format == source.Format
            && _textureDescription.SampleDescription.Count == source.SampleDescription.Count
            && _textureDescription.SampleDescription.Quality == source.SampleDescription.Quality)
        {
            return;
        }

        _texture?.Dispose();
        _texture = GpuTextureView.Create(
            device,
            new Texture2DDescription
            {
                Width = source.Width,
                Height = source.Height,
                MipLevels = source.MipLevels,
                ArraySize = source.ArraySize,
                Format = source.Format,
                SampleDescription = source.SampleDescription,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None,
            });
        _textureDescription = source;
    }

    private void ResetResources()
    {
        _texture?.Dispose();
        _texture = null;
        _descriptor = default;
        _textureDescription = default;
        _frameId = 0;
        DisposeSharedResources();
    }

    private void DisposePendingFrames()
    {
        while (_pendingFrames.TryDequeue(out PendingFrame pending))
        {
            pending.Completion.Dispose();
            pending.Frame.Dispose();
        }

        _pendingDevicePointer = nint.Zero;
    }

    private Query AcquireCompletionQuery(Device device)
        => _availableCompletionQueries.TryPop(out Query? completion)
            ? completion
            : new Query(
                device,
                new QueryDescription
                {
                    Type = QueryType.Event,
                    Flags = QueryFlags.None,
                });

    private void DisposeCompletionQueries()
    {
        while (_availableCompletionQueries.TryPop(out Query? completion))
        {
            completion.Dispose();
        }
    }

    private void DisposeSharedResources()
    {
        foreach (Texture2D sharedFrame in _sharedFrames.Values)
        {
            sharedFrame.Dispose();
        }

        _sharedFrames.Clear();
        _sharedDevice?.Dispose();
        _sharedDevice = null;
        _resourceSetId = 0;
        _devicePointer = nint.Zero;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PendingFrame(
        Query Completion,
        GpuFrame.Lease Frame);
}

/// <summary> shares render-device frame transfers between consumers of the same source </summary>
internal sealed class GpuFrameTransferService : IDisposable
{
    private readonly Lock _stateLock = new();
    private readonly Dictionary<IGpuFrameSource, TransferEntry> _transfers = new(ReferenceEqualityComparer.Instance);

    private bool _disposed;

    public Lease Acquire(IGpuFrameSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_transfers.TryGetValue(source, out TransferEntry? transfer))
            {
                transfer.ReferenceCount++;
                return new Lease(this, transfer);
            }

            transfer = new TransferEntry(source);
            _transfers.Add(source, transfer);
            return new Lease(this, transfer);
        }
    }

    public void Dispose()
    {
        TransferEntry[] transfers;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            transfers = [.. _transfers.Values];
            _transfers.Clear();
        }

        foreach (TransferEntry transfer in transfers)
        {
            transfer.Transfer.Dispose();
        }
    }

    private void Release(TransferEntry transfer)
    {
        bool dispose;
        lock (_stateLock)
        {
            dispose = --transfer.ReferenceCount == 0;
            if (dispose
                && _transfers.TryGetValue(transfer.Source, out TransferEntry? registered)
                && ReferenceEquals(registered, transfer))
            {
                _transfers.Remove(transfer.Source);
            }
        }

        if (dispose)
        {
            transfer.Transfer.Dispose();
        }
    }

    internal sealed class TransferEntry(IGpuFrameSource source)
    {
        public IGpuFrameSource Source { get; } = source;
        public GpuFrameTransfer Transfer { get; } = new();
        public int ReferenceCount { get; set; } = 1;
    }

    /// <summary> retains one shared frame transfer for a render consumer </summary>
    internal sealed class Lease : IDisposable
    {
        private GpuFrameTransferService? _owner;
        private readonly TransferEntry _transfer;

        internal Lease(GpuFrameTransferService owner, TransferEntry transfer)
        {
            _owner = owner;
            _transfer = transfer;
        }

        public bool TryUpdate(
            Device device,
            DeviceContext context,
            [NotNullWhen(true)] out ShaderResourceView? view,
            out GpuFrameDescriptor descriptor)
        {
            if (Volatile.Read(ref _owner) != null)
            {
                return _transfer.Transfer.TryUpdate(
                    _transfer.Source,
                    device,
                    context,
                    out view,
                    out descriptor);
            }

            view = null;
            descriptor = default;
            return false;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Release(_transfer);
    }
}

/// <summary> publishes complete frames to consumers through a single latest-frame slot </summary>
internal sealed class GpuFrameExchange : IGpuFrameSource, IDisposable
{
    private readonly Lock _stateLock = new();

    private GpuFrame? _current;
    private bool _disposed;

    /// <summary> replaces the published frame and always takes ownership of the supplied frame </summary>
    public bool Publish(GpuFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        GpuFrame? previous;
        bool accepted;
        lock (_stateLock)
        {
            accepted = !_disposed;
            if (!accepted)
            {
                previous = frame;
            }
            else
            {
                previous = _current;
                _current = frame;
            }
        }

        previous?.Dispose();
        return accepted;
    }

    /// <summary> releases the published frame after its active leases complete </summary>
    public void Clear()
    {
        GpuFrame? previous;
        lock (_stateLock)
        {
            previous = _current;
            _current = null;
        }

        previous?.Dispose();
    }

    public bool TryAcquireLatest(long afterFrameId, [NotNullWhen(true)] out GpuFrame.Lease? frame)
    {
        lock (_stateLock)
        {
            if (_disposed || _current == null || _current.Id <= afterFrameId)
            {
                frame = null;
                return false;
            }

            return _current.TryAcquire(out frame);
        }
    }

    public void Dispose()
    {
        GpuFrame? previous;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            previous = _current;
            _current = null;
        }

        previous?.Dispose();
    }
}
