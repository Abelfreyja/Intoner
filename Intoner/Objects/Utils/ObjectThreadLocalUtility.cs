using System.Threading;

namespace Intoner.Objects.Utils;

internal static class ObjectThreadLocalUtility
{
    public static bool TryRead<T>(ThreadLocal<T> storage, T fallback, out T value)
    {
        value = fallback;
        try
        {
            var current = storage.Value;
            value = current is null ? fallback : current;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public static bool TryWrite<T>(ThreadLocal<T> storage, T value)
    {
        try
        {
            storage.Value = value;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}

