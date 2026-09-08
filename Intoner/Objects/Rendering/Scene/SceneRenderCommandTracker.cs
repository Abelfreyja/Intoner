namespace Intoner.Scene.Rendering;

/// <summary> tracks the exact native target commands assigned to scene phases </summary>
internal sealed class SceneRenderCommandTracker
{
    internal const int MaximumCommandCount = 64;
    private const int BucketCount = sizeof(long) * 8;

    private readonly Lock _stateLock = new();
    private readonly Dictionary<nint, SceneRenderPhase> _commands = new(8);

    private long _bucketMask;

    /// <summary> records or updates a command and reports whether stale entries had to be discarded </summary>
    public bool Track(nint command, SceneRenderPhase phase)
    {
        lock (_stateLock)
        {
            bool reset = _commands.Count >= MaximumCommandCount && !_commands.ContainsKey(command);
            if (reset)
            {
                ClearLocked();
            }

            if (_commands.TryAdd(command, phase))
            {
                int bucket = GetBucket(command);
                Volatile.Write(
                    ref _bucketMask,
                    Volatile.Read(ref _bucketMask) | GetBit(bucket));
            }
            else
            {
                _commands[command] = phase;
            }

            return reset;
        }
    }

    /// <summary> removes a command when its native storage is reused before execution </summary>
    public void Remove(nint command)
    {
        if (!MayContain(command))
        {
            return;
        }

        lock (_stateLock)
        {
            _ = RemoveLocked(command, out _);
        }
    }

    /// <summary> removes a command and returns its assigned phase </summary>
    public bool TryTake(nint command, out SceneRenderPhase phase)
    {
        if (!MayContain(command))
        {
            phase = default;
            return false;
        }

        lock (_stateLock)
        {
            return RemoveLocked(command, out phase);
        }
    }

    public void Clear()
    {
        lock (_stateLock)
        {
            ClearLocked();
        }
    }

    private bool RemoveLocked(nint command, out SceneRenderPhase phase)
    {
        if (!_commands.Remove(command, out phase))
        {
            return false;
        }

        RebuildBucketMaskLocked();
        return true;
    }

    private void ClearLocked()
    {
        _commands.Clear();
        Volatile.Write(ref _bucketMask, 0);
    }

    private void RebuildBucketMaskLocked()
    {
        long mask = 0;
        foreach (nint command in _commands.Keys)
        {
            mask |= GetBit(GetBucket(command));
        }

        Volatile.Write(ref _bucketMask, mask);
    }

    private bool MayContain(nint command)
    {
        int bucket = GetBucket(command);
        return (Volatile.Read(ref _bucketMask) & GetBit(bucket)) != 0;
    }

    private static int GetBucket(nint command)
    {
        ulong address = (ulong)command;
        return (int)(((address >> 4) ^ (address >> 13)) & (BucketCount - 1));
    }

    private static long GetBit(int bucket)
        => 1L << bucket;
}
