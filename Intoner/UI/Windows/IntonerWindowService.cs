using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Intoner.UI.Performance;
using Intoner.UI.Theme;
using Microsoft.Extensions.Logging;

namespace Intoner.UI.Windows;

internal sealed class IntonerWindowService : IDisposable
{
    private readonly IUiBuilder _uiBuilder;
    private readonly ILogger<IntonerWindowService> _logger;
    private readonly FileDialogManager _fileDialogManager;
    private readonly IntonerThemeStyle _themeStyle;
    private readonly IntonerUiPerformanceService _uiPerformance;
    private readonly WindowSystem _windowSystem = new("Intoner");
    private bool _started;
    private bool _disposed;

    public IntonerWindowService(
        IUiBuilder uiBuilder,
        ILogger<IntonerWindowService> logger,
        FileDialogManager fileDialogManager,
        IntonerThemeStyle themeStyle,
        IntonerUiPerformanceService uiPerformance)
    {
        _uiBuilder = uiBuilder;
        _logger = logger;
        _fileDialogManager = fileDialogManager;
        _themeStyle = themeStyle;
        _uiPerformance = uiPerformance;
    }

    public void AddWindow(Window window)
    {
        ThrowIfDisposed();
        _windowSystem.AddWindow(window);
    }

    public void RemoveWindow(Window window)
        => _windowSystem.RemoveWindow(window);

    public void Start()
    {
        ThrowIfDisposed();

        if (_started)
        {
            return;
        }

        _uiBuilder.Draw += Draw;
        _started = true;
        _logger.LogTrace("Intoner window service started");
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _uiBuilder.Draw -= Draw;
        _started = false;
        _logger.LogTrace("Intoner window service stopped");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _windowSystem.RemoveAllWindows();
    }

    private void Draw()
    {
        using IntonerUiPerformanceService.Scope timing = _uiPerformance.Measure("IntonerWindowService.Draw");
        using IntonerThemeStyle.Scope style = _themeStyle.Push();
        _windowSystem.Draw();
        _fileDialogManager.Draw();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
