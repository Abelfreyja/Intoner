using Intoner.Scene;

namespace Intoner.Objects.UI.Services;

internal sealed class EditorSelectionService
{
    private readonly HashSet<Guid> _selectedIds = [];
    private readonly List<Guid> _selectionOrder = [];
    private Guid? _rangeAnchorId;
    private int _revision = 1;

    /// <summary> gets the selected item ids in selection order </summary>
    public IReadOnlyList<Guid> SelectedItemIds => _selectionOrder;

    /// <summary> gets the primary selected item id </summary>
    public Guid? PrimaryItemId => _selectionOrder.Count > 0
        ? _selectionOrder[^1]
        : null;

    /// <summary> gets the selected item count </summary>
    public int Count => _selectionOrder.Count;

    /// <summary> gets whether any item is selected </summary>
    public bool HasSelection => _selectionOrder.Count > 0;

    /// <summary> gets the current selection revision </summary>
    public int Revision => _revision;

    /// <summary> checks whether one item id is selected </summary>
    /// <param name="id">the item id to check</param>
    /// <returns>true when the item is selected</returns>
    public bool Contains(Guid id)
        => _selectedIds.Contains(id);

    /// <summary> selects one item or toggles it into the current selection </summary>
    /// <param name="id">the item id to select</param>
    /// <param name="toggleSelection">whether to toggle instead of replacing the selection</param>
    /// <returns>true when the selection changed</returns>
    public bool TrySelect(Guid id, bool toggleSelection)
    {
        _rangeAnchorId = id;
        return toggleSelection
            ? TryToggleSelection(id)
            : TrySelectOnly(id);
    }

    /// <summary> selects the visible range between the anchor and one item </summary>
    /// <param name="id">the range endpoint to select</param>
    /// <param name="orderedItemIds">the selectable item ids in visible row order</param>
    /// <param name="preserveSelection">whether to add the range to the current selection</param>
    /// <returns>true when the selection changed</returns>
    public bool TrySelectRange(Guid id, IReadOnlyList<Guid> orderedItemIds, bool preserveSelection)
    {
        var targetIndex = IndexOf(orderedItemIds, id);
        var anchorIndex = _rangeAnchorId.HasValue
            ? IndexOf(orderedItemIds, _rangeAnchorId.Value)
            : -1;
        if (targetIndex < 0)
        {
            return false;
        }

        if (anchorIndex < 0)
        {
            _rangeAnchorId = id;
            return TrySelectOnly(id);
        }

        return TryApplySelection(BuildRangeSelectionOrder(orderedItemIds, anchorIndex, targetIndex, id, preserveSelection));
    }

    /// <summary> focuses an unselected item without collapsing an existing selection that contains it </summary>
    /// <param name="id">the item that opened the context menu</param>
    /// <returns>true when the selection changed</returns>
    public bool TrySelectForContextMenu(Guid id)
    {
        if (Contains(id))
        {
            return false;
        }

        _rangeAnchorId = id;
        return TrySelectOnly(id);
    }

    /// <summary> replaces the current selection with the given ids </summary>
    /// <param name="itemIds">the ids to select</param>
    /// <returns>true when the selection changed</returns>
    public bool TryReplaceSelection(IEnumerable<Guid> itemIds)
    {
        var ids = BuildDistinctSelectionOrder(itemIds);
        _rangeAnchorId = ids.Count > 0 ? ids[^1] : null;
        return TryApplySelection(ids);
    }

    /// <summary> toggles distinct ids in one selection update while retaining the order of unaffected items </summary>
    /// <param name="itemIds"> the ids to toggle, in selection order </param>
    /// <returns> true when the selection changed </returns>
    public bool TryToggleSelection(IEnumerable<Guid> itemIds)
    {
        List<Guid> toggledIds = BuildDistinctSelectionOrder(itemIds);
        if (toggledIds.Count == 0)
        {
            return false;
        }

        HashSet<Guid> toggledSet = new(toggledIds);
        List<Guid> nextIds = new(_selectionOrder.Count + toggledIds.Count);
        nextIds.AddRange(_selectionOrder.Where(id => !toggledSet.Contains(id)));
        nextIds.AddRange(toggledIds.Where(id => !_selectedIds.Contains(id)));

        _rangeAnchorId = nextIds.Count > 0 ? nextIds[^1] : null;
        return TryApplySelection(nextIds);
    }

    /// <summary> clears the current selection </summary>
    /// <returns>true when the selection changed</returns>
    public bool TryClear()
    {
        _rangeAnchorId = null;
        if (_selectionOrder.Count == 0)
        {
            return false;
        }

        return ApplySelection([]);
    }

    /// <summary> removes selected ids that are no longer valid </summary>
    /// <param name="validItemIds">the ids that can remain selected</param>
    /// <returns>true when the selection changed</returns>
    public bool TryPrune(IReadOnlySet<Guid> validItemIds)
    {
        if (_rangeAnchorId.HasValue && !validItemIds.Contains(_rangeAnchorId.Value))
        {
            _rangeAnchorId = null;
        }

        var changed = false;
        for (var index = _selectionOrder.Count - 1; index >= 0; --index)
        {
            var id = _selectionOrder[index];
            if (validItemIds.Contains(id))
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
    /// <param name="itemLookup">the snapshot lookup keyed by id</param>
    /// <returns>the resolved selected snapshots in selection order</returns>
    public IReadOnlyList<TSnapshot> ResolveSelectedItems<TSnapshot>(IReadOnlyDictionary<Guid, TSnapshot> itemLookup)
        where TSnapshot : SceneItemSnapshot
    {
        if (!HasSelection)
        {
            return [];
        }

        var selectedItems = new List<TSnapshot>(Count);
        foreach (var id in _selectionOrder)
        {
            if (itemLookup.TryGetValue(id, out var snapshot))
            {
                selectedItems.Add(snapshot);
            }
        }

        return selectedItems;
    }

    /// <summary> resolves the current primary selected snapshot from one lookup </summary>
    /// <param name="itemLookup">the snapshot lookup keyed by id</param>
    /// <returns>the resolved primary snapshot, or null when it is missing</returns>
    public TSnapshot? ResolvePrimarySelectedItem<TSnapshot>(IReadOnlyDictionary<Guid, TSnapshot> itemLookup)
        where TSnapshot : SceneItemSnapshot
    {
        var primaryItemId = PrimaryItemId;
        return primaryItemId.HasValue && itemLookup.TryGetValue(primaryItemId.Value, out var snapshot)
            ? snapshot
            : null;
    }

    private bool TrySelectOnly(Guid id)
        => !HasSingleSelection(id) && ApplySelection([id]);

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
        => !HasSameSelectionOrder(ids) && ApplySelection(ids);

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

    private List<Guid> BuildRangeSelectionOrder(
        IReadOnlyList<Guid> orderedItemIds,
        int anchorIndex,
        int targetIndex,
        Guid targetId,
        bool preserveSelection)
    {
        var nextIds = preserveSelection
            ? new List<Guid>(_selectionOrder)
            : [];
        var nextSet = new HashSet<Guid>(nextIds);
        var startIndex = Math.Min(anchorIndex, targetIndex);
        var endIndex = Math.Max(anchorIndex, targetIndex);
        for (var index = startIndex; index <= endIndex; ++index)
        {
            var id = orderedItemIds[index];
            if (nextSet.Add(id))
            {
                nextIds.Add(id);
            }
        }

        nextIds.Remove(targetId);
        nextIds.Add(targetId);
        return nextIds;
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (var index = 0; index < ids.Count; ++index)
        {
            if (ids[index] == id)
            {
                return index;
            }
        }

        return -1;
    }
}
