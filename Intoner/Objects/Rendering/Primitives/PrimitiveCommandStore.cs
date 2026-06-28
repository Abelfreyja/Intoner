using System.Diagnostics;

namespace Intoner.Objects.Rendering.Primitives;

internal sealed class PrimitiveCommandStore(TimeSpan commandLifetime)
{
    private readonly Lock                 _lock      = new();
    private readonly PrimitiveCommandList _committed = new();

    private long _lastCommitTimestamp;
    private PrimitiveDrawState _committedState;

    public bool Commit(PrimitiveCommandList commands, PrimitiveDrawState state)
    {
        lock (_lock)
        {
            _committed.CopyFrom(commands);
            _committedState      = state;
            _lastCommitTimestamp = Stopwatch.GetTimestamp();
            return !_committed.IsEmpty;
        }
    }

    public bool TryGetLiveDrawOverGameUi(out bool drawOverGameUi)
    {
        lock (_lock)
        {
            if (!HasLiveCommandsLocked())
            {
                drawOverGameUi = false;
                return false;
            }

            drawOverGameUi = _committedState.DrawOverGameUi;
            return true;
        }
    }

    public bool TryCopyLiveTo(PrimitiveCommandList target, out PrimitiveDrawState state)
    {
        lock (_lock)
        {
            if (!HasLiveCommandsLocked())
            {
                target.Clear();
                state = default;
                return false;
            }

            target.CopyFrom(_committed);
            state = _committedState;
            return true;
        }
    }

    public bool HasLiveCommands()
    {
        lock (_lock)
        {
            return HasLiveCommandsLocked();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            ClearLocked();
        }
    }

    private bool HasLiveCommandsLocked()
    {
        if (_committed.IsEmpty)
        {
            return false;
        }

        if (Stopwatch.GetElapsedTime(_lastCommitTimestamp) <= commandLifetime)
        {
            return true;
        }

        ClearLocked();
        return false;
    }

    private void ClearLocked()
    {
        _committed.Clear();
        _lastCommitTimestamp = 0;
        _committedState = default;
    }
}

