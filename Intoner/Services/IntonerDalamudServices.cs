using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Intoner.Services;

internal sealed record IntonerDalamudServices(
    IDalamudPluginInterface PluginInterface,
    ICommandManager CommandManager,
    IClientState ClientState,
    ICondition Condition,
    IDataManager DataManager,
    IFramework Framework,
    IGameInteropProvider GameInteropProvider,
    IObjectTable ObjectTable,
    IPlayerState PlayerState,
    IPluginLog Log,
    ISigScanner SigScanner,
    ITextureProvider TextureProvider);
