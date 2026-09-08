using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
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
    private readonly IObjectSceneView             _sceneView;
    private readonly ISceneItemService            _sceneItemService;
    private readonly IHistoryCoordinator          _historyCoordinator;
    private readonly IObjectClipboardService      _objectClipboard;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly WindowsCaptureTargetService  _captureTargetService;
    private readonly EditorInteraction            _interaction;
    private readonly SceneEditorCommands          _sceneCommands;
    private readonly EditorOverlayLayer           _editorOverlayLayer;
    private readonly SceneListRow                 _sceneListRow;
    private readonly SceneItemControls            _sceneItems;
    private readonly EditorFilterBar              _filterBar;
    private readonly ObjectLibraryBrowser         _libraryBrowser;
    private readonly HashSet<string>              _collapsedPlacedFolders = new(StringComparer.OrdinalIgnoreCase);
    private string _objectFilter = string.Empty;
    private ObjectKind? _objectKindFilter;
    private const string SceneMenuPopupId = "##sceneMenu";
    private SceneListSource _sceneListSource;
    private SceneListState? _sceneListState;
    private IReadOnlyList<Guid>? _selectableSceneRowOrder;

    public SceneWorkspace(
        IObjectFolderService objectFolderService,
        IObjectSceneView sceneView,
        ISceneItemService sceneItemService,
        IHistoryCoordinator historyCoordinator,
        IObjectClipboardService objectClipboard,
        IIntonerConfigurationService configurationService,
        WindowsCaptureTargetService captureTargetService,
        EditorInteraction interaction,
        SceneEditorCommands sceneCommands,
        EditorOverlayLayer editorOverlayLayer,
        SceneListRow sceneListRow,
        SceneItemControls sceneItems,
        EditorFilterBar filterBar,
        ObjectLibraryBrowser libraryBrowser)
    {
        _objectFolderService  = objectFolderService;
        _sceneView            = sceneView;
        _sceneItemService     = sceneItemService;
        _historyCoordinator   = historyCoordinator;
        _objectClipboard      = objectClipboard;
        _configurationService = configurationService;
        _captureTargetService = captureTargetService;
        _interaction          = interaction;
        _sceneCommands        = sceneCommands;
        _editorOverlayLayer   = editorOverlayLayer;
        _sceneListRow         = sceneListRow;
        _sceneItems           = sceneItems;
        _filterBar            = filterBar;
        _libraryBrowser       = libraryBrowser;
    }

    private void OpenClearPlacedObjectsDialog()
    {
        _interaction.CommitPendingHistory();
        long persistentRevision = _sceneItemService.GetRevisions().Persistent;
        _interaction.OpenDialog(EditorDialog.Request.TryConfirmation(
            "placed-objects-clear",
            "Clear Placed Objects",
            "Clear Objects",
            () => _historyCoordinator.TryClearPlacedObjects(persistentRevision)) with
        {
            Icon = FontAwesomeIcon.Ban,
            ConfirmIcon = FontAwesomeIcon.Trash,
            Accent = ThemeColors.DimRed,
            Description = "This removes every placed object from the current workspace. You can undo the clear from History.",
            FailureMessage = "The placed objects could not be cleared. The scene may have changed; close and reopen this dialog to try again.",
        });
    }

    internal sealed record PlacedObjectFolderGroup(
        string FolderKey,
        string DisplayLabel,
        string ColorValue,
        IReadOnlyList<ObjectSnapshot> AllObjects,
        IReadOnlyList<ObjectSnapshot> VisibleObjects,
        IReadOnlyList<PlacedObjectFolderGroup> Children);

    internal sealed record SceneListState(
        IReadOnlyList<SceneItemSnapshot> RootItems,
        IReadOnlyList<PlacedObjectFolderGroup> RootFolders,
        IReadOnlyList<string> Folders);

    internal readonly record struct SceneListSource(
        IReadOnlyList<ObjectSnapshot> Objects,
        IReadOnlyList<DisplaySnapshot> Displays,
        IReadOnlySet<Guid> ActiveObjectIds,
        long PersistentRevision,
        string Filter,
        ObjectKind? KindFilter);

    internal void DrawObjectListPanel(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlySet<Guid> activeObjectIds)
    {
        SceneListState listState = GetSceneListState(objects, displays, activeObjectIds);
        DrawPlacedObjectsHero(objects, displays, activeObjectIds, listState.Folders.Count);
        DrawPlacedObjectsListCard(objects, displays, activeObjectIds);
    }

    private void DrawPlacedObjectsHero(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlySet<Guid> activeObjectIds,
        int folderCount)
    {
        var objectCount = objects.Count;
        var activeCount = objects.Count(snapshot => activeObjectIds.Contains(snapshot.Id));
        var lockedCount = objects.Count(static snapshot => snapshot.Locked);
        var countsByKind = objects
            .GroupBy(static snapshot => snapshot.Kind)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var currentFilter = _objectFilter;
        var currentKindFilter = _objectKindFilter;
        IReadOnlyList<EditorBadge> badges = BuildPlacedObjectBadges(
            objectCount,
            activeCount,
            lockedCount,
            displays.Count,
            folderCount);

        var actionButtonEdge = EditorIconButton.MeasureMaxEdge(FontAwesomeIcon.EllipsisH, FontAwesomeIcon.Ban);
        var actionWidth = EditorLayout.ResolveActionStripWidth(actionButtonEdge, 2);

        EditorCard.DrawPanelCard(
            "placed-objects-hero",
            ThemeColors.ButtonDefault with { W = 0.30f },
            ThemeColors.AccentPrimary with { W = 0.24f },
            8f * ImGuiHelpers.GlobalScale,
            new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale),
            () =>
            {
                Components.EditorCard.DrawCardHeader(
                    "placedObjectsHeroHeader",
                    FontAwesomeIcon.FolderOpen,
                    "Placed Objects",
                    badges,
                    ThemeColors.AccentPrimary,
                    () => DrawSceneHeaderActions(_sceneView.HasPersistedObjects(), actionButtonEdge),
                    actionWidth);
                _ = EditorSearchField.Draw(
                    "objectListFilter",
                    ref currentFilter,
                    new EditorSearchFieldOptions("Search placed objects", ThemeColors.AccentPrimary));
                currentKindFilter = DrawPlacedObjectKindFilters(currentKindFilter, countsByKind);
            });

        _objectFilter = currentFilter;
        _objectKindFilter = currentKindFilter;
    }

    private void DrawSceneHeaderActions(bool hasPersistedObjects, float edge)
    {
        bool open = EditorIconButton.DrawAccent(
            "sceneMenu",
            FontAwesomeIcon.EllipsisH,
            "Scene Menu",
            ThemeColors.AccentPrimary,
            edge);
        var menuAnchor = new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);

        ImGui.SameLine();
        using (ImRaii.Disabled(!hasPersistedObjects))
        {
            if (EditorIconButton.DrawAccent(
                    "sceneClearObjects",
                    FontAwesomeIcon.Ban,
                    "Clear Placed Objects",
                    ThemeColors.DimRed,
                    edge,
                    tooltipTitleColor: ThemeColors.DimRed))
            {
                OpenClearPlacedObjectsDialog();
            }
        }

        using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdown(SceneMenuPopupId, open, menuAnchor);
        if (!popup)
        {
            return;
        }

        EditorContextMenu.DrawFirstSectionLabel("Paste");
        bool canPasteObjects = _objectClipboard.CanPasteObjects();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Paste,
                "Paste Objects",
                enabled: canPasteObjects,
                tooltip: canPasteObjects ? null : "Clipboard does not contain Intoner objects."))
        {
            _ = _sceneCommands.PasteObjectsFromClipboard(ObjectPasteDestination.KeepOrganization);
        }

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.LevelDownAlt,
                "Paste Ungrouped",
                enabled: canPasteObjects,
                tooltip: canPasteObjects ? null : "Clipboard does not contain Intoner objects."))
        {
            _ = _sceneCommands.PasteObjectsFromClipboard(ObjectPasteDestination.Ungrouped);
        }

        EditorContextMenu.DrawSectionLabel("Create");
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Folder, "Create Folder"))
        {
            OpenCreateFolderDialog();
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Desktop, "Create Display"))
        {
            _sceneCommands.AddDisplay();
        }

        EditorContextMenu.DrawSectionLabel("Scene Tools");
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Sync, "Refresh Capture Sources"))
        {
            _captureTargetService.RefreshTargets();
        }
    }

    private ObjectKind? DrawPlacedObjectKindFilters(ObjectKind? currentFilter, IReadOnlyDictionary<ObjectKind, int> countsByKind)
    {
        var filterValue = currentFilter?.ToString() ?? string.Empty;
        var filterCounts = Enum.GetValues<ObjectKind>()
            .Select(kind => new ObjectCatalogFilterCount(kind.ToString(), countsByKind.GetValueOrDefault(kind)))
            .ToList();
        var nextFilterValue = _filterBar.DrawCatalogFilterButtons("placed_objects", filterValue, filterCounts);
        return Enum.TryParse<ObjectKind>(nextFilterValue, out var nextFilter)
            ? nextFilter
            : null;
    }

    private SceneListState GetSceneListState(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlySet<Guid> activeObjectIds)
    {
        SceneListSource source = new(objects, displays, activeObjectIds,
            _sceneItemService.GetRevisions().Persistent, _objectFilter, _objectKindFilter);
        if (_sceneListState is not null && _sceneListSource == source)
        {
            return _sceneListState;
        }

        IReadOnlyList<string> folders = _sceneView.GetPlacedFolders();
        HashSet<string> knownFolderSet = new(folders, StringComparer.OrdinalIgnoreCase);
        _collapsedPlacedFolders.RemoveWhere(folder => !knownFolderSet.Contains(folder));
        _sceneListState = BuildSceneListState(objects, displays, folders,
            _objectFolderService.GetSceneFolderColors(), activeObjectIds, _objectFilter, _objectKindFilter);
        _sceneListSource = source;
        _selectableSceneRowOrder = null;
        return _sceneListState;
    }

    private bool IsPlacedFolderCollapsed(string folderPath)
        => _collapsedPlacedFolders.Contains(folderPath);

    private void TogglePlacedFolderCollapsed(string folderPath)
    {
        if (!_collapsedPlacedFolders.Add(folderPath))
        {
            _collapsedPlacedFolders.Remove(folderPath);
        }

        _selectableSceneRowOrder = null;
    }

    private void DrawPlacedObjectsListCard(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlySet<Guid> activeObjectIds)
    {
        var padding = new Vector2(10f * ImGuiHelpers.GlobalScale, 8f * ImGuiHelpers.GlobalScale);
        var itemSpacingY = 2f * ImGuiHelpers.GlobalScale;
        var availableHeight = MathF.Max(1f, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var innerHeight = MathF.Max(1f, availableHeight - (padding.Y * 2f) - (itemSpacingY * 2f));
        var background = ThemeColors.ButtonDefault with { W = 0.24f };
        var rounding = 8f * ImGuiHelpers.GlobalScale;

        EditorCard.DrawPanelCard(
            "placed-objects-list",
            background,
            ThemeColors.AccentPrimary with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                SceneListState listState = GetSceneListState(objects, displays, activeObjectIds);
                IReadOnlyList<string> placedFolders = listState.Folders;
                IReadOnlyList<Guid> selectableRowOrder = _selectableSceneRowOrder ??= BuildSelectableSceneRowOrder(listState);
                var hasAnyPlacedEntries = objects.Count > 0 || placedFolders.Count > 0 || displays.Count > 0;

                if (listState.RootFolders.Count == 0
                    && listState.RootItems.Count == 0)
                {
                    EditorEmptyState.Draw(
                        !hasAnyPlacedEntries
                            ? "Create or import an object to utilize the list."
                            : "No placed items or folders match the current filter.",
                        innerHeight);
                    return;
                }

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = EditorScrollList.Begin(
                    "##placedObjectEntries",
                    new Vector2(0f, innerHeight),
                    _editorOverlayLayer.CreateScrollPanelOptions(background, rounding, ThemeColors.AccentPrimary));
                if (child)
                {
                    var topInset = 2f * ImGuiHelpers.GlobalScale;
                    ImGui.Dummy(new Vector2(0f, topInset));

                    SceneListRowSize rowSize = _configurationService.Current.Ui.PlacedListRowSize;
                    SceneListRow.Metrics rowMetrics = SceneListRow.ResolveMetrics(rowSize);
                    using var zeroItemSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 0f));
                    UiVirtualList.Draw(
                        listState.RootItems,
                        UiVirtualListOptions.Rows(rowMetrics.ItemHeight, rowMetrics.ItemSpacing),
                        (item, _) => DrawSceneRootItem(item, activeObjectIds, placedFolders, selectableRowOrder, rowMetrics));

                    if (listState.RootItems.Count > 0 && listState.RootFolders.Count > 0)
                    {
                        ImGui.Dummy(new Vector2(0f, rowMetrics.GroupSpacing));
                    }

                    for (var groupIndex = 0; groupIndex < listState.RootFolders.Count; ++groupIndex)
                    {
                        DrawPlacedObjectFolderTree(
                            listState.RootFolders[groupIndex],
                            activeObjectIds,
                            placedFolders,
                            selectableRowOrder,
                            rowMetrics,
                            depth: 0);

                        if (groupIndex + 1 < listState.RootFolders.Count)
                        {
                            ImGui.Dummy(new Vector2(0f, rowMetrics.GroupSpacing));
                        }
                    }

                }

            });
    }

    private static SceneListState BuildSceneListState(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<DisplaySnapshot> displays,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, string> folderColors,
        IReadOnlySet<Guid> activeObjectIds,
        string filter,
        ObjectKind? kindFilter)
    {
        List<SceneItemSnapshot> rootItems = objects
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
                .Select(static snapshot => snapshot.FolderPath)));

        if (!HierarchyIndex<string, string>.TryCreate(
                orderedFolders,
                static folder => folder,
                ObjectFolderUtility.GetParentFolderPath,
                string.Empty,
                StringComparer.OrdinalIgnoreCase,
                out HierarchyIndex<string, string> hierarchy,
                out _))
        {
            return new SceneListState(rootItems, [], folders);
        }

        IReadOnlyDictionary<string, IReadOnlyList<ObjectSnapshot>> subtreeObjects =
            hierarchy.CollectSubtreeValues(folder => groupedObjects.GetValueOrDefault(folder) ?? []);
        IReadOnlyList<PlacedObjectFolderGroup> rootFolders = hierarchy.Roots
            .Select(folder => BuildPlacedFolderGroup(
                folder,
                hierarchy,
                groupedObjects,
                subtreeObjects,
                folderColors,
                activeObjectIds,
                filter,
                kindFilter,
                ancestorMatchesSearch: false))
            .Where(static group => group is not null)
            .Cast<PlacedObjectFolderGroup>()
            .ToArray();
        return new SceneListState(rootItems, rootFolders, folders);
    }

    private static PlacedObjectFolderGroup? BuildPlacedFolderGroup(
        string folder,
        HierarchyIndex<string, string> hierarchy,
        IReadOnlyDictionary<string, List<ObjectSnapshot>> groupedObjects,
        IReadOnlyDictionary<string, IReadOnlyList<ObjectSnapshot>> subtreeObjects,
        IReadOnlyDictionary<string, string> folderColors,
        IReadOnlySet<Guid> activeObjectIds,
        string filter,
        ObjectKind? kindFilter,
        bool ancestorMatchesSearch)
    {
        IReadOnlyList<ObjectSnapshot> directObjects = groupedObjects.GetValueOrDefault(folder) ?? [];
        bool folderMatchesSearch = ancestorMatchesSearch
            || string.IsNullOrWhiteSpace(filter)
            || folder.Contains(filter, StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<ObjectSnapshot> visibleObjects = directObjects
            .Where(snapshot => SceneItemPresentation.MatchesObjectKindFilter(snapshot, kindFilter))
            .Where(snapshot => folderMatchesSearch
                || SceneItemPresentation.MatchesObjectSearchFilter(snapshot, activeObjectIds.Contains(snapshot.Id), filter))
            .ToArray();
        IReadOnlyList<PlacedObjectFolderGroup> children = hierarchy.GetChildren(folder)
            .Select(child => BuildPlacedFolderGroup(
                child,
                hierarchy,
                groupedObjects,
                subtreeObjects,
                folderColors,
                activeObjectIds,
                filter,
                kindFilter,
                folderMatchesSearch))
            .Where(static child => child is not null)
            .Cast<PlacedObjectFolderGroup>()
            .ToArray();
        bool includeEmptyFolder = !kindFilter.HasValue && folderMatchesSearch;
        if (visibleObjects.Count == 0 && children.Count == 0 && !includeEmptyFolder)
        {
            return null;
        }

        return new PlacedObjectFolderGroup(
            folder,
            ObjectFolderUtility.GetFolderName(folder),
            ObjectFolderUtility.GetFolderColorValue(folderColors, folder),
            subtreeObjects[folder],
            visibleObjects,
            children);
    }

    private void DrawSceneRootItem(
        SceneItemSnapshot item,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<string> placedFolders,
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
                    placedFolders,
                    selectableRowOrder,
                    rowMetrics);
                return;
            default:
                return;
        }
    }

    private void OpenCreateFolderDialog(string? parentFolderPath = null)
    {
        string parentPath = ObjectFolderUtility.SanitizeFolderPath(parentFolderPath);
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "folder-libraryBrowser",
            "Create Folder",
            "Create Folder",
            input => _sceneCommands.TryCreateFolderWithHistory(ObjectFolderUtility.CombineFolderPath(parentPath, input))) with
        {
            Icon = FontAwesomeIcon.FolderPlus,
            Accent = ThemeColors.AccentPrimary,
            Placeholder = "folder name",
            Detail = parentPath.Length > 0 ? $"Inside: {parentPath}" : string.Empty,
            Validate = input => ValidateFolderName(input, parentPath),
        });
    }

    private void OpenRenameFolderDialog(string folderPath)
    {
        string parentPath = ObjectFolderUtility.GetParentFolderPath(folderPath);
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
            "folder-rename",
            "Rename Folder",
            "Rename Folder",
            input => _sceneCommands.TryRenameFolderWithHistory(
                folderPath,
                ObjectFolderUtility.CombineFolderPath(parentPath, input))) with
        {
            Icon = FontAwesomeIcon.Pen,
            Accent = ThemeColors.AccentPrimary,
            InitialValue = ObjectFolderUtility.GetFolderName(folderPath),
            Placeholder = "folder name",
            Detail = $"Current: {folderPath}",
            Validate = input => ValidateFolderName(input, parentPath, folderPath),
        });
    }

    private string? ValidateFolderName(
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

        if (_sceneView.GetPlacedFolders().Any(folder => string.Equals(
                folder,
                folderPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            return "A folder with this name already exists here.";
        }

        return null;
    }

    private SceneListRow.FolderResult DrawPlacedObjectFolderRow(
        PlacedObjectFolderGroup group,
        IReadOnlySet<Guid> activeObjectIds,
        SceneListRow.Metrics rowMetrics,
        bool collapsed,
        int depth)
    {
        var allVisible = group.AllObjects.Count > 0 && group.AllObjects.All(static snapshot => snapshot.Visible);
        var allLocked = group.AllObjects.Count > 0 && group.AllObjects.All(static snapshot => snapshot.Locked);
        var accent = FolderColorPicker.ResolveAccent(group.ColorValue);
        var folderDetail = rowMetrics.Size == SceneListRowSize.Large
            ? BuildPlacedObjectCountLabel(
                group.AllObjects.Count,
                group.AllObjects.Count(snapshot => activeObjectIds.Contains(snapshot.Id)),
                group.AllObjects.Count(static snapshot => snapshot.Locked))
            : string.Empty;
        SceneListRow.Folder folder = new()
        {
            Id = $"placedFolderEntry:{group.FolderKey}",
            Name = group.DisplayLabel,
            Detail = folderDetail,
            Accent = accent,
            ChildCount = group.AllObjects.Count,
            Collapsed = collapsed,
        };
        EditorListCard.Interaction interaction = SceneListRow.DrawFolderInteraction(
            folder,
            rowMetrics,
            depth == 0 ? null : rowMetrics.ChildInset * depth,
            depth == 0 ? null : rowMetrics.ChildRightInset);

        using (var folderPopup = EditorContextMenu.BeginForLastItem($"##placedFolderContext:{group.FolderKey}"))
        {
            if (folderPopup)
            {
                bool hasSelectableChildren = group.AllObjects.Any(static snapshot => !snapshot.Locked);
                if (EditorContextMenu.DrawItem(FontAwesomeIcon.Check, "Select All Children", enabled: hasSelectableChildren))
                {
                    _interaction.SelectionChanged(_interaction.Selection.TryReplaceSelection(
                        group.AllObjects
                            .Where(static snapshot => !snapshot.Locked)
                            .Select(static snapshot => snapshot.Id)));
                }

                EditorContextMenu.DrawSeparator();
                if (EditorContextMenu.DrawItem(FontAwesomeIcon.Copy, "Copy Folder"))
                {
                    _ = _objectClipboard.CopyFolder(group.FolderKey, group.AllObjects);
                }

                if (EditorContextMenu.DrawItem(FontAwesomeIcon.Cut, "Cut Folder"))
                {
                    _ = _sceneCommands.TryCutPlacedFolderToClipboard(group.FolderKey, group.AllObjects);
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
                    _ = _sceneCommands.PasteObjectsFromClipboard(ObjectPasteDestination.Folder(group.FolderKey));
                }

                EditorContextMenu.DrawSeparator();

                if (EditorContextMenu.DrawItem(
                        FontAwesomeIcon.LayerGroup,
                        "Save Folder to Library",
                        enabled: _libraryBrowser.CanSavePlacedFolderAsPrefab(group.AllObjects.Count)))
                {
                    _libraryBrowser.OpenSaveLibraryPrefabDialog(group.FolderKey, group.DisplayLabel, group.AllObjects.Count);
                }

                DrawPlacedFolderMoveMenu(group);
                if (EditorContextMenu.DrawItem(FontAwesomeIcon.FolderPlus, "New Folder Here..."))
                {
                    OpenCreateFolderDialog(group.FolderKey);
                }

                if (EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, "Rename Folder"))
                {
                    OpenRenameFolderDialog(group.FolderKey);
                }

                EditorContextMenu.DrawSeparator();
                if (EditorContextMenu.DrawItem(
                        FontAwesomeIcon.Unlink,
                        "Dissolve Folder",
                        color: ThemeColors.DimRed))
                {
                    _ = _sceneCommands.TryDissolveFolderWithHistory(group.FolderKey);
                }

                EditorContextMenu.DrawSectionLabel("Folder Color");
                if (FolderColorPicker.Draw($"placedFolderColor:{group.FolderKey}", group.ColorValue) is { } colorValue
                 && _sceneCommands.TrySetFolderColorWithHistory(group.FolderKey, colorValue))
                {
                    ImGui.CloseCurrentPopup();
                }
            }
        }

        var min = interaction.Min;
        var max = interaction.Max;
        var padX = 6f * ImGuiHelpers.GlobalScale;
        var cursorAfterRow = ImGui.GetCursorPos();
        var buttonSize = SceneListRow.ResolveActionButtonEdge();
        var buttonGap = 2f * ImGuiHelpers.GlobalScale;
        var rowHeight = max.Y - min.Y;
        var buttonY = min.Y + ((rowHeight - buttonSize) * 0.5f);
        var buttonRight = max.X - padX;
        var lockButtonPos = new Vector2(buttonRight - buttonSize, buttonY);
        var visibilityButtonPos = new Vector2(lockButtonPos.X - buttonGap - buttonSize, buttonY);
        if (interaction.Clicked)
        {
            var buttonExtent = new Vector2(buttonSize);
            var clickedVisibilityButton = EditorInputUtility.IsMouseInside(visibilityButtonPos, visibilityButtonPos + buttonExtent);
            var clickedLockButton = EditorInputUtility.IsMouseInside(lockButtonPos, lockButtonPos + buttonExtent);
            if (!clickedVisibilityButton && !clickedLockButton)
            {
                TogglePlacedFolderCollapsed(group.FolderKey);
                collapsed = IsPlacedFolderCollapsed(group.FolderKey);
                folder = folder with { Collapsed = collapsed };
            }
        }

        _sceneListRow.DrawFolderChrome(folder, interaction);

        ImGui.SetCursorScreenPos(visibilityButtonPos);
        using (ImRaii.Disabled(group.AllObjects.Count == 0))
        {
            if (SceneListRow.DrawActionButton(
                    $"placedFolderVisibility:{group.FolderKey}",
                    allVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                    allVisible ? "Hide folder objects" : "Show folder objects",
                    allVisible ? ThemeColors.AccentGreen : ThemeColors.DimRed,
                    highlighted: !allVisible,
                    edge: buttonSize))
            {
                var nextVisible = !allVisible;
                var targetObjects = group.AllObjects
                    .Where(snapshot => snapshot.Visible != nextVisible)
                    .ToList();
                if (targetObjects.Count > 0)
                {
                    _ = _historyCoordinator.TryApplySelectedSnapshotUpdate(
                        SceneHistoryKind.Visibility,
                        nextVisible ? "Show Objects" : "Hide Objects",
                        targetObjects,
                        snapshot => snapshot with { Visible = nextVisible });
                }
            }
        }

        ImGui.SetCursorScreenPos(lockButtonPos);
        using (ImRaii.Disabled(group.AllObjects.Count == 0))
        {
            if (SceneListRow.DrawActionButton(
                    $"placedFolderLock:{group.FolderKey}",
                    allLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock,
                    allLocked ? "Unlock folder objects" : "Lock folder objects",
                    ThemeColors.AccentYellow,
                    highlighted: allLocked,
                    edge: buttonSize))
            {
                var nextLocked = !allLocked;
                var targetObjects = group.AllObjects
                    .Where(snapshot => snapshot.Locked != nextLocked)
                    .ToList();
                if (targetObjects.Count > 0)
                {
                    _ = _historyCoordinator.TryApplySelectedSnapshotUpdate(
                        SceneHistoryKind.Organization,
                        nextLocked ? "Lock Objects" : "Unlock Objects",
                        targetObjects,
                        snapshot => snapshot with { Locked = nextLocked });
                }
            }
        }

        ImGui.SetCursorPos(cursorAfterRow);
        var contentRight = visibilityButtonPos.X - (8f * ImGuiHelpers.GlobalScale);
        SceneListRow.DrawFolderContent(folder, rowMetrics, interaction, contentRight);
        return new SceneListRow.FolderResult(
            collapsed,
            new SceneListRow.RowGeometry(interaction.Min, interaction.Max));
    }

    private void DrawPlacedObjectFolderTree(
        PlacedObjectFolderGroup group,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<string> placedFolders,
        IReadOnlyList<Guid> selectableRowOrder,
        SceneListRow.Metrics rowMetrics,
        int depth)
    {
        SceneListRow.FolderResult row = DrawPlacedObjectFolderRow(
            group,
            activeObjectIds,
            rowMetrics,
            IsPlacedFolderCollapsed(group.FolderKey),
            depth);
        if (row.Collapsed || group.VisibleObjects.Count == 0 && group.Children.Count == 0)
        {
            return;
        }

        DrawPlacedObjectFolderChildren(
            group,
            activeObjectIds,
            placedFolders,
            selectableRowOrder,
            rowMetrics,
            depth);
    }

    private void DrawPlacedObjectFolderChildren(
        PlacedObjectFolderGroup group,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<string> placedFolders,
        IReadOnlyList<Guid> selectableRowOrder,
        SceneListRow.Metrics rowMetrics,
        int depth)
    {
        int childDepth = depth + 1;
        using SceneListRow.FolderChildrenScope children = SceneListRow.BeginFolderChildren(
            rowMetrics,
            FolderColorPicker.ResolveAccent(group.ColorValue),
            childDepth);
        int childCount = group.VisibleObjects.Count + group.Children.Count;
        int childIndex = group.VisibleObjects.Count;
        using UiVirtualList.Scope list = UiVirtualList.Begin(
            group.VisibleObjects.Count,
            UiVirtualListOptions.Rows(rowMetrics.ItemHeight, rowMetrics.ItemSpacing) with
            {
                DrawTrailingSpacing = group.Children.Count > 0,
            });
        while (list.Step())
        {
            for (int index = list.DisplayStart; index < list.DisplayEnd; ++index)
            {
                ObjectSnapshot snapshot = group.VisibleObjects[index];
                SceneListRow.RowGeometry geometry = _sceneItems.DrawPlacedObjectCard(
                    snapshot,
                    activeObjectIds.Contains(snapshot.Id),
                    _interaction.Selection.Contains(snapshot.Id),
                    placedFolders,
                    selectableRowOrder,
                    rowMetrics,
                    rowMetrics.ChildInset * childDepth,
                    rowMetrics.ChildRightInset);
                children.Add(geometry);
                list.FinishItem(index);
            }
        }

        foreach (PlacedObjectFolderGroup childGroup in group.Children)
        {
            SceneListRow.FolderResult childRow = DrawPlacedObjectFolderRow(
                childGroup,
                activeObjectIds,
                rowMetrics,
                IsPlacedFolderCollapsed(childGroup.FolderKey),
                childDepth);
            children.Add(childRow.Geometry);
            bool hasNext = ++childIndex < childCount;
            if (!childRow.Collapsed
             && (childGroup.VisibleObjects.Count > 0 || childGroup.Children.Count > 0))
            {
                DrawPlacedObjectFolderChildren(
                    childGroup,
                    activeObjectIds,
                    placedFolders,
                    selectableRowOrder,
                    rowMetrics,
                    childDepth);
            }

            children.AddSpacing(hasNext);
        }
    }

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

        foreach (PlacedObjectFolderGroup group in listState.RootFolders)
        {
            AddSelectableFolderItems(group, itemIds);
        }

        return itemIds;
    }

    private void AddSelectableFolderItems(PlacedObjectFolderGroup group, ICollection<Guid> itemIds)
    {
        if (IsPlacedFolderCollapsed(group.FolderKey))
        {
            return;
        }

        foreach (ObjectSnapshot snapshot in group.VisibleObjects.Where(static snapshot => !snapshot.Locked))
        {
            itemIds.Add(snapshot.Id);
        }

        foreach (PlacedObjectFolderGroup child in group.Children)
        {
            AddSelectableFolderItems(child, itemIds);
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

    private static IReadOnlyList<EditorBadge> BuildPlacedObjectBadges(
        int objectCount,
        int activeCount,
        int lockedCount,
        int displayCount,
        int folderCount)
    {
        List<EditorBadge> badges = new(5)
        {
            EditorBadge.Count(FontAwesomeIcon.Cube, objectCount, "placed", "placed"),
            EditorBadge.Count(FontAwesomeIcon.Running, activeCount, "active", "active"),
        };

        if (displayCount > 0)
        {
            badges.Add(EditorBadge.Count(FontAwesomeIcon.Desktop, displayCount, "display", "displays"));
        }

        if (folderCount > 0)
        {
            badges.Add(EditorBadge.Count(FontAwesomeIcon.Folder, folderCount, "folder", "folders"));
        }

        if (lockedCount > 0)
        {
            badges.Add(EditorBadge.Count(FontAwesomeIcon.Lock, lockedCount, "locked", "locked"));
        }

        return badges;
    }

    private void DrawPlacedFolderMoveMenu(PlacedObjectFolderGroup group)
    {
        using EditorContextMenu.SubMenuScope moveMenu = EditorContextMenu.BeginSubMenu(
            $"placedFolderMove:{group.FolderKey}",
            FontAwesomeIcon.FolderOpen,
            "Move to Folder");
        if (!moveMenu)
        {
            return;
        }

        IReadOnlyList<string> folders = _sceneView.GetPlacedFolders()
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
            () => _ = _sceneCommands.TryRenameFolderWithHistory(group.FolderKey, folderName),
            folder => _ = _sceneCommands.TryRenameFolderWithHistory(
                group.FolderKey,
                ObjectFolderUtility.CombineFolderPath(folder, folderName)));
    }
}
