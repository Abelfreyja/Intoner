using Microsoft.Extensions.Logging;

namespace Intoner.Logging;

internal sealed record IntonerLogOptions
{
    public const int DefaultCategoryWidth = 18;
    public const int DefaultTraceFileRetentionCount = 9;
    public const long DefaultTraceFileSizeLimitBytes = 50L * 1024L * 1024L;

    public required string TraceDirectory { get; init; }
    public LogLevel TraceMinimumLevel { get; init; } = LogLevel.Trace;
    public int CategoryWidth { get; init; } = DefaultCategoryWidth;
    public int TraceFileRetentionCount { get; init; } = DefaultTraceFileRetentionCount;
    public long TraceFileSizeLimitBytes { get; init; } = DefaultTraceFileSizeLimitBytes;
}
