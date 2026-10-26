using Dalamud.Game.Command;
using Dalamud.Interface;
using Intoner.Services.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Intoner.Services;

internal sealed class IntonerPluginHost : IAsyncDisposable
{
    private const string CommandName = "/intoner";

    private readonly IntonerDalamudServices _dalamudServices;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private ServiceProvider? _provider;
    private IntonerSessionHost? _sessionHost;
    private CancellationTokenSource? _lifecycleCts;
    private CancellationToken _lifecycleToken;
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

            await Task.Run(() => IntonerSignatures.Verify(_dalamudServices.SigScanner,
                _provider.GetRequiredService<ILogger<IntonerPluginHost>>()), cancellationToken).ConfigureAwait(false);
            await _provider.GetRequiredService<IntonerPluginRuntime>().StartAsync(cancellationToken).ConfigureAwait(false);

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
            _lifecycleToken = _lifecycleCts.Token;
            _dalamudServices.ClientState.Login += HandleClientLogin;
            _dalamudServices.ClientState.Logout += HandleClientLogout;
            _clientEventsRegistered = true;
            _loaded = true;

            using CancellationTokenSource loadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleToken, cancellationToken);
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

    private async Task UpdateSessionHostAsync(CancellationToken cancellationToken, bool resetSession = false)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (resetSession || !IsGameSessionActive)
            {
                await DisposeServicesAsync(TakeSessionHost(), null).ConfigureAwait(false);
            }

            if (!IsGameSessionActive || _sessionHost is not null)
            {
                return;
            }

            IntonerSessionHost host = await IntonerSessionHost.CreateAsync(_provider!).ConfigureAwait(false);
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
        => RunLifecycleTask(() => UpdateSessionHostAsync(_lifecycleToken));

    private void HandleClientLogout(int type, int code)
    {
        _ = type;
        _ = code;
        RunLifecycleTask(() => UpdateSessionHostAsync(CancellationToken.None, resetSession: true));
    }

    internal void RunLifecycleTask(Func<Task> taskFactory)
    {
        if (_disposed)
        {
            return;
        }

        _ = Task.Run(() => RunLifecycleTaskAsync(taskFactory));
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
