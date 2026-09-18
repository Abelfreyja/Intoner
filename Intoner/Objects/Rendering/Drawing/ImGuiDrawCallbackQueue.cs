using Dalamud.Bindings.ImGui;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Intoner.Objects.Rendering.Drawing;

/// <summary> owns ImGui draw callbacks and releases their jobs after the draw command runs </summary>
internal sealed unsafe class ImGuiDrawCallbackQueue(ILogger logger) : IDisposable
{
    private static readonly ConcurrentDictionary<long, Entry> Jobs = new();
    private static readonly ImDrawCallback ProcessCallback = ProcessQueuedJob;
    private static readonly ImDrawCallback ReleaseCallback = ReleaseQueuedJob;
    private static readonly Action EmptyDrawContent = static () => { };
    private static long _nextJobId;

    private readonly Lock _stateLock = new();
    private bool _disposed;

    /// <summary> queues a callback job with no draw commands between process and release </summary>
    public void QueueCallback<TJob>(
        ImDrawListPtr drawList,
        TJob job,
        Action<TJob> processJob,
        Action<TJob>? releaseJob = null)
        where TJob : class
        => QueueDraw(drawList, job, processJob, EmptyDrawContent, releaseJob);

    /// <summary> queues a callback job and draw commands that consume the processed output </summary>
    public void QueueDraw<TJob>(
        ImDrawListPtr drawList,
        TJob job,
        Action<TJob> processJob,
        Action drawContent,
        Action<TJob>? releaseJob = null)
        where TJob : class
    {
        ArgumentNullException.ThrowIfNull(drawContent);

        long jobId = RegisterJob(job, processJob, releaseJob);
        var releaseQueued = false;
        void* jobPtr = (void*)(nint)jobId;
        try
        {
            drawList.AddCallback(ProcessCallback, jobPtr);
            drawContent();
            drawList.AddCallback(ReleaseCallback, jobPtr);
            releaseQueued = true;
        }
        finally
        {
            if (!releaseQueued)
            {
                ReleaseJob(jobId);
            }
        }
    }

    /// <summary> transfers a job to the queue before its native draw commands are recorded </summary>
    internal long RegisterJob<TJob>(TJob job, Action<TJob> processJob, Action<TJob>? releaseJob = null)
        where TJob : class
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(processJob);

        lock (_stateLock)
        {
            if (!_disposed)
            {
                long jobId = Interlocked.Increment(ref _nextJobId);
                Jobs[jobId] = new Entry(this, jobId, new QueuedJob<TJob>(job, processJob, releaseJob));
                return jobId;
            }
        }

        try
        {
            releaseJob?.Invoke(job);
        }
        catch (Exception ex)
        {
            LogFailure(ex, "release");
        }

        throw new ObjectDisposedException(nameof(ImGuiDrawCallbackQueue));
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        ReleaseOwnedJobs();
    }

    private static void ProcessQueuedJob(ImDrawList* _, ImDrawCmd* cmd)
        => ProcessJob((long)(nint)cmd->UserCallbackData);

    internal static void ProcessJob(long jobId)
    {
        if (!Jobs.TryGetValue(jobId, out Entry? entry))
        {
            return;
        }

        try
        {
            entry.Process();
        }
        catch (Exception ex)
        {
            entry.Owner.LogFailure(ex, "processing");
        }
    }

    private static void ReleaseQueuedJob(ImDrawList* _, ImDrawCmd* cmd)
        => ReleaseJob((long)(nint)cmd->UserCallbackData);

    internal static void ReleaseJob(long jobId)
    {
        if (Jobs.TryGetValue(jobId, out Entry? entry))
        {
            entry.Release(waitForCompletion: false);
        }
    }

    private void ReleaseOwnedJobs()
    {
        foreach (Entry entry in Jobs.Values.Where(entry => ReferenceEquals(entry.Owner, this)))
        {
            entry.Release(waitForCompletion: true);
        }
    }

    private void LogFailure(Exception exception, string operation)
    {
        try
        {
            logger.LogError(exception, "ImGui draw callback {Operation} failed", operation);
        }
        catch
        {
            // logging must not release through a native callback
        }
    }

    private sealed class Entry(ImGuiDrawCallbackQueue owner, long jobId, IQueuedJob job)
    {
        private readonly object _stateLock = new();
        private bool _processing;
        private bool _releaseRequested;
        private bool _releaseStarted;
        private bool _released;

        public ImGuiDrawCallbackQueue Owner { get; } = owner;

        public void Process()
        {
            lock (_stateLock)
            {
                if (_releaseRequested || _processing)
                {
                    return;
                }

                _processing = true;
            }

            try
            {
                job.Process();
            }
            finally
            {
                bool release;
                lock (_stateLock)
                {
                    _processing = false;
                    release = TryStartReleaseLocked();
                }

                if (release)
                {
                    ReleasePayload();
                }
            }
        }

        public void Release(bool waitForCompletion)
        {
            bool release;
            lock (_stateLock)
            {
                _releaseRequested = true;
                release = TryStartReleaseLocked();
                while (waitForCompletion && !release && !_released)
                {
                    Monitor.Wait(_stateLock);
                }
            }

            if (release)
            {
                ReleasePayload();
            }
        }

        private void ReleasePayload()
        {
            try
            {
                job.Release();
            }
            catch (Exception ex)
            {
                Owner.LogFailure(ex, "release");
            }
            finally
            {
                lock (_stateLock)
                {
                    _released = true;
                    Jobs.TryRemove(jobId, out _);
                    Monitor.PulseAll(_stateLock);
                }
            }
        }

        private bool TryStartReleaseLocked()
        {
            if (!_releaseRequested || _processing || _releaseStarted)
            {
                return false;
            }

            _releaseStarted = true;
            return true;
        }
    }

    private interface IQueuedJob
    {
        void Process();

        void Release();
    }

    private sealed class QueuedJob<TJob>(
        TJob job,
        Action<TJob> processJob,
        Action<TJob>? releaseJob) : IQueuedJob
        where TJob : class
    {
        public void Process()
            => processJob(job);

        public void Release()
            => releaseJob?.Invoke(job);
    }
}

/// <summary> typed ImGui draw callback queue with shared process and release handlers </summary>
internal sealed class ImGuiDrawCallbackQueue<TJob> : IDisposable
    where TJob : class
{
    private readonly ImGuiDrawCallbackQueue _queue;
    private readonly Action<TJob>           _processJob;
    private readonly Action<TJob>?          _releaseJob;

    public ImGuiDrawCallbackQueue(ILogger logger, Action<TJob> processJob, Action<TJob>? releaseJob = null)
    {
        ArgumentNullException.ThrowIfNull(processJob);

        _queue = new ImGuiDrawCallbackQueue(logger);
        _processJob = processJob;
        _releaseJob = releaseJob;
    }

    /// <summary> queues a callback job with no draw commands between process and release </summary>
    public void QueueCallback(ImDrawListPtr drawList, TJob job)
        => _queue.QueueCallback(drawList, job, _processJob, _releaseJob);

    /// <summary> queues a callback job and draw commands that consume the processed output </summary>
    public void QueueDraw(ImDrawListPtr drawList, TJob job, Action drawContent)
        => _queue.QueueDraw(drawList, job, _processJob, drawContent, _releaseJob);

    public void Dispose()
        => _queue.Dispose();
}
