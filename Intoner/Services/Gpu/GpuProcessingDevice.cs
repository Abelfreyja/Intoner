using System.Runtime.InteropServices;
using Dalamud.Interface;
using Microsoft.Extensions.Logging;
using SharpDX.Direct3D11;
using DxgiDevice = SharpDX.DXGI.Device;

namespace Intoner.Services.Gpu;

internal sealed partial class GpuProcessingDevice : IDisposable
{
    private const uint D3D11SdkVersion = 7;

    private readonly ILogger _logger;
    private readonly IUiBuilder _uiBuilder;
    private readonly object _sync = new();

    private nint _device;
    private nint _deviceContext;
    private bool _initialized;
    private bool _available;

    public GpuProcessingDevice(ILogger logger, IUiBuilder uiBuilder)
    {
        _logger = logger;
        _uiBuilder = uiBuilder;
    }

    public bool TryGetDevice(out nint device)
    {
        device = nint.Zero;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        lock (_sync)
        {
            if (!_initialized)
            {
                _initialized = true;
                InitializeUnsafe();
            }

            if (!_available || _device == nint.Zero)
            {
                return false;
            }

            device = _device;
            return true;
        }
    }

    public bool TryCreateOperationDeviceClone(out nint device)
    {
        device = nint.Zero;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        nint context = nint.Zero;
        lock (_sync)
        {
            if (!_initialized)
            {
                _initialized = true;
                InitializeUnsafe();
            }

            if (!TryCreateDeviceUnsafe(out device, out context))
            {
                return false;
            }

            _available = true;
        }

        ReleaseComObject(ref context);
        return true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _initialized = false;
            _available = false;
            ReleaseDeviceUnsafe();
        }
    }

    public nint GetCurrentDevicePointer()
    {
        lock (_sync)
        {
            return _device;
        }
    }

    public nint MarkDeviceLost(Exception? exception = null)
    {
        nint lostDevicePointer;
        lock (_sync)
        {
            lostDevicePointer = _device;
            _initialized = false;
            _available = false;
            ReleaseDeviceUnsafe();
        }

        if (exception is null)
        {
            _logger.LogWarning("GPU processing device marked lost; it will reinitialize on next request.");
        }
        else
        {
            _logger.LogWarning(exception, "GPU processing device marked lost; it will reinitialize on next request.");
        }

        return lostDevicePointer;
    }

    private void InitializeUnsafe()
    {
        ReleaseDeviceUnsafe();

        try
        {
            if (TryCreateDeviceUnsafe(out var device, out var context))
            {
                _device = device;
                _deviceContext = context;
                _available = true;
                _logger.LogInformation("GPU processing device initialized.");
                return;
            }

            _available = false;
        }
        catch (Exception ex)
        {
            _available = false;
            _logger.LogDebug(ex, "GPU processing initialization failed.");
        }
    }

    private bool TryCreateDeviceUnsafe(out nint device, out nint context)
    {
        if (TryCreateDeviceFromGameAdapter(out device, out context))
        {
            return true;
        }

        var hr = D3D11CreateDevice(
            nint.Zero,
            D3DDriverType.Hardware,
            nint.Zero,
            0,
            nint.Zero,
            0,
            D3D11SdkVersion,
            out device,
            out _,
            out context);

        if (hr >= 0 && device != nint.Zero)
        {
            _logger.LogDebug("GPU processing operation device created using fallback adapter.");
            return true;
        }

        ReleaseComObject(ref context);
        ReleaseComObject(ref device);
        _logger.LogDebug("GPU processing disabled: fallback D3D11CreateDevice failed (hr=0x{Hr:X8}).", unchecked((uint)hr));
        return false;
    }

    private bool TryCreateDeviceFromGameAdapter(out nint device, out nint context)
    {
        device = nint.Zero;
        context = nint.Zero;

        var uiDeviceHandle = _uiBuilder.DeviceHandle;
        if (uiDeviceHandle == nint.Zero)
        {
            return false;
        }

        var uiDeviceRefAdded = false;
        Device? uiDevice = null;
        try
        {
            Marshal.AddRef(uiDeviceHandle);
            uiDeviceRefAdded = true;
            uiDevice = new Device(uiDeviceHandle);
            using (uiDevice)
            using (var dxgiDevice = uiDevice.QueryInterface<DxgiDevice>())
            using (var adapter = dxgiDevice.Adapter)
            {
                var requestedFeatureLevel = unchecked((uint)uiDevice.FeatureLevel);
                var requestedFeatureLevelMemory = Marshal.AllocHGlobal(sizeof(uint));
                try
                {
                    Marshal.WriteInt32(requestedFeatureLevelMemory, unchecked((int)requestedFeatureLevel));
                    var hr = D3D11CreateDevice(
                        adapter.NativePointer,
                        D3DDriverType.Unknown,
                        nint.Zero,
                        (uint)uiDevice.CreationFlags,
                        requestedFeatureLevelMemory,
                        1,
                        D3D11SdkVersion,
                        out device,
                        out var createdFeatureLevel,
                        out context);

                    if (hr >= 0 && device != nint.Zero)
                    {
                        return true;
                    }

                    _logger.LogDebug(
                        "GPU processing game-adapter init failed (hr=0x{Hr:X8}, requestedFeatureLevel=0x{RequestedFeatureLevel:X8}, createdFeatureLevel=0x{CreatedFeatureLevel:X8}).",
                        unchecked((uint)hr),
                        requestedFeatureLevel,
                        createdFeatureLevel);
                    ReleaseComObject(ref context);
                    ReleaseComObject(ref device);
                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(requestedFeatureLevelMemory);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GPU processing game-adapter initialization failed.");
            ReleaseComObject(ref context);
            ReleaseComObject(ref device);
            return false;
        }
        finally
        {
            if (uiDeviceRefAdded && uiDevice is null)
            {
                Marshal.Release(uiDeviceHandle);
            }
        }
    }

    private void ReleaseDeviceUnsafe()
    {
        ReleaseComObject(ref _deviceContext);
        ReleaseComObject(ref _device);
    }

    private static void ReleaseComObject(ref nint ptr)
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

    private enum D3DDriverType : uint
    {
        Unknown = 0,
        Hardware = 1,
    }

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static partial int D3D11CreateDevice(
        nint adapter,
        D3DDriverType driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out nint device,
        out uint featureLevel,
        out nint immediateContext);
}
