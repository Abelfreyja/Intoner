using Intoner.Logging;
using Intoner.Objects.Runtime;
using Microsoft.Extensions.Logging;

namespace Intoner.Scene;

/// <summary> describes whether a scene mutation applied, was rejected, or could not be fully recovered </summary>
internal enum SceneMutationStatus
{
    Applied,
    AppliedWithRuntimeFailure,
    Rejected,
    RecoveryRequired,
}

internal static class SceneMutationStatusExtensions
{
    public static bool IsApplied(this SceneMutationStatus status)
        => status is SceneMutationStatus.Applied or SceneMutationStatus.AppliedWithRuntimeFailure;

    public static bool IsFullyApplied(this SceneMutationStatus status)
        => status == SceneMutationStatus.Applied;

    public static SceneMutationStatus MergeApplied(
        this SceneMutationStatus current,
        SceneMutationStatus next)
        => current == SceneMutationStatus.AppliedWithRuntimeFailure
           || next == SceneMutationStatus.AppliedWithRuntimeFailure
            ? SceneMutationStatus.AppliedWithRuntimeFailure
            : SceneMutationStatus.Applied;
}

/// <summary> provides shared scene item queries and mutations across placed item domains </summary>
internal interface ISceneItemService
{
    /// <summary> gets all persisted scene items </summary>
    IReadOnlyList<SceneItemSnapshot> GetPlacedItems();

    /// <summary> gets all stored items, including disabled layouts </summary>
    IReadOnlyList<SceneItemSnapshot> GetStoredItems();

    /// <summary> gets the version used by stored item queries and history validation </summary>
    long GetStoredRevision();

    /// <summary> gets the content and identity versions used to check history </summary>
    long GetHistoryRevision();

    /// <summary> reconciles each runtime domain once after history replay or rollback </summary>
    void ReconcileStoredChanges();

    /// <summary> checks expected stored snapshots and history dependencies without mutating them </summary>
    SceneHistoryResult CheckStoredChanges(IReadOnlyList<SceneItemSnapshotChange> changes, SceneItemHistoryDependencies dependencies);

    /// <summary> replays checked stored changes independently of the current location </summary>
    SceneHistoryResult ApplyStoredChanges(IReadOnlyList<SceneItemSnapshotChange> changes, SceneItemHistoryDependencies dependencies);

    /// <summary> commits related stored metadata within the same checked rollback boundary </summary>
    SceneHistoryResult ApplyStoredChanges(IReadOnlyList<SceneItemSnapshotChange> changes, SceneItemHistoryDependencies dependencies, Func<bool> commit);

    /// <summary> resolves one persisted scene item by its process wide identity </summary>
    bool TryGetPlacedItem(Guid id, out SceneItemSnapshot snapshot);

    /// <summary> resolves current snapshots from one cached scene revision in request order </summary>
    bool TryResolvePlacedItems<TSnapshot>(IReadOnlyList<TSnapshot> requested,
        out IReadOnlyList<TSnapshot> resolved) where TSnapshot : SceneItemSnapshot;

    /// <summary> gets all currently active scene items </summary>
    IReadOnlyList<SceneItemSnapshot> GetActiveItems();

    /// <summary> gets all currently active scene item runtimes </summary>
    IReadOnlyList<ISceneItemRuntime> GetRuntimeItems();

    /// <summary> gets all current active scene item bounds </summary>
    IReadOnlyList<SceneItemBoundsSnapshot> GetBoundsSnapshots();

    /// <summary> gets the combined active, persisted, and bounds revisions </summary>
    SceneRevisions GetRevisions();

    /// <summary> resolves the manipulation behavior owned by a scene item's domain </summary>
    bool TryGetManipulation(SceneItemSnapshot snapshot, out SceneItemManipulationPolicy manipulation);

    /// <summary> applies one scene item update through its owning domain </summary>
    SceneMutationStatus Update(SceneItemSnapshot snapshot, out SceneItemSnapshot appliedSnapshot);

    /// <summary> applies an ordered scene item update batch </summary>
    SceneMutationStatus UpdateMany(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> appliedSnapshots);

    /// <summary> captures an manipulation session. disposal restores uncommitted previews </summary>
    /// <param name="snapshots"> unique persisted snapshots in the order required for each preview update </param>
    /// <returns> an edit session, or null for empty, duplicate, missing, or stale snapshots; preview updates require active runtimes </returns>
    SceneEditSession? BeginEdit(IReadOnlyList<SceneItemSnapshot> snapshots);

    /// <summary> applies ordered scene item create, update, and remove changes </summary>
    SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes);

    /// <summary> applies scene item changes and commits related state through the same rollback boundary </summary>
    SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes, Func<bool> commit);

    /// <summary> checks dependency snapshots and an optional persistent revision under the mutation lock before applying changes </summary>
    SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes,
        IReadOnlyCollection<SceneItemSnapshot> expectedUnchanged, Func<bool> commit, long? expectedPersistentRevision = null);

    /// <summary> duplicates scene items through their owning domains and rolls back completed domains on failure </summary>
    SceneMutationStatus DuplicateMany(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicates);
}

internal sealed class SceneItemService : ISceneItemService
{
    private readonly ILogger<SceneItemService> _logger;
    private readonly SceneItemDomains _domains;
    private readonly ISceneIdentityRegistry _identities;
    private readonly SceneItemMutationCoordinator _mutations;
    private readonly Lock _mutationLock;
    private readonly SceneQueryCache<SceneItemSnapshot> _placedItems = new();
    private readonly SceneQueryCache<SceneItemSnapshot> _storedItems = new();
    private readonly SceneQueryCache<SceneItemSnapshot> _activeItems = new();
    private readonly SceneQueryCache<ISceneItemRuntime> _runtimeItems = new();
    private readonly SceneQueryCache<SceneItemBoundsSnapshot> _boundsSnapshots = new();

    public SceneItemService(
        ILogger<SceneItemService> logger,
        ObjectStateLock stateLock,
        IEnumerable<ISceneItemDomain> domains,
        ISceneIdentityRegistry identities)
    {
        _logger = logger;
        _identities = identities;
        _mutationLock = stateLock.Value;
        _domains = new SceneItemDomains(logger, domains);
        _mutations = new SceneItemMutationCoordinator(logger, _domains);
    }

    public IReadOnlyList<SceneItemSnapshot> GetPlacedItems()
        => GetPlacedQuery().Items;

    private SceneQueryResult<SceneItemSnapshot> GetPlacedQuery()
        => GetCachedQuery(
            _placedItems,
            static domain => domain.Revisions.Persistent,
            static domain => domain.GetPlacedItems(),
            static item => item.Id,
            static item => item.CreatedAtUtc);

    public bool TryGetPlacedItem(Guid id, out SceneItemSnapshot snapshot)
        => GetPlacedQuery().ItemsById.TryGetValue(id, out snapshot!);

    public bool TryResolvePlacedItems<TSnapshot>(IReadOnlyList<TSnapshot> requested,
        out IReadOnlyList<TSnapshot> resolved) where TSnapshot : SceneItemSnapshot
    {
        resolved = [];
        if (requested.Count == 0)
            return true;

        IReadOnlyDictionary<Guid, SceneItemSnapshot> current = GetPlacedQuery().ItemsById;
        TSnapshot[] snapshots = new TSnapshot[requested.Count];
        for (int index = 0; index < requested.Count; ++index)
        {
            if (!current.TryGetValue(requested[index].Id, out SceneItemSnapshot? item) || item is not TSnapshot snapshot)
                return false;
            snapshots[index] = snapshot;
        }
        resolved = snapshots;
        return true;
    }

    public IReadOnlyList<SceneItemSnapshot> GetActiveItems()
        => GetCachedQuery(
            _activeItems,
            static domain => domain.Revisions.Active,
            static domain => domain.GetActiveItems(),
            static item => item.Id,
            static item => item.CreatedAtUtc).Items;

    public IReadOnlyList<ISceneItemRuntime> GetRuntimeItems()
        => GetCachedQuery(
            _runtimeItems,
            static domain => domain.Revisions.Active,
            static domain => domain.GetRuntimeItems(),
            static item => item.Snapshot.Id,
            static item => item.Snapshot.CreatedAtUtc).Items;

    public IReadOnlyList<SceneItemBoundsSnapshot> GetBoundsSnapshots()
        => GetCachedQuery(
            _boundsSnapshots,
            static domain => domain.Revisions.Bounds,
            static domain => domain.GetBoundsSnapshots(),
            static bounds => bounds.Id,
            getCreatedAt: null).Items;

    public SceneRevisions GetRevisions()
    {
        long active = 0;
        long persistent = 0;
        long bounds = 0;
        for (int index = 0; index < _domains.All.Count; ++index)
        {
            ISceneItemDomain domain = _domains.All[index];
            SceneRevisions revisions = domain.Revisions;
            active = unchecked(active + revisions.Active);
            persistent = unchecked(persistent + revisions.Persistent);
            bounds = unchecked(bounds + revisions.Bounds);
        }

        return new SceneRevisions(active, persistent, bounds);
    }

    public bool TryGetManipulation(
        SceneItemSnapshot snapshot,
        out SceneItemManipulationPolicy manipulation)
        => _mutations.TryGetManipulation(snapshot, out manipulation);

    public SceneMutationStatus Update(SceneItemSnapshot snapshot, out SceneItemSnapshot appliedSnapshot)
    {
        lock (_mutationLock)
        {
            SceneMutationStatus status = _mutations.UpdateMany([snapshot], out IReadOnlyList<SceneItemSnapshot> appliedSnapshots);
            if (status.IsApplied()
                && appliedSnapshots.Count == 1)
            {
                appliedSnapshot = appliedSnapshots[0];
                return status;
            }

            appliedSnapshot = null!;
            return status.IsApplied() ? SceneMutationStatus.RecoveryRequired : status;
        }
    }

    public SceneMutationStatus UpdateMany(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> appliedSnapshots)
    {
        lock (_mutationLock)
        {
            return _mutations.UpdateMany(snapshots, out appliedSnapshots);
        }
    }

    public SceneEditSession? BeginEdit(IReadOnlyList<SceneItemSnapshot> snapshots)
    {
        lock (_mutationLock)
        {
            if (snapshots.Count == 0 || !_domains.TryCapturePlacedItems(out Dictionary<Guid, SceneItemOwnership> placed))
            {
                return null;
            }

            var items = new SceneItemOwnership[snapshots.Count];
            var ids = new HashSet<Guid>();
            for (int index = 0; index < snapshots.Count; ++index)
            {
                SceneItemSnapshot snapshot = snapshots[index];
                if (!ids.Add(snapshot.Id) || !placed.TryGetValue(snapshot.Id, out items[index])
                    || !Equals(items[index].Snapshot, snapshot))
                {
                    return null;
                }
            }

            return new SceneEditSession(_logger, _mutationLock, _mutations, items);
        }
    }

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        lock (_mutationLock)
        {
            return _mutations.ApplyChanges(changes);
        }
    }

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes, Func<bool> commit)
        => ApplyChanges(changes, [], commit);

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes,
        IReadOnlyCollection<SceneItemSnapshot> expectedUnchanged, Func<bool> commit, long? expectedPersistentRevision = null)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_mutationLock)
        {
            if (expectedPersistentRevision.HasValue && GetRevisions().Persistent != expectedPersistentRevision.Value)
                return SceneMutationStatus.Rejected;
            if (expectedUnchanged.Count > 0)
            {
                IReadOnlyDictionary<Guid, SceneItemSnapshot> current = GetPlacedQuery().ItemsById;
                if (expectedUnchanged.Any(expected => current.GetValueOrDefault(expected.Id) != expected))
                    return SceneMutationStatus.Rejected;
            }
            return _mutations.ApplyChanges(changes, commit);
        }
    }

    public SceneMutationStatus DuplicateMany(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicates)
    {
        lock (_mutationLock)
        {
            return _mutations.DuplicateMany(snapshots, out duplicates);
        }
    }

    private SceneQueryResult<T> GetCachedQuery<T>(
        SceneQueryCache<T> cache,
        Func<ISceneItemDomain, long> getRevision,
        Func<ISceneItemDomain, IReadOnlyList<T>> getItems,
        Func<T, Guid> getId,
        Func<T, DateTime>? getCreatedAt)
    {
        using Lock.Scope scope = _mutationLock.EnterScope();
        while (true)
        {
            long revision = GetQueryRevision(getRevision);

            if (cache.Result.Revision == revision)
            {
                return cache.Result;
            }

            var itemsById = new Dictionary<Guid, T>();
            var duplicateIds = new HashSet<Guid>();
            foreach (ISceneItemDomain domain in _domains.All)
            {
                foreach (T item in getItems(domain))
                {
                    Guid id = getId(item);
                    if (id == Guid.Empty)
                    {
                        _logger.LogError("scene item domain {DomainType} returned an empty item id", domain.GetType().Name);
                        continue;
                    }

                    if (duplicateIds.Contains(id) || itemsById.TryAdd(id, item))
                    {
                        continue;
                    }

                    _logger.LogError("duplicate scene item id {ItemId} was returned by registered domains", id);
                    _ = itemsById.Remove(id);
                    _ = duplicateIds.Add(id);
                }
            }

            T[] items = getCreatedAt is null
                ? [.. itemsById.Values]
                : itemsById.Values.OrderBy(getCreatedAt).ToArray();

            if (GetQueryRevision(getRevision) != revision)
            {
                continue;
            }

            cache.Result = new SceneQueryResult<T>(revision, items, itemsById);
            return cache.Result;
        }
    }

    public void ReconcileStoredChanges()
    {
        foreach (ISceneItemDomain domain in _domains.All)
        {
            try
            {
                domain.ReconcileStoredChanges();
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write(_logger, LogLevel.Warning, exception, "saved changes could not be reconciled in {DomainType}", domain.GetType().Name);
            }
        }
    }

    public long GetHistoryRevision() => unchecked(GetStoredRevision() + _identities.Revision);

    public long GetStoredRevision()
        => GetQueryRevision(static domain => domain.StoredRevision);

    private long GetQueryRevision(Func<ISceneItemDomain, long> getRevision)
    {
        long revision = 0;
        for (int index = 0; index < _domains.All.Count; ++index)
        {
            revision = unchecked(revision + getRevision(_domains.All[index]));
        }

        return revision;
    }

    public IReadOnlyList<SceneItemSnapshot> GetStoredItems() => GetStoredQuery().Items;

    private SceneQueryResult<SceneItemSnapshot> GetStoredQuery()
        => GetCachedQuery(_storedItems, static domain => domain.StoredRevision,
            static domain => domain.GetStoredItems(), static item => item.Id, static item => item.CreatedAtUtc);

    public SceneHistoryResult CheckStoredChanges(IReadOnlyList<SceneItemSnapshotChange> changes, SceneItemHistoryDependencies dependencies)
    {
        lock (_mutationLock)
        {
            IReadOnlyDictionary<Guid, SceneItemSnapshot> current = GetStoredQuery().ItemsById;
            HashSet<Guid> ids = [];
            HashSet<Guid> created = [];
            foreach (SceneItemSnapshotChange change in changes)
            {
                SceneItemSnapshot item = (change.After ?? change.Before)!;

                if (item.Id == Guid.Empty || !ids.Add(item.Id))
                {
                    return SceneHistoryResult.Failed("The history action contains invalid object identities.");
                }

                if (!_domains.TryResolve(change, out ISceneItemDomain domain))
                {
                    return SceneHistoryResult.Conflict("The recorded object type is no longer available.");
                }

                if (change.After is { } after)
                {
                    SceneHistoryResult owner = domain.ValidateStoredOwner(after);

                    if (!owner.IsSuccess)
                    {
                        return owner;
                    }
                }

                if (change.Before is null)
                {
                    created.Add(item.Id);
                }

                current.TryGetValue(item.Id, out SceneItemSnapshot? stored);

                if (stored != change.Before)
                {
                    string name = SceneHistoryDescription.GetItemName(item);
                    string reason = (stored, change.Before) switch
                    {
                        (null, _) => $"{name} no longer exists in saved data.",
                        (_, null) => $"The recorded identity for {name} is already in use.",
                        _ => $"{name} was changed externally and no longer matches this action.",
                    };
                    return SceneHistoryResult.Conflict(reason);
                }
            }

            if (created.Count > 0 && !_identities.TryValidateNewItems(created, out _))
            {
                return SceneHistoryResult.Conflict("An object identity is now owned by another scene source.");
            }

            return dependencies.Check(current);
        }
    }

    public SceneHistoryResult ApplyStoredChanges(IReadOnlyList<SceneItemSnapshotChange> changes, SceneItemHistoryDependencies dependencies)
        => ApplyStoredChanges(changes, dependencies, static () => true);

    public SceneHistoryResult ApplyStoredChanges(IReadOnlyList<SceneItemSnapshotChange> changes, SceneItemHistoryDependencies dependencies, Func<bool> commit)
    {
        lock (_mutationLock)
        {
            SceneHistoryResult validation = CheckStoredChanges(changes, dependencies);

            if (!validation.IsSuccess)
            {
                return validation;
            }

            return SceneHistoryResult.FromMutation(_mutations.ApplyStoredChanges(changes, commit), "The saved change could not be applied.");
        }
    }

    private sealed class SceneQueryCache<T>
    {
        public SceneQueryResult<T> Result { get; set; } = new(long.MinValue, [], new Dictionary<Guid, T>());
    }

    private sealed record SceneQueryResult<T>(long Revision, IReadOnlyList<T> Items, IReadOnlyDictionary<Guid, T> ItemsById);
}
