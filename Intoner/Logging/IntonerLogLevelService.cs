using Microsoft.Extensions.Logging;

namespace Intoner.Logging;

/// <summary> stores runtime logging thresholds shared by the logging provider and settings UI </summary>
internal interface IIntonerLogLevelService
{
    /// <summary> minimum log level written into Dalamud's logs </summary>
    LogLevel DalamudMinimumLevel { get; }

    /// <summary> updates the minimum log level written into Dalamud's logs </summary>
    /// <param name="level"> new minimum level </param>
    void SetDalamudMinimumLevel(LogLevel level);
}

internal static class IntonerLogLevels
{
    public const LogLevel DefaultDalamudMinimumLevel = LogLevel.Information;

    public static LogLevel NormalizeDalamudMinimumLevel(LogLevel level)
        => level is LogLevel.None || !Enum.IsDefined(level)
            ? DefaultDalamudMinimumLevel
            : level;
}

internal sealed class IntonerLogLevelService : IIntonerLogLevelService
{
    private int _dalamudMinimumLevel = (int)IntonerLogLevels.DefaultDalamudMinimumLevel;

    public LogLevel DalamudMinimumLevel
        => (LogLevel)Volatile.Read(ref _dalamudMinimumLevel);

    public void SetDalamudMinimumLevel(LogLevel level)
        => Volatile.Write(ref _dalamudMinimumLevel, (int)IntonerLogLevels.NormalizeDalamudMinimumLevel(level));
}
