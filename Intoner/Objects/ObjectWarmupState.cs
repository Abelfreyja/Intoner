using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;

namespace Intoner.Objects;

/// <summary> stores background warmup state for cached object data </summary>
internal sealed class ObjectWarmupState<TValue> : IDisposable
    where TValue : class
{
    private readonly ILogger _logger;
    private readonly Func<CancellationToken, TValue> _buildValue;
    private readonly Lock _stateLock = new();
    private readonly string _loadingStatusText;
    private readonly string _readyStatusText;
    private readonly string _failedStatusText;
    private readonly string _failureLogMessage;

    private TValue? _value;
    private Task? _warmupTask;
    private CancellationTokenSource? _warmupCancellation;
    private string _statusText;
    private bool _hasFailed;
    private bool _disposed;
    private int _desiredVersion = 1;
    private int _completedVersion;
    private int _buildingVersion;

    public ObjectWarmupState(
        ILogger logger,
        Func<CancellationToken, TValue> buildValue,
        string waitingStatusText,
        string loadingStatusText,
        string readyStatusText,
        string failedStatusText,
        string failureLogMessage)
    {
        _logger = logger;
        _buildValue = buildValue;
        _statusText = waitingStatusText;
        _loadingStatusText = loadingStatusText;
        _readyStatusText = readyStatusText;
        _failedStatusText = failedStatusText;
        _failureLogMessage = failureLogMessage;
    }

    public bool IsReady
    {
        get
        {
            lock (_stateLock)
            {
                return _value is not null;
            }
        }
    }

    public bool IsLoading
    {
        get
        {
            lock (_stateLock)
            {
                return _warmupTask is { IsCompleted: false };
            }
        }
    }

    public bool HasFailed
    {
        get
        {
            lock (_stateLock)
            {
                return _hasFailed;
            }
        }
    }

    public string StatusText
    {
        get
        {
            lock (_stateLock)
            {
                return _statusText;
            }
        }
    }

    public void EnsureWarmup()
    {
        lock (_stateLock)
        {
            EnsureWarmupLocked();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        Task? warmupTask;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _warmupCancellation;
            warmupTask = _warmupTask;
            _warmupCancellation = null;
            _warmupTask = null;
            _value = null;
            _buildingVersion = 0;
        }

        cancellation?.Cancel();
        WaitForWarmup(warmupTask);
        cancellation?.Dispose();
    }

    public bool TryGetValue([NotNullWhen(true)] out TValue? value)
    {
        lock (_stateLock)
        {
            value = _value;
            return value is not null;
        }
    }

    public TValue GetValue()
        => GetValue(CancellationToken.None);

    public TValue GetValue(CancellationToken cancellationToken)
    {
        EnsureWarmup();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? warmupTask;
            lock (_stateLock)
            {
                if (_value is not null)
                {
                    return _value;
                }

                ObjectDisposedException.ThrowIf(_disposed, this);
                EnsureWarmupLocked();
                warmupTask = _warmupTask;
            }

            if (warmupTask is not null)
            {
                WaitForWarmupValue(warmupTask, cancellationToken);
            }
        }
    }

    public void Invalidate()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            checked
            {
                _desiredVersion++;
            }

            EnsureWarmupLocked();
        }
    }

    private void EnsureWarmupLocked()
    {
        if (_disposed)
        {
            return;
        }

        if (_completedVersion >= _desiredVersion && _value is not null)
        {
            return;
        }

        if (_warmupTask is { IsCompleted: false } && _buildingVersion >= _desiredVersion)
        {
            return;
        }

        _hasFailed = false;
        _statusText = _loadingStatusText;
        StartWarmupLocked(_desiredVersion);
    }

    private void StartWarmupLocked(int buildVersion)
    {
        _warmupCancellation?.Cancel();
        _warmupCancellation?.Dispose();
        _warmupCancellation = new CancellationTokenSource();
        CancellationToken token = _warmupCancellation.Token;
        _buildingVersion = buildVersion;
        _warmupTask = Task.Run(() => WarmupValue(buildVersion, token), token);
    }

    private void WarmupValue(int buildVersion, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            TValue value = _buildValue(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StoreReadyValue(value, buildVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (_disposed || _buildingVersion != buildVersion)
                {
                    return;
                }

                _hasFailed = true;
                _statusText = _failedStatusText;
                _warmupCancellation?.Dispose();
                _warmupCancellation = null;
                _warmupTask = null;
                _buildingVersion = 0;
            }

            _logger.LogError(ex, _failureLogMessage);
        }
    }

    private void StoreReadyValue(TValue value, int buildVersion)
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_buildingVersion != buildVersion)
            {
                return;
            }

            _value = value;
            _hasFailed = false;
            _completedVersion = Math.Max(_completedVersion, buildVersion);

            if (_desiredVersion > buildVersion)
            {
                _statusText = _loadingStatusText;
                StartWarmupLocked(_desiredVersion);
                return;
            }

            _statusText = _readyStatusText;
            _warmupCancellation?.Dispose();
            _warmupCancellation = null;
            _warmupTask = null;
            _buildingVersion = 0;
        }
    }

    private void WaitForWarmup(Task? warmupTask)
    {
        try
        {
            warmupTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to stop object warmup");
        }
    }

    private static void WaitForWarmupValue(Task warmupTask, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                warmupTask.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                return;
            }

            warmupTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}

