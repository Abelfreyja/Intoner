using System.Runtime.InteropServices;

namespace Intoner.Scene.Rendering;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneRenderCommand(
    nint Address,
    uint FrameNumber,
    SceneRenderPhase Phase,
    SceneRenderTarget Target,
    SceneProjection? Projection = null)
{
    public bool IsFromActiveFrame(uint frameNumber)
        => unchecked(frameNumber - FrameNumber) <= 1;
}

/// <summary> tracks the exact native target commands assigned to scene phases </summary>
internal sealed class SceneRenderCommandTracker
{
    internal const int MaximumCommandCount = 64;
    private const int BucketCount = sizeof(long) * 8;

    private readonly Lock _stateLock = new();
    private readonly Dictionary<nint, SceneRenderCommand> _commands = new(8);

    private long _bucketMask;
    private uint _newestFrame;

    /// <summary> records or updates a command and reports whether stale entries had to be discarded </summary>
    public bool Track(in SceneRenderCommand command)
    {
        lock (_stateLock)
        {
            if (_commands.Count == 0 || unchecked((int)(command.FrameNumber - _newestFrame)) > 0)
            {
                _newestFrame = command.FrameNumber;
                RemoveExpiredCommands();
            }

            if (!command.IsFromActiveFrame(_newestFrame))
            {
                return false;
            }

            bool hasPrevious = _commands.TryGetValue(command.Address, out SceneRenderCommand previous);

            if (hasPrevious && unchecked((int)(command.FrameNumber - previous.FrameNumber)) < 0)
            {
                return false;
            }

            bool reset = _commands.Count >= MaximumCommandCount && !hasPrevious;

            if (reset)
            {
                _commands.Clear();
                _bucketMask = 0;
            }

            _commands[command.Address] = command;
            Volatile.Write(ref _bucketMask, _bucketMask | GetBucketBit(command.Address));
            return reset;
        }
    }

    /// <summary> removes a command and returns its retained scene state </summary>
    public bool TryTake(nint address, out SceneRenderCommand command)
    {
        if ((Volatile.Read(ref _bucketMask) & GetBucketBit(address)) == 0)
        {
            command = default;
            return false;
        }

        lock (_stateLock)
        {
            if (!_commands.Remove(address, out command))
            {
                return false;
            }

            RebuildBucketMask();
            return true;
        }
    }

    public void Clear()
    {
        lock (_stateLock)
        {
            _commands.Clear();
            Volatile.Write(ref _bucketMask, 0);
        }
    }

    private void RemoveExpiredCommands()
    {
        long mask = 0;
        foreach (KeyValuePair<nint, SceneRenderCommand> entry in _commands)
        {
            if (entry.Value.IsFromActiveFrame(_newestFrame))
            {
                mask |= GetBucketBit(entry.Key);
            }
            else
            {
                _commands.Remove(entry.Key);
            }
        }

        Volatile.Write(ref _bucketMask, mask);
    }

    private void RebuildBucketMask()
    {
        long mask = 0;
        foreach (nint address in _commands.Keys)
        {
            mask |= GetBucketBit(address);
        }

        Volatile.Write(ref _bucketMask, mask);
    }

    private static long GetBucketBit(nint command)
    {
        ulong address = (ulong)command;
        int bucket = (int)(((address >> 4) ^ (address >> 13)) & (BucketCount - 1));
        return 1L << bucket;
    }
}
