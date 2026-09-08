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
        => PlacementEvaluations = _placementValidationService.Evaluate(GetEditorSceneData().Objects, bounds);

    internal sealed class EditorSceneData
    {
        public long PersistentRevision { get; init; } = long.MinValue;
        public long ActiveRevision { get; init; } = long.MinValue;
        public IReadOnlyList<ObjectSnapshot> Objects { get; init; } = [];
        public IReadOnlyList<DisplaySnapshot> Displays { get; init; } = [];
        public IReadOnlyList<ObjectSnapshot> ActiveObjects { get; init; } = [];
        public IReadOnlyDictionary<Guid, SceneItemSnapshot> ItemLookup { get; init; }
            = new Dictionary<Guid, SceneItemSnapshot>();
        public IReadOnlyDictionary<Guid, SceneItemSnapshot> ActiveItemLookup { get; init; }
            = new Dictionary<Guid, SceneItemSnapshot>();
        public IReadOnlySet<Guid> ActiveObjectIds { get; init; } = new HashSet<Guid>();
        public IReadOnlySet<Guid> SelectableItemIds { get; init; } = new HashSet<Guid>();
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
            if (scene.ActiveItemLookup.TryGetValue(itemId, out SceneItemSnapshot? activeSnapshot))
            {
                selectedSnapshots.Add(activeSnapshot);
                continue;
            }

            if (scene.ItemLookup.TryGetValue(itemId, out SceneItemSnapshot? placedSnapshot))
            {
                selectedSnapshots.Add(placedSnapshot);
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

        IReadOnlyList<SceneItemSnapshot> items = _sceneItemService.GetPlacedItems();
        Dictionary<Guid, SceneItemSnapshot> itemLookup = items.ToDictionary(static entry => entry.Id);
        SceneItemSnapshot[] activeItems = _sceneItemService.GetActiveItems()
            .Where(snapshot => itemLookup.ContainsKey(snapshot.Id))
            .ToArray();
        ObjectSnapshot[] objects = [.. items.OfType<ObjectSnapshot>()];
        ObjectSnapshot[] activeObjects = [.. activeItems.OfType<ObjectSnapshot>()];
        _editorSceneData = new EditorSceneData
        {
            PersistentRevision = revisions.Persistent,
            ActiveRevision = revisions.Active,
            Objects = objects,
            Displays = [.. items.OfType<DisplaySnapshot>()],
            ActiveObjects = activeObjects,
            ItemLookup = itemLookup,
            ActiveItemLookup = activeItems.ToDictionary(static entry => entry.Id),
            ActiveObjectIds = activeObjects
                .Select(static entry => entry.Id)
                .ToHashSet(),
            SelectableItemIds = items
                .Where(static snapshot => !snapshot.Locked)
                .Select(static snapshot => snapshot.Id)
                .ToHashSet(),
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
        foreach (Guid itemId in _selection.SelectedItemIds)
        {
            if (scene.ActiveItemLookup.TryGetValue(itemId, out SceneItemSnapshot? activeItem))
            {
                if (activeItem is not ObjectSnapshot)
                {
                    return false;
                }

                continue;
            }

            if (!scene.ItemLookup.TryGetValue(itemId, out SceneItemSnapshot? placedItem)
                || placedItem is not ObjectSnapshot)
            {
                return false;
            }
        }

        return true;
    }
}
