using System.Collections.Concurrent;

namespace Intoner.Objects.Preview;

internal sealed class PreviewWorkScheduler : IDisposable
{
    private const int WorkerShutdownWaitMilliseconds = 2000;

    private readonly ConcurrentQueue<string> _loadQueue = new();
    private readonly ConcurrentQueue<string> _thumbnailRenderQueue = new();
    private readonly SemaphoreSlim           _loadSignal = new(0);
    private readonly SemaphoreSlim           _renderSignal = new(0);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action<string>          _processLoad;
    private readonly Action<string>          _processRender;
    private readonly Task[]                  _loadWorkers;
    private readonly Task[]                  _renderWorkers;

    private int _disposed;
    private int _workerResourcesDisposed;

    public PreviewWorkScheduler(
        int loadWorkerCount,
        int renderWorkerCount,
        Action<string> processLoad,
        Action<string> processRender)
    {
        _processLoad   = processLoad;
        _processRender = processRender;

        _loadWorkers   = Enumerable.Range(0, loadWorkerCount)
            .Select(_ => Task.Factory.StartNew(
                RunLoadWorker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        _renderWorkers = Enumerable.Range(0, renderWorkerCount)
            .Select(_ => Task.Factory.StartNew(
                RunRenderWorker,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
    }

    public int LoadQueueDepth
        => _loadQueue.Count;

    public int RenderQueueDepth
        => _thumbnailRenderQueue.Count;

    public bool IsCancellationRequested
        => _cancellation.IsCancellationRequested;

    public CancellationToken CancellationToken
        => _cancellation.Token;

    public void EnqueueLoad(string assetKey)
    {
        if (IsDisposed)
        {
            return;
        }

        _loadQueue.Enqueue(assetKey);
        _loadSignal.Release();
    }

    public void EnqueueRender(string assetKey)
    {
        if (IsDisposed)
        {
            return;
        }

        _thumbnailRenderQueue.Enqueue(assetKey);
        _renderSignal.Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        ReleaseWorkers(_loadSignal, _loadWorkers.Length);
        ReleaseWorkers(_renderSignal, _renderWorkers.Length);

        var workers = _loadWorkers.Concat(_renderWorkers).ToArray();
        WaitForWorkers(workers, WorkerShutdownWaitMilliseconds);
        if (workers.All(static worker => worker.IsCompleted))
        {
            DisposeWorkerResources();
            return;
        }

        _ = Task.Run(() => DisposeWorkerResourcesAfterExit(workers));
    }

    private bool IsDisposed
        => Volatile.Read(ref _disposed) != 0;

    private static void ReleaseWorkers(SemaphoreSlim signal, int workerCount)
    {
        for (var i = 0; i < workerCount; i++)
        {
            signal.Release();
        }
    }

    private static void WaitForWorkers(Task[] workers, int millisecondsTimeout)
    {
        try
        {
            Task.WaitAll(workers, millisecondsTimeout);
        }
        catch (AggregateException)
        {
            // ignored during shutdown
        }
        catch (OperationCanceledException)
        {
            // ignored during shutdown
        }
    }

    private void RunLoadWorker()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                _loadSignal.Wait(_cancellation.Token);
                while (_loadQueue.TryDequeue(out string? assetKey))
                {
                    if (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _processLoad(assetKey);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored during shutdown
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            // ignored during shutdown
        }
    }

    private void RunRenderWorker()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                _renderSignal.Wait(_cancellation.Token);
                while (_thumbnailRenderQueue.TryDequeue(out string? assetKey))
                {
                    if (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    _processRender(assetKey);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // ignored during shutdown
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            // ignored during shutdown
        }
    }

    private void DisposeWorkerResourcesAfterExit(Task[] workers)
    {
        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException)
        {
            // ignored during shutdown
        }
        catch (OperationCanceledException)
        {
            // ignored during shutdown
        }
        finally
        {
            DisposeWorkerResources();
        }
    }

    private void DisposeWorkerResources()
    {
        if (Interlocked.Exchange(ref _workerResourcesDisposed, 1) != 0)
        {
            return;
        }

        _loadSignal.Dispose();
        _renderSignal.Dispose();
        _cancellation.Dispose();
    }
}
