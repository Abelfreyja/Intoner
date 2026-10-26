using Intoner.Scene;
using Intoner.Objects.Models;

namespace Intoner.Objects.UI.Services;

internal enum EditorSelectionRole
{
    None,
    Secondary,
    Primary,
}

internal sealed class EditorSelectionService
{
    private readonly HashSet<Guid> _selectedIds = [];
    private readonly List<Guid> _selectionOrder = [];
    private readonly Dictionary<Guid, Guid[]> _groupMembers = [];
    private readonly Dictionary<Guid, Guid> _memberGroups = [];
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
        => _selectedIds.Contains(id)
        || _memberGroups.TryGetValue(id, out Guid groupId) && _selectedIds.Contains(groupId);

    /// <summary> updates selection membership from the current persistent scene </summary>
    public void UpdateGroups(IReadOnlyList<SceneItemSnapshot> items)
    {
        _groupMembers.Clear();
        _memberGroups.Clear();
        ILookup<Guid?, ObjectSnapshot> members = items.OfType<ObjectSnapshot>()
            .Where(static snapshot => snapshot.GroupId.HasValue)
            .ToLookup(static snapshot => snapshot.GroupId);
        foreach (Guid groupId in items.OfType<SceneGroupSnapshot>().Select(static group => group.Id))
        {
            Guid[] ids = members[groupId].Select(static snapshot => snapshot.Id).ToArray();
            _groupMembers.Add(groupId, ids);
            foreach (Guid id in ids)
                _memberGroups.Add(id, groupId);
        }

        Guid[] normalized = _selectionOrder.Where(id => !_memberGroups.TryGetValue(id, out Guid groupId)
            || !_selectedIds.Contains(groupId)).ToArray();
        TryApplySelection(normalized);
    }

    /// <summary> resolves a world selection hit to its editing group while list children remain individually selectable </summary>
    public Guid ResolveWorldSelectionId(Guid id)
        => _memberGroups.GetValueOrDefault(id, id);

    public EditorSelectionRole GetRole(Guid id)
    {
        if (id == PrimaryItemId || _memberGroups.TryGetValue(id, out Guid groupId) && groupId == PrimaryItemId)
        {
            return EditorSelectionRole.Primary;
        }

        return Contains(id) ? EditorSelectionRole.Secondary : EditorSelectionRole.None;
    }

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
        List<Guid> ids = itemIds.Distinct().ToList();
        _rangeAnchorId = ids.Count > 0 ? ids[^1] : null;
        return TryApplySelection(ids);
    }

    /// <summary> toggles distinct ids in one selection update while retaining the order of unaffected items </summary>
    /// <param name="itemIds"> the ids to toggle, in selection order </param>
    /// <returns> true when the selection changed </returns>
    public bool TryToggleSelection(IEnumerable<Guid> itemIds)
    {
        List<Guid> toggledIds = itemIds.Distinct().ToList();
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
        foreach (Guid id in _selectionOrder)
        {
            if (!itemLookup.TryGetValue(id, out TSnapshot? snapshot))
            {
                continue;
            }
            if (_groupMembers.TryGetValue(id, out Guid[]? members))
            {
                int start = selectedItems.Count;
                foreach (Guid member in members)
                {
                    if (!itemLookup.TryGetValue(member, out TSnapshot? memberSnapshot))
                        break;
                    selectedItems.Add(memberSnapshot);
                }
                if (selectedItems.Count - start != members.Length)
                {
                    selectedItems.RemoveRange(start, selectedItems.Count - start);
                    continue;
                }
            }
            selectedItems.Add(snapshot);
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
        => _selectedIds.Contains(id)
            ? TryRemoveSelectedId(id)
            : TryAddSelectedId(id);

    private bool TryAddSelectedId(Guid id)
    {
        RemoveOverlappingSelection(id, _selectedIds, _selectionOrder);
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
    {
        List<Guid> normalized = new(ids.Count);
        HashSet<Guid> selected = [];
        foreach (Guid id in ids)
        {
            RemoveOverlappingSelection(id, selected, normalized);
            if (selected.Add(id))
                normalized.Add(id);
        }

        return !_selectionOrder.SequenceEqual(normalized) && ApplySelection(normalized);
    }

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

    private void RemoveOverlappingSelection(Guid id, HashSet<Guid> selected, List<Guid> order)
    {
        if (_memberGroups.TryGetValue(id, out Guid groupId) && selected.Remove(groupId))
        {
            order.Remove(groupId);
        }
        if (_groupMembers.TryGetValue(id, out Guid[]? members))
        {
            bool removed = false;
            foreach (Guid member in members)
                removed |= selected.Remove(member);
            if (removed)
                order.RemoveAll(member => _memberGroups.TryGetValue(member, out Guid owner) && owner == id);
        }
    }

    private void IncrementRevision()
        => _revision++;

    private bool HasSingleSelection(Guid id)
        => _selectionOrder.Count == 1 && _selectionOrder[0] == id;

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
