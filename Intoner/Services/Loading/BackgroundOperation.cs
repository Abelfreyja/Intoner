using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Services.Loading;

/// <summary> owns repeatable background commands consumed by one UI owner, and awaits pending work on disposal </summary>
internal sealed class BackgroundOperation<TResult>(ILogger logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private Task<TResult>? _task;
    private bool _disposed;

    public bool IsBusy
        => _task is not null;

    public bool TryStart(Func<CancellationToken, Task<TResult>> operation)
    {
        if (_disposed || IsBusy)
        {
            return false;
        }

        CancellationToken cancellationToken = _cancellation.Token;
        _task = Task.Run(() => operation(cancellationToken), cancellationToken);
        return true;
    }

    public bool TryTakeCompleted([NotNullWhen(true)] out Task<TResult>? task)
    {
        task = null;
        if (_task is not { IsCompleted: true } completed)
        {
            return false;
        }

        task = completed;
        _task = null;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_task is { } task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // disposal cancels pending work
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background operation failed during disposal");
        }
        finally
        {
            _task = null;
            _cancellation.Dispose();
        }
    }
}
