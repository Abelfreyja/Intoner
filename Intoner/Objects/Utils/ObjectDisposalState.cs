using System.Threading;

namespace Intoner.Objects.Utils;

internal sealed class ObjectDisposalState
{
    private int _disposeRequested;

    public bool IsDisposing
        => Volatile.Read(ref _disposeRequested) != 0;

    public bool TryBeginDispose()
        => Interlocked.Exchange(ref _disposeRequested, 1) == 0;
}

