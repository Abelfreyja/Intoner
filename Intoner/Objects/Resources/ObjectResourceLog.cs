using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Resources;

/// <summary> writes resource diagnostics </summary>
internal static class ObjectResourceLog
{
    [ThreadStatic]
    private static int _writeDepth;

    [ThreadStatic]
    private static List<Action>? _pendingWrites;

    public static WriteScope DeferWrites()
    {
        ++_writeDepth;
        return new WriteScope();
    }

    public static void Write(Action write)
    {
        if (_writeDepth == 0)
        {
            write();
            return;
        }

        (_pendingWrites ??= []).Add(write);
    }

    public static void LogHookFailure(ILogger logger, ObjectResourceTracker tracker, Exception exception, string hook, nint address)
    {
        tracker.TryGetHandleScope(address, out ObjectResourceScope scope);
        logger.LogError(exception, "object resource {Hook} hook failed; address=0x{Address:X}; collection={CollectionId}; generation={ResourceScopeId}; trackedPath={Path}",
            hook, (ulong)address, scope.ResourceCollectionId, scope.ResourceScopeId, scope.ResolvedPath);
    }

    // these scopes are synchronous and must end on the thread that entered them
    internal readonly ref struct WriteScope : IDisposable
    {
        public void Dispose()
            => EndWrites();

        private static void EndWrites()
        {
            if (--_writeDepth != 0)
            {
                return;
            }

            List<Action>? writes = _pendingWrites;
            _pendingWrites = null;
            if (writes is null)
            {
                return;
            }

            foreach (Action write in writes)
            {
                write();
            }
        }
    }
}
