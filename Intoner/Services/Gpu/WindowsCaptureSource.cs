using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;
using DxgiDevice = SharpDX.DXGI.Device;
using DxgiResource = SharpDX.DXGI.Resource;

namespace Intoner.Services.Gpu;

/// <summary> creates Windows capture sources backed by one isolated MTA worker and D3D11 device </summary>
internal sealed class WindowsCaptureService : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly CaptureWorker _worker;
    private readonly Lock _sourceLock = new();
    private readonly Dictionary<CaptureTargetKey, SourceEntry> _sources = [];

    private bool _disposed;

    public WindowsCaptureService(
        ILoggerFactory loggerFactory,
        GpuProcessingService gpuProcessingService)
    {
        _loggerFactory = loggerFactory;
        _worker = new CaptureWorker(
            loggerFactory.CreateLogger<CaptureWorker>(),
            gpuProcessingService);
    }

    public SourceLease AcquireSource(WindowsCaptureTarget target)
    {
        if (target.Handle == nint.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        var key = new CaptureTargetKey(target.Target.Kind, target.Handle);
        lock (_sourceLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sources.TryGetValue(key, out SourceEntry? existing))
            {
                if (!existing.Source.IsClosed)
                {
                    existing.ReferenceCount++;
                    return new SourceLease(this, existing);
                }

                _sources.Remove(key);
            }

            WindowsCaptureSource source = CreateSource(target);
            var entry = new SourceEntry(key, source);
            _sources.Add(key, entry);
            return new SourceLease(this, entry);
        }
    }

    public void Dispose()
    {
        SourceEntry[] sources;
        lock (_sourceLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sources = [.. _sources.Values];
            _sources.Clear();
        }

        foreach (SourceEntry source in sources)
        {
            source.Source.Dispose();
        }

        _worker.Dispose();
    }

    private WindowsCaptureSource CreateSource(WindowsCaptureTarget target)
    {
        var source = new WindowsCaptureSource(
            _loggerFactory.CreateLogger<WindowsCaptureSource>(),
            _worker,
            target);
        if (_worker.TryRegister(source))
        {
            return source;
        }

        source.RejectRegistration();
        throw new InvalidOperationException("The Windows capture worker is unavailable.");
    }

    private void Release(SourceEntry entry)
    {
        bool dispose;
        lock (_sourceLock)
        {
            dispose = --entry.ReferenceCount == 0;
            if (dispose
                && _sources.TryGetValue(entry.Key, out SourceEntry? registered)
                && ReferenceEquals(registered, entry))
            {
                _sources.Remove(entry.Key);
            }
        }

        if (dispose)
        {
            entry.Source.Dispose();
        }
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct CaptureTargetKey(WindowsCaptureTargetKind Kind, nint Handle);

    internal sealed class SourceEntry(CaptureTargetKey key, WindowsCaptureSource source)
    {
        public CaptureTargetKey Key { get; } = key;
        public WindowsCaptureSource Source { get; } = source;
        public int ReferenceCount { get; set; } = 1;
    }

    /// <summary> retains one shared native capture source for a consumer </summary>
    internal sealed class SourceLease : IDisposable
    {
        private WindowsCaptureService? _owner;
        private readonly SourceEntry _entry;

        internal SourceLease(WindowsCaptureService owner, SourceEntry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public IGpuFrameSource FrameSource
            => Volatile.Read(ref _owner) != null
                ? _entry.Source
                : throw new ObjectDisposedException(nameof(SourceLease));

        public bool IsClosed
            => Volatile.Read(ref _owner) == null || _entry.Source.IsClosed;

        public event Action? Closed
        {
            add => _entry.Source.Closed += value;
            remove => _entry.Source.Closed -= value;
        }

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.Release(_entry);
    }

    internal sealed class CaptureWorker : IDisposable
    {
        private const int ActiveWaitMilliseconds = 16;

        private readonly ILogger<CaptureWorker> _logger;
        private readonly GpuProcessingService _gpuProcessingService;
        private readonly ConcurrentQueue<WindowsCaptureSource> _pendingSources = [];
        private readonly List<WindowsCaptureSource> _sources = [];
        private readonly AutoResetEvent _wake = new(false);
        private readonly Lock _stateLock = new();
        private readonly Thread _thread;

        private Device? _device;
        private DeviceContext? _context;
        private int _disposed;
        private int _stopping;
        private int _unavailable;

        public CaptureWorker(
            ILogger<CaptureWorker> logger,
            GpuProcessingService gpuProcessingService)
        {
            _logger = logger;
            _gpuProcessingService = gpuProcessingService;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Intoner Windows Capture",
            };
            _thread.Start();
        }

        public Device Device
            => _device
               ?? throw new InvalidOperationException("The capture device is unavailable.");

        public DeviceContext Context
            => _context
               ?? throw new InvalidOperationException("The capture device context is unavailable.");

        public bool TryRegister(WindowsCaptureSource source)
        {
            lock (_stateLock)
            {
                if (_stopping != 0 || Volatile.Read(ref _unavailable) != 0)
                {
                    return false;
                }

                _pendingSources.Enqueue(source);
                _wake.Set();
                return true;
            }
        }

        public void Wake()
        {
            lock (_stateLock)
            {
                if (_stopping == 0)
                {
                    _wake.Set();
                }
            }
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed != 0)
                {
                    return;
                }

                _disposed = 1;
                Volatile.Write(ref _stopping, 1);
                _wake.Set();
            }

            if (_thread.Join(TimeSpan.FromSeconds(5)))
            {
                _wake.Dispose();
            }
            else
            {
                _logger.LogWarning("Windows capture worker did not stop within the shutdown timeout");
            }
        }

        private void Run()
        {
            bool apartmentInitialized = false;
            try
            {
                WindowsCaptureInterop.InitializeApartment();
                apartmentInitialized = true;
                while (Volatile.Read(ref _stopping) == 0)
                {
                    DrainPendingSources();
                    UpdateSources();
                    _wake.WaitOne(_sources.Count == 0 ? Timeout.Infinite : ActiveWaitMilliseconds);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _unavailable, 1);
                _logger.LogError(ex, "Windows capture worker failed");
            }
            finally
            {
                lock (_stateLock)
                {
                    Volatile.Write(ref _stopping, 1);
                }

                DrainPendingSources(false);
                foreach (WindowsCaptureSource source in _sources)
                {
                    source.RequestShutdownOnWorker();
                }

                while (_sources.Count > 0)
                {
                    UpdateSources();
                    if (_sources.Count > 0)
                    {
                        Thread.Sleep(1);
                    }
                }

                _sources.Clear();
                DisposeDevice();
                if (apartmentInitialized)
                {
                    try
                    {
                        WindowsCaptureInterop.UninitializeApartment();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Windows capture apartment shutdown failed");
                    }
                }
            }
        }

        private void DrainPendingSources(bool ensureDevice = true)
        {
            if (_pendingSources.IsEmpty)
            {
                return;
            }

            if (ensureDevice)
            {
                EnsureDevice();
            }

            while (_pendingSources.TryDequeue(out WindowsCaptureSource? source))
            {
                _sources.Add(source);
            }
        }

        private void UpdateSources()
        {
            for (int index = _sources.Count - 1; index >= 0; --index)
            {
                if (!_sources[index].UpdateOnWorker())
                {
                    _sources.RemoveAt(index);
                }
            }
        }

        private void EnsureDevice()
        {
            if (_device != null)
            {
                return;
            }

            nint devicePointer = nint.Zero;
            try
            {
                if (!_gpuProcessingService.TryCreateCompatibleDeviceClone(out devicePointer)
                    || devicePointer == nint.Zero)
                {
                    throw new InvalidOperationException(
                        "A D3D11 capture device could not be created on the game adapter.");
                }

                _device = new Device(devicePointer);
                devicePointer = nint.Zero;
                _context = _device.ImmediateContext;
            }
            finally
            {
                D3D11ComReference.Release(ref devicePointer);
            }
        }

        private void DisposeDevice()
        {
            try
            {
                _context?.Dispose();
            }
            finally
            {
                _context = null;
                _device?.Dispose();
                _device = null;
            }
        }
    }
}

/// <summary> copies Windows capture frames into shared textures owned by the capture worker </summary>
internal sealed class WindowsCaptureSource : IGpuFrameSource, IDisposable
{
    private const int FrameBufferCount = 3;

    private static long _nextResourceSetId;

    private readonly ILogger<WindowsCaptureSource> _logger;
    private readonly WindowsCaptureService.CaptureWorker _worker;
    private readonly WindowsCaptureTarget _target;
    private readonly GpuFrameExchange _frames = new();
    private readonly ConcurrentQueue<CaptureBuffer> _returnedBuffers = [];
    private readonly Queue<CaptureBuffer> _availableBuffers = [];
    private readonly List<CaptureBuffer> _buffers = [];

    private nint _winRtDevice;
    private nint _item;
    private nint _framePool;
    private nint _session;
    private IDisposable? _itemClosedSubscription;
    private WindowsCaptureSize _poolSize;
    private WindowsCaptureSize _pendingPoolSize;
    private long _resourceSetId;
    private bool _initialized;
    private int _outstandingBufferCount;
    private int _captureItemClosed;
    private int _stopRequested;

    internal WindowsCaptureSource(
        ILogger<WindowsCaptureSource> logger,
        WindowsCaptureService.CaptureWorker worker,
        WindowsCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(worker);
        if (target.Handle == nint.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        _logger = logger;
        _worker = worker;
        _target = target;
    }

    public event Action? Closed;

    public bool IsClosed
        => Volatile.Read(ref _stopRequested) != 0;

    public bool TryAcquireLatest(long afterFrameId, [NotNullWhen(true)] out GpuFrame.Lease? frame)
    {
        if (!IsClosed)
        {
            return _frames.TryAcquireLatest(afterFrameId, out frame);
        }

        frame = null;
        return false;
    }

    public void Dispose()
        => RequestStop();

    internal bool UpdateOnWorker()
    {
        DrainReturnedBuffers();
        if (IsClosed)
        {
            return TryFinishOnWorker();
        }

        try
        {
            if (!_initialized)
            {
                InitializeOnWorker();
            }

            if (Volatile.Read(ref _captureItemClosed) != 0)
            {
                RequestStop();
                DrainReturnedBuffers();
                return TryFinishOnWorker();
            }

            PublishLatestFrameOnWorker();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "screen capture failed for {Target}",
                _target.Target.Name);
            RequestStop();
            DrainReturnedBuffers();
            return TryFinishOnWorker();
        }
    }

    internal void RequestShutdownOnWorker()
        => RequestStop();

    internal void RejectRegistration()
        => RequestStop();

    private void InitializeOnWorker()
    {
        _item = _target.Target.Kind switch
        {
            WindowsCaptureTargetKind.Window => WindowsCaptureInterop.CreateCaptureItem(_target.Handle, true),
            WindowsCaptureTargetKind.Monitor => WindowsCaptureInterop.CreateCaptureItem(_target.Handle, false),
            _ => throw new InvalidOperationException("The capture target kind is invalid."),
        };
        _itemClosedSubscription = WindowsCaptureInterop.SubscribeCaptureItemClosed(
            _item,
            HandleCaptureItemClosed);

        _poolSize = ValidateSize(WindowsCaptureInterop.GetCaptureItemSize(_item));
        using DxgiDevice dxgiDevice = _worker.Device.QueryInterface<DxgiDevice>();
        _winRtDevice = WindowsCaptureInterop.CreateDirect3DDevice(dxgiDevice.NativePointer);
        _framePool = WindowsCaptureInterop.CreateFramePool(
            _winRtDevice,
            FrameBufferCount,
            _poolSize);
        _session = WindowsCaptureInterop.CreateCaptureSession(_framePool, _item);
        _ = WindowsCaptureInterop.TrySetCursorCaptureEnabled(_session, enabled: false);
        WindowsCaptureInterop.StartCapture(_session);
        _initialized = true;
    }

    private void PublishLatestFrameOnWorker()
    {
        if (_pendingPoolSize.IsValid)
        {
            TryRecreateFramePoolOnWorker();
            if (_pendingPoolSize.IsValid)
            {
                return;
            }
        }

        nint frame = nint.Zero;
        CaptureBuffer? buffer = null;
        bool bufferLeased = false;
        try
        {
            frame = TakeLatestFrame(_framePool);
            if (frame == nint.Zero)
            {
                return;
            }

            WindowsCaptureSize contentSize = WindowsCaptureInterop.GetFrameSize(frame);
            if (!contentSize.IsValid)
            {
                return;
            }

            if (contentSize != _poolSize)
            {
                _pendingPoolSize = contentSize;
                _frames.Clear();
                return;
            }

            using Texture2D sourceTexture = WindowsCaptureInterop.GetFrameTexture(frame);
            ValidateCaptureTexture(sourceTexture.Description, contentSize);
            EnsureCaptureBuffers(contentSize);
            if (!TryAcquireCaptureBuffer(out buffer))
            {
                return;
            }

            var contentRegion = new ResourceRegion(
                0,
                0,
                0,
                contentSize.Width,
                contentSize.Height,
                1);
            _worker.Context.CopySubresourceRegion(
                sourceTexture,
                0,
                contentRegion,
                buffer.Texture,
                0,
                0,
                0,
                0);
            _worker.Context.Flush();

            CaptureBufferOwner? bufferOwner = new(this, buffer);
            bufferLeased = true;
            try
            {
                var gpuFrame = new GpuFrame(
                    buffer.SharedHandle,
                    _resourceSetId,
                    buffer.Id,
                    new GpuFrameDescriptor(
                        contentSize.Width,
                        contentSize.Height,
                        GpuFrameAlphaMode.Premultiplied),
                    bufferOwner);
                bufferOwner = null;
                _frames.Publish(gpuFrame);
            }
            finally
            {
                bufferOwner?.Dispose();
            }
        }
        finally
        {
            if (buffer != null && !bufferLeased)
            {
                _availableBuffers.Enqueue(buffer);
            }

            WindowsCaptureInterop.Release(ref frame);
        }
    }

    private void EnsureCaptureBuffers(WindowsCaptureSize size)
    {
        if (_buffers.Count != 0)
        {
            return;
        }

        _resourceSetId = Interlocked.Increment(ref _nextResourceSetId);
        for (int index = 0; index < FrameBufferCount; ++index)
        {
            CaptureBuffer buffer = CaptureBuffer.Create(_worker.Device, size);
            _buffers.Add(buffer);
            _availableBuffers.Enqueue(buffer);
        }
    }

    private static void ValidateCaptureTexture(
        in Texture2DDescription description,
        WindowsCaptureSize size)
    {
        if (description.Width < size.Width
            || description.Height < size.Height
            || description.MipLevels != 1
            || description.ArraySize != 1
            || description.Format != Format.B8G8R8A8_UNorm
            || description.SampleDescription.Count != 1
            || description.SampleDescription.Quality != 0)
        {
            throw new InvalidOperationException(
                $"Windows capture returned an unsupported texture shape: "
                + $"{description.Width}x{description.Height}, "
                + $"mips {description.MipLevels}, array {description.ArraySize}, "
                + $"format {description.Format}, samples "
                + $"{description.SampleDescription.Count}:{description.SampleDescription.Quality}.");
        }
    }

    private bool TryAcquireCaptureBuffer([NotNullWhen(true)] out CaptureBuffer? buffer)
        => _availableBuffers.TryDequeue(out buffer);

    private void TryRecreateFramePoolOnWorker()
    {
        DrainReturnedBuffers();
        if (!_pendingPoolSize.IsValid || _outstandingBufferCount != 0)
        {
            return;
        }

        DisposeCaptureBuffers();
        WindowsCaptureInterop.RecreateFramePool(
            _framePool,
            _winRtDevice,
            FrameBufferCount,
            _pendingPoolSize);
        _poolSize = _pendingPoolSize;
        _pendingPoolSize = default;
    }

    private void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        _frames.Dispose();
        try
        {
            NotifyClosed();
        }
        finally
        {
            _worker.Wake();
        }
    }

    private void NotifyClosed()
    {
        Action? closed = Closed;
        if (closed == null)
        {
            return;
        }

        foreach (Delegate callback in closed.GetInvocationList())
        {
            try
            {
                ((Action)callback)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "screen capture close notification failed for {Target}",
                    _target.Target.Name);
            }
        }
    }

    private void HandleCaptureItemClosed()
    {
        if (Interlocked.Exchange(ref _captureItemClosed, 1) == 0)
        {
            _worker.Wake();
        }
    }

    private bool TryFinishOnWorker()
    {
        DrainReturnedBuffers();
        if (_outstandingBufferCount != 0)
        {
            return true;
        }

        DisposeNativeStateOnWorker();
        return false;
    }

    private void DisposeNativeStateOnWorker()
    {
        DisposeCaptureBuffers();
        TryReleaseNativeStateOnWorker(
            "capture item event",
            () => Interlocked.Exchange(ref _itemClosedSubscription, null)?.Dispose());
        TryReleaseNativeStateOnWorker(
            "session",
            () => WindowsCaptureInterop.CloseAndRelease(ref _session));
        TryReleaseNativeStateOnWorker(
            "frame pool",
            () => WindowsCaptureInterop.CloseAndRelease(ref _framePool));
        TryReleaseNativeStateOnWorker(
            "capture item",
            () => WindowsCaptureInterop.Release(ref _item));
        TryReleaseNativeStateOnWorker(
            "WinRT device",
            () => WindowsCaptureInterop.Release(ref _winRtDevice));
        _initialized = false;
    }

    private void DisposeCaptureBuffers()
    {
        _availableBuffers.Clear();
        foreach (CaptureBuffer buffer in _buffers)
        {
            buffer.Dispose();
        }

        _buffers.Clear();
        _resourceSetId = 0;
    }

    private void TryReleaseNativeStateOnWorker(string resourceName, Action release)
    {
        try
        {
            release();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "screen capture {Resource} cleanup failed for {Target}",
                resourceName,
                _target.Target.Name);
        }
    }

    private void ReturnBuffer(CaptureBuffer buffer)
    {
        _returnedBuffers.Enqueue(buffer);
        _worker.Wake();
    }

    private void DrainReturnedBuffers()
    {
        while (_returnedBuffers.TryDequeue(out CaptureBuffer? buffer))
        {
            --_outstandingBufferCount;
            if (!IsClosed && !_pendingPoolSize.IsValid)
            {
                _availableBuffers.Enqueue(buffer);
            }
        }
    }

    private static nint TakeLatestFrame(nint framePool)
    {
        nint latest = nint.Zero;
        try
        {
            for (int index = 0; index < FrameBufferCount; ++index)
            {
                nint frame = WindowsCaptureInterop.TryGetNextFrame(framePool);
                if (frame == nint.Zero)
                {
                    break;
                }

                WindowsCaptureInterop.Release(ref latest);
                latest = frame;
            }

            nint result = latest;
            latest = nint.Zero;
            return result;
        }
        finally
        {
            WindowsCaptureInterop.Release(ref latest);
        }
    }

    private static WindowsCaptureSize ValidateSize(WindowsCaptureSize size)
        => size.IsValid
            ? size
            : throw new InvalidOperationException("The capture target has no drawable area.");

    private sealed class CaptureBufferOwner : IDisposable
    {
        private WindowsCaptureSource? _owner;
        private CaptureBuffer? _buffer;

        public CaptureBufferOwner(
            WindowsCaptureSource owner,
            CaptureBuffer buffer)
        {
            _owner = owner;
            _buffer = buffer;
            ++owner._outstandingBufferCount;
        }

        public void Dispose()
        {
            WindowsCaptureSource? owner = Interlocked.Exchange(ref _owner, null);
            CaptureBuffer? buffer = Interlocked.Exchange(ref _buffer, null);
            if (owner != null && buffer != null)
            {
                owner.ReturnBuffer(buffer);
            }
        }
    }

    private sealed class CaptureBuffer : IDisposable
    {
        private static long _nextId;

        private CaptureBuffer(
            Texture2D texture,
            nint sharedHandle)
        {
            Id = Interlocked.Increment(ref _nextId);
            Texture = texture;
            SharedHandle = sharedHandle;
        }

        public long Id { get; }
        public Texture2D Texture { get; }
        public nint SharedHandle { get; }

        public static CaptureBuffer Create(
            Device device,
            WindowsCaptureSize size)
        {
            Texture2D? texture = null;
            try
            {
                texture = new Texture2D(
                    device,
                    new Texture2DDescription
                    {
                        Width = size.Width,
                        Height = size.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.Shared,
                    });
                using DxgiResource resource = texture.QueryInterface<DxgiResource>();
                nint handle = resource.SharedHandle;
                if (handle == nint.Zero)
                {
                    throw new InvalidOperationException("The capture shared texture has no handle.");
                }

                var buffer = new CaptureBuffer(texture, handle);
                texture = null;
                return buffer;
            }
            catch (Exception ex)
            {
                string adapterName;
                using (DxgiDevice dxgiDevice = device.QueryInterface<DxgiDevice>())
                using (Adapter adapter = dxgiDevice.Adapter)
                {
                    adapterName = adapter.Description.Description;
                }

                throw new InvalidOperationException(
                    $"Could not create the {size.Width}x{size.Height} capture transfer texture "
                    + $"on {adapterName} ({device.FeatureLevel}, {device.CreationFlags}).",
                    ex);
            }
            finally
            {
                texture?.Dispose();
            }
        }

        public void Dispose()
            => Texture.Dispose();
    }
}
