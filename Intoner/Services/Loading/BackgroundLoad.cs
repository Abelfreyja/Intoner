using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Services.Loading;

internal readonly struct LoadProgress
{
    private readonly Action<double>? _reportProgress;

    public LoadProgress(CancellationToken cancellationToken, Action<double>? reportProgress)
    {
        CancellationToken = cancellationToken;
        _reportProgress = reportProgress;
    }

    public CancellationToken CancellationToken { get; }

    public static LoadProgress Unreported(CancellationToken cancellationToken = default)
        => new(cancellationToken, null);

    public void Report(double progress)
    {
        if (!double.IsFinite(progress))
        {
            throw new ArgumentOutOfRangeException(nameof(progress), "load progress must be finite");
        }

        _reportProgress?.Invoke(Math.Clamp(progress, 0d, 1d));
    }

    public LoadProgress Slice(double start, double end)
    {
        if (!double.IsFinite(start) || start < 0d || start > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (!double.IsFinite(end) || end < start || end > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        if (_reportProgress is not { } reportProgress)
        {
            return new LoadProgress(CancellationToken, null);
        }

        return new LoadProgress(
            CancellationToken,
            progress => reportProgress(start + ((end - start) * Math.Clamp(progress, 0d, 1d))));
    }

    public LoadProgressPlan CreatePlan(double totalWeight = 100d)
        => new(this, totalWeight);

    public void ReportItems(int completed, int total, int reportInterval = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completed);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reportInterval);
        if (completed > total)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), "completed items cannot exceed the total");
        }

        if (_reportProgress is null
         || (completed != total && completed % reportInterval != 0))
        {
            return;
        }

        Report(total == 0 ? 1d : completed / (double)total);
    }
}

internal sealed class LoadProgressPlan
{
    private readonly LoadProgress _progress;
    private readonly double _totalWeight;
    private double _assignedWeight;

    public LoadProgressPlan(LoadProgress progress, double totalWeight)
    {
        if (!double.IsFinite(totalWeight) || totalWeight <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(totalWeight), "total load weight must be positive and finite");
        }

        _progress = progress;
        _totalWeight = totalWeight;
    }

    public LoadProgress Next(double weight)
    {
        if (!double.IsFinite(weight) || weight <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "load weight must be positive and finite");
        }

        if (_assignedWeight + weight > _totalWeight)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "load progress weights exceed the plan total");
        }

        double start = _assignedWeight / _totalWeight;
        _assignedWeight += weight;
        return _progress.Slice(start, _assignedWeight / _totalWeight);
    }
}

internal readonly record struct BackgroundLoadMessages(
    string Loading,
    string Ready,
    string Failed,
    string FailureLog);

/// <summary> owns one background load operation and its immutable public status </summary>
internal sealed class BackgroundLoad<TValue> : ILoadOperation, IDisposable
    where TValue : class
{
    private readonly ILogger _logger;
    private readonly Func<LoadProgress, TValue?> _load;
    private readonly BackgroundLoadMessages _messages;
    private readonly Lock _stateLock = new();

    private TValue? _value;
    private Task? _task;
    private CancellationTokenSource? _cancellation;
    private Exception? _failure;
    private LoadStatus _status;
    private bool _disposed;

    public BackgroundLoad(
        ILogger logger,
        Func<LoadProgress, TValue?> load,
        BackgroundLoadMessages messages)
    {
        _logger = logger;
        _load = load;
        _messages = messages;
        _status = new LoadStatus(LoadPhase.Waiting, messages.Loading, 0d);
    }

    public LoadStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return _status;
            }
        }
    }

    public void EnsureLoaded()
    {
        lock (_stateLock)
        {
            if (_disposed || _value is not null || _failure is not null || _task is not null)
            {
                return;
            }

            CancellationTokenSource cancellation = new();
            _cancellation = cancellation;
            _status = new LoadStatus(LoadPhase.Running, _messages.Loading, 0d);
            _task = Task.Run(() => Run(cancellation), cancellation.Token);
        }
    }

    public bool TryGet([NotNullWhen(true)] out TValue? value)
    {
        lock (_stateLock)
        {
            value = _value;
            return value is not null;
        }
    }

    public TValue Get(CancellationToken cancellationToken = default)
    {
        EnsureLoaded();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? task;
            lock (_stateLock)
            {
                if (_value is not null)
                {
                    return _value;
                }

                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_failure is not null)
                {
                    throw new InvalidOperationException(_messages.Failed, _failure);
                }

                task = _task;
            }

            WaitForValue(task, cancellationToken);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            task = _task;
            _cancellation = null;
            _task = null;
            _value = null;
        }

        cancellation?.Cancel();
        WaitForStop(task);
        cancellation?.Dispose();
    }

    private void Run(CancellationTokenSource cancellation)
    {
        CancellationToken cancellationToken = cancellation.Token;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TValue? value = _load(new LoadProgress(
                cancellationToken,
                progress => StoreProgress(cancellation, progress)));
            if (value is null)
            {
                throw new InvalidOperationException("background load returned no value");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Complete(value, cancellation))
            {
                cancellation.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // cancellation is expected during disposal
        }
        catch (Exception ex)
        {
            if (Fail(ex, cancellation))
            {
                cancellation.Dispose();
                _logger.LogError(ex, _messages.FailureLog);
            }
        }
    }

    private bool Complete(TValue value, CancellationTokenSource cancellation)
    {
        lock (_stateLock)
        {
            if (_disposed || !ReferenceEquals(_cancellation, cancellation))
            {
                return false;
            }

            _value = value;
            _failure = null;
            _status = new LoadStatus(LoadPhase.Ready, _messages.Ready, 1d);
            _cancellation = null;
            _task = null;
            return true;
        }
    }

    private bool Fail(Exception failure, CancellationTokenSource cancellation)
    {
        lock (_stateLock)
        {
            if (_disposed || !ReferenceEquals(_cancellation, cancellation))
            {
                return false;
            }

            _failure = failure;
            _status = new LoadStatus(LoadPhase.Failed, _messages.Failed, _status.Progress);
            _cancellation = null;
            _task = null;
            return true;
        }
    }

    private void StoreProgress(CancellationTokenSource cancellation, double progress)
    {
        lock (_stateLock)
        {
            if (_disposed || !ReferenceEquals(_cancellation, cancellation))
            {
                return;
            }

            _status = _status.WithProgress(Math.Max(_status.Progress, progress));
        }
    }

    private void WaitForStop(Task? task)
    {
        try
        {
            task?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // cancellation is expected during disposal
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to stop background load");
        }
    }

    private static void WaitForValue(Task? task, CancellationToken cancellationToken)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                return;
            }

            task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // disposal may cancel the load while a caller is waiting
        }
    }
}
