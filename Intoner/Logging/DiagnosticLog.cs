using Microsoft.Extensions.Logging;

namespace Intoner.Logging
{
    internal static class DiagnosticLog
    {
        public static IDisposable? BeginScope(ILogger logger, string message, params object[] values)
        {
            IDisposable? scope = null;
            Write(() => scope = logger.BeginScope(message, values));
            return scope is null ? null : new DiagnosticScope(scope);
        }

        private sealed class DiagnosticScope(IDisposable scope) : IDisposable
        {
            public void Dispose()
                => Write(scope.Dispose);
        }

        public static void Write(ILogger logger, LogLevel level, Exception? exception, string message, params object?[] values)
        {
            try
            {
                logger.Log(level, exception, message, values);
            }
            catch (Exception)
            {
                // keep logging failures from interrupting recovery
            }
        }

        public static void Write(Action write)
        {
            try
            {
                write();
            }
            catch (Exception)
            {
                // logging failures must not interrupt callbacks or cleanup
            }
        }
    }
}
