using Intoner.Displays;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Services;
using Intoner.Scene;

namespace Intoner.Objects.UI;

internal sealed class EditorSceneState
{
    private readonly ISceneItemService          _sceneItemService;
    private readonly EditorSelectionService     _selection;
    private readonly PlacementValidationService _placementValidationService;

    public IReadOnlyDictionary<Guid, PlacementEvaluation> PlacementEvaluations { get; private set; }
        = new Dictionary<Guid, PlacementEvaluation>();

    private EditorSceneData _editorSceneData = new();
    private EditorSceneData? _selectedScene;
    private int _selectedRevision;
    private IReadOnlyList<SceneItemSnapshot> _selectedItems = [];

    public EditorSceneState(
        ISceneItemService sceneItemService,
        EditorSelectionService selection,
        PlacementValidationService placementValidationService)
    {
        _sceneItemService           = sceneItemService;
        _selection                  = selection;
        _placementValidationService = placementValidationService;
    }

    public void EvaluatePlacement(IReadOnlyList<ObjectBoundsSnapshot> bounds)
        => PlacementEvaluations = _placementValidationService.Evaluate(GetEditorSceneData().CurrentObjects, bounds);

    internal sealed class EditorSceneData
    {
        public long PersistentRevision { get; init; } = long.MinValue;
        public long ActiveRevision { get; init; } = long.MinValue;
        public IReadOnlyList<ObjectSnapshot> Objects { get; init; } = [];
        public IReadOnlyList<ObjectSnapshot> CurrentObjects { get; init; } = [];
        public IReadOnlyList<DisplaySnapshot> Displays { get; init; } = [];
        public IReadOnlyList<ObjectSnapshot> ActiveObjects { get; init; } = [];
        public IReadOnlyDictionary<Guid, SceneItemSnapshot> ItemLookup { get; init; }
            = new Dictionary<Guid, SceneItemSnapshot>();
        public IReadOnlyDictionary<Guid, SceneItemSnapshot> ActiveItemLookup { get; init; }
            = new Dictionary<Guid, SceneItemSnapshot>();
        public IReadOnlySet<Guid> ActiveObjectIds { get; init; } = new HashSet<Guid>();
        public IReadOnlySet<Guid> SelectableItemIds { get; init; } = new HashSet<Guid>();

        public SceneItemSnapshot? FindCurrentItem(Guid itemId)
            => ActiveItemLookup.GetValueOrDefault(itemId) ?? ItemLookup.GetValueOrDefault(itemId);
    }

    internal IReadOnlyList<SceneItemSnapshot> ResolveSelectedCurrentItems()
    {
        if (!_selection.HasSelection)
        {
            _selectedScene = null;
            _selectedItems = [];
            return [];
        }

        EditorSceneData scene = GetEditorSceneData();

        if (_selectedScene == scene && _selectedRevision == _selection.Revision)
        {
            return _selectedItems;
        }

        var selectedSnapshots = new List<SceneItemSnapshot>(_selection.Count);
        foreach (SceneItemSnapshot selected in _selection.ResolveSelectedItems(scene.ItemLookup))
        {
            if (scene.FindCurrentItem(selected.Id) is { } snapshot)
            {
                selectedSnapshots.Add(snapshot);
            }
        }

        _selectedScene = scene;
        _selectedRevision = _selection.Revision;
        _selectedItems = selectedSnapshots.AsReadOnly();
        return _selectedItems;
    }

    internal EditorSceneData GetEditorSceneData()
    {
        while (true)
        {
            SceneRevisions revisions = _sceneItemService.GetRevisions();
            if (_editorSceneData.PersistentRevision == revisions.Persistent
                && _editorSceneData.ActiveRevision == revisions.Active)
                return _editorSceneData;

            bool persistentChanged = _editorSceneData.PersistentRevision != revisions.Persistent;
            IReadOnlyList<SceneItemSnapshot> items = persistentChanged ? _sceneItemService.GetPlacedItems() : [];
            IReadOnlyDictionary<Guid, SceneItemSnapshot> itemLookup = persistentChanged
                ? items.ToDictionary(static entry => entry.Id)
                : _editorSceneData.ItemLookup;
            IReadOnlyList<ObjectSnapshot> objects = persistentChanged ? [.. items.OfType<ObjectSnapshot>()] : _editorSceneData.Objects;
            IReadOnlyList<SceneItemSnapshot> activeItems = _sceneItemService.GetActiveItems();
            Dictionary<Guid, SceneItemSnapshot> activeItemLookup = new(activeItems.Count);
            List<ObjectSnapshot> activeObjects = new(Math.Min(objects.Count, activeItems.Count));
            HashSet<Guid> activeObjectIds = new(Math.Min(objects.Count, activeItems.Count));
            foreach (SceneItemSnapshot active in activeItems)
            {
                if (!itemLookup.ContainsKey(active.Id))
                    continue;
                activeItemLookup.Add(active.Id, active);
                if (active is ObjectSnapshot snapshot)
                {
                    activeObjects.Add(snapshot);
                    activeObjectIds.Add(snapshot.Id);
                }
            }

            SceneRevisions current = _sceneItemService.GetRevisions();
            if (current.Persistent != revisions.Persistent || current.Active != revisions.Active)
                continue;

            var currentObjects = new ObjectSnapshot[objects.Count];
            for (int index = 0; index < objects.Count; ++index)
            {
                ObjectSnapshot snapshot = objects[index];
                currentObjects[index] = activeItemLookup.TryGetValue(snapshot.Id, out SceneItemSnapshot? active)
                    ? (ObjectSnapshot)active
                    : snapshot;
            }

            if (persistentChanged)
                _selection.UpdateGroups(items);
            _editorSceneData = new EditorSceneData
            {
                PersistentRevision = revisions.Persistent,
                ActiveRevision = revisions.Active,
                Objects = objects,
                CurrentObjects = currentObjects,
                Displays = persistentChanged ? [.. items.OfType<DisplaySnapshot>()] : _editorSceneData.Displays,
                ActiveObjects = activeObjects,
                ItemLookup = itemLookup,
                ActiveItemLookup = activeItemLookup,
                ActiveObjectIds = activeObjectIds,
                SelectableItemIds = persistentChanged
                    ? items.Where(static snapshot => !snapshot.Locked).Select(static snapshot => snapshot.Id).ToHashSet()
                    : _editorSceneData.SelectableItemIds,
            };
            return _editorSceneData;
        }
    }

    internal bool HasSelectedClipboardObjects()
    {
        if (!_selection.HasSelection)
        {
            return false;
        }

        EditorSceneData scene = GetEditorSceneData();
        IReadOnlyList<Guid> ids = _selection.SelectedItemIds;
        for (int index = 0; index < ids.Count; ++index)
        {
            if (scene.FindCurrentItem(ids[index]) is not (ObjectSnapshot or SceneGroupSnapshot))
            {
                return false;
            }
        }

        return true;
    }
}
