using System.Diagnostics;

namespace Intoner.Logging;

internal readonly struct ValueStopwatch
{
    private readonly long _startedAt;

    private ValueStopwatch(long startedAt)
        => _startedAt = startedAt;

    public static ValueStopwatch StartNew()
        => new(Stopwatch.GetTimestamp());

    public TimeSpan Elapsed
        => TimeSpan.FromTicks((long)(ElapsedTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

    public long ElapsedMilliseconds
        => ElapsedTicks * 1000 / Stopwatch.Frequency;

    public long ElapsedTicks
        => _startedAt == 0 ? 0 : Stopwatch.GetTimestamp() - _startedAt;
}
