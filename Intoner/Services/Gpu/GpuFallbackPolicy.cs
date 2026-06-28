using Microsoft.Extensions.Logging;

namespace Intoner.Services.Gpu;

internal static class GpuFallbackPolicy
{
    public static bool TryRun(
        Func<bool> action,
        GpuProcessingService gpuProcessingService,
        ILogger logger,
        CancellationToken cancellationToken,
        string fallbackMessage,
        LogLevel logLevel = LogLevel.Debug)
    {
        try
        {
            return action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            gpuProcessingService.NotifyOperationFailure(ex);
            logger.Log(logLevel, ex, fallbackMessage);
            return false;
        }
    }

    public static async Task<bool> TryRunAsync(
        Func<Task<bool>> action,
        GpuProcessingService gpuProcessingService,
        ILogger logger,
        CancellationToken cancellationToken,
        string fallbackMessage,
        LogLevel logLevel = LogLevel.Debug)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            gpuProcessingService.NotifyOperationFailure(ex);
            logger.Log(logLevel, ex, fallbackMessage);
            return false;
        }
    }
}
