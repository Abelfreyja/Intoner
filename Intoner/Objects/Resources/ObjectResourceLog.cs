using Intoner.Logging;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Resources
{
    /// <summary> writes resource diagnostics </summary>
    internal static class ObjectResourceLog
    {
        [ThreadStatic]
        private static int _writeDepth;

        [ThreadStatic]
        private static int _nativeWriteDepth;

        [ThreadStatic]
        private static List<(Action Write, bool Native)>? _pendingWrites;

        public static WriteScope DeferWrites()
        {
            ++_writeDepth;
            return new WriteScope(native: false);
        }

        public static WriteScope DeferNativeWrites()
        {
            ++_writeDepth;
            ++_nativeWriteDepth;
            return new WriteScope(native: true);
        }

        public static void Write(Action write)
        {
            if (_writeDepth == 0)
            {
                write();
                return;
            }

            (_pendingWrites ??= []).Add((write, _nativeWriteDepth != 0));
        }

        public static void LogHookFailure(ILogger logger, ObjectResourceTracker tracker, Exception exception, string hook, nint address)
        {
            tracker.TryGetHandleScope(address, out ObjectResourceScope scope);
            Write(() => logger.LogError(exception, "object resource {Hook} hook failed; address=0x{Address:X}; collection={CollectionId}; generation={ResourceScopeId}; trackedPath={Path}",
                hook, (ulong)address, scope.ResourceCollectionId, scope.ResourceScopeId, scope.ResolvedPath));
        }

        // these scopes are synchronous and must end on the thread that entered them
        internal readonly ref struct WriteScope(bool native) : IDisposable
        {
            public void Dispose()
                => ExitWriteScope(native);

            private static void ExitWriteScope(bool native)
            {
                if (native)
                {
                    --_nativeWriteDepth;
                }

                if (--_writeDepth != 0)
                {
                    return;
                }

                List<(Action Write, bool Native)>? writes = _pendingWrites;
                _pendingWrites = null;

                if (writes is null)
                {
                    return;
                }

                foreach ((Action write, bool nativeWrite) in writes)
                {
                    if (nativeWrite)
                    {
                        DiagnosticLog.Write(write);
                    }
                    else
                    {
                        write();
                    }
                }
            }
        }
    }
}
