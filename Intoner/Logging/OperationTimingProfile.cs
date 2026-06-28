using System.Collections.Concurrent;
using System.Diagnostics;

namespace Intoner.Logging;

internal sealed class OperationTimingProfile
{
    private static readonly AsyncLocal<OperationTimingProfile?> ActiveProfile = new();

    private readonly ConcurrentDictionary<string, TimingBucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly long _startedAt = Stopwatch.GetTimestamp();

    public OperationTimingProfile(string name, Guid operationId)
    {
        Name = name;
        OperationId = operationId;
    }

    public string Name { get; }
    public Guid OperationId { get; }
    public static OperationTimingProfile? Current => ActiveProfile.Value;
    public double ElapsedMs => TicksToMs(Stopwatch.GetTimestamp() - _startedAt);

    public static IDisposable Activate(OperationTimingProfile? profile)
        => new ActivationScope(profile);

    public static ValueStopwatch StartStopwatch()
        => ValueStopwatch.StartNew();

    public static TimingScope MeasureCurrent(string bucket)
        => Current?.Measure(bucket) ?? default;

    public static void CountCurrent(string counter)
        => Current?.Increment(counter);

    public TimingScope Measure(string bucket)
        => new(this, bucket);

    public void Increment(string counter)
        => _counters.AddOrUpdate(counter, 1, static (_, value) => value + 1);

    public void RecordElapsed(string bucket, long startedAt)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - startedAt;
        if (elapsedTicks > 0)
        {
            RecordTicks(bucket, elapsedTicks);
        }
    }

    public string FormatSummary()
    {
        var timings = _buckets
            .Select(static pair => pair.Value.Snapshot(pair.Key))
            .Where(static bucket => bucket.Count > 0)
            .OrderByDescending(static bucket => bucket.TotalTicks)
            .Select(static bucket => $"{bucket.Name}={TicksToMs(bucket.TotalTicks):0.###}ms/{bucket.Count}x/max{TicksToMs(bucket.MaxTicks):0.###}ms")
            .ToArray();
        var counters = _counters
            .Where(static pair => pair.Value > 0)
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key}={pair.Value}x")
            .ToArray();

        return timings.Length == 0 && counters.Length == 0
            ? "no samples"
            : string.Join(' ', timings.Concat(counters));
    }

    private void RecordTicks(string bucket, long elapsedTicks)
    {
        var counter = _buckets.GetOrAdd(bucket, static _ => new TimingBucket());
        counter.Add(elapsedTicks);
    }

    private static double TicksToMs(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;

    public readonly struct TimingScope : IDisposable
    {
        private readonly OperationTimingProfile? _profile;
        private readonly string? _bucket;
        private readonly long _startedAt;

        public TimingScope(OperationTimingProfile profile, string bucket)
        {
            _profile = profile;
            _bucket = bucket;
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_profile is not null && _bucket is not null)
            {
                _profile.RecordElapsed(_bucket, _startedAt);
            }
        }
    }

    private sealed class ActivationScope : IDisposable
    {
        private readonly OperationTimingProfile? _previous;

        public ActivationScope(OperationTimingProfile? profile)
        {
            _previous = ActiveProfile.Value;
            ActiveProfile.Value = profile;
        }

        public void Dispose()
            => ActiveProfile.Value = _previous;
    }

    private sealed class TimingBucket
    {
        private long _count;
        private long _totalTicks;
        private long _maxTicks;

        public void Add(long elapsedTicks)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalTicks, elapsedTicks);

            var current = Interlocked.Read(ref _maxTicks);
            while (elapsedTicks > current)
            {
                var previous = Interlocked.CompareExchange(ref _maxTicks, elapsedTicks, current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }

        public TimingBucketSnapshot Snapshot(string name)
            => new(
                name,
                Interlocked.Read(ref _count),
                Interlocked.Read(ref _totalTicks),
                Interlocked.Read(ref _maxTicks));
    }

    private readonly record struct TimingBucketSnapshot(
        string Name,
        long Count,
        long TotalTicks,
        long MaxTicks);
}
