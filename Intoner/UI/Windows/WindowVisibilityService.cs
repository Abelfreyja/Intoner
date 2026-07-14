using Dalamud.Interface;
using Intoner.Objects.Filesystem.Configuration;

namespace Intoner.UI.Windows;

internal sealed class WindowVisibilityService : IDisposable
{
    private readonly IUiBuilder _uiBuilder;
    private readonly IObjectConfigurationService _configurationService;
    private readonly Lock _lock = new();

    private bool _started;
    private bool _disposed;

    public WindowVisibilityService(IUiBuilder uiBuilder, IObjectConfigurationService configurationService)
    {
        _uiBuilder = uiBuilder;
        _configurationService = configurationService;
    }

    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _configurationService.ConfigurationChanged += HandleConfigurationChanged;
            _started = true;
            ApplyVisibilityPolicyUnsafe();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopUnsafe();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            StopUnsafe();
            _disposed = true;
        }
    }

    private void HandleConfigurationChanged()
    {
        lock (_lock)
        {
            if (_started)
            {
                ApplyVisibilityPolicyUnsafe();
            }
        }
    }

    private void StopUnsafe()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _configurationService.ConfigurationChanged -= HandleConfigurationChanged;
        _uiBuilder.DisableUserUiHide = false;
        _uiBuilder.DisableCutsceneUiHide = false;
        _uiBuilder.DisableGposeUiHide = false;
    }

    private void ApplyVisibilityPolicyUnsafe()
    {
        UiConfiguration configuration = _configurationService.Current.Ui;
        _uiBuilder.DisableUserUiHide = !configuration.HideWithGameUi;
        _uiBuilder.DisableCutsceneUiHide = !configuration.HideInCutscenes;
        _uiBuilder.DisableGposeUiHide = !configuration.HideInGpose;
    }
}
