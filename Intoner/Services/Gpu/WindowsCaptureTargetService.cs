using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace Intoner.Services.Gpu;

internal enum WindowsCaptureTargetKind
{
    None = 0,
    Window = 1,
    Monitor = 2,
}

internal sealed record WindowsCaptureTargetDescriptor
{
    public WindowsCaptureTargetKind Kind { get; init; }
    public string Name { get; init; } = string.Empty;
    public string MonitorDeviceName { get; init; } = string.Empty;
    public string WindowExecutablePath { get; init; } = string.Empty;
    public string WindowClassName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public long WindowHandle { get; init; }

    public bool IsConfigured
        => Kind switch
        {
            WindowsCaptureTargetKind.Monitor => !string.IsNullOrEmpty(MonitorDeviceName),
            WindowsCaptureTargetKind.Window => !string.IsNullOrEmpty(WindowExecutablePath)
                                               && !string.IsNullOrEmpty(WindowClassName)
                                               && !string.IsNullOrEmpty(WindowTitle),
            _ => false,
        };

    public bool HasSameIdentity(WindowsCaptureTargetDescriptor other)
        => Kind == other.Kind
           && Kind switch
           {
               WindowsCaptureTargetKind.Monitor => string.Equals(
                   MonitorDeviceName,
                   other.MonitorDeviceName,
                   StringComparison.OrdinalIgnoreCase),
               WindowsCaptureTargetKind.Window => string.Equals(
                                                      WindowExecutablePath,
                                                      other.WindowExecutablePath,
                                                      StringComparison.OrdinalIgnoreCase)
                                                  && string.Equals(
                                                      WindowClassName,
                                                      other.WindowClassName,
                                                      StringComparison.Ordinal)
                                                  && string.Equals(
                                                      WindowTitle,
                                                      other.WindowTitle,
                                                      StringComparison.Ordinal),
               _ => true,
           };

    public bool HasSameCaptureSource(WindowsCaptureTargetDescriptor other)
        => HasSameIdentity(other)
           && (Kind != WindowsCaptureTargetKind.Window
               || WindowHandle == 0
               || other.WindowHandle == 0
               || WindowHandle == other.WindowHandle);

    public WindowsCaptureTargetDescriptor Normalize()
    {
        string name = Name?.Trim() ?? string.Empty;
        string monitorDeviceName = MonitorDeviceName?.Trim() ?? string.Empty;
        string windowExecutablePath = WindowExecutablePath?.Trim() ?? string.Empty;
        string windowClassName = WindowClassName?.Trim() ?? string.Empty;
        string windowTitle = WindowTitle?.Trim() ?? string.Empty;
        return Kind switch
        {
            WindowsCaptureTargetKind.Monitor => new WindowsCaptureTargetDescriptor
            {
                Kind = Kind,
                Name = name.Length > 0 ? name : monitorDeviceName,
                MonitorDeviceName = monitorDeviceName,
            },
            WindowsCaptureTargetKind.Window => new WindowsCaptureTargetDescriptor
            {
                Kind = Kind,
                Name = name.Length > 0 ? name : windowTitle,
                WindowExecutablePath = windowExecutablePath,
                WindowClassName = windowClassName,
                WindowTitle = windowTitle,
                WindowHandle = WindowHandle,
            },
            _ => new WindowsCaptureTargetDescriptor(),
        };
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct WindowsCaptureTarget(
    WindowsCaptureTargetDescriptor Target,
    nint Handle);

internal sealed partial class WindowsCaptureTargetService : IDisposable
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxWindowClassLength = 256;
    private const int MaxProcessPathLength = 32768;
    private const long TargetCacheLifetimeMilliseconds = 2000;

    private static readonly EnumWindowsCallback WindowCallback = CollectWindow;
    private static readonly MonitorEnumCallback MonitorCallback = CollectMonitor;

    private readonly ILogger<WindowsCaptureTargetService> _logger;
    private readonly Lock _refreshLock = new();

    private IReadOnlyList<WindowsCaptureTarget> _cachedTargets = [];
    private Task? _refreshTask;
    private long _cacheExpiresAtMilliseconds;
    private bool _disposed;

    public WindowsCaptureTargetService(ILogger<WindowsCaptureTargetService> logger)
    {
        _logger = logger;
        RequestRefresh();
    }

    public IReadOnlyList<WindowsCaptureTarget> GetTargets()
    {
        if (Environment.TickCount64 >= Volatile.Read(ref _cacheExpiresAtMilliseconds))
        {
            RequestRefresh();
        }

        return Volatile.Read(ref _cachedTargets);
    }

    public void RefreshTargets()
        => RequestRefresh();

    public void Dispose()
    {
        Task? refreshTask;
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            refreshTask = _refreshTask;
        }

        refreshTask?.GetAwaiter().GetResult();
    }

    private void RequestRefresh()
    {
        lock (_refreshLock)
        {
            if (_disposed || _refreshTask is { IsCompleted: false })
            {
                return;
            }

            _refreshTask = Task.Run(RefreshTargetsWorker);
        }
    }

    private void RefreshTargetsWorker()
    {
        try
        {
            List<WindowsCaptureTarget> targets = CollectTargets();
            IReadOnlyList<WindowsCaptureTarget> snapshot = targets
                .OrderBy(static target => target.Target.Kind == WindowsCaptureTargetKind.Monitor ? 0 : 1)
                .ThenBy(static target => target.Target.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Volatile.Write(ref _cachedTargets, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Windows capture target refresh failed");
        }
        finally
        {
            Volatile.Write(
                ref _cacheExpiresAtMilliseconds,
                Environment.TickCount64 + TargetCacheLifetimeMilliseconds);
        }
    }

    private static List<WindowsCaptureTarget> CollectTargets()
    {
        List<WindowsCaptureTarget> targets = [];
        GCHandle stateHandle = GCHandle.Alloc(targets);
        try
        {
            _ = EnumDisplayMonitors(nint.Zero, nint.Zero, MonitorCallback, GCHandle.ToIntPtr(stateHandle));
            _ = EnumWindows(WindowCallback, GCHandle.ToIntPtr(stateHandle));
            return targets;
        }
        finally
        {
            stateHandle.Free();
        }
    }

    public bool TryResolve(WindowsCaptureTargetDescriptor model, out WindowsCaptureTarget target)
    {
        target = default;
        if (!model.IsConfigured)
        {
            return false;
        }

        IReadOnlyList<WindowsCaptureTarget> targets = GetTargets();
        if (model.Kind == WindowsCaptureTargetKind.Monitor)
        {
            target = targets.FirstOrDefault(candidate => model.HasSameIdentity(candidate.Target));
            return target.Handle != nint.Zero;
        }

        if (model.Kind != WindowsCaptureTargetKind.Window)
        {
            return false;
        }

        if (model.WindowHandle != 0)
        {
            foreach (WindowsCaptureTarget candidate in targets)
            {
                if (candidate.Handle == (nint)model.WindowHandle
                    && HasSameWindowApplication(model, candidate.Target))
                {
                    target = candidate;
                    return true;
                }
            }
        }

        return TryResolveUniqueWindow(model, targets, requireMatchingTitle: true, out target)
               || TryResolveUniqueWindow(model, targets, requireMatchingTitle: false, out target);
    }

    private static bool TryResolveUniqueWindow(
        WindowsCaptureTargetDescriptor model,
        IReadOnlyList<WindowsCaptureTarget> targets,
        bool requireMatchingTitle,
        out WindowsCaptureTarget target)
    {
        target = default;
        bool found = false;
        foreach (WindowsCaptureTarget candidate in targets)
        {
            WindowsCaptureTargetDescriptor descriptor = candidate.Target;
            if (descriptor.Kind != WindowsCaptureTargetKind.Window
                || !HasSameWindowApplication(model, descriptor)
                || requireMatchingTitle
                   && !string.Equals(
                       model.WindowTitle,
                       descriptor.WindowTitle,
                       StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                target = default;
                return false;
            }

            target = candidate;
            found = true;
        }

        return found;
    }

    private static bool HasSameWindowApplication(
        WindowsCaptureTargetDescriptor left,
        WindowsCaptureTargetDescriptor right)
        => right.Kind == WindowsCaptureTargetKind.Window
           && string.Equals(
               left.WindowExecutablePath,
               right.WindowExecutablePath,
               StringComparison.OrdinalIgnoreCase)
           && string.Equals(
               left.WindowClassName,
               right.WindowClassName,
               StringComparison.Ordinal);

    public static bool IsAvailable(WindowsCaptureTarget target)
        => target.Target.Kind switch
        {
            WindowsCaptureTargetKind.Window => NativeWindowInterop.IsWindow(target.Handle),
            WindowsCaptureTargetKind.Monitor => TryGetMonitorInfo(target.Handle, out _),
            _ => false,
        };

    private static bool CollectWindow(nint window, nint state)
    {
        if (!TryCreateWindowTarget(window, out WindowsCaptureTarget target))
        {
            return true;
        }

        GetTargetList(state).Add(target);
        return true;
    }

    private static bool CollectMonitor(nint monitor, nint deviceContext, ref NativeRect bounds, nint state)
    {
        if (!TryCreateMonitorTarget(monitor, out WindowsCaptureTarget target))
        {
            return true;
        }

        GetTargetList(state).Add(target);
        return true;
    }

    private static List<WindowsCaptureTarget> GetTargetList(nint state)
        => (List<WindowsCaptureTarget>)GCHandle.FromIntPtr(state).Target!;

    private static bool TryCreateWindowTarget(nint window, out WindowsCaptureTarget target)
    {
        target = default;
        if (!IsCaptureCandidate(window)
            || !IsWindowVisible(window)
            || NativeWindowInterop.IsCloaked(window)
            || !HasDrawableClientArea(window)
            || !TryGetWindowText(window, out string title)
            || !TryGetWindowClass(window, out string className)
            || !TryGetWindowExecutablePath(window, out string executablePath))
        {
            return false;
        }

        target = new WindowsCaptureTarget(
            new WindowsCaptureTargetDescriptor
            {
                Kind = WindowsCaptureTargetKind.Window,
                Name = title,
                WindowExecutablePath = executablePath,
                WindowClassName = className,
                WindowTitle = title,
                WindowHandle = (long)window,
            },
            window);
        return true;
    }

    private static bool TryCreateMonitorTarget(nint monitor, out WindowsCaptureTarget target)
    {
        target = default;
        if (!TryGetMonitorInfo(monitor, out MonitorInfo monitorInfo)
            || string.IsNullOrWhiteSpace(monitorInfo.DeviceName))
        {
            return false;
        }

        string deviceName = monitorInfo.DeviceName.TrimEnd('\0');
        string displayName = TryGetMonitorDisplayName(deviceName, out string resolvedName)
            ? resolvedName
            : deviceName;
        target = new WindowsCaptureTarget(
            new WindowsCaptureTargetDescriptor
            {
                Kind = WindowsCaptureTargetKind.Monitor,
                Name = displayName,
                MonitorDeviceName = deviceName,
            },
            monitor);
        return true;
    }

    private static bool IsCaptureCandidate(nint window)
    {
        if (window == GetShellWindow())
        {
            return false;
        }

        return !GetWindowDisplayAffinity(window, out uint affinity) || affinity == 0;
    }

    private static bool TryGetMonitorInfo(nint monitor, out MonitorInfo monitorInfo)
    {
        monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
        };
        return GetMonitorInfo(monitor, ref monitorInfo);
    }

    private static bool TryGetWindowText(nint window, out string title)
    {
        int length = GetWindowTextLength(window);
        if (length <= 0)
        {
            title = string.Empty;
            return false;
        }

        var buffer = new StringBuilder(length + 1);
        if (GetWindowText(window, buffer, buffer.Capacity) <= 0)
        {
            title = string.Empty;
            return false;
        }

        title = buffer.ToString().Trim();
        return title.Length > 0;
    }

    private static bool TryGetWindowClass(nint window, out string className)
    {
        var buffer = new StringBuilder(MaxWindowClassLength);
        if (GetClassName(window, buffer, buffer.Capacity) <= 0)
        {
            className = string.Empty;
            return false;
        }

        className = buffer.ToString();
        return className.Length > 0;
    }

    private static bool TryGetWindowExecutablePath(nint window, out string executablePath)
    {
        uint processId = NativeWindowInterop.GetProcessId(window);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            executablePath = string.Empty;
            return false;
        }

        using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            executablePath = string.Empty;
            return false;
        }

        int capacity = MaxProcessPathLength;
        var buffer = new StringBuilder(capacity);
        if (!QueryFullProcessImageName(process, 0, buffer, ref capacity))
        {
            executablePath = string.Empty;
            return false;
        }

        executablePath = buffer.ToString();
        return executablePath.Length > 0;
    }

    private static bool TryGetMonitorDisplayName(string deviceName, out string displayName)
    {
        var displayDevice = new DisplayDevice
        {
            Size = Marshal.SizeOf<DisplayDevice>(),
        };
        if (!EnumDisplayDevices(deviceName, 0, ref displayDevice, 0))
        {
            displayName = string.Empty;
            return false;
        }

        displayName = displayDevice.DeviceString.TrimEnd('\0').Trim();
        return displayName.Length > 0;
    }

    private static bool HasDrawableClientArea(nint window)
        => NativeWindowInterop.GetClientRect(window, out NativeRect bounds)
           && bounds.Right > bounds.Left
           && bounds.Bottom > bounds.Top;

    private delegate bool EnumWindowsCallback(nint window, nint state);
    private delegate bool MonitorEnumCallback(nint monitor, nint deviceContext, ref NativeRect bounds, nint state);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsCallback callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRect,
        MonitorEnumCallback callback,
        nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maxCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string deviceName,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);
}
