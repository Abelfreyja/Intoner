using Microsoft.Extensions.Logging;

namespace Intoner.Scene;

/// <summary> coordinates checked scene item mutations and cross domain rollback </summary>
internal sealed class SceneItemMutationCoordinator
{
    private readonly ILogger _logger;
    private readonly SceneItemDomains _domains;

    public SceneItemMutationCoordinator(ILogger logger, SceneItemDomains domains)
    {
        _logger = logger;
        _domains = domains;
    }

    public bool TryGetManipulation(
        SceneItemSnapshot snapshot,
        out SceneItemManipulationPolicy manipulation)
    {
        if (_domains.TryResolve(snapshot, out ISceneItemDomain domain))
        {
            manipulation = domain.Manipulation;
            return true;
        }

        manipulation = null!;
        return false;
    }

    public SceneMutationStatus UpdateMany(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> appliedSnapshots)
    {
        appliedSnapshots = [];
        if (snapshots.Count == 0)
        {
            return SceneMutationStatus.Applied;
        }

        if (!TryBuildUpdateChanges(snapshots, out IReadOnlyList<SceneItemSnapshotChange> changes))
        {
            return SceneMutationStatus.Rejected;
        }

        IReadOnlyList<SceneItemSnapshot> resolvedSnapshots = [];
        SceneMutationStatus status = ApplyChanges(
            changes,
            () => TryResolvePlacedItems(snapshots, out resolvedSnapshots));
        if (status.IsApplied())
        {
            appliedSnapshots = resolvedSnapshots;
        }

        return status;
    }

    public SceneMutationStatus ApplyChanges(IReadOnlyList<SceneItemSnapshotChange> changes)
        => ApplyChanges(changes, static () => true);

    public SceneMutationStatus ApplyChanges(
        IReadOnlyList<SceneItemSnapshotChange> changes,
        Func<bool> commit)
    {
        if (!TryBuildChangeBatches(changes, out IReadOnlyList<DomainChangeBatch> batches))
        {
            return SceneMutationStatus.Rejected;
        }

        var applied = new List<DomainChangeBatch>(batches.Count);
        SceneMutationStatus result = SceneMutationStatus.Applied;
        foreach (DomainChangeBatch batch in batches)
        {
            SceneMutationStatus status = ApplyBatch(batch);
            if (status.IsApplied())
            {
                applied.Add(batch);
                result = result.MergeApplied(status);
                continue;
            }

            if (status == SceneMutationStatus.RecoveryRequired)
            {
                applied.Add(batch);
            }

            bool batchRecovered = Rollback(applied);
            return status == SceneMutationStatus.RecoveryRequired || !batchRecovered
                ? SceneMutationStatus.RecoveryRequired
                : SceneMutationStatus.Rejected;
        }

        SceneMutationStatus commitStatus = Commit(commit);
        if (commitStatus == SceneMutationStatus.Applied)
        {
            return result;
        }

        bool commitRecovered = Rollback(applied);
        return commitStatus == SceneMutationStatus.RecoveryRequired || !commitRecovered
            ? SceneMutationStatus.RecoveryRequired
            : SceneMutationStatus.Rejected;
    }

    public SceneMutationStatus DuplicateMany(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshot> duplicates)
    {
        duplicates = [];
        if (snapshots.Count == 0)
        {
            return SceneMutationStatus.Applied;
        }

        if (!TryBuildInputBatches(
                snapshots,
                out IReadOnlyList<DomainInputBatch> batches,
                out IReadOnlyDictionary<Guid, SceneItemOwnership> placedItems))
        {
            return SceneMutationStatus.Rejected;
        }

        var ordered = new SceneItemSnapshot[snapshots.Count];
        var assignedIds = placedItems.Keys.ToHashSet();
        foreach (DomainInputBatch batch in batches)
        {
            if (!TryCreateDuplicates(batch, out IReadOnlyList<SceneItemSnapshot> batchDuplicates)
                || !TryAssignDuplicates(batch, batchDuplicates, assignedIds, ordered))
            {
                return SceneMutationStatus.Rejected;
            }
        }

        SceneItemSnapshotChange[] changes = ordered
            .Select(static snapshot => new SceneItemSnapshotChange(null, snapshot))
            .ToArray();
        IReadOnlyList<SceneItemSnapshot> resolvedDuplicates = [];
        SceneMutationStatus status = ApplyChanges(
            changes,
            () => TryResolvePlacedItems(ordered, out resolvedDuplicates));
        if (status.IsApplied())
        {
            duplicates = resolvedDuplicates;
        }

        return status;
    }

    private SceneMutationStatus ApplyBatch(DomainChangeBatch batch)
    {
        try
        {
            return batch.Domain.ApplyChanges(batch.Changes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "scene item change failed in domain {DomainType}", batch.Domain.GetType().Name);
            return SceneMutationStatus.RecoveryRequired;
        }
    }

    private SceneMutationStatus Commit(Func<bool> commit)
    {
        try
        {
            return commit()
                ? SceneMutationStatus.Applied
                : SceneMutationStatus.Rejected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "scene item transaction commit failed");
            return SceneMutationStatus.RecoveryRequired;
        }
    }

    private bool Rollback(IReadOnlyList<DomainChangeBatch> applied)
    {
        bool recovered = true;
        for (int index = applied.Count - 1; index >= 0; --index)
        {
            DomainChangeBatch batch = applied[index];
            SceneItemSnapshotChange[] reverseChanges = Reverse(batch.Changes);
            try
            {
                if (batch.Domain.ApplyChanges(reverseChanges).IsFullyApplied())
                {
                    continue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "scene item rollback failed in domain {DomainType}", batch.Domain.GetType().Name);
                recovered = false;
                continue;
            }

            _logger.LogError("could not roll back scene items in domain {DomainType}", batch.Domain.GetType().Name);
            recovered = false;
        }

        return recovered;
    }

    private bool TryBuildUpdateChanges(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        changes = [];
        if (!TryResolveInputs(snapshots, out SceneItemOwnership[] currentItems, out _))
        {
            return false;
        }

        var result = new SceneItemSnapshotChange[snapshots.Count];
        for (int index = 0; index < snapshots.Count; ++index)
        {
            result[index] = new SceneItemSnapshotChange(currentItems[index].Snapshot, snapshots[index]);
        }

        changes = result;
        return true;
    }

    private bool TryBuildInputBatches(
        IReadOnlyList<SceneItemSnapshot> snapshots,
        out IReadOnlyList<DomainInputBatch> batches,
        out IReadOnlyDictionary<Guid, SceneItemOwnership> placedItems)
    {
        batches = [];
        if (!TryResolveInputs(
                snapshots,
                out SceneItemOwnership[] currentItems,
                out Dictionary<Guid, SceneItemOwnership> currentById))
        {
            placedItems = currentById;
            return false;
        }

        var result = new List<DomainInputBatch>();
        var byDomain = new Dictionary<ISceneItemDomain, DomainInputBatch>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < snapshots.Count; ++index)
        {
            SceneItemOwnership current = currentItems[index];
            ISceneItemDomain domain = current.Domain;

            if (!byDomain.TryGetValue(domain, out DomainInputBatch? batch))
            {
                batch = new DomainInputBatch(domain);
                byDomain.Add(domain, batch);
                result.Add(batch);
            }

            batch.Snapshots.Add(current.Snapshot);
            batch.InputIndices.Add(index);
        }

        batches = result;
        placedItems = currentById;
        return true;
    }

    private bool TryBuildChangeBatches(
        IReadOnlyList<SceneItemSnapshotChange> changes,
        out IReadOnlyList<DomainChangeBatch> batches)
    {
        batches = [];
        if (!_domains.TryCapturePlacedItems(out Dictionary<Guid, SceneItemOwnership> placedItems))
        {
            return false;
        }

        var result = new List<DomainChangeBatch>();
        foreach (SceneItemSnapshotChange change in changes)
        {
            if (!change.HasChange)
            {
                continue;
            }

            if (!_domains.TryResolve(change, out ISceneItemDomain domain))
            {
                return false;
            }

            SceneItemSnapshot candidate = change.After ?? change.Before!;
            if (candidate.Id == Guid.Empty
                || change.Before is { } before && before.Id != candidate.Id
                || change.After is { } after && after.Id != candidate.Id)
            {
                return false;
            }

            if (change.Before is null)
            {
                if (!placedItems.TryAdd(candidate.Id, new SceneItemOwnership(domain, change.After!)))
                {
                    return false;
                }
            }
            else if (!placedItems.TryGetValue(candidate.Id, out SceneItemOwnership current)
                     || !ReferenceEquals(current.Domain, domain)
                     || !Equals(current.Snapshot, change.Before))
            {
                return false;
            }
            else if (change.After is null)
            {
                _ = placedItems.Remove(candidate.Id);
            }
            else
            {
                placedItems[candidate.Id] = new SceneItemOwnership(domain, change.After);
            }

            DomainChangeBatch? batch = result.Count > 0 ? result[^1] : null;
            if (batch is null || !ReferenceEquals(batch.Domain, domain))
            {
                batch = new DomainChangeBatch(domain);
                result.Add(batch);
            }

            batch.Changes.Add(change);
        }

        batches = result;
        return true;
    }

    private bool TryResolvePlacedItems(
        IReadOnlyList<SceneItemSnapshot> requested,
        out IReadOnlyList<SceneItemSnapshot> resolved)
    {
        resolved = [];
        if (!TryResolveInputs(requested, out SceneItemOwnership[] currentItems, out _))
        {
            return false;
        }

        var result = new SceneItemSnapshot[requested.Count];
        for (int index = 0; index < requested.Count; ++index)
        {
            result[index] = currentItems[index].Snapshot;
        }

        resolved = result;
        return true;
    }

    private bool TryResolveInputs(
        IReadOnlyList<SceneItemSnapshot> requested,
        out SceneItemOwnership[] currentItems,
        out Dictionary<Guid, SceneItemOwnership> currentById)
    {
        currentItems = [];
        if (!_domains.TryCapturePlacedItems(out currentById))
        {
            return false;
        }

        var result = new SceneItemOwnership[requested.Count];
        var requestedIds = new HashSet<Guid>();
        for (int index = 0; index < requested.Count; ++index)
        {
            SceneItemSnapshot item = requested[index];
            if (item.Id == Guid.Empty
                || !requestedIds.Add(item.Id)
                || !_domains.TryResolve(item, out ISceneItemDomain domain)
                || !currentById.TryGetValue(item.Id, out SceneItemOwnership current)
                || !ReferenceEquals(current.Domain, domain))
            {
                return false;
            }

            result[index] = current;
        }

        currentItems = result;
        return true;
    }

    private bool TryCreateDuplicates(
        DomainInputBatch batch,
        out IReadOnlyList<SceneItemSnapshot> duplicates)
    {
        try
        {
            return batch.Domain.TryCreateDuplicates(batch.Snapshots, out duplicates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "scene item duplicate preparation failed in domain {DomainType}", batch.Domain.GetType().Name);
            duplicates = [];
            return false;
        }
    }

    private static bool TryAssignDuplicates(
        DomainInputBatch batch,
        IReadOnlyList<SceneItemSnapshot> duplicates,
        HashSet<Guid> assignedIds,
        SceneItemSnapshot[] ordered)
    {
        if (duplicates.Count != batch.Snapshots.Count)
        {
            return false;
        }

        for (int index = 0; index < duplicates.Count; ++index)
        {
            SceneItemSnapshot duplicate = duplicates[index];
            if (duplicate.Id == Guid.Empty
                || !batch.Domain.CanHandle(duplicate)
                || !assignedIds.Add(duplicate.Id))
            {
                return false;
            }

            ordered[batch.InputIndices[index]] = duplicate;
        }

        return true;
    }

    private static SceneItemSnapshotChange[] Reverse(IReadOnlyList<SceneItemSnapshotChange> changes)
    {
        var reversed = new SceneItemSnapshotChange[changes.Count];
        for (int index = 0; index < changes.Count; ++index)
        {
            reversed[index] = changes[^(index + 1)].Reverse();
        }

        return reversed;
    }

    private sealed class DomainInputBatch(ISceneItemDomain domain)
    {
        public ISceneItemDomain Domain { get; } = domain;
        public List<SceneItemSnapshot> Snapshots { get; } = [];
        public List<int> InputIndices { get; } = [];
    }

    private sealed class DomainChangeBatch(ISceneItemDomain domain)
    {
        public ISceneItemDomain Domain { get; } = domain;
        public List<SceneItemSnapshotChange> Changes { get; } = [];
    }
}
