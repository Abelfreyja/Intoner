namespace Intoner.Utils;

internal sealed class DisposalState
{
    private int _disposeRequested;

    public bool IsDisposing
        => Volatile.Read(ref _disposeRequested) != 0;

    public bool TryBeginDispose()
        => Interlocked.Exchange(ref _disposeRequested, 1) == 0;
}

