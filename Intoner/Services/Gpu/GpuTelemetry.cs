using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Intoner.Services.Gpu;

internal enum GpuTelemetryStage
{
    Upload,
    Dispatch,
    Readback,
    Total,
}

internal struct GpuTelemetryData
{
    public long UploadTicks;
    public long DispatchTicks;
    public long ReadbackTicks;
    public long TotalTicks;
}

internal static class GpuTelemetry
{
    public static long BeginStage()
        => Stopwatch.GetTimestamp();

    public static void EndStage(ref GpuTelemetryData telemetry, GpuTelemetryStage stage, long startedAtTicks)
        => AddTicks(ref telemetry, stage, Stopwatch.GetTimestamp() - startedAtTicks);

    public static void AddTicks(ref GpuTelemetryData telemetry, GpuTelemetryStage stage, long ticks)
    {
        switch (stage)
        {
            case GpuTelemetryStage.Upload:
                telemetry.UploadTicks += ticks;
                break;
            case GpuTelemetryStage.Dispatch:
                telemetry.DispatchTicks += ticks;
                break;
            case GpuTelemetryStage.Readback:
                telemetry.ReadbackTicks += ticks;
                break;
            case GpuTelemetryStage.Total:
                telemetry.TotalTicks += ticks;
                break;
        }
    }

    public static void Log(
        ILogger logger,
        LogLevel level,
        string pipeline,
        string operation,
        in GpuTelemetryData telemetry,
        string? note = null)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        var totalTicks = telemetry.TotalTicks;
        if (totalTicks <= 0)
        {
            totalTicks = telemetry.UploadTicks + telemetry.DispatchTicks + telemetry.ReadbackTicks;
        }

        logger.Log(
            level,
            "GPU telemetry [{Pipeline}/{Operation}]: upload={UploadMs:F2}ms dispatch={DispatchMs:F2}ms readback={ReadbackMs:F2}ms total={TotalMs:F2}ms note={Note}.",
            pipeline,
            operation,
            ConvertTicksToMilliseconds(telemetry.UploadTicks),
            ConvertTicksToMilliseconds(telemetry.DispatchTicks),
            ConvertTicksToMilliseconds(telemetry.ReadbackTicks),
            ConvertTicksToMilliseconds(totalTicks),
            note ?? string.Empty);
    }

    private static double ConvertTicksToMilliseconds(long ticks)
        => ticks <= 0 ? 0d : ticks * 1000d / Stopwatch.Frequency;
}
