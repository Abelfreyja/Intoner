using System.Runtime.InteropServices;
using Dalamud.Interface;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;

namespace Intoner.Services.Gpu;

internal abstract class GpuUiDeviceResourceHost : IDisposable
{
    private readonly ILogger    _logger;
    private readonly IUiBuilder _uiBuilder;
    private readonly string     _initializationFailureMessage;

    private Device?        _device;
    private DeviceContext? _context;
    private nint           _deviceHandle;
    private bool           _disposed;
    private bool           _loggedInitializationFailure;
    private bool           _resetRequested;

    protected GpuUiDeviceResourceHost(
        ILogger logger,
        IUiBuilder uiBuilder,
        string initializationFailureMessage)
    {
        _logger                       = logger;
        _uiBuilder                    = uiBuilder;
        _initializationFailureMessage = initializationFailureMessage;
    }

    protected Device? ActiveDevice
        => _device;

    protected DeviceContext? ActiveContext
        => _context;

    protected bool IsDisposed
        => _disposed;

    protected bool TryEnsureDevice(out bool resetDeviceResources)
    {
        resetDeviceResources = false;
        if (_disposed || !OperatingSystem.IsWindows())
        {
            return false;
        }

        if (_resetRequested)
        {
            ClearDeviceResources();
            resetDeviceResources = true;
        }

        var deviceHandle = _uiBuilder.DeviceHandle;
        if (deviceHandle == nint.Zero)
        {
            return false;
        }

        if (_device is not null && _context is not null && _deviceHandle == deviceHandle)
        {
            return true;
        }

        ClearDeviceResources();
        resetDeviceResources = true;

        try
        {
            Marshal.AddRef(deviceHandle);
            _device       = new Device(deviceHandle);
            _context      = _device.ImmediateContext;
            _deviceHandle = deviceHandle;

            CreateDeviceResources(_device, _context);
            _loggedInitializationFailure = false;
            return true;
        }
        catch (Exception ex)
        {
            ClearDeviceResources();
            if (!_loggedInitializationFailure)
            {
                _loggedInitializationFailure = true;
                _logger.LogWarning(ex, _initializationFailureMessage);
            }

            return false;
        }
    }

    protected void RequestDeviceReset()
        => _resetRequested = true;

    protected void ClearDeviceResources()
    {
        _resetRequested = false;
        DisposeDeviceResources();
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _deviceHandle = nint.Zero;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing)
        {
            try
            {
                DisposeManagedResources();
            }
            finally
            {
                ClearDeviceResources();
            }
        }
    }

    protected abstract void CreateDeviceResources(Device device, DeviceContext context);

    protected abstract void DisposeDeviceResources();

    protected virtual void DisposeManagedResources()
    { }
}
