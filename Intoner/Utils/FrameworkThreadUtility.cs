using Dalamud.Plugin.Services;

namespace Intoner.Utils;

internal static class FrameworkThreadUtility
{
    public static T Run<T>(IFramework framework, Func<T> func)
    {
        if (FrameworkUnloadUtility.IsGameFrameworkDestroying())
        {
            throw new InvalidOperationException("cannot run framework work while the game framework is destroying");
        }

        if (framework.IsInFrameworkUpdateThread)
        {
            return func();
        }

        return framework.RunOnFrameworkThread(func).GetAwaiter().GetResult();
    }

    public static void Run(IFramework framework, Action action)
    {
        if (FrameworkUnloadUtility.IsGameFrameworkDestroying())
        {
            throw new InvalidOperationException("cannot run framework work while the game framework is destroying");
        }

        if (framework.IsInFrameworkUpdateThread)
        {
            action();
            return;
        }

        framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();
    }

    public static bool TryRun(IFramework framework, Action action)
    {
        if (FrameworkUnloadUtility.IsGameFrameworkDestroying())
        {
            return false;
        }

        if (framework.IsInFrameworkUpdateThread)
        {
            action();
            return true;
        }

        framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();
        return true;
    }

    public static Task RunDuringShutdownAsync(IFramework framework, Action action)
    {
        if (framework.IsInFrameworkUpdateThread || FrameworkUnloadUtility.IsGameFrameworkDestroying())
        {
            action();
            return Task.CompletedTask;
        }

        return framework.RunOnFrameworkThread(action);
    }

    public static void RunDuringShutdown(IFramework framework, Action action)
    {
        if (framework.IsInFrameworkUpdateThread || FrameworkUnloadUtility.IsGameFrameworkDestroying())
        {
            action();
            return;
        }

        framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();
    }
}
