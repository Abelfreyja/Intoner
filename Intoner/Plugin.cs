using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Intoner.Services;

namespace Intoner;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private readonly IntonerPluginHost _host;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        ICondition condition,
        IDataManager dataManager,
        IFramework framework,
        IGameInteropProvider gameInteropProvider,
        IObjectTable objectTable,
        IPlayerState playerState,
        IPluginLog log,
        ISigScanner sigScanner,
        ITextureProvider textureProvider)
    {
        _host = new IntonerPluginHost(new IntonerDalamudServices(
            pluginInterface,
            commandManager,
            clientState,
            condition,
            dataManager,
            framework,
            gameInteropProvider,
            objectTable,
            playerState,
            log,
            sigScanner,
            textureProvider));
    }

    public Task LoadAsync(CancellationToken cancellationToken)
        => _host.LoadAsync(cancellationToken);

    public ValueTask DisposeAsync()
        => _host.DisposeAsync();
}
