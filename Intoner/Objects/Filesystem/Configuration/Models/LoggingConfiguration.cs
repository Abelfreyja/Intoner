using Intoner.Logging;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Filesystem.Configuration;

internal sealed class LoggingConfiguration
{
    public required LogLevel DalamudMinimumLevel { get; set; }

    public static LoggingConfiguration CreateDefault()
        => new()
        {
            DalamudMinimumLevel = IntonerLogLevels.DefaultDalamudMinimumLevel,
        };

    public LoggingConfiguration Copy()
        => new()
        {
            DalamudMinimumLevel = DalamudMinimumLevel,
        };
}
