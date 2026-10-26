using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Intoner.Objects.Runtime;

internal enum ObjectFrameworkStage
{
    Callback,
    LayoutMaintenance,
    SceneRefresh,
    RuntimeUpdates,
    BoundsPublication,
    FurnitureReadiness,
    FurnitureObservation,
    FurnitureVisuals,
    FurnitureResources,
}

internal enum ObjectFrameworkCounter
{
    ActiveObjects,
    FurnitureObjects,
    FurnitureNodes,
    FurnitureNotReady,
    FurnitureActiveColliders,
    FurnitureReplayAttempts,
    FurnitureReplayPending,
    FurnitureResourceChanges,
    FurnitureWorldBoundsQueries,
    FurnitureOrientedBoundsQueries,
    FurnitureClearanceQueries,
}

internal sealed class ObjectFrameworkDiagnostics(ILogger<ObjectFrameworkDiagnostics> logger)
{
    public const int CaptureFrames = 300;

    private readonly long[] _ticks = new long[Enum.GetValues<ObjectFrameworkStage>().Length];
    private readonly long[] _counts = new long[Enum.GetValues<ObjectFrameworkCounter>().Length];
    private int _state;
    private int _frames;
    private long _longestFrame;
    private bool _inFrame;
    private string _lastReport = string.Empty;

    public bool IsCapturing => Volatile.Read(ref _state) != 0;
    public string LastReport => Volatile.Read(ref _lastReport);

    public void RequestCapture()
        => Interlocked.CompareExchange(ref _state, 1, 0);

    public Scope BeginFrame()
    {
        if (Volatile.Read(ref _state) == 0)
        {
            return default;
        }

        if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
        {
            Array.Clear(_ticks);
            Array.Clear(_counts);
            _frames = 0;
            _longestFrame = 0;
        }

        _inFrame = true;
        return Measure(ObjectFrameworkStage.Callback);
    }

    public Scope Measure(ObjectFrameworkStage stage)
        => _inFrame ? new Scope(this, stage) : default;

    public void Count(ObjectFrameworkCounter counter, int count = 1)
    {
        if (_inFrame)
        {
            _counts[(int)counter] += count;
        }
    }

    private void Finish(ObjectFrameworkStage stage, long elapsed)
    {
        _ticks[(int)stage] += elapsed;
        if (stage != ObjectFrameworkStage.Callback)
        {
            return;
        }

        _inFrame = false;
        _longestFrame = Math.Max(_longestFrame, elapsed);
        if (++_frames != CaptureFrames)
        {
            return;
        }

        StringBuilder report = new();
        report.AppendLine($"Object framework capture: {_frames} frames (instrumented; stages are inclusive)");
        double millisecondsPerTick = 1000.0 / Stopwatch.Frequency;
        report.AppendLine(CultureInfo.InvariantCulture, $"Longest callback: {_longestFrame * millisecondsPerTick:F4} ms");
        foreach (ObjectFrameworkStage measured in Enum.GetValues<ObjectFrameworkStage>())
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{measured}: {_ticks[(int)measured] * millisecondsPerTick / _frames:F4} ms/frame");
        }

        foreach (ObjectFrameworkCounter counter in Enum.GetValues<ObjectFrameworkCounter>())
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{counter}: {(double)_counts[(int)counter] / _frames:F3}/frame");
        }

        string result = report.ToString();
        Volatile.Write(ref _lastReport, result);
        Volatile.Write(ref _state, 0);
        logger.LogInformation("{FrameworkCapture}", result);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly ObjectFrameworkDiagnostics? _owner;
        private readonly ObjectFrameworkStage _stage;
        private readonly long _started;

        internal Scope(ObjectFrameworkDiagnostics owner, ObjectFrameworkStage stage)
        {
            _owner = owner;
            _stage = stage;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_owner is not null)
            {
                _owner.Finish(_stage, Stopwatch.GetTimestamp() - _started);
            }
        }
    }
}
