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
    private readonly EditorInteraction       _interaction;
    private readonly EditorSceneState        _sceneState;
    private readonly SceneEditorCommands     _sceneCommands;
    private readonly SceneListRow            _sceneListRow;
    private readonly IFurnitureStainService  _furnitureStainService;

    public SceneItemControls(
        IHistoryCoordinator historyCoordinator,
        IObjectKindService objectKindService,
        IObjectClipboardService objectClipboard,
        EditorInteraction interaction,
        EditorSceneState sceneState,
        SceneEditorCommands sceneCommands,
        SceneListRow sceneListRow,
        IFurnitureStainService furnitureStainService)
    {
        _historyCoordinator    = historyCoordinator;
        _objectKindService     = objectKindService;
        _objectClipboard       = objectClipboard;
        _interaction           = interaction;
        _sceneState            = sceneState;
        _sceneCommands         = sceneCommands;
        _sceneListRow          = sceneListRow;
        _furnitureStainService = furnitureStainService;
    }

    private void DrawDuplicateSelectedButton()
    {
        var selectedExists = _interaction.Selection.HasSelection;

        using (ImRaii.Disabled(!selectedExists))
        {
            if (EditorIconButton.DrawDefault("objectDuplicate", FontAwesomeIcon.Copy, "Duplicate Selected"))
            {
                IReadOnlyList<SceneItemSnapshot> selectedSnapshots = _sceneState.ResolveSelectedCurrentItems();
                if (selectedSnapshots.Count > 0)
                {
                    _ = _historyCoordinator.TryDuplicateSceneItems(selectedSnapshots);
                }
            }
        }
    }

    private void DrawMoveSelectedToPlayerButton(bool selectedActive)
    {
        var selectedId = _interaction.Selection.PrimaryItemId;
        var selectedExists = selectedId.HasValue;

        using (ImRaii.Disabled(!selectedExists || !selectedActive))
        {
            if (EditorIconButton.DrawDefault("objectMoveToPlayer", FontAwesomeIcon.Running, "Move To Player")
                && selectedId is { } selectedObjectId
                && selectedActive)
            {
                _ = _historyCoordinator.TryMoveItemToPlayer(selectedObjectId);
            }
        }
    }

    private void DrawToggleLockSelectedButton()
    {
        var selectedExists = _interaction.Selection.HasSelection;

        using (ImRaii.Disabled(!selectedExists))
        {
            var selectedSnapshots = selectedExists
                ? _sceneState.ResolveSelectedCurrentItems()
                : [];
            var allLocked = selectedSnapshots.Count > 0 && selectedSnapshots.All(static snapshot => snapshot.Locked);
            var icon = allLocked ? FontAwesomeIcon.Unlock : FontAwesomeIcon.Lock;
            var tooltip = allLocked ? "Unlock Selected" : "Lock Selected";

            if (EditorIconButton.DrawDefault("objectToggleSelectedLock", icon, tooltip))
            {
                _ = _sceneCommands.TrySetSceneItemsLockedWithHistory(selectedSnapshots, !allLocked);
            }
        }
    }

    private void DrawRemoveSelectedButton()
    {
        var selectedExists = _interaction.Selection.HasSelection;

        using (ImRaii.Disabled(!selectedExists))
        {
            if (EditorIconButton.DrawDefault(
                    "objectRemoveSelected",
                    FontAwesomeIcon.Trash,
                    "Remove Selected",
                    iconColor: ThemeColors.DimRed,
                    tooltipTitleColor: ThemeColors.DimRed))
            {
                IReadOnlyList<SceneItemSnapshot> selectedSnapshots = _sceneState.ResolveSelectedCurrentItems();
                if (selectedSnapshots.Count > 0)
                {
                    _ = _historyCoordinator.TryRemoveSceneItems(selectedSnapshots);
                }
            }
        }
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
        EditorListCard.Interaction interaction = SceneListRow.DrawInteraction(row, rowMetrics);
        HandleContextMenuSelection(display, interaction.RightClicked);
        using (EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem($"##{row.Id}:context"))
        {
            if (contextMenu)
            {
                DrawSceneItemContextMenu(display, ResolveContextMenuItems(display), []);
            }
        }

        _sceneListRow.DrawContent(row, rowMetrics, interaction);
        HandleSceneListSelection(display, interaction.Clicked, selectableRowOrder);
    }

    internal SceneListRow.RowGeometry DrawPlacedObjectCard(
        ObjectSnapshot snapshot,
        bool isActive,
        bool selected,
        IReadOnlyList<string> placedFolders,
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
            Status = new SceneListRow.Status(
                isActive ? "Active" : "Inactive",
                isActive ? ThemeColors.AccentGreen : ThemeColors.AccentBlue),
            Accent = ThemeColors.AccentPrimary,
            Emphasized = isActive,
            Visible = snapshot.Visible,
            Locked = snapshot.Locked,
            Selected = selected,
            ColorMarker = colorMarker,
            LeftInset = leftInset,
            RightInset = rightInset,
        };
        EditorListCard.Interaction interaction = SceneListRow.DrawInteraction(row, rowMetrics);
        HandleContextMenuSelection(snapshot, interaction.RightClicked);
        using (EditorContextMenu.PopupScope contextMenu = EditorContextMenu.BeginForLastItem($"##{row.Id}:context"))
        {
            if (contextMenu)
            {
                DrawSceneItemContextMenu(snapshot, ResolveContextMenuItems(snapshot), placedFolders);
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
            return [invokedItem];
        }

        IReadOnlyList<SceneItemSnapshot> selectedItems = _sceneState.ResolveSelectedCurrentItems();
        return selectedItems.Count > 0
            ? selectedItems
            : [invokedItem];
    }

    private void DrawSceneItemContextMenu(
        SceneItemSnapshot invokedItem,
        IReadOnlyList<SceneItemSnapshot> contextItems,
        IReadOnlyList<string> placedFolders)
    {
        List<ObjectSnapshot> selectedObjects = contextItems.OfType<ObjectSnapshot>().ToList();
        if (invokedItem is ObjectSnapshot)
        {
            DrawSceneItemFolderMenu(invokedItem.Id, selectedObjects, placedFolders);

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

        bool allVisible = contextItems.All(static item => item.Visible);
        bool nextVisible = !allVisible;
        if (EditorContextMenu.DrawItem(
                nextVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                SceneEditorCommands.ResolveSceneItemActionTitle(nextVisible ? "Show" : "Hide", contextItems.Count)))
        {
            _ = _sceneCommands.TrySetSceneItemsVisibleWithHistory(contextItems, nextVisible);
        }

        bool allLocked = contextItems.All(static item => item.Locked);
        bool nextLocked = !allLocked;
        if (EditorContextMenu.DrawItem(
                nextLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock,
                SceneEditorCommands.ResolveSceneItemActionTitle(nextLocked ? "Lock" : "Unlock", contextItems.Count)))
        {
            _ = _sceneCommands.TrySetSceneItemsLockedWithHistory(contextItems, nextLocked);
        }

        EditorContextMenu.DrawSeparator();

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Copy,
                SceneEditorCommands.ResolveSceneItemActionTitle("Duplicate", contextItems.Count)))
        {
            _ = _historyCoordinator.TryDuplicateSceneItems(contextItems);
        }

        bool canCopyObjects = invokedItem is ObjectSnapshot
            && selectedObjects.Count == contextItems.Count;
        string copyLabel = selectedObjects.Count == 1
            ? "Copy Object"
            : "Copy Objects";
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Copy,
                copyLabel,
                enabled: canCopyObjects)
            && canCopyObjects)
        {
            _ = _objectClipboard.CopyObjects(selectedObjects);
        }

        string cutLabel = selectedObjects.Count == 1
            ? "Cut Object"
            : "Cut Objects";
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Cut,
                cutLabel,
                enabled: canCopyObjects)
            && canCopyObjects)
        {
            _ = _sceneCommands.CutObjectsToClipboard(selectedObjects);
        }

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Trash,
                SceneEditorCommands.ResolveSceneItemActionTitle("Delete", contextItems.Count),
                color: ThemeColors.DimRed))
        {
            _ = _historyCoordinator.TryRemoveSceneItems(contextItems);
        }
    }

    private void DrawSceneItemFolderMenu(
        Guid invokedItemId,
        IReadOnlyList<ObjectSnapshot> selectedObjects,
        IReadOnlyList<string> placedFolders)
    {
        using EditorContextMenu.SubMenuScope assignFolder = EditorContextMenu.BeginSubMenu(
            $"placedObjectAssignFolder:{invokedItemId}",
            FontAwesomeIcon.FolderOpen,
            "Assign Folder");
        if (!assignFolder)
        {
            return;
        }

        FolderSelectionMenu.DrawPaths(
            placedFolders,
            ResolveCommonFolderPath(selectedObjects),
            folderPath => _ = _sceneCommands.TrySetObjectFolderWithHistory(selectedObjects, folderPath));
    }

    private static string? ResolveCommonFolderPath(IReadOnlyList<ObjectSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return null;
        }

        string folderPath = snapshots[0].FolderPath;
        for (var index = 1; index < snapshots.Count; ++index)
        {
            if (!string.Equals(folderPath, snapshots[index].FolderPath, StringComparison.OrdinalIgnoreCase))
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
                marker = new SceneListRow.ColorMarker(
                    ThemeColors.Color(
                        Math.Clamp(bgObjectModel.DyeColor.X, 0f, 1f),
                        Math.Clamp(bgObjectModel.DyeColor.Y, 0f, 1f),
                        Math.Clamp(bgObjectModel.DyeColor.Z, 0f, 1f),
                        1f),
                    FormatColorLabel(bgObjectModel.DyeColor),
                    $"Dye Color: {FormatColorLabel(bgObjectModel.DyeColor)}");
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
                marker = new SceneListRow.ColorMarker(
                    ThemeColors.Color(
                        Math.Clamp(lightColor.X, 0f, 1f),
                        Math.Clamp(lightColor.Y, 0f, 1f),
                        Math.Clamp(lightColor.Z, 0f, 1f),
                        1f),
                    FormatColorLabel(lightColor),
                    $"Light Color: {FormatColorLabel(lightColor)}");
                return true;
        }

        marker = default;
        return false;
    }

    internal void DrawSelectedObjectActions(bool selectedActive)
    {
        DrawCutSelectedObjectsButton();
        ImGui.SameLine();
        DrawCopySelectedObjectsButton();
        ImGui.SameLine();
        DrawDuplicateSelectedButton();
        ImGui.SameLine();
        DrawMoveSelectedToPlayerButton(selectedActive);
        ImGui.SameLine();
        DrawToggleLockSelectedButton();
        ImGui.SameLine();
        DrawRemoveSelectedButton();
    }

    internal static Vector2 ResolveSelectedObjectActionsSize()
    {
        static float Edge(FontAwesomeIcon icon)
            => EditorIconButton.MeasureEdge(icon);

        float cutEdge = Edge(FontAwesomeIcon.Cut);
        float copyClipboardEdge = Edge(FontAwesomeIcon.Copy);
        float copyEdge = Edge(FontAwesomeIcon.Copy);
        float runningEdge = Edge(FontAwesomeIcon.Running);
        float lockEdge = MathF.Max(
            Edge(FontAwesomeIcon.Lock),
            Edge(FontAwesomeIcon.Unlock));
        float trashEdge = Edge(FontAwesomeIcon.Trash);
        float width = cutEdge + copyClipboardEdge + copyEdge + runningEdge + lockEdge + trashEdge + (ImGui.GetStyle().ItemSpacing.X * 5f);
        float height = MathF.Max(
            cutEdge,
            MathF.Max(MathF.Max(MathF.Max(copyClipboardEdge, copyEdge), MathF.Max(runningEdge, lockEdge)), trashEdge));
        return new Vector2(width, height);
    }

    private static bool IsApproximatelyDefault(Vector4 value, Vector4 expected, float epsilon = 0.001f)
        => NumericsUtility.IsNearlyEqual(value, expected, epsilon);

    private static bool IsApproximatelyDefault(Vector3 value, Vector3 expected, float epsilon = 0.001f)
        => NumericsUtility.IsNearlyEqual(value, expected, epsilon);

    private static string FormatColorLabel(Vector4 color)
        => $"{color.X:0.##}, {color.Y:0.##}, {color.Z:0.##}";

    private static string FormatColorLabel(Vector3 color)
        => $"{color.X:0.##}, {color.Y:0.##}, {color.Z:0.##}";

    private static string FormatByteColorLabel(Vector4 color)
        => $"{ColorUtility.ToRoundedByteComponent(color.X)}, {ColorUtility.ToRoundedByteComponent(color.Y)}, {ColorUtility.ToRoundedByteComponent(color.Z)}";

    private void DrawCopySelectedObjectsButton()
        => DrawSelectedObjectClipboardButton(
            "objectCopyClipboard",
            FontAwesomeIcon.Copy,
            "Copy",
            _objectClipboard.CopyObjects);

    private void DrawCutSelectedObjectsButton()
        => DrawSelectedObjectClipboardButton(
            "objectCutClipboard",
            FontAwesomeIcon.Cut,
            "Cut",
            _sceneCommands.CutObjectsToClipboard);

    private void DrawSelectedObjectClipboardButton(
        string id,
        FontAwesomeIcon icon,
        string action,
        Func<IReadOnlyList<ObjectSnapshot>, bool> execute)
    {
        IReadOnlyList<ObjectSnapshot> snapshots = _sceneState.ResolveSelectedClipboardObjects();
        using (ImRaii.Disabled(snapshots.Count == 0))
        {
            string target = snapshots.Count == 1 ? "Selected Object" : $"{snapshots.Count} Selected Objects";
            if (EditorIconButton.DrawDefault(id, icon, $"{action} {target}"))
            {
                _ = execute(snapshots);
            }
        }
    }
}
