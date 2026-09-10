using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
using Microsoft.Extensions.Logging;
using Penumbra.Api.IpcSubscribers;

namespace Intoner.Services.Dependencies;

/// <summary> exposes Penumbra availability and the IPC capabilities used by Intoner's collections </summary>
internal interface IPenumbraDependency : IDependency, IDisposable
{
    /// <summary> stable identifier used by dependency consumers </summary>
    const string Id = "penumbra";

    /// <summary> raised when the Penumbra mod root changes </summary>
    event Action? ModDirectoryChanged;

    /// <summary> gets the current Penumbra mod root directory when available </summary>
    string ModDirectory { get; }

    /// <summary> refreshes the current Penumbra mod root directory </summary>
    void RefreshModDirectory();
}

internal sealed class PenumbraDependency : IPenumbraDependency
{
    private const string InternalName = "Penumbra";
    private static readonly DependencyApiVersion RequiredApiVersion = new(5, 19);
    private static readonly DependencyDefinition DependencyDefinition = new(
        IPenumbraDependency.Id,
        "Penumbra",
        "Adds Penumbra mods and settings to object collections.",
        DependencyRequirement.Optional,
        "penumbra mod mods collection collections dependency optional plugin api");

    private readonly ILogger<PenumbraDependency> _logger;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ApiVersion _apiVersion;
    private readonly GetEnabledState _getEnabledState;
    private readonly GetModDirectory _getModDirectory;
    private readonly Luna.EventSubscriber _initialized;
    private readonly Luna.EventSubscriber _disposed;
    private readonly Luna.EventSubscriber<bool> _enabledChanged;
    private readonly Luna.EventSubscriber<string, bool> _modDirectoryChanged;

    private bool _isDisposed;
    private string _modDirectory = string.Empty;
    private DependencyStatus _status;

    public PenumbraDependency(
        ILogger<PenumbraDependency> logger,
        IDalamudPluginInterface pluginInterface)
    {
        _logger = logger;
        _pluginInterface = pluginInterface;
        _status = CreateStatus(DependencyState.Unknown, "Checking for Penumbra...");
        _apiVersion = new ApiVersion(pluginInterface);
        _getEnabledState = new GetEnabledState(pluginInterface);
        _getModDirectory = new GetModDirectory(pluginInterface);
        _initialized = Initialized.Subscriber(pluginInterface, HandleInitialized);
        _disposed = Disposed.Subscriber(pluginInterface, HandleDisposed);
        _enabledChanged = EnabledChange.Subscriber(pluginInterface, HandleEnabledChanged);
        _modDirectoryChanged = Penumbra.Api.IpcSubscribers.ModDirectoryChanged.Subscriber(
            pluginInterface,
            HandleModDirectoryChanged);
        _pluginInterface.ActivePluginsChanged += HandleActivePluginsChanged;

        Refresh();
        RefreshModDirectory();
    }

    public event Action? StatusChanged;
    public event Action? ModDirectoryChanged;

    public DependencyDefinition Definition
        => DependencyDefinition;

    public DependencyStatus Status
        => Volatile.Read(ref _status);

    public string ModDirectory
    {
        get => Volatile.Read(ref _modDirectory);
        private set
        {
            string previous = Interlocked.Exchange(ref _modDirectory, value);
            if (string.Equals(previous, value, StringComparison.Ordinal))
            {
                return;
            }

            NotifySubscribers(ModDirectoryChanged, "mod directory");
        }
    }

    public void Refresh()
        => UpdateStatus(EvaluateStatus());

    public void RefreshModDirectory()
    {
        if (!Status.IsAvailable)
        {
            ModDirectory = string.Empty;
            return;
        }

        try
        {
            ModDirectory = _getModDirectory.Invoke();
        }
        catch (IpcNotReadyError ex)
        {
            ModDirectory = string.Empty;
            _logger.LogDebug(ex, "Penumbra mod directory IPC is not ready");
        }
        catch (Exception ex)
        {
            ModDirectory = string.Empty;
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
        _pluginInterface.ActivePluginsChanged -= HandleActivePluginsChanged;
        _modDirectoryChanged.Dispose();
        _enabledChanged.Dispose();
        _disposed.Dispose();
        _initialized.Dispose();
    }

    private DependencyStatus EvaluateStatus()
    {
        IExposedPlugin? plugin = null;

        try
        {
            plugin = FindPlugin();
            if (plugin is null)
            {
                return CreateStatus(
                    DependencyState.Missing,
                    "Install Penumbra to use its mods in Intoner's object collections.");
            }

            if (!plugin.IsLoaded)
            {
                return CreateStatus(
                    DependencyState.Disabled,
                    "Enable Penumbra to use its mods in Intoner's object collections.",
                    plugin.Version);
            }

            (int major, int minor) = _apiVersion.Invoke();
            DependencyApiVersion apiVersion = new(major, minor);
            if (major != RequiredApiVersion.Major || minor < RequiredApiVersion.Minor)
            {
                return CreateStatus(
                    DependencyState.Incompatible,
                    $"Update Penumbra to use it with Intoner. API {RequiredApiVersion} is required.",
                    plugin.Version,
                    apiVersion);
            }

            if (!_getEnabledState.Invoke())
            {
                return CreateStatus(
                    DependencyState.FeatureDisabled,
                    "Enable Mods in Penumbra to use its mods in Intoner's object collections.",
                    plugin.Version,
                    apiVersion);
            }

            return CreateStatus(
                DependencyState.Available,
                "Penumbra is connected and ready to use.",
                plugin.Version,
                apiVersion);
        }
        catch (IpcNotReadyError)
        {
            return CreateStatus(
                DependencyState.NotReady,
                "Penumbra is still starting.",
                plugin?.Version);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "failed to evaluate Penumbra dependency status");
            return CreateStatus(
                DependencyState.Error,
                "Intoner couldn't connect to Penumbra. Check /xllog for details.",
                plugin?.Version);
        }
    }

    private static DependencyStatus CreateStatus(
        DependencyState state,
        string message,
        Version? pluginVersion = null,
        DependencyApiVersion? apiVersion = null)
        => new(
            DependencyDefinition,
            state,
            message,
            pluginVersion,
            apiVersion,
            RequiredApiVersion);

    private IExposedPlugin? FindPlugin()
    {
        IExposedPlugin? installed = null;
        foreach (IExposedPlugin plugin in _pluginInterface.InstalledPlugins)
        {
            if (!string.Equals(plugin.InternalName, InternalName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (plugin.IsLoaded)
            {
                return plugin;
            }

            installed ??= plugin;
        }

        return installed;
    }

    private void UpdateStatus(DependencyStatus status)
    {
        DependencyStatus current = Status;
        if (current == status)
        {
            return;
        }

        Volatile.Write(ref _status, status);
        _logger.LogTrace("Penumbra dependency state changed from {Previous} to {Current}", current.State, status.State);
        if (!status.IsAvailable)
        {
            ModDirectory = string.Empty;
        }

        NotifySubscribers(StatusChanged, "status");
    }

    private void NotifySubscribers(Action? subscribers, string eventName)
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Penumbra dependency {EventName} handler failed", eventName);
            }
        }
    }

    private void HandleActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if (!args.AffectedInternalNames.Contains(InternalName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Refresh();
        RefreshModDirectory();
    }

    private void HandleInitialized()
    {
        Refresh();
        RefreshModDirectory();
    }

    private void HandleDisposed()
    {
        UpdateStatus(CreateStatus(
            DependencyState.Disabled,
            "Enable Penumbra to use its mods in Intoner's object collections.",
            Status.PluginVersion));
    }

    private void HandleEnabledChanged(bool enabled)
    {
        if (enabled)
        {
            Refresh();
        }
        else
        {
            UpdateStatus(CreateStatus(
                DependencyState.FeatureDisabled,
                "Enable Mods in Penumbra to use its mods in Intoner's object collections.",
                Status.PluginVersion,
                Status.ApiVersion));
        }

        RefreshModDirectory();
    }

    private void HandleModDirectoryChanged(string modDirectory, bool valid)
        => ModDirectory = Status.IsAvailable && valid
            ? modDirectory
            : string.Empty;
}
