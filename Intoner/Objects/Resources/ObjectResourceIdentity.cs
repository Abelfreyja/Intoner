using System.Runtime.CompilerServices;

namespace Intoner.Objects.Resources;

/// <summary> assigns native resource identities that do not repeat across plugin reloads </summary>
internal static class ObjectResourceIdentity
{
    private static readonly StrongBox<long> Counter = GetCounter();

    public static long Next()
    {
        lock (Counter)
        {
            return Counter.Value = checked(Counter.Value + 1);
        }
    }

    private static StrongBox<long> GetCounter()
    {
        // keep this key stable across reloads, even when the allocator moves
        const string counterKey = "Intoner.Objects.Resources.ObjectResolvedCollectionStore.ResourceScopeIdCounter";
        using Mutex initializationMutex = new(false, $"Local\\{counterKey}.{Environment.ProcessId}");
        initializationMutex.WaitOne();

        try
        {
            // retain only a BCL counter, not plugin types
            if (AppDomain.CurrentDomain.GetData(counterKey) is StrongBox<long> counter)
            {
                return counter;
            }

            counter = new StrongBox<long>();
            AppDomain.CurrentDomain.SetData(counterKey, counter);
            return counter;
        }
        finally
        {
            initializationMutex.ReleaseMutex();
        }
    }
}
