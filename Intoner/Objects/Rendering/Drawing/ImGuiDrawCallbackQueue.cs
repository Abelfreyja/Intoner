using Dalamud.Bindings.ImGui;
using System.Collections.Concurrent;

namespace Intoner.Objects.Rendering.Drawing;

/// <summary> owns ImGui draw callbacks and releases their jobs after the draw command runs </summary>
internal sealed unsafe class ImGuiDrawCallbackQueue : IDisposable
{
    private static readonly ConcurrentDictionary<long, Entry> Jobs = new();
    private static readonly ImDrawCallback ProcessCallback = ProcessQueuedJob;
    private static readonly ImDrawCallback ReleaseCallback = ReleaseQueuedJob;
    private static readonly Action EmptyDrawContent = static () => { };
    private static long _nextJobId;

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
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(processJob);
        ArgumentNullException.ThrowIfNull(drawContent);

        if (_disposed)
        {
            releaseJob?.Invoke(job);
            throw new ObjectDisposedException(nameof(ImGuiDrawCallbackQueue));
        }

        long jobId = Interlocked.Increment(ref _nextJobId);
        if (!Jobs.TryAdd(jobId, new Entry(this, new QueuedJob<TJob>(job, processJob, releaseJob))))
        {
            releaseJob?.Invoke(job);
            throw new InvalidOperationException("Failed to queue ImGui draw callback.");
        }

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseOwnedJobs();
    }

    private static void ProcessQueuedJob(ImDrawList* _, ImDrawCmd* cmd)
    {
        long jobId = (long)(nint)cmd->UserCallbackData;
        if (!Jobs.TryGetValue(jobId, out Entry? entry))
        {
            return;
        }

        try
        {
            entry.Job.Process();
        }
        catch
        {
            ReleaseJob(jobId);
            throw;
        }
    }

    private static void ReleaseQueuedJob(ImDrawList* _, ImDrawCmd* cmd)
        => ReleaseJob((long)(nint)cmd->UserCallbackData);

    private static void ReleaseJob(long jobId)
    {
        if (Jobs.TryRemove(jobId, out Entry? entry))
        {
            entry.Job.Release();
        }
    }

    private void ReleaseOwnedJobs()
    {
        foreach ((long jobId, Entry entry) in Jobs)
        {
            if (ReferenceEquals(entry.Owner, this))
            {
                ReleaseJob(jobId);
            }
        }
    }

    private sealed record Entry(ImGuiDrawCallbackQueue Owner, IQueuedJob Job);

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
    private readonly ImGuiDrawCallbackQueue _queue = new();
    private readonly Action<TJob>          _processJob;
    private readonly Action<TJob>?         _releaseJob;

    public ImGuiDrawCallbackQueue(Action<TJob> processJob, Action<TJob>? releaseJob = null)
    {
        ArgumentNullException.ThrowIfNull(processJob);

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
