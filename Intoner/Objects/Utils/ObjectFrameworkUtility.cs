using Dalamud.Plugin.Services;
using Intoner.Utils;

namespace Intoner.Objects.Utils;

internal static class ObjectFrameworkUtility
{
    public static T RunOnFrameworkThread<T>(IFramework framework, Func<T> func)
        => FrameworkThreadUtility.Run(framework, func);

    public static void RunOnFrameworkThread(IFramework framework, Action action)
        => FrameworkThreadUtility.Run(framework, action);

    public static bool TryRunOnFrameworkThread(IFramework framework, Action action)
        => FrameworkThreadUtility.TryRun(framework, action);

    public static bool IsGameFrameworkDestroying()
        => FrameworkUnloadUtility.IsGameFrameworkDestroying();
}

