using Microsoft.Extensions.Logging;
using Intoner.Objects.Runtime;

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

    /// <summary> resolves one persisted scene item by its process wide identity </summary>
    bool TryGetPlacedItem(Guid id, out SceneItemSnapshot snapshot);

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

    /// <summary> applies ordered scene item create, update, and remove changes </summary>
    SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes);

    /// <summary> applies scene item changes and commits related state through the same rollback boundary </summary>
    SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes, Func<bool> commit);

    /// <summary> duplicates scene items through their owning domains and rolls back completed domains on failure </summary>
    SceneMutationStatus DuplicateMany(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicates);
}

internal sealed class SceneItemService : ISceneItemService
{
    private readonly ILogger<SceneItemService> _logger;
    private readonly SceneItemDomains _domains;
    private readonly SceneItemMutationCoordinator _mutations;
    private readonly Lock _mutationLock;
    private readonly Lock _queryLock = new();
    private readonly SceneQueryCache<SceneItemSnapshot> _placedItems = new();
    private readonly SceneQueryCache<SceneItemSnapshot> _activeItems = new();
    private readonly SceneQueryCache<ISceneItemRuntime> _runtimeItems = new();
    private readonly SceneQueryCache<SceneItemBoundsSnapshot> _boundsSnapshots = new();

    public SceneItemService(
        ILogger<SceneItemService> logger,
        ObjectStateLock stateLock,
        IEnumerable<ISceneItemDomain> domains)
    {
        _logger = logger;
        _mutationLock = stateLock.Value;
        _domains = new SceneItemDomains(logger, domains);
        _mutations = new SceneItemMutationCoordinator(logger, _domains);
    }

    public IReadOnlyList<SceneItemSnapshot> GetPlacedItems()
        => GetCachedItems(
            _placedItems,
            static revisions => revisions.Persistent,
            static domain => domain.GetPlacedItems(),
            static item => item.Id,
            static item => item.CreatedAtUtc);

    public bool TryGetPlacedItem(Guid id, out SceneItemSnapshot snapshot)
    {
        foreach (SceneItemSnapshot candidate in GetPlacedItems())
        {
            if (candidate.Id == id)
            {
                snapshot = candidate;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    public IReadOnlyList<SceneItemSnapshot> GetActiveItems()
        => GetCachedItems(
            _activeItems,
            static revisions => revisions.Active,
            static domain => domain.GetActiveItems(),
            static item => item.Id,
            static item => item.CreatedAtUtc);

    public IReadOnlyList<ISceneItemRuntime> GetRuntimeItems()
        => GetCachedItems(
            _runtimeItems,
            static revisions => revisions.Active,
            static domain => domain.GetRuntimeItems(),
            static item => item.Snapshot.Id,
            static item => item.Snapshot.CreatedAtUtc);

    public IReadOnlyList<SceneItemBoundsSnapshot> GetBoundsSnapshots()
        => GetCachedItems(
            _boundsSnapshots,
            static revisions => revisions.Bounds,
            static domain => domain.GetBoundsSnapshots(),
            static bounds => bounds.Id,
            getCreatedAt: null);

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

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        lock (_mutationLock)
        {
            return _mutations.ApplyChanges(changes);
        }
    }

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes, Func<bool> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_mutationLock)
        {
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

    private IReadOnlyList<T> GetCachedItems<T>(
        SceneQueryCache<T> cache,
        Func<SceneRevisions, long> getRevision,
        Func<ISceneItemDomain, IReadOnlyList<T>> getItems,
        Func<T, Guid> getId,
        Func<T, DateTime>? getCreatedAt)
    {
        while (true)
        {
            long revision = getRevision(GetRevisions());
            lock (_queryLock)
            {
                if (getRevision(GetRevisions()) != revision)
                {
                    continue;
                }

                if (cache.Revision == revision)
                {
                    return cache.Items;
                }
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
            if (getRevision(GetRevisions()) != revision)
            {
                continue;
            }

            lock (_queryLock)
            {
                if (getRevision(GetRevisions()) != revision)
                {
                    continue;
                }

                if (cache.Revision == revision)
                {
                    return cache.Items;
                }

                cache.Set(
                    revision,
                    items);
                return cache.Items;
            }
        }
    }

    private sealed class SceneQueryCache<T>
    {
        public long Revision { get; private set; } = long.MinValue;
        public IReadOnlyList<T> Items { get; private set; } = [];

        public void Set(long revision, IReadOnlyList<T> items)
        {
            Revision = revision;
            Items = items;
        }
    }
}
