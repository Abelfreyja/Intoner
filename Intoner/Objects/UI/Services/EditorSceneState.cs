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
            return [];
        }

        EditorSceneData scene = GetEditorSceneData();
        var selectedSnapshots = new List<SceneItemSnapshot>(_selection.Count);
        foreach (Guid itemId in _selection.SelectedItemIds)
        {
            if (scene.FindCurrentItem(itemId) is { } snapshot)
            {
                selectedSnapshots.Add(snapshot);
            }
        }

        return selectedSnapshots;
    }

    internal EditorSceneData GetEditorSceneData()
    {
        SceneRevisions revisions = _sceneItemService.GetRevisions();
        if (_editorSceneData.PersistentRevision == revisions.Persistent
            && _editorSceneData.ActiveRevision == revisions.Active)
        {
            return _editorSceneData;
        }

        bool persistentChanged = _editorSceneData.PersistentRevision != revisions.Persistent;
        IReadOnlyList<SceneItemSnapshot> items = persistentChanged ? _sceneItemService.GetPlacedItems() : [];
        IReadOnlyDictionary<Guid, SceneItemSnapshot> itemLookup = persistentChanged
            ? items.ToDictionary(static entry => entry.Id)
            : _editorSceneData.ItemLookup;
        SceneItemSnapshot[] activeItems = _sceneItemService.GetActiveItems()
            .Where(snapshot => itemLookup.ContainsKey(snapshot.Id))
            .ToArray();
        IReadOnlyList<ObjectSnapshot> objects = persistentChanged ? [.. items.OfType<ObjectSnapshot>()] : _editorSceneData.Objects;
        ObjectSnapshot[] activeObjects = [.. activeItems.OfType<ObjectSnapshot>()];
        Dictionary<Guid, SceneItemSnapshot> activeItemLookup = activeItems.ToDictionary(static entry => entry.Id);
        var currentObjects = new ObjectSnapshot[objects.Count];
        for (int index = 0; index < objects.Count; ++index)
        {
            ObjectSnapshot snapshot = objects[index];
            currentObjects[index] = activeItemLookup.TryGetValue(snapshot.Id, out SceneItemSnapshot? active)
                ? (ObjectSnapshot)active
                : snapshot;
        }

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
            ActiveObjectIds = activeObjects
                .Select(static entry => entry.Id)
                .ToHashSet(),
            SelectableItemIds = persistentChanged
                ? items.Where(static snapshot => !snapshot.Locked).Select(static snapshot => snapshot.Id).ToHashSet()
                : _editorSceneData.SelectableItemIds,
        };
        return _editorSceneData;
    }

    internal IReadOnlyList<ObjectSnapshot> ResolveSelectedClipboardObjects()
    {
        IReadOnlyList<SceneItemSnapshot> selectedItems = ResolveSelectedCurrentItems();
        ObjectSnapshot[] selectedObjects = selectedItems.OfType<ObjectSnapshot>().ToArray();
        return selectedObjects.Length == selectedItems.Count ? selectedObjects : [];
    }

    internal bool HasSelectedClipboardObjects()
    {
        if (!_selection.HasSelection)
        {
            return false;
        }

        EditorSceneData scene = GetEditorSceneData();
        return _selection.SelectedItemIds.All(itemId => scene.FindCurrentItem(itemId) is ObjectSnapshot);
    }
}
