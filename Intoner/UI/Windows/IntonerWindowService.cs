using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Intoner.UI.Performance;
using Microsoft.Extensions.Logging;

namespace Intoner.UI.Windows;

internal sealed class IntonerWindowService : IDisposable
{
    private readonly IUiBuilder _uiBuilder;
    private readonly ILogger<IntonerWindowService> _logger;
    private readonly FileDialogManager _fileDialogManager;
    private readonly IntonerThemeStyle _themeStyle;
    private readonly IntonerUiPerformanceService _uiPerformance;
    private readonly WindowVisibilityService _visibilityService;
    private readonly WindowSystem _windowSystem = new("Intoner");
    private readonly Lock _lock = new();
    private bool _started;
    private bool _disposed;

    public IntonerWindowService(
        IUiBuilder uiBuilder,
        ILogger<IntonerWindowService> logger,
        FileDialogManager fileDialogManager,
        IntonerThemeStyle themeStyle,
        IntonerUiPerformanceService uiPerformance,
        WindowVisibilityService visibilityService)
    {
        _uiBuilder = uiBuilder;
        _logger = logger;
        _fileDialogManager = fileDialogManager;
        _themeStyle = themeStyle;
        _uiPerformance = uiPerformance;
        _visibilityService = visibilityService;
    }

    public void AddWindow(Window window)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            _windowSystem.AddWindow(window);
        }
    }

    public void RemoveWindow(Window window)
    {
        lock (_lock)
        {
            _windowSystem.RemoveWindow(window);
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_started)
            {
                return;
            }

            _visibilityService.Start();
            _started = true;
            _uiBuilder.Draw += Draw;
            _logger.LogTrace("Intoner window service started");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopLocked();
        }
    }

    private void StopLocked()
    {
        if (!_started)
        {
            return;
        }

        _uiBuilder.Draw -= Draw;
        _visibilityService.Stop();
        _started = false;
        _logger.LogTrace("Intoner window service stopped");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopLocked();
            _windowSystem.RemoveAllWindows();
        }
    }

    private void Draw()
    {
        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            using IntonerUiPerformanceService.Scope timing = _uiPerformance.Measure("IntonerWindowService.Draw");
            using IntonerThemeStyle.Scope style = _themeStyle.Push();
            _windowSystem.Draw();
            _fileDialogManager.Draw();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
