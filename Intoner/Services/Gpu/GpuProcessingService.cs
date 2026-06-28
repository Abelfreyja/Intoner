using Dalamud.Interface;
using Microsoft.Extensions.Logging;
using SharpDX;
using System.Runtime.InteropServices;

namespace Intoner.Services.Gpu;

public sealed class GpuProcessingService : IDisposable
{
    private const int MaxConcurrentGpuOperations = 2;

    private readonly GpuProcessingDevice _device;
    private readonly SemaphoreSlim _globalOperationSemaphore = new(MaxConcurrentGpuOperations, MaxConcurrentGpuOperations);
    private readonly SemaphoreSlim _textureOperationSemaphore = new(1, 1);
    private readonly SemaphoreSlim _modelOperationSemaphore = new(1, 1);
    private readonly SemaphoreSlim _genericOperationSemaphore = new(1, 1);
    private int _disposeState;

    public GpuProcessingService(ILogger<GpuProcessingService> logger, IUiBuilder uiBuilder)
    {
        _device = new GpuProcessingDevice(logger, uiBuilder);
    }

    public bool IsDisposed
        => Volatile.Read(ref _disposeState) != 0;

    public bool TryGetDevice(out nint d3d11Device)
    {
        d3d11Device = nint.Zero;
        if (IsDisposed)
        {
            return false;
        }

        return _device.TryGetDevice(out d3d11Device);
    }

    public bool TryCreateOperationDeviceClone(out nint d3d11Device)
    {
        d3d11Device = nint.Zero;
        if (IsDisposed)
        {
            return false;
        }

        return _device.TryCreateOperationDeviceClone(out d3d11Device);
    }

    internal static void ReleaseComObject(ref nint ptr)
    {
        if (ptr == nint.Zero)
        {
            return;
        }

        try
        {
            Marshal.Release(ptr);
        }
        catch
        {
            // ignore release errors
        }
        finally
        {
            ptr = nint.Zero;
        }
    }

    public IDisposable EnterOperationScope(CancellationToken token)
        => EnterOperationScope(token, GpuJobFlags.None);

    public IDisposable EnterOperationScope(CancellationToken token, GpuJobFlags flags)
    {
        return EnterOperationScopeAsync(token, flags).GetAwaiter().GetResult();
    }

    public bool TryEnterOperationScope(CancellationToken token, GpuJobFlags flags, out IDisposable? scope)
    {
        scope = null;
        if (token.IsCancellationRequested || IsDisposed)
        {
            return false;
        }

        var laneSemaphore = ResolveLaneSemaphore(flags);
        if (!laneSemaphore.Wait(0))
        {
            return false;
        }

        var laneHeld = true;
        var globalHeld = false;
        try
        {
            if (token.IsCancellationRequested || IsDisposed)
            {
                return false;
            }

            if (!_globalOperationSemaphore.Wait(0))
            {
                return false;
            }

            globalHeld = true;
            if (token.IsCancellationRequested || IsDisposed)
            {
                return false;
            }

            scope = new GpuOperationScope(_globalOperationSemaphore, laneSemaphore);
            globalHeld = false; // scope owns release
            laneHeld = false;   // scope owns release
            return true;
        }
        finally
        {
            if (globalHeld)
            {
                _globalOperationSemaphore.Release();
            }

            if (laneHeld)
            {
                laneSemaphore.Release();
            }
        }
    }

    public async Task<IDisposable> EnterOperationScopeAsync(CancellationToken token, GpuJobFlags flags)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(GpuProcessingService));
        }

        var laneSemaphore = ResolveLaneSemaphore(flags);
        var laneHeld = false;
        var globalHeld = false;
        try
        {
            await laneSemaphore.WaitAsync(token).ConfigureAwait(false);
            laneHeld = true;

            await _globalOperationSemaphore.WaitAsync(token).ConfigureAwait(false);
            globalHeld = true;

            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(GpuProcessingService));
            }

            return new GpuOperationScope(_globalOperationSemaphore, laneSemaphore);
        }
        catch
        {
            if (globalHeld)
            {
                _globalOperationSemaphore.Release();
            }

            if (laneHeld)
            {
                laneSemaphore.Release();
            }

            throw;
        }
    }

    public async Task RunWithOperationAsync(Func<Task> action, CancellationToken token)
        => await RunWithOperationAsync(action, token, GpuJobFlags.None).ConfigureAwait(false);

    public async Task RunWithOperationAsync(Func<Task> action, CancellationToken token, GpuJobFlags flags)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(GpuProcessingService));
        }

        var laneSemaphore = ResolveLaneSemaphore(flags);
        var laneHeld = false;
        var globalHeld = false;
        try
        {
            await laneSemaphore.WaitAsync(token).ConfigureAwait(false);
            laneHeld = true;
            await _globalOperationSemaphore.WaitAsync(token).ConfigureAwait(false);
            globalHeld = true;
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(GpuProcessingService));
            }

            await action().ConfigureAwait(false);
        }
        finally
        {
            if (globalHeld)
            {
                _globalOperationSemaphore.Release();
            }

            if (laneHeld)
            {
                laneSemaphore.Release();
            }
        }
    }

    public void NotifyOperationFailure(Exception exception)
    {
        if (IsDisposed)
        {
            return;
        }

        if (!IsRecoverableGpuFailure(exception))
        {
            return;
        }

        var lostDevice = _device.MarkDeviceLost(exception);
        GpuResourcePoolService.Shared.InvalidateDeviceResources(lostDevice);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        var currentDevice = _device.GetCurrentDevicePointer();
        _genericOperationSemaphore.Dispose();
        _modelOperationSemaphore.Dispose();
        _textureOperationSemaphore.Dispose();
        _globalOperationSemaphore.Dispose();
        _device.Dispose();
        GpuResourcePoolService.Shared.InvalidateDeviceResources(currentDevice);
    }

    private readonly struct GpuOperationScope : IDisposable
    {
        private readonly SemaphoreSlim _globalSemaphore;
        private readonly SemaphoreSlim _laneSemaphore;

        public GpuOperationScope(
            SemaphoreSlim globalSemaphore,
            SemaphoreSlim laneSemaphore)
        {
            _globalSemaphore = globalSemaphore;
            _laneSemaphore = laneSemaphore;
        }

        public void Dispose()
        {
            _laneSemaphore.Release();
            _globalSemaphore.Release();
        }
    }

    private SemaphoreSlim ResolveLaneSemaphore(GpuJobFlags flags)
    {
        if ((flags & GpuJobFlags.TextureProcessing) != 0
            && (flags & GpuJobFlags.ModelProcessing) == 0)
        {
            return _textureOperationSemaphore;
        }

        if ((flags & GpuJobFlags.ModelProcessing) != 0
            && (flags & GpuJobFlags.TextureProcessing) == 0)
        {
            return _modelOperationSemaphore;
        }

        return _genericOperationSemaphore;
    }

    private static bool IsRecoverableGpuFailure(Exception exception)
    {
        var root = exception is AggregateException aggregate
            ? aggregate.GetBaseException()
            : exception;

        if (root is SharpDXException dx)
        {
            var hr = unchecked((uint)dx.ResultCode.Code);
            return hr is 0x887A0005u // DXGI_ERROR_DEVICE_REMOVED
                or 0x887A0006u // DXGI_ERROR_DEVICE_HUNG
                or 0x887A0007u // DXGI_ERROR_DEVICE_RESET
                or 0x887A0020u; // DXGI_ERROR_DRIVER_INTERNAL_ERROR
        }

        if (root is AccessViolationException)
        {
            return true;
        }

        return root.Message.Contains("device removed", StringComparison.OrdinalIgnoreCase)
            || root.Message.Contains("device reset", StringComparison.OrdinalIgnoreCase)
            || root.Message.Contains("DXGI_ERROR_DEVICE", StringComparison.OrdinalIgnoreCase);
    }
}
