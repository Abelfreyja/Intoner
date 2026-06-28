using Intoner.Objects.Models;

namespace Intoner.Objects.UI.Services;

internal sealed class SelectionService
{
    private readonly HashSet<Guid> _selectedIds = [];
    private readonly List<Guid> _selectionOrder = [];
    private int _revision = 1;

    /// <summary> gets the selected object ids in selection order </summary>
    public IReadOnlyList<Guid> SelectedObjectIds => _selectionOrder;

    /// <summary> gets the primary selected object id </summary>
    public Guid? PrimaryObjectId => _selectionOrder.Count > 0
        ? _selectionOrder[^1]
        : null;

    /// <summary> gets the selected object count </summary>
    public int Count => _selectionOrder.Count;

    /// <summary> gets whether any object is selected </summary>
    public bool HasSelection => _selectionOrder.Count > 0;

    /// <summary> gets the current selection revision </summary>
    public int Revision => _revision;

    /// <summary> checks whether one object id is selected </summary>
    /// <param name="id">the object id to check</param>
    /// <returns>true when the object is selected</returns>
    public bool Contains(Guid id)
        => _selectedIds.Contains(id);

    /// <summary> selects one object or toggles it into the current selection </summary>
    /// <param name="id">the object id to select</param>
    /// <param name="toggleSelection">whether to toggle instead of replacing the selection</param>
    /// <returns>true when the selection changed</returns>
    public bool TrySelect(Guid id, bool toggleSelection)
        => toggleSelection
            ? TryToggleSelection(id)
            : TrySelectOnly(id);

    /// <summary> replaces the current selection with the given ids </summary>
    /// <param name="objectIds">the ids to select</param>
    /// <returns>true when the selection changed</returns>
    public bool TryReplaceSelection(IEnumerable<Guid> objectIds)
        => TryApplySelection(BuildDistinctSelectionOrder(objectIds));

    /// <summary> clears the current selection </summary>
    /// <returns>true when the selection changed</returns>
    public bool TryClear()
    {
        if (_selectionOrder.Count == 0)
        {
            return false;
        }

        return ApplySelection([]);
    }

    /// <summary> removes selected ids that are no longer valid </summary>
    /// <param name="validObjectIds">the ids that can remain selected</param>
    /// <returns>true when the selection changed</returns>
    public bool TryPrune(IReadOnlySet<Guid> validObjectIds)
    {
        var changed = false;
        for (var index = _selectionOrder.Count - 1; index >= 0; --index)
        {
            var id = _selectionOrder[index];
            if (validObjectIds.Contains(id))
            {
                continue;
            }

            _selectionOrder.RemoveAt(index);
            _selectedIds.Remove(id);
            changed = true;
        }

        if (changed)
        {
            IncrementRevision();
        }

        return changed;
    }

    /// <summary> resolves the currently selected snapshots from one lookup </summary>
    /// <param name="objectLookup">the snapshot lookup keyed by id</param>
    /// <returns>the resolved selected snapshots in selection order</returns>
    public IReadOnlyList<ObjectSnapshot> ResolveSelectedObjects(IReadOnlyDictionary<Guid, ObjectSnapshot> objectLookup)
    {
        if (!HasSelection)
        {
            return [];
        }

        var selectedObjects = new List<ObjectSnapshot>(Count);
        foreach (var id in _selectionOrder)
        {
            if (objectLookup.TryGetValue(id, out var snapshot))
            {
                selectedObjects.Add(snapshot);
            }
        }

        return selectedObjects;
    }

    /// <summary> resolves the current primary selected snapshot from one lookup </summary>
    /// <param name="objectLookup">the snapshot lookup keyed by id</param>
    /// <returns>the resolved primary snapshot, or null when it is missing</returns>
    public ObjectSnapshot? ResolvePrimarySelectedObject(IReadOnlyDictionary<Guid, ObjectSnapshot> objectLookup)
    {
        var primaryObjectId = PrimaryObjectId;
        return primaryObjectId.HasValue && objectLookup.TryGetValue(primaryObjectId.Value, out var snapshot)
            ? snapshot
            : null;
    }

    /// <summary> resolves the active subset of a selected snapshot list </summary>
    /// <param name="selectedObjects">the selected snapshots in selection order</param>
    /// <param name="activeObjectIds">the ids that are currently active</param>
    /// <returns>the selected snapshots that are also active</returns>
    public IReadOnlyList<ObjectSnapshot> ResolveActiveSelectedObjects(IReadOnlyList<ObjectSnapshot> selectedObjects, IReadOnlySet<Guid> activeObjectIds)
    {
        if (selectedObjects.Count == 0)
        {
            return [];
        }

        var activeSelectedObjects = new List<ObjectSnapshot>(selectedObjects.Count);
        foreach (var snapshot in selectedObjects)
        {
            if (activeObjectIds.Contains(snapshot.Id))
            {
                activeSelectedObjects.Add(snapshot);
            }
        }

        return activeSelectedObjects;
    }

    private bool TrySelectOnly(Guid id)
        => HasSingleSelection(id)
            ? false
            : ApplySelection([id]);

    private bool TryToggleSelection(Guid id)
        => Contains(id)
            ? TryRemoveSelectedId(id)
            : TryAddSelectedId(id);

    private bool TryAddSelectedId(Guid id)
    {
        if (!_selectedIds.Add(id))
        {
            return false;
        }

        _selectionOrder.Add(id);
        IncrementRevision();
        return true;
    }

    private bool TryRemoveSelectedId(Guid id)
    {
        if (!_selectedIds.Remove(id))
        {
            return false;
        }

        _selectionOrder.Remove(id);
        IncrementRevision();
        return true;
    }

    private bool TryApplySelection(IReadOnlyList<Guid> ids)
        => HasSameSelectionOrder(ids)
            ? false
            : ApplySelection(ids);

    private bool ApplySelection(IReadOnlyList<Guid> ids)
    {
        _selectedIds.Clear();
        _selectionOrder.Clear();
        for (var index = 0; index < ids.Count; ++index)
        {
            var id = ids[index];
            _selectedIds.Add(id);
            _selectionOrder.Add(id);
        }

        IncrementRevision();
        return true;
    }

    private void IncrementRevision()
        => _revision++;

    private bool HasSingleSelection(Guid id)
        => _selectionOrder.Count == 1 && _selectionOrder[0] == id;

    private bool HasSameSelectionOrder(IReadOnlyList<Guid> ids)
    {
        if (_selectionOrder.Count != ids.Count)
        {
            return false;
        }

        for (var index = 0; index < ids.Count; ++index)
        {
            if (_selectionOrder[index] != ids[index])
            {
                return false;
            }
        }

        return true;
    }

    private static List<Guid> BuildDistinctSelectionOrder(IEnumerable<Guid> ids)
    {
        var nextIds = new List<Guid>();
        var nextSet = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (!nextSet.Add(id))
            {
                continue;
            }

            nextIds.Add(id);
        }

        return nextIds;
    }
}

