using Dalamud.Game.Command;
using Dalamud.Interface;
using Intoner.Objects;

namespace Intoner.Services;

internal sealed class IntonerPluginHost : IAsyncDisposable
{
    private const string CommandName = "/intoner";

    private readonly IntonerDalamudServices _dalamudServices;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private ObjectServiceHost? _objectHost;
    private CancellationTokenSource? _lifecycleCts;
    private bool _loaded;
    private bool _commandRegistered;
    private bool _uiEventsRegistered;
    private bool _clientEventsRegistered;
    private bool _disposed;

    public IntonerPluginHost(IntonerDalamudServices dalamudServices)
    {
        _dalamudServices = dalamudServices;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loaded)
        {
            return;
        }

        try
        {
            _dalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Intoner.",
            });
            _commandRegistered = true;

            IUiBuilder uiBuilder = _dalamudServices.PluginInterface.UiBuilder;
            uiBuilder.OpenConfigUi += HandleOpenConfigUiRequested;
            uiBuilder.OpenMainUi += HandleOpenMainUiRequested;
            _uiEventsRegistered = true;

            _lifecycleCts = new CancellationTokenSource();
            _dalamudServices.ClientState.Login += HandleClientLogin;
            _dalamudServices.ClientState.Logout += HandleClientLogout;
            _clientEventsRegistered = true;
            _loaded = true;

            using CancellationTokenSource loadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token, cancellationToken);
            await UpdateObjectHostAsync(loadCts.Token).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_lifecycleCts is not null)
        {
            await _lifecycleCts.CancelAsync().ConfigureAwait(false);
        }

        if (_clientEventsRegistered)
        {
            _dalamudServices.ClientState.Login -= HandleClientLogin;
            _dalamudServices.ClientState.Logout -= HandleClientLogout;
        }

        if (_uiEventsRegistered)
        {
            IUiBuilder uiBuilder = _dalamudServices.PluginInterface.UiBuilder;
            uiBuilder.OpenConfigUi -= HandleOpenConfigUiRequested;
            uiBuilder.OpenMainUi -= HandleOpenMainUiRequested;
        }

        if (_commandRegistered)
        {
            _dalamudServices.CommandManager.RemoveHandler(CommandName);
        }

        try
        {
            await UpdateObjectHostAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleCts?.Dispose();
        }
    }

    private bool IsGameSessionActive
        => !_disposed && _dalamudServices.ClientState.IsLoggedIn;

    private async Task UpdateObjectHostAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsGameSessionActive)
            {
                await DisposeObjectHostAsync().ConfigureAwait(false);
                return;
            }

            if (_objectHost is not null)
            {
                return;
            }

            ObjectServiceHost host = await ObjectServiceHost.CreateAsync(_dalamudServices, cancellationToken).ConfigureAwait(false);
            if (!IsGameSessionActive)
            {
                await host.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _objectHost = host;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async ValueTask DisposeObjectHostAsync()
    {
        if (_objectHost is not null)
        {
            await _objectHost.DisposeAsync().ConfigureAwait(false);
            _objectHost = null;
        }
    }

    private void HandleClientLogin()
        => RunLifecycleTask(() => UpdateObjectHostAsync(_lifecycleCts?.Token ?? CancellationToken.None));

    private void HandleClientLogout(int type, int code)
    {
        _ = type;
        _ = code;
        RunLifecycleTask(() => UpdateObjectHostAsync(CancellationToken.None));
    }

    private void RunLifecycleTask(Func<Task> taskFactory)
    {
        if (_disposed)
        {
            return;
        }

        _ = RunLifecycleTaskAsync(taskFactory);
    }

    private async Task RunLifecycleTaskAsync(Func<Task> taskFactory)
    {
        try
        {
            await taskFactory().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown cancels pending session startup
        }
        catch (Exception ex)
        {
            _dalamudServices.Log.Error(ex, "Intoner session lifecycle failed");
        }
    }

    private void OnCommand(string command, string args)
    {
        _ = command;
        _ = args;
        RequestMainWindowToggle();
    }

    private void HandleOpenMainUiRequested()
        => RequestMainWindowToggle();

    private void HandleOpenConfigUiRequested()
    {
        if (IsGameSessionActive)
        {
            _objectHost?.RequestConfigWindow();
        }
    }

    private void RequestMainWindowToggle()
    {
        if (IsGameSessionActive)
        {
            _objectHost?.RequestMainWindowToggle();
        }
    }
}
