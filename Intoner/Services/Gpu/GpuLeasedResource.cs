namespace Intoner.Services.Gpu;

internal sealed class GpuLeasedResource<TResource>(
    TResource resource,
    Action<TResource> disposeResource) : IDisposable where TResource : class
{
    private readonly Lock _sync = new();
    private readonly TResource _resource = resource;
    private readonly Action<TResource> _disposeResource = disposeResource;

    private int _activeLeaseCount;
    private bool _disposeRequested;
    private bool _resourceDisposed;

    public Lease Acquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, _resource);
            _activeLeaseCount++;
        }

        return new Lease(this);
    }

    public void Dispose()
    {
        bool disposeResourceNow;
        lock (_sync)
        {
            if (_disposeRequested)
            {
                return;
            }

            _disposeRequested = true;
            disposeResourceNow = _activeLeaseCount == 0;
        }

        if (disposeResourceNow)
        {
            DisposeResource();
        }
    }

    private void ReleaseLease()
    {
        bool disposeResourceNow;
        lock (_sync)
        {
            if (_activeLeaseCount > 0)
            {
                _activeLeaseCount--;
            }

            disposeResourceNow = _disposeRequested && _activeLeaseCount == 0;
        }

        if (disposeResourceNow)
        {
            DisposeResource();
        }
    }

    private void DisposeResource()
    {
        lock (_sync)
        {
            if (_resourceDisposed)
            {
                return;
            }

            _resourceDisposed = true;
        }

        _disposeResource(_resource);
    }

    internal sealed class Lease(GpuLeasedResource<TResource> owner) : IDisposable
    {
        private GpuLeasedResource<TResource>? _owner = owner;

        public TResource Resource
            => _owner?._resource ?? throw new ObjectDisposedException(nameof(Lease));

        public void Dispose()
        {
            GpuLeasedResource<TResource>? leasedResource = Interlocked.Exchange(ref _owner, null);
            leasedResource?.ReleaseLease();
        }
    }
}
