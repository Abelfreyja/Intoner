using Dalamud.Plugin.Services;
using ClientFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace Intoner.Utils;

internal static class FrameworkUnloadUtility
{
    public static bool IsPluginUnloadActive(IFramework framework)
        => framework.IsFrameworkUnloading;

    public static unsafe bool IsGameFrameworkDestroying()
    {
        var framework = ClientFramework.Instance();
        return framework != null && (framework->IsDestroying || framework->IsFreed);
    }

    public static bool IsUnloadActive(IFramework framework)
        => IsPluginUnloadActive(framework) || IsGameFrameworkDestroying();
}
