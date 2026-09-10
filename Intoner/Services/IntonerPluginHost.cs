using Dalamud.Game.Command;
using Dalamud.Interface;
using Microsoft.Extensions.DependencyInjection;

namespace Intoner.Services;

internal sealed class IntonerPluginHost : IAsyncDisposable
{
    private const string CommandName = "/intoner";

    private readonly IntonerDalamudServices _dalamudServices;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private ServiceProvider? _provider;
    private IntonerSessionHost? _sessionHost;
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
            ServiceCollection services = [];
            services.AddIntonerServices(_dalamudServices);
            _provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

            _dalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Intoner.",
            });
            _commandRegistered = true;

            IUiBuilder uiBuilder = _dalamudServices.PluginInterface.UiBuilder;
            uiBuilder.OpenConfigUi += HandleOpenConfigUiRequested;
            uiBuilder.OpenMainUi += RequestMainWindowToggle;
            _uiEventsRegistered = true;

            _lifecycleCts = new CancellationTokenSource();
            _dalamudServices.ClientState.Login += HandleClientLogin;
            _dalamudServices.ClientState.Logout += HandleClientLogout;
            _clientEventsRegistered = true;
            _loaded = true;

            using CancellationTokenSource loadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token, cancellationToken);
            await UpdateSessionHostAsync(loadCts.Token).ConfigureAwait(false);
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
        using CancellationTokenSource? lifecycleCts = _lifecycleCts;
        if (lifecycleCts is not null)
        {
            await lifecycleCts.CancelAsync().ConfigureAwait(false);
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
            uiBuilder.OpenMainUi -= RequestMainWindowToggle;
        }

        if (_commandRegistered)
        {
            _dalamudServices.CommandManager.RemoveHandler(CommandName);
        }

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeServicesAsync(TakeSessionHost(), _provider).ConfigureAwait(false);
        }
        finally
        {
            _provider = null;
            _lifecycleLock.Release();
        }
    }

    internal static async ValueTask DisposeServicesAsync(IAsyncDisposable? session, ServiceProvider? provider)
    {
        try
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (provider is not null)
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private bool IsGameSessionActive
        => !_disposed && _dalamudServices.ClientState.IsLoggedIn;

    private async Task UpdateSessionHostAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsGameSessionActive)
            {
                await DisposeServicesAsync(TakeSessionHost(), null).ConfigureAwait(false);
                return;
            }

            if (_sessionHost is not null)
            {
                return;
            }

            IntonerSessionHost host = await IntonerSessionHost.CreateAsync(_provider!, cancellationToken).ConfigureAwait(false);
            if (!IsGameSessionActive)
            {
                await host.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _sessionHost = host;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private IntonerSessionHost? TakeSessionHost()
    {
        IntonerSessionHost? host = _sessionHost;
        _sessionHost = null;
        return host;
    }

    private void HandleClientLogin()
        => RunLifecycleTask(() => UpdateSessionHostAsync(_lifecycleCts?.Token ?? CancellationToken.None));

    private void HandleClientLogout(int type, int code)
    {
        _ = type;
        _ = code;
        RunLifecycleTask(() => UpdateSessionHostAsync(CancellationToken.None));
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
        => RequestMainWindowToggle();

    private void HandleOpenConfigUiRequested()
    {
        if (IsGameSessionActive)
        {
            _sessionHost?.RequestConfigWindow();
        }
    }

    private void RequestMainWindowToggle()
    {
        if (IsGameSessionActive)
        {
            _sessionHost?.RequestMainWindowToggle();
        }
    }
}
