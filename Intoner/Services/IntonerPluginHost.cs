using Dalamud.Game.Command;
using Dalamud.Interface;
using Intoner.Objects;

namespace Intoner.Services;

internal sealed class IntonerPluginHost : IAsyncDisposable
{
    private const string CommandName = "/intoner";

    private readonly IntonerDalamudServices _dalamudServices;

    private ObjectServiceHost? _objectHost;
    private bool _commandRegistered;
    private bool _uiEventsRegistered;
    private bool _disposed;

    public IntonerPluginHost(IntonerDalamudServices dalamudServices)
    {
        _dalamudServices = dalamudServices;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_objectHost is not null)
        {
            return;
        }

        try
        {
            _objectHost = await ObjectServiceHost.CreateAsync(_dalamudServices, cancellationToken).ConfigureAwait(false);

            _dalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Intoner.",
            });
            _commandRegistered = true;

            IUiBuilder uiBuilder = _dalamudServices.PluginInterface.UiBuilder;
            uiBuilder.OpenConfigUi += HandleOpenConfigUiRequested;
            uiBuilder.OpenMainUi += HandleOpenMainUiRequested;
            _uiEventsRegistered = true;
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

        if (_objectHost is not null)
        {
            await _objectHost.DisposeAsync().ConfigureAwait(false);
            _objectHost = null;
        }
    }

    private void OnCommand(string command, string args)
        => RequestMainWindowToggle();

    private void HandleOpenMainUiRequested()
        => RequestMainWindowToggle();

    private void HandleOpenConfigUiRequested()
        => _objectHost?.RequestConfigWindow();

    private void RequestMainWindowToggle()
        => _objectHost?.RequestMainWindowToggle();
}
