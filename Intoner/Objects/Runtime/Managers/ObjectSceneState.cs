using Intoner.Objects.Models;
using Intoner.Scene;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Runtime;

/// <summary>
/// Stores active scene objects, failure diagnostics, and location refresh state.
/// </summary>
internal interface IObjectSceneState
{
    /// <summary>
    /// Gets the currently active object snapshots.
    /// </summary>
    /// <returns>The active object snapshots.</returns>
    IReadOnlyList<ObjectSnapshot> GetObjectSnapshots();

    /// <summary>
    /// Gets the current bounds snapshots for active objects.
    /// </summary>
    /// <returns>The active object bounds snapshots.</returns>
    IReadOnlyList<ObjectBoundsSnapshot> GetBoundsSnapshots();

    /// <summary>
    /// Gets a snapshot of all active scene entries.
    /// </summary>
    /// <returns>The active scene entries.</returns>
    IReadOnlyList<ObjectSceneEntry> GetEntriesSnapshot();

    /// <summary>
    /// Gets the revision of the active scene entry set.
    /// </summary>
    /// <returns>The current active revision.</returns>
    long GetActiveRevision();

    /// <summary>
    /// Gets the revision of the published bounds set.
    /// </summary>
    /// <returns>The current bounds revision.</returns>
    long GetBoundsRevision();

    /// <summary>
    /// Tries to resolve one active scene entry.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="entry">The resolved scene entry when active.</param>
    /// <returns>true when the object is active.</returns>
    bool TryGetEntry(Guid id, out ObjectSceneEntry entry);

    /// <summary>
    /// Tries to resolve one active object snapshot.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="snapshot">The resolved snapshot when active.</param>
    /// <returns>true when the object is active.</returns>
    bool TryGetObjectSnapshot(Guid id, out ObjectSnapshot snapshot);

    /// <summary>
    /// Gets a snapshot of all active object ids.
    /// </summary>
    /// <returns>The active object ids.</returns>
    IReadOnlySet<Guid> GetActiveObjectIds();

    /// <summary>
    /// Gets a snapshot of all current runtime failure codes.
    /// </summary>
    /// <returns>The runtime failure codes keyed by object id.</returns>
    IReadOnlyDictionary<Guid, string> GetRuntimeFailureCodes();

    /// <summary>
    /// Tries to resolve one runtime failure code.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="code">The failure code when one exists.</param>
    /// <returns>true when a failure code is stored.</returns>
    bool TryGetRuntimeFailureCode(Guid id, out string? code);

    /// <summary>
    /// Stores or replaces one active scene entry.
    /// </summary>
    /// <param name="entry">The scene entry to store.</param>
    void UpsertEntry(ObjectSceneEntry entry);

    /// <summary>
    /// Removes one active scene entry.
    /// </summary>
    /// <param name="id">The object id to remove.</param>
    /// <param name="entry">The removed entry when one existed.</param>
    /// <returns>true when the entry existed and was removed.</returns>
    bool TryRemoveEntry(Guid id, out ObjectSceneEntry entry);

    /// <summary>
    /// Removes all active scene entries.
    /// </summary>
    /// <returns>The removed scene entries.</returns>
    IReadOnlyList<ObjectSceneEntry> RemoveAllEntries();

    /// <summary>
    /// Replaces the current active bounds snapshot list.
    /// </summary>
    /// <param name="boundsSnapshots">The next bounds snapshot list.</param>
    void SetBoundsSnapshots(IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots);

    /// <summary>
    /// Stores one runtime failure code.
    /// </summary>
    /// <param name="id">The object id.</param>
    /// <param name="code">The failure code.</param>
    void SetRuntimeFailure(Guid id, string code);

    /// <summary>
    /// Clears one runtime failure code.
    /// </summary>
    /// <param name="id">The object id.</param>
    void ClearRuntimeFailure(Guid id);

    /// <summary>
    /// Clears runtime failure codes for objects that are no longer relevant.
    /// </summary>
    /// <param name="ids">The object ids to keep.</param>
    void ClearRuntimeFailuresExcept(IReadOnlyCollection<Guid> ids);

    /// <summary>
    /// Marks that the current location scene should be refreshed.
    /// </summary>
    void MarkNeedsRefresh();

    /// <summary>
    /// Gets the current location refresh state.
    /// </summary>
    /// <returns>The current refresh state.</returns>
    ObjectSceneRefreshState GetRefreshState();

    /// <summary>
    /// Acknowledges one scene refresh without discarding newer refresh requests.
    /// </summary>
    /// <param name="location"> The loaded location scope. </param>
    /// <param name="revision"> The refresh revision captured before reconciliation. </param>
    /// <param name="needsRetry"> Whether a transient failure requires another refresh. </param>
    /// <returns> true when another refresh remains pending. </returns>
    bool CompleteRefresh(SceneLocationScope location, long revision, bool needsRetry);

    /// <summary>
    /// Clears the current loaded-location state.
    /// </summary>
    /// <param name="needsRefresh">Whether another refresh should remain pending.</param>
    void ClearLoadedLocation(bool needsRefresh);

    /// <summary>
    /// Marks the runtime as entering a zone transition.
    /// </summary>
    void BeginZoning();

    /// <summary>
    /// Marks the runtime as leaving a zone transition.
    /// </summary>
    void EndZoning();

    /// <summary>
    /// Marks the runtime as logged out.
    /// </summary>
    void HandleLogout();
}

internal sealed class ObjectSceneState : IObjectSceneState
{
    private readonly Lock _stateLock;
    private readonly Dictionary<Guid, ObjectSceneEntry> _entries = [];
    private IReadOnlyList<ObjectSceneEntry> _entrySnapshot = Array.Empty<ObjectSceneEntry>();
    private IReadOnlyList<ObjectBoundsSnapshot> _boundsSnapshots = Array.Empty<ObjectBoundsSnapshot>();
    private readonly Dictionary<Guid, string> _runtimeFailureCodes = [];

    private SceneLocationScope _loadedLocation;
    private long _activeRevision = 1;
    private long _boundsRevision = 1;
    private long _refreshRevision;
    private bool _hasLoadedLocation;
    private bool _needsLocationRefresh;
    private bool _isZoning;

    public ObjectSceneState(ObjectStateLock stateLock)
    {
        _stateLock = stateLock.Value;
    }

    public IReadOnlyList<ObjectSnapshot> GetObjectSnapshots()
    {
        lock (_stateLock)
        {
            return _entries.Values
                .Select(static entry => entry.Snapshot)
                .OrderBy(static snapshot => snapshot.CreatedAtUtc)
                .ToList();
        }
    }

    public IReadOnlyList<ObjectBoundsSnapshot> GetBoundsSnapshots()
    {
        lock (_stateLock)
        {
            return _boundsSnapshots;
        }
    }

    public IReadOnlyList<ObjectSceneEntry> GetEntriesSnapshot()
    {
        lock (_stateLock)
        {
            return _entrySnapshot;
        }
    }

    public long GetActiveRevision()
    {
        lock (_stateLock)
        {
            return _activeRevision;
        }
    }

    public long GetBoundsRevision()
    {
        lock (_stateLock)
        {
            return _boundsRevision;
        }
    }

    public bool TryGetEntry(Guid id, out ObjectSceneEntry entry)
    {
        lock (_stateLock)
        {
            if (_entries.TryGetValue(id, out entry!))
            {
                return true;
            }
        }

        entry = null!;
        return false;
    }

    public bool TryGetObjectSnapshot(Guid id, out ObjectSnapshot snapshot)
    {
        if (TryGetEntry(id, out var entry))
        {
            snapshot = entry.Snapshot;
            return true;
        }

        snapshot = default!;
        return false;
    }

    public IReadOnlySet<Guid> GetActiveObjectIds()
    {
        lock (_stateLock)
        {
            return _entries.Keys.ToHashSet();
        }
    }

    public IReadOnlyDictionary<Guid, string> GetRuntimeFailureCodes()
    {
        lock (_stateLock)
        {
            return new Dictionary<Guid, string>(_runtimeFailureCodes);
        }
    }

    public bool TryGetRuntimeFailureCode(Guid id, out string? code)
    {
        lock (_stateLock)
        {
            if (_runtimeFailureCodes.TryGetValue(id, out var resolvedCode))
            {
                code = resolvedCode;
                return true;
            }
        }

        code = null;
        return false;
    }

    public void UpsertEntry(ObjectSceneEntry entry)
    {
        lock (_stateLock)
        {
            _entries[entry.Snapshot.Id] = entry;
            RebuildEntrySnapshotLocked();
            _runtimeFailureCodes.Remove(entry.Snapshot.Id);
            ++_activeRevision;
        }
    }

    public bool TryRemoveEntry(Guid id, out ObjectSceneEntry entry)
    {
        lock (_stateLock)
        {
            if (!_entries.Remove(id, out entry!))
            {
                entry = null!;
                return false;
            }

            ObjectBoundsSnapshot[] retainedBounds = _boundsSnapshots
                .Where(snapshot => snapshot.Id != id)
                .ToArray();
            if (retainedBounds.Length != _boundsSnapshots.Count)
            {
                _boundsSnapshots = Array.AsReadOnly(retainedBounds);
                ++_boundsRevision;
            }

            RebuildEntrySnapshotLocked();

            _runtimeFailureCodes.Remove(id);
            ++_activeRevision;
            return true;
        }
    }

    public IReadOnlyList<ObjectSceneEntry> RemoveAllEntries()
    {
        lock (_stateLock)
        {
            var entries = _entries.Values.ToList();
            if (entries.Count > 0)
            {
                ++_activeRevision;
            }

            _entries.Clear();
            _entrySnapshot = Array.Empty<ObjectSceneEntry>();
            if (_boundsSnapshots.Count > 0)
            {
                _boundsSnapshots = Array.Empty<ObjectBoundsSnapshot>();
                ++_boundsRevision;
            }

            _runtimeFailureCodes.Clear();
            return entries;
        }
    }

    public void SetBoundsSnapshots(IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots)
    {
        lock (_stateLock)
        {
            if (BoundsSnapshotsEqual(_boundsSnapshots, boundsSnapshots))
            {
                return;
            }

            _boundsSnapshots = Array.AsReadOnly(boundsSnapshots.ToArray());
            ++_boundsRevision;
        }
    }

    private static bool BoundsSnapshotsEqual(
        IReadOnlyList<ObjectBoundsSnapshot> current,
        IReadOnlyList<ObjectBoundsSnapshot> next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        for (int index = 0; index < current.Count; ++index)
        {
            if (!current[index].HasSameContent(next[index]))
            {
                return false;
            }
        }

        return true;
    }

    private void RebuildEntrySnapshotLocked()
        => _entrySnapshot = Array.AsReadOnly(_entries.Values.ToArray());

    public void SetRuntimeFailure(Guid id, string code)
    {
        lock (_stateLock)
        {
            _runtimeFailureCodes[id] = code;
        }
    }

    public void ClearRuntimeFailure(Guid id)
    {
        lock (_stateLock)
        {
            _runtimeFailureCodes.Remove(id);
        }
    }

    public void ClearRuntimeFailuresExcept(IReadOnlyCollection<Guid> ids)
    {
        lock (_stateLock)
        {
            foreach (var failureId in _runtimeFailureCodes.Keys.Except(ids).ToList())
            {
                _runtimeFailureCodes.Remove(failureId);
            }
        }
    }

    public void MarkNeedsRefresh()
    {
        lock (_stateLock)
        {
            ++_refreshRevision;
            _needsLocationRefresh = true;
        }
    }

    public ObjectSceneRefreshState GetRefreshState()
    {
        lock (_stateLock)
        {
            return new ObjectSceneRefreshState(
                _loadedLocation,
                _hasLoadedLocation,
                _needsLocationRefresh,
                _isZoning,
                _refreshRevision);
        }
    }

    public bool CompleteRefresh(SceneLocationScope location, long revision, bool needsRetry)
    {
        lock (_stateLock)
        {
            _loadedLocation = location;
            _hasLoadedLocation = true;
            _needsLocationRefresh = needsRetry || revision != _refreshRevision;
            return _needsLocationRefresh;
        }
    }

    public void ClearLoadedLocation(bool needsRefresh)
    {
        lock (_stateLock)
        {
            _hasLoadedLocation = false;
            _needsLocationRefresh = needsRefresh;
        }
    }

    public void BeginZoning()
    {
        lock (_stateLock)
        {
            ++_refreshRevision;
            _isZoning = true;
            _needsLocationRefresh = true;
            _hasLoadedLocation = false;
        }
    }

    public void EndZoning()
    {
        lock (_stateLock)
        {
            ++_refreshRevision;
            _isZoning = false;
            _needsLocationRefresh = true;
        }
    }

    public void HandleLogout()
    {
        lock (_stateLock)
        {
            ++_refreshRevision;
            _isZoning = true;
            _needsLocationRefresh = true;
            _hasLoadedLocation = false;
        }
    }
}

internal sealed class ObjectSceneEntry
{
    public required IObjectRuntime Runtime { get; init; }
    public required ObjectSceneSource Source { get; init; }
    public SceneResourceCollectionState ResourceCollection { get; init; }

    public ObjectSnapshot Snapshot
        => Runtime.Snapshot;
}

internal enum SceneResourceCollectionStatus
{
    Current,
    Pending,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct SceneResourceCollectionState(
    SceneResourceCollectionStatus Status,
    long Revision)
{
    public static SceneResourceCollectionState Pending { get; } = new(SceneResourceCollectionStatus.Pending, 0);

    public static SceneResourceCollectionState Current(long revision)
        => new(SceneResourceCollectionStatus.Current, NormalizeRevision(revision));

    public bool MatchesCurrentRevision(long revision)
        => Status == SceneResourceCollectionStatus.Current && Revision == NormalizeRevision(revision);

    public bool IsPendingWithoutRuntimeRevision(long revision)
        => Status == SceneResourceCollectionStatus.Pending && NormalizeRevision(revision) == 0;

    private static long NormalizeRevision(long revision)
        => revision < 0 ? 0 : revision;
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ObjectSceneRefreshState(
    SceneLocationScope LoadedLocation,
    bool HasLoadedLocation,
    bool NeedsRefresh,
    bool IsZoning,
    long Revision);


