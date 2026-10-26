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
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class SceneItemControls
{
    private readonly IHistoryCoordinator     _historyCoordinator;
    private readonly IObjectKindService      _objectKindService;
    private readonly IObjectClipboardService _objectClipboard;
    private readonly IObjectLayoutManager    _layoutManager;
    private readonly IObjectSceneView        _sceneView;
    private readonly EditorInteraction       _interaction;
    private readonly EditorSceneState        _sceneState;
    private readonly SceneEditorCommands     _sceneCommands;
    private readonly SceneListRow            _sceneListRow;
    private readonly IFurnitureStainService  _furnitureStainService;
    private readonly ObjectLibraryBrowser    _libraryBrowser;

    private readonly List<EditorIconButton.Item> _selectedActions = [];
    private readonly Action<string> _executeSelectedObjectAction;

    public SceneItemControls(
        IHistoryCoordinator historyCoordinator,
        IObjectKindService objectKindService,
        IObjectClipboardService objectClipboard,
        EditorInteraction interaction,
        EditorSceneState sceneState,
        SceneEditorCommands sceneCommands,
        SceneListRow sceneListRow,
        IFurnitureStainService furnitureStainService,
        IObjectLayoutManager layoutManager,
        IObjectSceneView sceneView,
        ObjectLibraryBrowser libraryBrowser)
    {
        _historyCoordinator    = historyCoordinator;
        _objectKindService     = objectKindService;
        _objectClipboard       = objectClipboard;
        _layoutManager         = layoutManager;
        _sceneView             = sceneView;
        _interaction           = interaction;
        _sceneState            = sceneState;
        _sceneCommands         = sceneCommands;
        _sceneListRow          = sceneListRow;
        _furnitureStainService = furnitureStainService;
        _libraryBrowser        = libraryBrowser;

        _executeSelectedObjectAction = ExecuteSelectedObjectAction;
    }


    internal void DrawDisplayListEntry(
        DisplaySnapshot display,
        IReadOnlyList<Guid> selectableRowOrder,
        SceneListRow.Metrics rowMetrics)
    {
        string sourceDetail = SceneItemPresentation.BuildDisplaySourceStatus(display, includeKind: true);
        SceneListRow.Item row = new()
        {
            Id = $"displayEntry:{display.Id:N}",
            Name = display.Name,
            Detail = sourceDetail,
            TypeLabel = "Display",
            TypeIcon = FontAwesomeIcon.Desktop,
            Accent = ThemeColors.AccentPrimary,
            Emphasized = true,
            Visible = display.Visible,
            Locked = display.Locked,
            Selected = _interaction.Selection.Contains(display.Id),
        };
        DrawItemRow(display, row, rowMetrics, selectableRowOrder);
    }

    internal SceneListRow.RowGeometry DrawPlacedObjectCard(
        ObjectSnapshot snapshot,
        bool isActive,
        bool selected,
        IReadOnlyList<Guid> selectableRowOrder,
        SceneListRow.Metrics rowMetrics,
        float? leftInset = null,
        float? rightInset = null)
    {
        SceneListRow.ColorMarker? colorMarker = TryGetPlacedObjectColorMarker(snapshot, out SceneListRow.ColorMarker resolvedMarker)
            ? resolvedMarker
            : null;
        SceneListRow.Item row = new()
        {
            Id = $"placedObjectEntry:{snapshot.Id}",
            Name = snapshot.Name,
            Detail = ObjectSnapshotUtility.GetAssetName(snapshot),
            TypeLabel = _objectKindService.GetDisplayName(snapshot.Kind),
            TypeIcon = SceneItemPresentation.ResolveObjectKindIcon(snapshot.Kind),
            Status = SceneListRow.Status.Activity(isActive),
            Accent = ThemeColors.AccentPrimary,
            Emphasized = isActive,
            Visible = snapshot.Visible,
            Locked = snapshot.Locked,
            Selected = selected,
            ColorMarker = colorMarker,
            LeftInset = leftInset,
            RightInset = rightInset,
        };
        return DrawItemRow(snapshot, row, rowMetrics, selectableRowOrder);
    }

    private SceneListRow.RowGeometry DrawItemRow(SceneItemSnapshot snapshot, SceneListRow.Item row,
        SceneListRow.Metrics rowMetrics, IReadOnlyList<Guid> selectableRowOrder)
    {
        EditorListCard.Interaction interaction = SceneListRow.DrawInteraction(row, rowMetrics);
        HandleContextMenuSelection(snapshot, interaction.RightClicked);
        using (EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem($"##{row.Id}:context"))
        {
            if (contextMenu)
            {
                DrawSceneItemContextMenu(snapshot, ResolveContextMenuItems(snapshot));
            }
        }

        _sceneListRow.DrawContent(row, rowMetrics, interaction);
        HandleSceneListSelection(snapshot, interaction.Clicked, selectableRowOrder);

        return new SceneListRow.RowGeometry(interaction.Min, interaction.Max);
    }

    private void HandleSceneListSelection(
        SceneItemSnapshot snapshot,
        bool clicked,
        IReadOnlyList<Guid> selectableRowOrder)
    {
        if (!clicked || snapshot.Locked)
        {
            return;
        }

        ImGuiIOPtr io = ImGui.GetIO();
        bool changed = io.KeyShift
            ? _interaction.Selection.TrySelectRange(snapshot.Id, selectableRowOrder, preserveSelection: io.KeyCtrl)
            : _interaction.Selection.TrySelect(snapshot.Id, toggleSelection: io.KeyCtrl);
        _interaction.SelectionChanged(changed);
    }

    internal SceneListRow.FolderResult DrawGroupRow(SceneGroupSnapshot group, string detail, int memberCount, bool isActive,
        SceneListRow.Metrics metrics, bool collapsed, IReadOnlyList<Guid> rowOrder, int depth)
    {
        SceneListRow.Folder row = new()
        {
            Id = $"sceneGroup:{group.Id:N}", Name = group.Name, Detail = detail,
            Status = SceneListRow.Status.Activity(isActive),
            Accent = EditorColors.GroupAccent, TypeIcon = FontAwesomeIcon.ObjectGroup, TypeLabel = "Group",
            ChildCount = memberCount, Collapsed = collapsed, Selectable = true,
            Selected = _interaction.Selection.Contains(group.Id),
            Visible = group.Visible, Locked = group.Locked,
        };
        SceneListRow.FolderResult result = _sceneListRow.DrawFolder(row, metrics, group,
            snapshot => DrawSceneItemContextMenu(snapshot, ResolveContextMenuItems(snapshot)),
            leftInset: depth == 0 ? null : metrics.ChildInset * depth,
            rightInset: depth == 0 ? null : metrics.ChildRightInset);
        HandleContextMenuSelection(group, result.RightClicked);
        HandleSceneListSelection(group, result.Clicked, rowOrder);
        return result;
    }

    private void HandleContextMenuSelection(SceneItemSnapshot invokedItem, bool rightClicked)
    {
        if (rightClicked && !invokedItem.Locked)
        {
            _interaction.SelectionChanged(_interaction.Selection.TrySelectForContextMenu(invokedItem.Id));
        }
    }

    private IReadOnlyList<SceneItemSnapshot> ResolveContextMenuItems(SceneItemSnapshot invokedItem)
    {
        if (!_interaction.Selection.Contains(invokedItem.Id))
        {
            if (invokedItem is SceneGroupSnapshot group)
                return [.. _sceneState.GetEditorSceneData().Objects.Where(snapshot => snapshot.GroupId == group.Id), group];
            return [invokedItem];
        }

        IReadOnlyList<SceneItemSnapshot> selectedItems = _sceneState.ResolveSelectedCurrentItems();
        return selectedItems.Count > 0
            ? selectedItems
            : [invokedItem];
    }

    private void DrawGroupContextActions(IReadOnlyList<SceneItemSnapshot> contextItems)
    {
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectGroup, "Group Objects",
                enabled: SceneEditorCommands.CanGroupItems(contextItems)))
            _ = _sceneCommands.TryGroupItems(contextItems);

        bool hasGroups = contextItems.Any(static item => item is SceneGroupSnapshot);
        if (contextItems.Count > 0 && contextItems.All(static item => item is ObjectSnapshot))
        {
            IReadOnlyList<SceneGroupSnapshot> targets = _sceneView.GetPlacedGroups();
            using (EditorContextMenu.SubMenuScope add = EditorContextMenu.BeginSubMenu("addToGroup", FontAwesomeIcon.ObjectGroup, "Add To Group",
                enabled: targets.Count > 0, tooltip: targets.Count == 0 ? "No groups exist. Use Group Objects to create one from the selection." : null))
            {
                if (add)
                {
                    foreach (SceneGroupSnapshot target in targets)
                    {
                        string? unavailableReason = SceneEditorCommands.GetAddToGroupUnavailableReason(contextItems, target);
                        if (EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectGroup, target.Name,
                                enabled: unavailableReason is null, id: $"addToGroup:{target.Id:N}", tooltip: unavailableReason))
                            _ = _sceneCommands.TryAddToGroup(contextItems, target);
                    }
                }
            }
            if (contextItems.Any(static snapshot => snapshot is ObjectSnapshot { GroupId: not null })
                && EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectUngroup, "Remove From Group"))
                _ = _sceneCommands.TryRemoveFromGroups(contextItems);
        }
        if (hasGroups && EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectUngroup, "Ungroup"))
            _ = _sceneCommands.TryUngroupItems(contextItems);
        EditorContextMenu.DrawSeparator();
    }

    private void DrawGroupContextMenu(SceneGroupSnapshot group, IReadOnlyList<SceneItemSnapshot> contextItems)
    {
        ObjectSnapshot[] members = _sceneState.GetEditorSceneData().Objects.Where(snapshot => snapshot.GroupId == group.Id).ToArray();
        SceneSelectionMenu.DrawChildren(_interaction, members);
        EditorContextMenu.DrawSeparator();
        DrawSceneItemClipboardActions(contextItems);
        string? unavailableReason = _sceneCommands.GetPasteIntoGroupUnavailableReason(group);
        bool canPaste = unavailableReason is null && _objectClipboard.CanPasteObjects(requireObjects: true);
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Paste, "Paste into Group", enabled: canPaste,
                tooltip: unavailableReason ?? (canPaste
                    ? "Place clipboard objects in this group without moving them. Copied folders and groups are not retained."
                    : "Clipboard does not contain Intoner objects.")))
            _ = _sceneCommands.PasteObjectsIntoGroup(group);
        DrawSceneItemDuplicateAction(contextItems);
        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.LayerGroup, "Save Group to Library",
                enabled: _libraryBrowser.CanSavePlacedObjectsAsPrefab(members.Length)))
            _libraryBrowser.OpenSaveGroupAsPrefabDialog(group, members.Length);
        DrawSceneItemDestinationMenu(group, contextItems);
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, "Rename Group"))
        {
            _interaction.OpenDialog(EditorDialog.Request.TextInput("group-rename", "Rename Group", "Rename",
                input => _historyCoordinator.TryApplySceneChanges(SceneHistoryKind.Organization, "Rename Group",
                    [group], [group with { Name = input.Trim() }], [group.Id])) with
            {
                InitialValue = group.Name, Placeholder = "group name", Icon = FontAwesomeIcon.ObjectGroup,
                Accent = EditorColors.GroupAccent,
                Validate = input => string.IsNullOrWhiteSpace(input) ? "Enter a group name." : null,
            });
        }
        EditorContextMenu.DrawSeparator();
        DrawSceneItemVisibilityActions(contextItems);
        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.ObjectUngroup, "Ungroup"))
            _ = _sceneCommands.TryUngroupItems(contextItems);
        DrawSceneItemDeleteAction(contextItems);
    }

    private void DrawSceneItemContextMenu(
        SceneItemSnapshot invokedItem,
        IReadOnlyList<SceneItemSnapshot> contextItems)
    {
        if (invokedItem is SceneGroupSnapshot group)
        {
            DrawGroupContextMenu(group, contextItems);
            return;
        }
        if (invokedItem is ObjectSnapshot)
        {
            List<ObjectSnapshot> selectedObjects = contextItems.OfType<ObjectSnapshot>().ToList();
            DrawSceneItemDestinationMenu(invokedItem, contextItems);
            bool canUnassignCollection = SceneEditorCommands.HasAnyObjectCollectionAssignment(selectedObjects);
            string unassignCollectionLabel = selectedObjects.Count == 1
                ? "Remove from Collection"
                : "Remove from Collections";
            if (EditorContextMenu.DrawItem(
                    FontAwesomeIcon.Unlink,
                    unassignCollectionLabel,
                    enabled: canUnassignCollection,
                    tooltip: canUnassignCollection
                        ? null
                        : "The selected objects have no collection assignment")
                && canUnassignCollection)
            {
                _sceneCommands.UnassignObjectCollections(selectedObjects);
            }
        }

        if (invokedItem is DisplaySnapshot)
        {
            bool canMoveToPlayer = contextItems.Count == 1;
            if (EditorContextMenu.DrawItem(FontAwesomeIcon.Running, "Move To Player", enabled: canMoveToPlayer)
                && canMoveToPlayer)
            {
                _ = _historyCoordinator.TryMoveItemToPlayer(contextItems[0].Id);
            }
        }

        EditorContextMenu.DrawSeparator();

        DrawSceneItemVisibilityActions(contextItems);
        EditorContextMenu.DrawSeparator();
        DrawSceneItemDuplicateAction(contextItems);
        DrawSceneItemClipboardActions(contextItems);
        EditorContextMenu.DrawSeparator();

        if (invokedItem is ObjectSnapshot)
            DrawGroupContextActions(contextItems);

        DrawSceneItemDeleteAction(contextItems);
    }

    private void DrawSceneItemVisibilityActions(IReadOnlyList<SceneItemSnapshot> contextItems)
    {
        bool allVisible = contextItems.All(static item => item.Visible);
        bool nextVisible = !allVisible;
        if (EditorContextMenu.DrawItem(
                nextVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                ResolveContextActionTitle(nextVisible ? "Show" : "Hide", contextItems)))
        {
            _ = _sceneCommands.TrySetSceneItemsVisibleWithHistory(contextItems, nextVisible);
        }

        bool allLocked = contextItems.All(static item => item.Locked);
        bool nextLocked = !allLocked;
        if (EditorContextMenu.DrawItem(
                nextLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock,
                ResolveContextActionTitle(nextLocked ? "Lock" : "Unlock", contextItems)))
        {
            _ = _sceneCommands.TrySetSceneItemsLockedWithHistory(contextItems, nextLocked);
        }
    }

    private void DrawSceneItemDuplicateAction(IReadOnlyList<SceneItemSnapshot> contextItems)
    {
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Copy,
                ResolveContextActionTitle("Duplicate", contextItems)))
        {
            _ = _historyCoordinator.TryDuplicateSceneItems(contextItems);
        }
    }

    private void DrawSceneItemClipboardActions(IReadOnlyList<SceneItemSnapshot> contextItems)
    {
        bool canCopyObjects = contextItems.All(static item => item is ObjectSnapshot or SceneGroupSnapshot);
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Copy,
                ResolveContextActionTitle("Copy", contextItems, objects: true),
                enabled: canCopyObjects)
            && canCopyObjects)
        {
            _ = _objectClipboard.CopyObjects(contextItems);
        }

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Cut,
                ResolveContextActionTitle("Cut", contextItems, objects: true),
                enabled: canCopyObjects)
            && canCopyObjects)
        {
            _ = _sceneCommands.CutObjectsToClipboard(contextItems);
        }
    }

    private void DrawSceneItemDeleteAction(IReadOnlyList<SceneItemSnapshot> contextItems)
    {
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Trash,
                ResolveContextActionTitle("Delete", contextItems),
                color: ThemeColors.DimRed))
        {
            _ = _historyCoordinator.TryRemoveSceneItems(contextItems);
        }
    }

    private static string ResolveContextActionTitle(string action, IReadOnlyList<SceneItemSnapshot> items, bool objects = false)
    {
        if (SceneGroupUtility.ResolveSelectedGroup(items) is not null)
            return $"{action} Group";
        if (objects && items.All(static item => item is ObjectSnapshot))
            return items.Count == 1 ? $"{action} Object" : $"{action} Objects";
        return SceneEditorCommands.ResolveSceneItemActionTitle(action, items.Count);
    }

    private void DrawSceneItemDestinationMenu(
        SceneItemSnapshot invokedObject,
        IReadOnlyList<SceneItemSnapshot> selectedObjects)
    {
        if (selectedObjects.Any(static item => item is not (ObjectSnapshot or SceneGroupSnapshot)))
            return;
        Guid? owner = ObjectFolderUtility.GetItemLayoutId(invokedObject);
        using EditorContextMenu.SubMenuScope assignFolder = EditorContextMenu.BeginSubMenu(
            $"placedObjectDestination:{invokedObject.Id}",
            FontAwesomeIcon.FolderOpen,
            "Move To");
        if (!assignFolder)
        {
            return;
        }

        string? selectedFolder = ResolveCommonFolderPath(selectedObjects);
        IReadOnlyList<string> localFolders = _sceneView.GetPlacedFolders(owner);
        if (localFolders.Count > 0)
        {
            using var folderIds = ImRaii.PushId("currentLayoutFolders");
            FolderSelectionMenu.DrawPaths(localFolders, selectedFolder,
                folder => SelectDestination(owner, folder));
            EditorContextMenu.DrawSeparator();
        }

        ObjectDestinationMenu.Draw(
            _layoutManager.GetEnabledLayouts(),
            _sceneView.GetPlacedFolders,
            owner,
            selectedFolder,
            SelectDestination);

        void SelectDestination(Guid? layoutId, string folderPath)
            => _ = _sceneCommands.TrySetObjectDestinationWithHistory(selectedObjects, layoutId, folderPath);
    }

    private static string? ResolveCommonFolderPath(IReadOnlyList<SceneItemSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return null;
        }

        string folderPath = ObjectFolderUtility.GetItemFolderPath(snapshots[0]);
        for (var index = 1; index < snapshots.Count; ++index)
        {
            if (ObjectFolderUtility.GetItemLayoutId(snapshots[index]) != ObjectFolderUtility.GetItemLayoutId(snapshots[0])
                || !string.Equals(folderPath, ObjectFolderUtility.GetItemFolderPath(snapshots[index]), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return folderPath;
    }

    private bool TryGetPlacedObjectColorMarker(ObjectSnapshot snapshot, out SceneListRow.ColorMarker marker)
    {
        switch (snapshot.Model)
        {
            case BgObjectModel bgObjectModel when !IsApproximatelyDefault(bgObjectModel.DyeColor, Vector4.One):
                string dyeColorLabel = FormatColorLabel(bgObjectModel.DyeColor);
                marker = new SceneListRow.ColorMarker(
                    ThemeColors.Color(
                        Math.Clamp(bgObjectModel.DyeColor.X, 0f, 1f),
                        Math.Clamp(bgObjectModel.DyeColor.Y, 0f, 1f),
                        Math.Clamp(bgObjectModel.DyeColor.Z, 0f, 1f),
                        1f),
                    dyeColorLabel,
                    $"Dye Color: {dyeColorLabel}");
                return true;

            case FurnitureModel furnitureModel when furnitureModel.Color.UseCustomColor:
                var customColorLabel = FormatByteColorLabel(furnitureModel.Color.CustomColor);
                marker = new SceneListRow.ColorMarker(
                    ThemeColors.Color(
                        Math.Clamp(furnitureModel.Color.CustomColor.X, 0f, 1f),
                        Math.Clamp(furnitureModel.Color.CustomColor.Y, 0f, 1f),
                        Math.Clamp(furnitureModel.Color.CustomColor.Z, 0f, 1f),
                        1f),
                    customColorLabel,
                    $"Custom Color: {customColorLabel}");
                return true;

            case FurnitureModel furnitureModel when furnitureModel.Color.StainId != 0:
                var stain = FurnitureStainSelector.Find(_furnitureStainService.GetStains(), furnitureModel.Color.StainId);
                var finish = stain.IsMetallic ? "glossy" : "matte";
                marker = new SceneListRow.ColorMarker(
                    stain.PreviewColor,
                    $"{stain.Id:000} | {stain.Name}",
                    $"Stain {stain.Id:000}: {stain.Name} ({finish})");
                return true;

            case LightModel lightModel when !IsApproximatelyDefault(lightModel.Color, new Vector3(20f, 20f, 20f)):
                var lightColor = Vector3.SquareRoot(lightModel.Color / 6f);
                string lightColorLabel = FormatColorLabel(lightColor);
                marker = new SceneListRow.ColorMarker(
                    ThemeColors.Color(
                        Math.Clamp(lightColor.X, 0f, 1f),
                        Math.Clamp(lightColor.Y, 0f, 1f),
                        Math.Clamp(lightColor.Z, 0f, 1f),
                        1f),
                    lightColorLabel,
                    $"Light Color: {lightColorLabel}");
                return true;
        }

        marker = default;
        return false;
    }

    internal EditorHeroCard.Actions GetSelectedObjectActions(bool selectedActive, SceneItemSnapshot? selected)
    {
        IReadOnlyList<SceneItemSnapshot> items = _sceneState.ResolveSelectedCurrentItems();
        bool hasSelection = items.Count > 0;
        bool canCopy = hasSelection && items.All(static item => item is ObjectSnapshot or SceneGroupSnapshot);
        bool allLocked = hasSelection && items.All(static item => item.Locked);
        _selectedActions.Clear();
        _selectedActions.Add(new("objectCutClipboard", FontAwesomeIcon.Cut, "Cut Selected", canCopy));
        _selectedActions.Add(new("objectCopyClipboard", FontAwesomeIcon.Copy, "Copy Selected", canCopy));
        _selectedActions.Add(new("objectDuplicate", FontAwesomeIcon.Copy, "Duplicate Selected", hasSelection));
        if (selected is SceneGroupSnapshot)
            _selectedActions.Add(new("groupUngroup", FontAwesomeIcon.ObjectUngroup, "Ungroup"));
        else if (selected is ObjectSnapshot)
            _selectedActions.Add(new("objectGroup", FontAwesomeIcon.ObjectGroup, "Group Objects", SceneEditorCommands.CanGroupItems(items)));
        _selectedActions.Add(new("objectMoveToPlayer", FontAwesomeIcon.Running, "Move To Player", hasSelection && selectedActive));
        if (selected is SceneGroupSnapshot group)
            _selectedActions.Add(new("groupCenterPivot", FontAwesomeIcon.Crosshairs, "Center Group Pivot", !group.Locked));
        _selectedActions.Add(new("objectToggleSelectedLock", allLocked ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock,
            allLocked ? "Unlock Selected" : "Lock Selected", hasSelection));
        _selectedActions.Add(new("objectRemoveSelected", FontAwesomeIcon.Trash, "Remove Selected", hasSelection,
            IconColor: ThemeColors.DimRed, TooltipTitleColor: ThemeColors.DimRed));
        return new EditorHeroCard.Actions(_selectedActions, _executeSelectedObjectAction);
    }

    private void ExecuteSelectedObjectAction(string id)
    {
        IReadOnlyList<SceneItemSnapshot> items = _sceneState.ResolveSelectedCurrentItems();
        if (items.Count == 0)
            return;

        switch (id)
        {
            case "objectCutClipboard":
                _ = _sceneCommands.CutObjectsToClipboard(items);
                break;
            case "objectCopyClipboard":
                _ = _objectClipboard.CopyObjects(items);
                break;
            case "objectDuplicate":
                _ = _historyCoordinator.TryDuplicateSceneItems(items);
                break;
            case "objectMoveToPlayer":
                if (_interaction.Selection.PrimaryItemId is { } itemId)
                    _ = _historyCoordinator.TryMoveItemToPlayer(itemId);
                break;
            case "objectToggleSelectedLock":
                _ = _sceneCommands.TrySetSceneItemsLockedWithHistory(items, !items.All(static item => item.Locked));
                break;
            case "objectRemoveSelected":
                _ = _historyCoordinator.TryRemoveSceneItems(items);
                break;
            case "groupCenterPivot":
                if (items.FirstOrDefault(item => item.Id == _interaction.Selection.PrimaryItemId) is SceneGroupSnapshot group)
                    _ = _sceneCommands.TryCenterGroupPivot(group);
                break;
            case "groupUngroup":
                _ = _sceneCommands.TryUngroupItems(items);
                break;
            case "objectGroup":
                _ = _sceneCommands.TryGroupItems(items);
                break;
        }
    }

    private static bool IsApproximatelyDefault(Vector4 value, Vector4 expected, float epsilon = 0.001f)
        => NumericsUtility.IsNearlyEqual(value, expected, epsilon);

    private static bool IsApproximatelyDefault(Vector3 value, Vector3 expected, float epsilon = 0.001f)
        => NumericsUtility.IsNearlyEqual(value, expected, epsilon);

    private static string FormatColorLabel(Vector4 color)
        => FormatColorLabel(new Vector3(color.X, color.Y, color.Z));

    private static string FormatColorLabel(Vector3 color)
        => $"{color.X:0.##}, {color.Y:0.##}, {color.Z:0.##}";

    private static string FormatByteColorLabel(Vector4 color)
        => $"{ColorUtility.ToRoundedByteComponent(color.X)}, {ColorUtility.ToRoundedByteComponent(color.Y)}, {ColorUtility.ToRoundedByteComponent(color.Z)}";
}
