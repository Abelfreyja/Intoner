using Intoner.Logging;
using Microsoft.Extensions.Logging;

namespace Intoner.Utils;

internal static class CommittedChangeNotification
{
    public static void Publish(Action? handlers, ILogger logger, string notification)
        => Publish(handlers, false, static (handler, _) => handler(), logger, notification);

    public static void Publish<T>(Action<T>? handlers, T value, ILogger logger, string notification)
        => Publish(handlers, value, static (handler, argument) => handler(argument), logger, notification);

    private static void Publish<THandler, TState>(THandler? handlers, TState state, Action<THandler, TState> invoke,
        ILogger logger, string notification) where THandler : Delegate
    {
        foreach (THandler handler in Delegate.EnumerateInvocationList(handlers))
        {
            try
            {
                invoke(handler, state);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(logger, LogLevel.Error, exception, "committed {Notification} subscriber failed", notification);
            }
        }
    }
}
