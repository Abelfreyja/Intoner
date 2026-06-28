using Dalamud.Plugin;
using Intoner.Ipc;
using Microsoft.Extensions.Logging;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using PenumbraEventSubscriber = Penumbra.Api.Helpers.EventSubscriber;
using PenumbraModSettingEventSubscriber = Penumbra.Api.Helpers.EventSubscriber<Penumbra.Api.Enums.ModSettingChange, System.Guid, string, bool>;

namespace Intoner.Objects.Interop.Ipc;

/// <summary> exposes the small Penumbra IPC surface needed by object collection resolution </summary>
internal interface IObjectPenumbraIpc : IDisposable
{
    /// <summary> raised when Penumbra availability changes </summary>
    event Action? AvailabilityChanged;

    /// <summary> raised when the Penumbra mod root changes </summary>
    event Action? ModDirectoryChanged;

    /// <summary> gets the current Penumbra IPC state </summary>
    IpcConnectionState State { get; }

    /// <summary> gets the current Penumbra mod root directory when available </summary>
    string ModDirectory { get; }

    /// <summary> refreshes the current Penumbra IPC state </summary>
    void CheckApi();

    /// <summary> refreshes the current Penumbra mod root directory </summary>
    void CheckModDirectory();
}

internal sealed class ObjectPenumbraIpc : IObjectPenumbraIpc
{
    private static readonly Version MinimumVersion = new(1, 2, 0, 22);

    private readonly ILogger<ObjectPenumbraIpc> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly GetEnabledState _getEnabledState;
    private readonly GetModDirectory _getModDirectory;
    private readonly PenumbraEventSubscriber _initialized;
    private readonly PenumbraEventSubscriber _disposed;
    private readonly PenumbraModSettingEventSubscriber _modSettingChanged;

    private bool _isDisposed;
    private string _modDirectory = string.Empty;

    public ObjectPenumbraIpc(ILogger<ObjectPenumbraIpc> logger, IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _pluginInterface = pluginInterface;
        _getEnabledState = new GetEnabledState(pluginInterface);
        _getModDirectory = new GetModDirectory(pluginInterface);
        _initialized = Initialized.Subscriber(pluginInterface, HandleInitialized);
        _disposed = Disposed.Subscriber(pluginInterface, HandleDisposed);
        _modSettingChanged = ModSettingChanged.Subscriber(pluginInterface, HandleModSettingChanged);

        CheckApi();
        CheckModDirectory();
    }

    public event Action? AvailabilityChanged;
    public event Action? ModDirectoryChanged;

    public IpcConnectionState State { get; private set; } = IpcConnectionState.Unknown;

    public string ModDirectory
    {
        get => _modDirectory;
        private set
        {
            if (string.Equals(_modDirectory, value, StringComparison.Ordinal))
            {
                return;
            }

            _modDirectory = value;
            ModDirectoryChanged?.Invoke();
        }
    }

    public void CheckApi()
        => UpdateState(EvaluateState());

    public void CheckModDirectory()
    {
        if (State != IpcConnectionState.Available)
        {
            ModDirectory = string.Empty;
            return;
        }

        try
        {
            ModDirectory = _getModDirectory.Invoke().ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to resolve Penumbra mod directory");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _modSettingChanged.Dispose();
        _disposed.Dispose();
        _initialized.Dispose();
    }

    private IpcConnectionState EvaluateState()
    {
        try
        {
            var plugin = _pluginInterface.InstalledPlugins
                .Where(static plugin => string.Equals(plugin.InternalName, "Penumbra", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static plugin => plugin.IsLoaded)
                .FirstOrDefault();

            if (plugin is null)
            {
                return IpcConnectionState.MissingPlugin;
            }

            if (plugin.Version < MinimumVersion)
            {
                return IpcConnectionState.VersionMismatch;
            }

            if (!plugin.IsLoaded || !_getEnabledState.Invoke())
            {
                return IpcConnectionState.PluginDisabled;
            }

            return IpcConnectionState.Available;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "failed to evaluate Penumbra IPC state");
            return IpcConnectionState.Error;
        }
    }

    private void UpdateState(IpcConnectionState state)
    {
        if (State == state)
        {
            return;
        }

        IpcConnectionState previous = State;
        State = state;
        _logger.LogTrace("Penumbra IPC state changed from {Previous} to {Current}", previous, state);

        if (state != IpcConnectionState.Available)
        {
            ModDirectory = string.Empty;
        }

        AvailabilityChanged?.Invoke();
    }

    private void HandleInitialized()
    {
        CheckApi();
        CheckModDirectory();
    }

    private void HandleDisposed()
    {
        ModDirectory = string.Empty;
        CheckApi();
    }

    private void HandleModSettingChanged(ModSettingChange change, Guid _, string __, bool ___)
    {
        if (change == ModSettingChange.EnableState)
        {
            CheckApi();
            CheckModDirectory();
        }
    }
}
