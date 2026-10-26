using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Displays;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Configuration;
using Intoner.Services.Gpu;
using Intoner.UI.Performance;
using System.Numerics;
using ObjectPasteDestination = Intoner.Objects.Api.ObjectPasteDestination;

namespace Intoner.Objects.UI;

internal sealed class SceneWorkspace
{
    private readonly IObjectFolderService         _objectFolderService;
    private readonly IObjectLayoutManager         _layoutManager;
    private readonly IObjectRevisionTracker       _revisionTracker;
    private readonly IObjectSceneView             _sceneView;
    private readonly ISceneItemService            _sceneItemService;
    private readonly IHistoryCoordinator          _historyCoordinator;
    private readonly IObjectClipboardService      _objectClipboard;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly WindowsCaptureTargetService  _captureTargetService;
    private readonly EditorInteraction            _interaction;
    private readonly SceneEditorCommands          _sceneCommands;
    private readonly SceneListRow                 _sceneListRow;
    private readonly SceneItemControls            _sceneItems;
    private readonly EditorFilterBar              _filterBar;
    private readonly ObjectLibraryBrowser         _libraryBrowser;
    private readonly EditorColumnPanel            _columnPanel;
    private readonly HashSet<string>              _collapsedPlacedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>              _expandedPlacedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid>                _collapsedLayouts = [];
    private readonly EditorTabBar                 _layoutTabBar = new();
    private Guid? _viewLayoutId;
    private IReadOnlyList<EditorTabBar.Tab> _layoutTabs = [];
    private IReadOnlyList<ObjectCatalogFilterCount> _allKindCounts = [];
    private IReadOnlyList<EditorBadge> _headerCounts = [];
    private IReadOnlyList<EditorBadge> _compactHeaderCounts = [];
    private string _objectFilter = string.Empty;
    private ObjectKind? _objectKindFilter;
    private const string SceneMenuPopupId = "##sceneMenu";
    private SceneListSource _sceneListSource;
    private IReadOnlyList<SceneListState>? _sceneListState;
    private IReadOnlyList<Guid>? _selectableSceneRowOrder;
    private PlacedLayoutView _rowOrderView;
    private IReadOnlyDictionary<Guid, SceneItemSnapshot> _activeItems = new Dictionary<Guid, SceneItemSnapshot>();

    public SceneWorkspace(
        IObjectFolderService objectFolderService,
        IObjectLayoutManager layoutManager,
        IObjectRevisionTracker revisionTracker,
        IObjectSceneView sceneView,
        ISceneItemService sceneItemService,
        IHistoryCoordinator historyCoordinator,
        IObjectClipboardService objectClipboard,
        IIntonerConfigurationService configurationService,
        WindowsCaptureTargetService captureTargetService,
        EditorInteraction interaction,
        SceneEditorCommands sceneCommands,
        SceneListRow sceneListRow,
        SceneItemControls sceneItems,
        EditorFilterBar filterBar,
        ObjectLibraryBrowser libraryBrowser,
        EditorColumnPanel columnPanel)
    {
        _objectFolderService  = objectFolderService;
        _layoutManager        = layoutManager;
        _revisionTracker      = revisionTracker;
        _sceneView            = sceneView;
        _sceneItemService     = sceneItemService;
        _historyCoordinator   = historyCoordinator;
        _objectClipboard      = objectClipboard;
        _configurationService = configurationService;
        _captureTargetService = captureTargetService;
        _interaction          = interaction;
        _sceneCommands        = sceneCommands;
        _sceneListRow         = sceneListRow;
        _sceneItems           = sceneItems;
        _filterBar            = filterBar;
        _libraryBrowser       = libraryBrowser;
        _columnPanel          = columnPanel;
    }

    private void OpenClearPlacedObjectsDialog(SceneListState? owner)
    {
        _interaction.CommitPendingHistory();
        long persistentRevision = _sceneItemService.GetRevisions().Persistent;
        ObjectSceneItemSnapshot[] targets = _sceneItemService.GetPlacedItems()
            .OfType<ObjectSceneItemSnapshot>()
            .Where(item => owner is null || item.LayoutId == owner.LayoutId)
            .ToArray();

        _interaction.OpenDialog(EditorDialog.Request.TryConfirmation(
            "placed-objects-clear",
            "Clear Placed Objects",
            "Clear Objects",
            () => _historyCoordinator.TryClearPlacedObjects(persistentRevision, targets)) with
        {
            Icon = FontAwesomeIcon.Ban,
            ConfirmIcon = FontAwesomeIcon.Trash,
            Accent = ThemeColors.DimRed,
            Description = owner is null
                ? "This removes every placed object from the current workspace. You can undo the clear from History."
                : $"This removes every placed object from {owner.Name}. You can undo the clear from History.",
            FailureMessage = "The placed objects could not be cleared. The scene may have changed; close and reopen this dialog to try again.",
        });
    }

    internal sealed record PlacedTreeNode(
        Guid? LayoutId,
        string FolderKey,
        string DisplayLabel,
        string ColorValue,
        IReadOnlyList<ObjectSnapshot> AllObjects,
        IReadOnlyList<ObjectSnapshot> VisibleObjects,
        IReadOnlyList<PlacedTreeNode> Children)
    {
        public string Id { get; init; } = $"{LayoutId?.ToString("N") ?? "standalone"}:folder:{FolderKey}";
        public string RowId { get; } = $"placedFolderEntry:{FolderKey}";
        public bool AllVisible { get; init; }
        public bool AllLocked { get; init; }
        public string Detail { get; init; } = string.Empty;
        public SceneGroupSnapshot? SceneGroup { get; init; }
        public Vector4 Accent => SceneGroup is null ? FolderColorPicker.ResolveAccent(ColorValue) : EditorColors.GroupAccent;
        public IReadOnlyList<SceneItemSnapshot> AllItems { get; init; } = [];
        public SceneListRow.FolderAction[] Actions { get; init; } = [];
    }

    internal sealed record SceneListState(
        Guid? LayoutId,
        string Name,
        IReadOnlyList<SceneItemSnapshot> RootItems,
        IReadOnlyList<PlacedTreeNode> RootNodes,
        IReadOnlyList<string> Folders,
        IReadOnlyList<ObjectSnapshot> AllObjects)
    {
        public int Count { get; init; }
        public IReadOnlyList<ObjectCatalogFilterCount> KindCounts { get; init; } = [];
        public IReadOnlyList<EditorBadge> Badges { get; init; } = [];
        public IReadOnlyList<EditorBadge> ObjectCountBadges { get; init; } = [];
    }

    internal readonly record struct SceneListSource(
        IReadOnlyList<ObjectSnapshot> Objects,
        IReadOnlyList<DisplaySnapshot> Displays,
        IReadOnlySet<Guid> ActiveObjectIds,
        long PersistentRevision,
        long SavedLayoutsRevision,
        string Filter,
        ObjectKind? KindFilter,
        Vector4 Accent,
        Vector4 DisabledText);

    internal void DrawObjectListPanel(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyDictionary<Guid, SceneItemSnapshot> activeItems)
    {
        _activeItems = activeItems;
        IReadOnlyList<SceneListState> listState = GetSceneListState(objects, displays, activeObjectIds);
        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        float height = EditorLayout.ResolveRemainingRegionHeight();
        _columnPanel.DrawPanel("placedObjects", FontAwesomeIcon.FolderOpen, "Placed Objects",
            string.Empty,
            ThemeColors.AccentPrimary, width, height,
            header =>
            {
                float edge = EditorLayout.Scaled(30f);
                float right = header.Max.X - (header.Height - edge) * 0.5f;
                float left = right - edge;
                float badgeLeft = EditorColumnPanel.DrawHeaderBadges(header, "Placed Objects", _headerCounts, left, _compactHeaderCounts);
                ImGui.SetCursorScreenPos(new Vector2(left, header.Min.Y + (header.Height - edge) * 0.5f));
                DrawSceneHeaderActions(listState, edge);
                return badgeLeft;
            },
            contentHeight => DrawPlacedObjectsList(objects, displays, activeObjectIds, contentHeight),
            navigationWidth => DrawPlacedNavigation(listState, navigationWidth),
            contentHorizontalInset: EditorLayout.Scaled(4f));
    }

    private float DrawPlacedNavigation(IReadOnlyList<SceneListState> owners, float width)
    {
        Vector2 start = ImGui.GetCursorScreenPos();
        float inset = EditorLayout.Scaled(8f);
        float gap = EditorLayout.Scaled(4f);
        float innerWidth = MathF.Max(1f, width - inset * 2f);
        float searchY = start.Y + inset;
        PlacedLayoutView view = _configurationService.Current.Ui.PlacedLayoutView;
        if (view == PlacedLayoutView.Tabs && owners.Count > 1)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + inset, searchY));
            string selected = _layoutTabBar.Draw("placedLayoutTabs", _layoutTabs,
                _viewLayoutId?.ToString("N") ?? "standalone", innerWidth, ThemeColors.AccentPrimary, "Find layout");
            Guid? next = string.Equals(selected, "standalone", StringComparison.Ordinal) ? null : Guid.ParseExact(selected, "N");
            if (next != _viewLayoutId)
            {
                _viewLayoutId = next;
                _selectableSceneRowOrder = null;
            }
            searchY += EditorTabBar.Height + gap;
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X + inset, searchY));
        _ = EditorSearchField.Draw("objectListFilter", ref _objectFilter,
            new EditorSearchFieldOptions("Search placed objects", ThemeColors.AccentPrimary, Width: innerWidth));
        if (owners.Count == 1 && owners[0].Count == 0 && !_objectKindFilter.HasValue)
            return searchY + EditorSearchField.Height + gap - start.Y;
        ImGui.SetCursorScreenPos(new Vector2(start.X + inset, searchY + EditorSearchField.Height + gap));
        IReadOnlyList<ObjectCatalogFilterCount> counts = view == PlacedLayoutView.Tabs
            ? owners.First(owner => owner.LayoutId == _viewLayoutId).KindCounts
            : _allKindCounts;
        string nextFilter = _filterBar.DrawWrappedFilterButtons("placedKind", _objectKindFilter?.ToString() ?? string.Empty, counts, innerWidth);
        _objectKindFilter = Enum.TryParse(nextFilter, out ObjectKind kind) ? kind : null;
        return ImGui.GetCursorScreenPos().Y + gap - start.Y;
    }

    private void DrawSceneHeaderActions(IReadOnlyList<SceneListState> owners, float edge)
    {
        bool open = EditorIconButton.DrawToggle(
            "sceneMenu",
            FontAwesomeIcon.EllipsisH,
            new Vector2(edge),
            false,
            ThemeColors.AccentPrimary,
            ThemeColors.AccentPrimary,
            "Scene Menu");
        Vector2 menuAnchor = new(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);
        using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdown(SceneMenuPopupId, open, menuAnchor);
        if (!popup)
        {
            return;
        }

        SceneListState? owner = _configurationService.Current.Ui.PlacedLayoutView == PlacedLayoutView.Tabs
            ? owners.First(item => item.LayoutId == _viewLayoutId)
            : null;
        Guid? destination = owner is null ? _sceneView.GetCreationLayoutId() : owner.LayoutId;
        IReadOnlyList<ObjectSnapshot> objects = owner?.AllObjects ?? _sceneListSource.Objects;
        IEnumerable<SceneItemSnapshot> children = owner?.LayoutId is null
            ? objects.Concat<SceneItemSnapshot>(_sceneListSource.Displays)
            : objects;
        SceneSelectionMenu.DrawChildren(_interaction, children);

        EditorContextMenu.DrawSectionLabel("Paste");
        bool canPasteObjects = _objectClipboard.CanPasteObjects();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Paste,
                "Paste Objects",
                enabled: canPasteObjects,
                tooltip: canPasteObjects ? null : "Clipboard does not contain Intoner objects."))
        {
            _ = _sceneCommands.PasteObjectsFromClipboard(destination, ObjectPasteDestination.KeepOrganization);
        }

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.LevelDownAlt,
                "Paste Without Folders",
                enabled: canPasteObjects,
                tooltip: canPasteObjects ? null : "Clipboard does not contain Intoner objects."))
        {
            _ = _sceneCommands.PasteObjectsFromClipboard(destination, ObjectPasteDestination.Ungrouped);
        }

        EditorContextMenu.DrawSectionLabel("Create");
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Folder, "Create Folder"))
        {
            OpenCreateFolderDialog(destination);
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectGroup, "Create Group"))
            _ = _sceneCommands.TryCreateEmptyGroup(destination);

        bool displayToolsEnabled = owner?.LayoutId is null;
        string? displayToolsTooltip = displayToolsEnabled ? null : "Displays belong to Unassigned.";

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Desktop, "Create Display", enabled: displayToolsEnabled, tooltip: displayToolsTooltip))
        {
            _sceneCommands.AddDisplay();
        }

        EditorContextMenu.DrawSectionLabel("Scene Tools");
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Sync, "Refresh Capture Sources", enabled: displayToolsEnabled, tooltip: displayToolsTooltip))
        {
            _captureTargetService.RefreshTargets();
        }

        EditorContextMenu.DrawSectionLabel("Clear");
        bool hasPlacedObjects = objects.Count > 0 || _sceneView.GetPlacedGroups().Any(group => owner is null || group.LayoutId == owner.LayoutId);

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Trash, "Clear Placed Objects", enabled: hasPlacedObjects, color: ThemeColors.DimRed))
            OpenClearPlacedObjectsDialog(owner);
    }

    private IReadOnlyList<SceneListState> GetSceneListState(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlySet<Guid> activeObjectIds)
    {
        SceneListSource source = new(objects, displays, activeObjectIds,
            _sceneItemService.GetRevisions().Persistent, _revisionTracker.GetSavedLayoutsRevision(), _objectFilter, _objectKindFilter,
            ThemeColors.AccentPrimary, ThemeColors.TextDisabled);
        if (_sceneListState is not null && _sceneListSource == source)
        {
            return _sceneListState;
        }

        ILookup<Guid?, ObjectSnapshot> objectsByLayout = objects.ToLookup(static snapshot => snapshot.LayoutId);
        ILookup<Guid?, SceneGroupSnapshot> groupsByLayout = _sceneView.GetPlacedGroups().ToLookup(static group => group.LayoutId);
        List<SceneListState> owners = [];
        AddOwner(null, ObjectDestinationMenu.UnassignedLabel, displays);
        foreach (ObjectLayoutSnapshot layout in _layoutManager.GetEnabledLayouts())
        {
            AddOwner(layout.Id, layout.Name, []);
        }

        HashSet<string> knownFolderSet = new(StringComparer.OrdinalIgnoreCase);
        foreach (SceneListState owner in owners)
        {
            foreach (string folder in owner.Folders)
            {
                knownFolderSet.Add($"{owner.LayoutId?.ToString("N") ?? "standalone"}:folder:{folder}");
            }
            foreach (SceneGroupSnapshot group in groupsByLayout[owner.LayoutId])
                knownFolderSet.Add($"{owner.LayoutId?.ToString("N") ?? "standalone"}:group:{group.Id:N}");
        }

        _collapsedPlacedFolders.RemoveWhere(folder => !knownFolderSet.Contains(folder));
        _expandedPlacedGroups.RemoveWhere(group => !knownFolderSet.Contains(group));
        _collapsedLayouts.RemoveWhere(id => !owners.Any(owner => (owner.LayoutId ?? Guid.Empty) == id));
        if (!owners.Any(owner => owner.LayoutId == _viewLayoutId))
            _viewLayoutId = null;
        _layoutTabs = owners.Select(owner => new EditorTabBar.Tab(
            owner.LayoutId?.ToString("N") ?? "standalone", owner.LayoutId.HasValue ? FontAwesomeIcon.LayerGroup : FontAwesomeIcon.Cubes,
            owner.Name, owner.Count.ToString(), Badges: owner.ObjectCountBadges,
            Accent: owner.LayoutId.HasValue ? null : ThemeColors.AccentRose)).ToList();
        _allKindCounts = BuildKindCounts(objects);
        int itemCount = objects.Count + displays.Count;
        EditorBadge itemCountBadge = EditorBadge.Count(null, itemCount, "item", "items", ThemeColors.TextDisabled);
        _headerCounts = owners.Count > 1
            ? [itemCountBadge,
               EditorBadge.Count(null, owners.Count - 1, "layout", "layouts", ThemeColors.AccentPrimary) with { Tooltip = "Enabled layouts" }]
            : [itemCountBadge];
        _compactHeaderCounts = owners.Count > 1
            ? [EditorBadge.Label(itemCount.ToString(), FontAwesomeIcon.Cubes, ThemeColors.TextDisabled, "Placed items"),
               EditorBadge.Label((owners.Count - 1).ToString(), FontAwesomeIcon.LayerGroup, ThemeColors.AccentPrimary, "Enabled layouts")]
            : [EditorBadge.Label(itemCount.ToString(), FontAwesomeIcon.Cubes, ThemeColors.TextDisabled, "Placed items")];
        _sceneListState = owners;
        _sceneListSource = source;
        _selectableSceneRowOrder = null;
        return _sceneListState;

        void AddOwner(Guid? id, string name, IReadOnlyList<DisplaySnapshot> ownerDisplays)
        {
            IReadOnlyList<string> folders = _sceneView.GetPlacedFolders(id);
            List<ObjectSnapshot> ownerObjects = objectsByLayout[id].ToList();
            int count = ownerObjects.Count + ownerDisplays.Count;
            EditorBadge countBadge = EditorBadge.Count(null, count, "item", "items", ThemeColors.TextDisabled);
            owners.Add(BuildSceneListState(id, name, ownerObjects,
                ownerDisplays, folders, _objectFolderService.GetSceneFolderColors(id), activeObjectIds, _objectFilter, _objectKindFilter,
                groupsByLayout[id].ToArray()) with
            {
                Count = count,
                KindCounts = BuildKindCounts(ownerObjects),
                Badges = id.HasValue
                    ? [EditorBadge.Label("Layout", color: ThemeColors.AccentPrimary), countBadge]
                    : [countBadge],
                ObjectCountBadges = [EditorBadge.IconOnly(FontAwesomeIcon.Cubes, "Placed objects", string.Empty),
                    EditorBadge.Label(ownerObjects.Count.ToString(), color: ThemeColors.TextDisabled)],
            });
        }
    }

    private static IReadOnlyList<ObjectCatalogFilterCount> BuildKindCounts(IReadOnlyList<ObjectSnapshot> objects)
        => Enum.GetValues<ObjectKind>()
            .Select(kind => new ObjectCatalogFilterCount(kind.ToString(), objects.Count(snapshot => snapshot.Kind == kind)))
            .ToArray();

    private bool IsPlacedTreeNodeCollapsed(PlacedTreeNode node)
        => node.SceneGroup is null
            ? _collapsedPlacedFolders.Contains(node.Id)
            : !_expandedPlacedGroups.Contains(node.Id);

    private void TogglePlacedTreeNodeCollapsed(PlacedTreeNode node)
    {
        HashSet<string> states = node.SceneGroup is null ? _collapsedPlacedFolders : _expandedPlacedGroups;

        if (!states.Add(node.Id))
        {
            states.Remove(node.Id);
        }

        _selectableSceneRowOrder = null;
    }

    private void DrawPlacedObjectsList(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlySet<Guid> activeObjectIds,
        float height)
    {
        IReadOnlyList<SceneListState> owners = GetSceneListState(objects, displays, activeObjectIds);
        PlacedLayoutView view = _configurationService.Current.Ui.PlacedLayoutView;
        bool sections = view == PlacedLayoutView.Sections && owners.Count > 1;
        if (_rowOrderView != view)
        {
            _rowOrderView = view;
            _selectableSceneRowOrder = null;
        }
        IReadOnlyList<Guid> selectableRowOrder = _selectableSceneRowOrder ??= owners
            .Where(owner => sections ? !_collapsedLayouts.Contains(owner.LayoutId ?? Guid.Empty) : owner.LayoutId == _viewLayoutId)
            .SelectMany(BuildSelectableSceneRowOrder).ToList();
        SceneListRow.Metrics metrics = SceneListRow.ResolveMetrics(_configurationService.Current.Ui.PlacedListRowSize);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
        bool first = true;
        foreach (SceneListState owner in owners)
        {
            if (!sections && owner.LayoutId != _viewLayoutId)
                continue;

            using var id = ImRaii.PushId(owner.LayoutId?.ToString("N") ?? "standalone");
            if (!first)
                ImGui.Dummy(new Vector2(0f, EditorLayout.Scaled(4f)));
            first = false;
            if (sections && (DrawLayoutSection(owner) || owner.RootItems.Count == 0 && owner.RootNodes.Count == 0))
                continue;
            if (sections)
                ImGui.Dummy(new Vector2(0f, metrics.ItemSpacing));

            Vector2 bodyStart = ImGui.GetCursorScreenPos();
            using (ImRaii.PushIndent(sections ? EditorLayout.Scaled(16f) : 0f))
            {
                if (owner.RootItems.Count == 0 && owner.RootNodes.Count == 0)
                {
                    EditorEmptyState.Draw(_objectFilter.Length > 0 || _objectKindFilter.HasValue
                        ? "No matching items." : "No placed items.", height);
                }
                else
                {
                    UiVirtualList.Draw(owner.RootItems, UiVirtualListOptions.Rows(metrics.ItemHeight, metrics.ItemSpacing),
                        (item, _) => DrawSceneRootItem(item, activeObjectIds, selectableRowOrder, metrics));
                    if (owner.RootItems.Count > 0 && owner.RootNodes.Count > 0)
                        ImGui.Dummy(new Vector2(0f, metrics.ItemSpacing));
                    for (int index = 0; index < owner.RootNodes.Count; ++index)
                    {
                        DrawPlacedTree(owner.RootNodes[index], activeObjectIds,
                            selectableRowOrder, metrics, depth: 0);
                        if (index + 1 < owner.RootNodes.Count)
                            ImGui.Dummy(new Vector2(0f, metrics.ItemSpacing));
                    }
                }
            }
            if (sections)
            {
                float x = bodyStart.X + EditorLayout.Scaled(9f);
                Vector4 accent = owner.LayoutId.HasValue ? ThemeColors.AccentPrimary : ThemeColors.AccentRose;
                ImGui.GetWindowDrawList().AddLine(new Vector2(x, bodyStart.Y),
                    new Vector2(x, ImGui.GetCursorScreenPos().Y), ImGui.GetColorU32(accent with { W = 0.22f }), EditorLayout.Scaled(1f));
            }
        }
    }

    private bool DrawLayoutSection(SceneListState owner)
    {
        Guid id = owner.LayoutId ?? Guid.Empty;
        bool collapsed = _collapsedLayouts.Contains(id);
        Vector4 accent = owner.LayoutId.HasValue ? ThemeColors.AccentPrimary : ThemeColors.AccentRose;
        EditorListCard.Interaction interaction = _sceneListRow.DrawSection("layoutSection", owner.Name,
            owner.LayoutId.HasValue ? FontAwesomeIcon.LayerGroup : FontAwesomeIcon.Cubes, owner.Badges, collapsed, accent);
        if (interaction.Clicked)
        {
            if (!_collapsedLayouts.Add(id))
                _collapsedLayouts.Remove(id);
            collapsed = !collapsed;
            _selectableSceneRowOrder = null;
        }
        using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginForLastItem("##layoutSectionMenu");
        if (popup)
        {
            SceneSelectionMenu.DrawChildren(_interaction, owner.AllObjects);
            EditorContextMenu.DrawSeparator();

            if (EditorContextMenu.DrawItem(FontAwesomeIcon.Plus, "Create Folder"))
                OpenCreateFolderDialog(owner.LayoutId);
            if (EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectGroup, "Create Group"))
                _ = _sceneCommands.TryCreateEmptyGroup(owner.LayoutId);
            if (EditorContextMenu.DrawItem(FontAwesomeIcon.Paste, "Paste Objects", enabled: _objectClipboard.CanPasteObjects()))
                _ = _sceneCommands.PasteObjectsFromClipboard(owner.LayoutId, ObjectPasteDestination.KeepOrganization);
        }
        return collapsed;
    }

    private static SceneListState BuildSceneListState(
        Guid? layoutId,
        string name,
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors,
        IReadOnlySet<Guid> activeObjectIds,
        string filter,
        ObjectKind? kindFilter,
        IReadOnlyList<SceneGroupSnapshot> sceneGroups)
    {
        ILookup<Guid?, ObjectSnapshot> members = objects.ToLookup(static snapshot => snapshot.GroupId);
        ILookup<string, PlacedTreeNode> groupRows = sceneGroups
            .Select(group => BuildPlacedGroup(group, members[group.Id].ToArray(), activeObjectIds, filter, kindFilter))
            .OfType<PlacedTreeNode>()
            .ToLookup(static row => row.SceneGroup!.FolderPath, StringComparer.OrdinalIgnoreCase);
        List<SceneItemSnapshot> rootItems = objects
            .Where(static snapshot => !snapshot.GroupId.HasValue)
            .Where(static snapshot => string.IsNullOrWhiteSpace(snapshot.FolderPath))
            .Where(snapshot => SceneItemPresentation.MatchesObjectFilter(snapshot, activeObjectIds.Contains(snapshot.Id), filter, kindFilter))
            .Cast<SceneItemSnapshot>()
            .Concat(kindFilter.HasValue ? [] : SceneItemPresentation.FilterDisplays(displays, filter))
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToList();
        var groupedObjects = objects
            .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.FolderPath))
            .GroupBy(static snapshot => snapshot.FolderPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var orderedFolders = ObjectFolderUtility.ExpandFolders(
            folders.Concat(objects
                .Where(static snapshot => !string.IsNullOrWhiteSpace(snapshot.FolderPath))
                .Select(static snapshot => snapshot.FolderPath)).Concat(sceneGroups.Select(static group => group.FolderPath)));

        if (!HierarchyIndex<string, string>.TryCreate(
                orderedFolders,
                static folder => folder,
                ObjectFolderUtility.GetParentFolderPath,
                string.Empty,
                StringComparer.OrdinalIgnoreCase,
                out HierarchyIndex<string, string> hierarchy,
                out _))
        {
            return new SceneListState(layoutId, name, rootItems, [], folders, objects);
        }

        IReadOnlyDictionary<string, IReadOnlyList<ObjectSnapshot>> subtreeObjects =
            hierarchy.CollectSubtreeValues(folder => groupedObjects.GetValueOrDefault(folder) ?? []);
        ILookup<string, SceneGroupSnapshot> groupsByFolder = sceneGroups.ToLookup(static group => group.FolderPath, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, IReadOnlyList<SceneGroupSnapshot>> subtreeGroups =
            hierarchy.CollectSubtreeValues(folder => groupsByFolder[folder].ToArray());
        IReadOnlyList<PlacedTreeNode> rootFolders = hierarchy.Roots
            .Select(folder => BuildFolder(folder, ancestorMatchesSearch: false))
            .Where(static group => group is not null)
            .Cast<PlacedTreeNode>()
            .Concat(groupRows[string.Empty])
            .ToArray();
        return new SceneListState(layoutId, name, rootItems, rootFolders, folders, objects);

        PlacedTreeNode? BuildFolder(string folder, bool ancestorMatchesSearch)
        {
            IReadOnlyList<ObjectSnapshot> directObjects = groupedObjects.GetValueOrDefault(folder) ?? [];
            bool folderMatchesSearch = ancestorMatchesSearch
                || string.IsNullOrWhiteSpace(filter)
                || folder.Contains(filter, StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<ObjectSnapshot> visibleObjects = directObjects
                .Where(static snapshot => !snapshot.GroupId.HasValue)
                .Where(snapshot => SceneItemPresentation.MatchesObjectKindFilter(snapshot, kindFilter))
                .Where(snapshot => folderMatchesSearch
                    || SceneItemPresentation.MatchesObjectSearchFilter(snapshot, activeObjectIds.Contains(snapshot.Id), filter))
                .ToArray();
            IReadOnlyList<PlacedTreeNode> children = hierarchy.GetChildren(folder)
                .Select(child => BuildFolder(child, folderMatchesSearch))
                .Where(static child => child is not null)
                .Cast<PlacedTreeNode>()
                .Concat(groupRows[folder])
                .ToArray();
            bool includeEmptyFolder = !kindFilter.HasValue && folderMatchesSearch;
            if (visibleObjects.Count == 0 && children.Count == 0 && !includeEmptyFolder)
            {
                return null;
            }

            IReadOnlyList<ObjectSnapshot> allObjects = subtreeObjects[folder];
            int lockedCount = 0;
            int activeCount = 0;
            foreach (ObjectSnapshot snapshot in allObjects)
            {
                lockedCount += snapshot.Locked ? 1 : 0;
                activeCount += activeObjectIds.Contains(snapshot.Id) ? 1 : 0;
            }

            IReadOnlyList<SceneItemSnapshot> allItems = [.. allObjects, .. subtreeGroups[folder]];
            bool hasItems = allItems.Count > 0;
            bool allVisible = hasItems && allItems.All(static item => item.Visible);
            bool allLocked = hasItems && allItems.All(static item => item.Locked);
            string visibilityTooltip = "Folder is empty";
            Vector4 visibilityColor = ThemeColors.TextDisabled;

            if (hasItems)
            {
                visibilityTooltip = allVisible ? "Hide folder objects" : "Show folder objects";
                visibilityColor = allVisible ? ThemeColors.AccentGreen : ThemeColors.DimRed;
            }

            return new PlacedTreeNode(
                layoutId,
                folder,
                ObjectFolderUtility.GetFolderName(folder),
                ObjectFolderUtility.GetFolderColorValue(folderColors, folder),
                allObjects,
                visibleObjects,
                children)
            {
                AllItems = allItems,
                AllVisible = allVisible,
                AllLocked = allLocked,
                Detail = BuildPlacedObjectCountLabel(allObjects.Count, activeCount, lockedCount),
                Actions =
                [
                    new($"placedFolderVisibility:{folder}",
                        !hasItems || allVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                        visibilityTooltip, visibilityColor,
                        hasItems && !allVisible, hasItems),
                    new($"placedFolderLock:{folder}",
                        allLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock,
                        allLocked ? "Unlock folder objects" : "Lock folder objects",
                        ThemeColors.AccentYellow, allLocked, hasItems),
                ],
            };
        }
    }

    private static PlacedTreeNode? BuildPlacedGroup(
        SceneGroupSnapshot group,
        ObjectSnapshot[] members,
        IReadOnlySet<Guid> activeObjectIds,
        string filter,
        ObjectKind? kindFilter)
    {
        bool matches = string.IsNullOrWhiteSpace(filter) || group.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || group.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase);
        List<ObjectSnapshot> visible = [];
        int activeCount = 0;
        int lockedCount = 0;
        foreach (ObjectSnapshot member in members)
        {
            bool active = activeObjectIds.Contains(member.Id);
            activeCount += active ? 1 : 0;
            lockedCount += member.Locked ? 1 : 0;
            if (SceneItemPresentation.MatchesObjectFilter(member, active, matches ? string.Empty : filter, kindFilter))
                visible.Add(member);
        }

        if (visible.Count == 0 && (members.Length > 0 || kindFilter.HasValue || !matches))
            return null;

        return new PlacedTreeNode(group.LayoutId, group.Id.ToString("N"), group.Name, string.Empty, members, visible, [])
        {
            Id = $"{group.LayoutId?.ToString("N") ?? "standalone"}:group:{group.Id:N}",
            SceneGroup = group,
            AllItems = [.. members, group],
            AllVisible = group.Visible,
            AllLocked = group.Locked,
            Detail = BuildPlacedObjectCountLabel(members.Length, activeCount, lockedCount),
        };
    }

    private void DrawSceneRootItem(
        SceneItemSnapshot item,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<Guid> selectableRowOrder,
        SceneListRow.Metrics rowMetrics)
    {
        switch (item)
        {
            case DisplaySnapshot display:
                _sceneItems.DrawDisplayListEntry(display, selectableRowOrder, rowMetrics);
                return;
            case ObjectSnapshot snapshot:
                bool isActive = activeObjectIds.Contains(snapshot.Id);
                _ = _sceneItems.DrawPlacedObjectCard(
                    snapshot,
                    isActive,
                    _interaction.Selection.Contains(snapshot.Id),
                    selectableRowOrder,
                    rowMetrics);
                return;
            default:
                return;
        }
    }

    private void OpenCreateFolderDialog(Guid? layoutId, string? parentFolderPath = null)
    {
        string parentPath = ObjectFolderUtility.SanitizeFolderPath(parentFolderPath);
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "folder-libraryBrowser",
            "Create Folder",
            "Create Folder",
            input => _sceneCommands.TryCreateFolderWithHistory(layoutId, ObjectFolderUtility.CombineFolderPath(parentPath, input))) with
        {
            Icon = FontAwesomeIcon.FolderPlus,
            Accent = ThemeColors.AccentPrimary,
            Placeholder = "folder name",
            Detail = parentPath.Length > 0 ? $"Inside: {parentPath}" : string.Empty,
            Validate = input => ValidateFolderName(layoutId, input, parentPath),
        });
    }

    private void OpenRenameFolderDialog(Guid? layoutId, string folderPath)
    {
        string parentPath = ObjectFolderUtility.GetParentFolderPath(folderPath);
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "folder-rename",
            "Rename Folder",
            "Rename Folder",
            input => _sceneCommands.TryRenameFolderWithHistory(
                layoutId,
                folderPath,
                ObjectFolderUtility.CombineFolderPath(parentPath, input))) with
        {
            Icon = FontAwesomeIcon.Pen,
            Accent = ThemeColors.AccentPrimary,
            InitialValue = ObjectFolderUtility.GetFolderName(folderPath),
            Placeholder = "folder name",
            Detail = $"Current: {folderPath}",
            Validate = input => ValidateFolderName(layoutId, input, parentPath, folderPath),
        });
    }

    private string? ValidateFolderName(
        Guid? layoutId,
        string input,
        string? parentFolderPath = null,
        string? currentFolderPath = null)
    {
        string name = input.Trim();
        if (name.Length == 0)
        {
            return "Enter a folder name.";
        }

        if (name.Contains('/') || name.Contains('\\'))
        {
            return "Folder names cannot contain path separators.";
        }

        string folderPath = ObjectFolderUtility.CombineFolderPath(parentFolderPath, name);
        if (currentFolderPath is not null && string.Equals(
                folderPath,
                ObjectFolderUtility.SanitizeFolderPath(currentFolderPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return "Choose a different folder name.";
        }

        if (_sceneView.GetPlacedFolders(layoutId).Any(folder => string.Equals(
                folder,
                folderPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            return "A folder with this name already exists here.";
        }

        return null;
    }

    private SceneListRow.FolderResult DrawPlacedTreeRow(
        PlacedTreeNode group,
        SceneListRow.Metrics rowMetrics,
        bool collapsed,
        int depth)
    {
        if (group.SceneGroup is { } sceneGroup)
        {
            SceneListRow.FolderResult groupResult = _sceneItems.DrawGroupRow(sceneGroup, group.Detail, group.AllObjects.Count,
                _activeItems.ContainsKey(sceneGroup.Id), rowMetrics, collapsed, _selectableSceneRowOrder ?? [], depth);
            if (groupResult.Collapsed != collapsed)
                TogglePlacedTreeNodeCollapsed(group);
            return groupResult;
        }
        bool allVisible = group.AllVisible;
        bool allLocked = group.AllLocked;
        SceneListRow.Folder folder = new()
        {
            Id = group.RowId,
            Name = group.DisplayLabel,
            Detail = rowMetrics.Size == SceneListRowSize.Large
                ? group.Detail
                : string.Empty,
            Accent = group.Accent,
            ChildCount = group.AllObjects.Count,
            Collapsed = collapsed,
        };
        SceneListRow.FolderResult result = _sceneListRow.DrawFolder(
            folder, rowMetrics, (Workspace: this, Group: group),
            static context => context.Workspace.DrawPlacedFolderContextMenu(context.Group),
            group.Actions,
            leftInset: depth == 0 ? null : rowMetrics.ChildInset * depth,
            rightInset: depth == 0 ? null : rowMetrics.ChildRightInset);
        if (result.Collapsed != collapsed)
        {
            TogglePlacedTreeNodeCollapsed(group);
        }

        if (result.ClickedAction == 0)
        {
            _ = _sceneCommands.TrySetSceneItemsVisibleWithHistory(group.AllItems, !allVisible);
        }
        else if (result.ClickedAction == 1)
        {
            _ = _sceneCommands.TrySetSceneItemsLockedWithHistory(group.AllItems, !allLocked);
        }

        return result;
    }

    private void DrawPlacedFolderContextMenu(PlacedTreeNode group)
    {
        SceneSelectionMenu.DrawChildren(_interaction, group.AllObjects);

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Copy, "Copy Folder"))
        {
            _ = _objectClipboard.CopyFolder(group.LayoutId, group.FolderKey, group.AllItems);
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Cut, "Cut Folder"))
        {
            _ = _sceneCommands.TryCutPlacedFolderToClipboard(group.LayoutId, group.FolderKey, group.AllObjects);
        }

        bool canPasteIntoFolder = _objectClipboard.CanPasteObjects();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Paste,
                "Paste into Folder",
                enabled: canPasteIntoFolder,
                tooltip: canPasteIntoFolder
                    ? "Place clipboard objects in this folder."
                    : "Clipboard does not contain Intoner objects."))
        {
            _ = _sceneCommands.PasteObjectsFromClipboard(group.LayoutId, ObjectPasteDestination.Folder(group.FolderKey));
        }

        EditorContextMenu.DrawSeparator();

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.LayerGroup,
                "Save Folder to Library",
                enabled: _libraryBrowser.CanSavePlacedObjectsAsPrefab(group.AllObjects.Count)))
        {
            _libraryBrowser.OpenSaveLibraryPrefabDialog(group.LayoutId, group.FolderKey, group.DisplayLabel, group.AllObjects.Count);
        }

        DrawPlacedFolderMoveMenu(group);
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.FolderPlus, "New Folder Here..."))
        {
            OpenCreateFolderDialog(group.LayoutId, group.FolderKey);
        }
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectGroup, "New Group Here"))
            _ = _sceneCommands.TryCreateEmptyGroup(group.LayoutId, group.FolderKey);

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, "Rename Folder"))
        {
            OpenRenameFolderDialog(group.LayoutId, group.FolderKey);
        }

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Unlink,
                "Dissolve Folder",
                color: ThemeColors.DimRed))
        {
            _ = _sceneCommands.TryDissolveFolderWithHistory(group.LayoutId, group.FolderKey);
        }

        EditorContextMenu.DrawSectionLabel("Folder Color");
        if (FolderColorPicker.Draw($"placedFolderColor:{group.FolderKey}", group.ColorValue) is { } colorValue
         && _sceneCommands.TrySetFolderColorWithHistory(group.LayoutId, group.FolderKey, colorValue))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawPlacedTree(
        PlacedTreeNode group,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<Guid> selectableRowOrder,
        SceneListRow.Metrics rowMetrics,
        int depth)
    {
        SceneListRow.FolderResult row = DrawPlacedTreeRow(
            group,
            rowMetrics,
            IsPlacedTreeNodeCollapsed(group),
            depth);
        if (row.Collapsed || group.VisibleObjects.Count == 0 && group.Children.Count == 0)
        {
            return;
        }

        DrawPlacedTreeChildren(
            group,
            activeObjectIds,
            selectableRowOrder,
            rowMetrics,
            depth);
    }

    private void DrawPlacedTreeChildren(
        PlacedTreeNode group,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<Guid> selectableRowOrder,
        SceneListRow.Metrics rowMetrics,
        int depth)
        => SceneListRow.DrawFolderChildren(
            group.VisibleObjects, group.Children, rowMetrics,
            group.Accent, depth, rowMetrics.ItemHeight,
            (Workspace: this, Active: activeObjectIds, Order: selectableRowOrder, Metrics: rowMetrics),
            static (snapshot, childDepth, context) => context.Workspace._sceneItems.DrawPlacedObjectCard(snapshot,
                context.Active.Contains(snapshot.Id), context.Workspace._interaction.Selection.Contains(snapshot.Id),
                context.Order, context.Metrics, context.Metrics.ChildInset * childDepth, context.Metrics.ChildRightInset),
            static (child, childDepth, context) => context.Workspace.DrawPlacedTreeRow(child, context.Metrics,
                context.Workspace.IsPlacedTreeNodeCollapsed(child), childDepth),
            static (child, childDepth, context) => context.Workspace.DrawPlacedTreeChildren(child, context.Active, context.Order, context.Metrics, childDepth));

    private IReadOnlyList<Guid> BuildSelectableSceneRowOrder(SceneListState listState)
    {
        var itemIds = new List<Guid>(listState.RootItems.Count);
        foreach (SceneItemSnapshot item in listState.RootItems)
        {
            if (!item.Locked)
            {
                itemIds.Add(item.Id);
            }
        }

        foreach (PlacedTreeNode group in listState.RootNodes)
        {
            AddSelectableTreeItems(group, itemIds);
        }

        return itemIds;
    }

    private void AddSelectableTreeItems(PlacedTreeNode group, ICollection<Guid> itemIds)
    {
        if (group.SceneGroup is { Locked: false } sceneGroup)
            itemIds.Add(sceneGroup.Id);
        if (IsPlacedTreeNodeCollapsed(group))
        {
            return;
        }

        foreach (ObjectSnapshot snapshot in group.VisibleObjects.Where(static snapshot => !snapshot.Locked))
        {
            itemIds.Add(snapshot.Id);
        }

        foreach (PlacedTreeNode child in group.Children)
        {
            AddSelectableTreeItems(child, itemIds);
        }
    }

    private static string BuildPlacedObjectCountLabel(int objectCount, int activeCount, int lockedCount)
    {
        var placedLabel = objectCount == 1 ? "1 placed" : $"{objectCount} placed";
        var activeLabel = activeCount == 1 ? "1 active" : $"{activeCount} active";
        if (lockedCount == 0)
        {
            return $"{placedLabel} | {activeLabel}";
        }

        var lockedLabel = lockedCount == 1 ? "1 locked" : $"{lockedCount} locked";
        return $"{placedLabel} | {activeLabel} | {lockedLabel}";
    }

    private void DrawPlacedFolderMoveMenu(PlacedTreeNode group)
    {
        using EditorContextMenu.SubMenuScope moveMenu = EditorContextMenu.BeginSubMenu(
            $"placedFolderMove:{group.FolderKey}",
            FontAwesomeIcon.FolderOpen,
            "Move to Folder");
        if (!moveMenu)
        {
            return;
        }

        IReadOnlyList<string> folders = _sceneView.GetPlacedFolders(group.LayoutId)
            .Where(folder => !ObjectFolderUtility.IsSameOrDescendant(folder, group.FolderKey))
            .ToArray();
        string currentParent = ObjectFolderUtility.GetParentFolderPath(group.FolderKey);
        string folderName = ObjectFolderUtility.GetFolderName(group.FolderKey);
        FolderSelectionMenu.Draw(
            folders,
            currentParent.Length == 0,
            folder => string.Equals(folder, currentParent, StringComparison.OrdinalIgnoreCase),
            static folder => folder,
            ObjectFolderUtility.GetParentFolderPath,
            ObjectFolderUtility.GetFolderName,
            () => _ = _sceneCommands.TryRenameFolderWithHistory(group.LayoutId, group.FolderKey, folderName),
            folder => _ = _sceneCommands.TryRenameFolderWithHistory(
                group.LayoutId,
                group.FolderKey,
                ObjectFolderUtility.CombineFolderPath(folder, folderName)));
    }
}
