using Dalamud.Plugin.Services;
using Intoner.Objects.Runtime;
using Intoner.Scene;
using Intoner.Utils;
using Microsoft.Extensions.Logging;

namespace Intoner.Displays;

/// <summary> reconciles persisted displays with active capture runtimes </summary>
internal sealed class DisplayRuntimeManager : IDisposable
{
    private const long ReconciliationRetryDelayMilliseconds = 2000;

    private readonly ILogger<DisplayRuntimeManager> _logger;
    private readonly IFramework _framework;
    private readonly ISceneLocationService _locationService;
    private readonly DisplayState _state;
    private readonly DisplayRuntimeFactory _runtimeFactory;
    private readonly Lock _stateLock;
    private readonly Dictionary<Guid, DisplayRuntime> _runtimes = [];

    private DisplayRuntime[] _runtimeSnapshot = [];
    private long _activeRevision;
    private long _boundsRevision;
    private long _reconciledPersistentRevision = -1;
    private long _attemptedPersistentRevision = -1;
    private long _retryReconciliationAfterMilliseconds;
    private long _nextMaintenanceMilliseconds = long.MaxValue;
    private int _maintenanceRequested;
    private bool _disposed;

    public DisplayRuntimeManager(
        ILogger<DisplayRuntimeManager> logger,
        IFramework framework,
        ObjectStateLock stateLock,
        ISceneLocationService locationService,
        DisplayState state,
        DisplayRuntimeFactory runtimeFactory)
    {
        _logger = logger;
        _framework = framework;
        _stateLock = stateLock.Value;
        _locationService = locationService;
        _state = state;
        _runtimeFactory = runtimeFactory;

        _locationService.LocationInvalidated += HandleLocationInvalidated;
        _framework.Update += HandleFrameworkUpdate;
    }

    public long ActiveRevision
        => Interlocked.Read(ref _activeRevision);

    public long BoundsRevision
        => Interlocked.Read(ref _boundsRevision);

    public IReadOnlyList<ISceneItemRuntime> GetRuntimeItems()
    {
        lock (_stateLock)
        {
            return _runtimes.Values
                .Select(static runtime => (ISceneItemRuntime)runtime)
                .ToList();
        }
    }

    public IReadOnlyList<SceneItemSnapshot> GetActiveItems()
    {
        lock (_stateLock)
        {
            return _runtimes.Values
                .Select(static runtime => (SceneItemSnapshot)runtime.Snapshot)
                .ToList();
        }
    }

    public IReadOnlyList<SceneItemBoundsSnapshot> GetBoundsSnapshots()
    {
        lock (_stateLock)
        {
            return _runtimes.Values
                .Select(static runtime => DisplayRuntime.CreateBoundsSnapshot(runtime.Snapshot))
                .ToArray();
        }
    }

    public bool ReconcileAfterMutation()
    {
        if (_disposed)
        {
            return false;
        }

        return !_framework.IsInFrameworkUpdateThread
               || TryReconcileOnFramework(force: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _locationService.LocationInvalidated -= HandleLocationInvalidated;
        _framework.Update -= HandleFrameworkUpdate;
        FrameworkThreadUtility.RunDuringShutdown(_framework, DisposeRuntimes);
    }

    private void HandleFrameworkUpdate(IFramework framework)
    {
        if (_disposed)
        {
            return;
        }

        _ = TryReconcileOnFramework();
        long now = Environment.TickCount64;
        if (Interlocked.Exchange(ref _maintenanceRequested, 0) != 0
            || now >= Volatile.Read(ref _nextMaintenanceMilliseconds))
        {
            MaintainRuntimes(now);
        }
    }

    private void RequestReconciliation(bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!FrameworkThreadUtility.TryRun(
                    _framework,
                    () => _ = TryReconcileOnFramework(force)))
            {
                MarkReconciliationPending(delayRetry: false);
            }
        }
        catch (Exception ex)
        {
            HandleReconciliationFailure(ex);
        }
    }

    private bool TryReconcileOnFramework(bool force = false)
    {
        try
        {
            return ReconcileOnFramework(force);
        }
        catch (Exception ex)
        {
            HandleReconciliationFailure(ex);
            return false;
        }
    }

    private bool ReconcileOnFramework(bool force)
    {
        long desiredRevision = _state.Revision;
        long now = Environment.TickCount64;
        lock (_stateLock)
        {
            if (!force && _reconciledPersistentRevision == desiredRevision)
            {
                return true;
            }

            if (!force
                && _attemptedPersistentRevision == desiredRevision
                && now < _retryReconciliationAfterMilliseconds)
            {
                return false;
            }
        }

        IReadOnlyList<DisplaySnapshot> snapshots = _state.GetSnapshots(out desiredRevision);
        lock (_stateLock)
        {
            if (snapshots.Count == 0 && _runtimes.Count == 0)
            {
                MarkReconciledLocked(desiredRevision);
                return true;
            }
        }

        SceneLocationScope location = _locationService.GetCurrentLocationScope();
        DisplaySnapshot[] desired = snapshots
            .Where(snapshot => MatchesLocation(snapshot, location))
            .ToArray();
        bool complete = ApplyRuntimeSnapshot(desired);
        lock (_stateLock)
        {
            long currentRevision = _state.Revision;
            if (complete && desiredRevision == currentRevision)
            {
                MarkReconciledLocked(desiredRevision);
            }
            else if (desiredRevision == currentRevision)
            {
                ScheduleReconciliationRetryLocked(desiredRevision, Environment.TickCount64);
            }
            else
            {
                MarkReconciliationPendingLocked();
            }
        }

        return complete;
    }

    private void HandleLocationInvalidated()
        => RequestReconciliation(force: true);

    private bool ApplyRuntimeSnapshot(IReadOnlyList<DisplaySnapshot> desired)
    {
        var desiredById = desired.ToDictionary(static snapshot => snapshot.Id);
        bool runtimeChanged = false;
        bool complete = true;
        Guid[] removedIds;
        lock (_stateLock)
        {
            removedIds = _runtimes.Keys
                .Where(id => !desiredById.ContainsKey(id))
                .ToArray();
        }

        try
        {
            foreach (Guid id in removedIds)
            {
                runtimeChanged |= RemoveRuntime(id);
            }

            foreach (DisplaySnapshot snapshot in desired)
            {
                DisplayRuntime? runtime = GetRuntime(snapshot.Id);
                if (runtime is not null)
                {
                    bool snapshotChanged = runtime.Snapshot != snapshot;
                    if (runtime.TryUpdate(snapshot))
                    {
                        runtimeChanged |= snapshotChanged;
                        continue;
                    }

                    runtimeChanged |= RemoveRuntime(snapshot.Id);
                }

                bool added = TryCreateAndAddRuntime(snapshot);
                runtimeChanged |= added;
                complete &= added;
            }

            return complete;
        }
        finally
        {
            if (runtimeChanged)
            {
                Interlocked.Increment(ref _activeRevision);
                Interlocked.Increment(ref _boundsRevision);
            }

            ScheduleMaintenance();
        }
    }

    private bool TryCreateAndAddRuntime(DisplaySnapshot snapshot)
    {
        if (!_runtimeFactory.TryCreate(snapshot, RequestMaintenance, out DisplayRuntime? runtime)
            || runtime is null)
        {
            return false;
        }

        lock (_stateLock)
        {
            if (!_disposed && _runtimes.TryAdd(snapshot.Id, runtime))
            {
                RebuildRuntimeSnapshotLocked();
                return true;
            }
        }

        runtime.Dispose();
        return false;
    }

    private bool RemoveRuntime(Guid id)
    {
        DisplayRuntime? runtime;
        lock (_stateLock)
        {
            if (_runtimes.Remove(id, out runtime))
            {
                RebuildRuntimeSnapshotLocked();
            }
        }

        if (runtime is null)
        {
            return false;
        }

        runtime.Dispose();
        return true;
    }

    private void DisposeRuntimes()
    {
        DisplayRuntime[] runtimes;
        lock (_stateLock)
        {
            runtimes = [.. _runtimes.Values];
            _runtimes.Clear();
            Volatile.Write(ref _runtimeSnapshot, []);
            Volatile.Write(ref _nextMaintenanceMilliseconds, long.MaxValue);
        }

        foreach (DisplayRuntime runtime in runtimes)
        {
            runtime.Dispose();
        }
    }

    private DisplayRuntime? GetRuntime(Guid id)
    {
        lock (_stateLock)
        {
            return _runtimes.GetValueOrDefault(id);
        }
    }

    private void RebuildRuntimeSnapshotLocked()
        => Volatile.Write(ref _runtimeSnapshot, [.. _runtimes.Values]);

    private void MaintainRuntimes(long now)
    {
        bool retryRequired = false;
        foreach (DisplayRuntime runtime in Volatile.Read(ref _runtimeSnapshot))
        {
            retryRequired |= !runtime.MaintainCapture(now);
        }

        if (retryRequired)
        {
            MarkReconciliationPending(delayRetry: true);
        }

        ScheduleMaintenance();
    }

    private void ScheduleMaintenance()
    {
        long next = long.MaxValue;
        foreach (DisplayRuntime runtime in Volatile.Read(ref _runtimeSnapshot))
        {
            next = Math.Min(next, runtime.NextMaintenanceMilliseconds);
        }

        Volatile.Write(ref _nextMaintenanceMilliseconds, next);
    }

    private void RequestMaintenance()
    {
        if (!Volatile.Read(ref _disposed))
        {
            Interlocked.Exchange(ref _maintenanceRequested, 1);
        }
    }

    private void MarkReconciliationPending(bool delayRetry)
    {
        lock (_stateLock)
        {
            if (delayRetry)
            {
                ScheduleReconciliationRetryLocked(_state.Revision, Environment.TickCount64);
            }
            else
            {
                MarkReconciliationPendingLocked();
            }
        }
    }

    private void HandleReconciliationFailure(Exception exception)
    {
        MarkReconciliationPending(delayRetry: true);
        _logger.LogError(exception, "failed to reconcile display runtimes");
    }

    private void MarkReconciledLocked(long revision)
    {
        _reconciledPersistentRevision = revision;
        _attemptedPersistentRevision = revision;
        _retryReconciliationAfterMilliseconds = long.MaxValue;
    }

    private void ScheduleReconciliationRetryLocked(long revision, long now)
    {
        _reconciledPersistentRevision = -1;
        _attemptedPersistentRevision = revision;
        _retryReconciliationAfterMilliseconds = now + ReconciliationRetryDelayMilliseconds;
    }

    private void MarkReconciliationPendingLocked()
    {
        _reconciledPersistentRevision = -1;
        _attemptedPersistentRevision = -1;
        _retryReconciliationAfterMilliseconds = 0;
    }

    private static bool MatchesLocation(DisplaySnapshot snapshot, SceneLocationScope location)
        => !snapshot.CreatedIn.IsValid || snapshot.CreatedIn.Scope == location;
}
