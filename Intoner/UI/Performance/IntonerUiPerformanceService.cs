using Dalamud.Interface.Windowing;
using Intoner.Logging;
using Microsoft.Extensions.Logging;

namespace Intoner.UI.Performance;

internal sealed class IntonerUiPerformanceService(ILogger<IntonerUiPerformanceService> logger)
{
    private static readonly TimeSpan SlowDrawThreshold = TimeSpan.FromMilliseconds(50);

    public Scope Measure(Window window)
        => Measure(window.WindowName ?? window.GetType().Name);

    public Scope Measure(string name)
    {
        return logger.IsEnabled(LogLevel.Debug)
            ? new Scope(logger, name, ValueStopwatch.StartNew())
            : default;
    }

    public readonly struct Scope : IDisposable
    {
        private readonly ILogger? _logger;
        private readonly string? _name;
        private readonly ValueStopwatch _stopwatch;

        public Scope(ILogger logger, string name, ValueStopwatch stopwatch)
        {
            _logger = logger;
            _name = name;
            _stopwatch = stopwatch;
        }

        public void Dispose()
        {
            if (_logger is null || _name is null)
            {
                return;
            }

            TimeSpan elapsed = _stopwatch.Elapsed;
            if (elapsed >= SlowDrawThreshold)
            {
                _logger.LogDebug("Slow UI draw {Name}: {ElapsedMs:0.###}ms", _name, elapsed.TotalMilliseconds);
            }
        }
    }
}
