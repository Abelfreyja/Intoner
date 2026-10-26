using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal sealed partial class ObjectLayoutManager
{
    public PersistentMutationStatus ReplaceStoredLayouts(IReadOnlyList<ObjectLayoutSnapshot> layouts,
        IReadOnlyList<Guid> enabledIds, Guid? creationId)
    {
        lock (_stateLock)
        {
            HashSet<Guid> ids = layouts.Select(static layout => layout.Id).ToHashSet();

            if (!TryValidateLayoutSet(layouts, out _) || ids.Count != layouts.Count
                || enabledIds.Distinct().Count() != enabledIds.Count || enabledIds.Any(id => !ids.Contains(id))
                || creationId.HasValue && !enabledIds.Contains(creationId.Value))
            {
                return PersistentMutationStatus.InvalidRequest;
            }

            List<ObjectSavedLayoutChange> changes = [];
            foreach (ObjectLayoutSnapshot layout in layouts)
            {
                _layouts.TryGetValue(layout.Id, out ObjectLayoutSnapshot? before);

                if (before is null || !LayoutsMatch(before, layout))
                {
                    changes.Add(new ObjectSavedLayoutChange(before, layout));
                }
            }

            foreach (Guid id in _layoutOrder.Where(id => !ids.Contains(id)))
            {
                changes.Add(new ObjectSavedLayoutChange(_layouts[id], null));
            }

            return CommitLayoutChanges(changes, layouts.Select(static layout => layout.Id).ToArray(), enabledIds, creationId);
        }
    }

    private PersistentMutationStatus CommitLayoutChanges(IReadOnlyList<ObjectSavedLayoutChange> changes,
        IReadOnlyList<Guid> order, IReadOnlyList<Guid> enabledIds, Guid? creationId)
    {
        bool selectionChanged = _creationLayoutId != creationId || !_enabledLayoutIds.SequenceEqual(enabledIds);
        bool orderChanged = !_layoutOrder.SequenceEqual(order);
        List<ObjectSavedLayoutChange> attempted = [];
        try
        {
            foreach (ObjectSavedLayoutChange change in changes)
            {
                bool stored;

                if (change.After is { } after)
                {
                    stored = _layoutStore.TrySaveLayout(after);

                    if (stored)
                    {
                        attempted.Add(change);
                    }
                }
                else
                {
                    attempted.Add(change);
                    stored = _layoutStore.TryDeleteLayout(change.Id);
                }

                if (!stored)
                {
                    return RestoreLayoutFiles(attempted)
                        ? PersistentMutationStatus.StorageFailed : PersistentMutationStatus.RecoveryRequired;
                }
            }

            if (selectionChanged && !StoreWorkspaceLayouts(enabledIds, creationId))
            {
                return RestoreLayoutFiles(attempted)
                    ? PersistentMutationStatus.StorageFailed : PersistentMutationStatus.RecoveryRequired;
            }
        }
        catch
        {
            return RestoreLayoutFiles(attempted)
                ? PersistentMutationStatus.StorageFailed : PersistentMutationStatus.RecoveryRequired;
        }

        foreach (ObjectSavedLayoutChange change in changes)
        {
            if (change.After is { } after)
            {
                _layouts[change.Id] = after;
            }
            else
            {
                _layouts.Remove(change.Id);
            }
        }

        _layoutOrder.Clear();
        _layoutOrder.AddRange(order);
        _enabledLayoutIds.Clear();
        _enabledLayoutIds.AddRange(enabledIds);
        _creationLayoutId = creationId;
        _layoutCounter = Math.Max(_layoutCounter, _layouts.Count);

        if (changes.Count > 0 || selectionChanged || orderChanged)
        {
            _revisionTracker.IncrementSavedLayouts();
        }

        return PersistentMutationStatus.Success;
    }

    private bool RestoreLayoutFiles(IReadOnlyList<ObjectSavedLayoutChange> attempted)
    {
        bool restored = true;
        for (int index = attempted.Count - 1; index >= 0; --index)
        {
            ObjectSavedLayoutChange change = attempted[index];
            try
            {
                restored &= change.Before is { } before
                    ? _layoutStore.TrySaveLayout(before)
                    : _layoutStore.TryDeleteLayout(change.Id);
            }
            catch
            {
                restored = false;
            }
        }

        return restored;
    }
}
