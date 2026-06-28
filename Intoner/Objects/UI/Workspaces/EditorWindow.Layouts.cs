using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.UI;
using Intoner.UI.Performance;
using System.Numerics;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string LayoutImportPopupId = "##layoutImportPopup";

    private void DrawLayoutListPanel(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? defaultLayoutId)
    {
        DrawLayoutListHero(layouts, defaultLayoutId);
        DrawLayoutListCard(layouts, defaultLayoutId);
    }

    private void DrawLayoutInspectorPanel(IReadOnlyList<ObjectSnapshot> objects, IReadOnlyList<ObjectSnapshot> activeObjects, IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? defaultLayoutId)
    {
        DrawPanelCard(
            "layout-manager-hero",
            EditorColors.ButtonDefault with { W = 0.30f },
            EditorColors.AccentPurple with { W = 0.24f },
            Scaled(8f),
            ResolveObjectListCardPadding(),
            () =>
            {
                DrawCardHeader(
                    "layoutManagerHeroHeader",
                    FontAwesomeIcon.Folder,
                    "Manage Layouts",
                    defaultLayoutId.HasValue
                        ? $"Default layout: {ResolveLayoutName(layouts, defaultLayoutId.Value)}"
                        : "No default layout selected",
                    EditorColors.AccentPurple);
            });

        var unassignedCount = objects.Count(static snapshot => !snapshot.LayoutId.HasValue);
        DrawLayoutSaveSection(objects.Count, unassignedCount);

        var currentLayoutHeight = ResolveRemainingRegionHeight(Scaled(40f), Scaled(4f));
        DrawLayoutCurrentSection(layouts, defaultLayoutId, activeObjects, currentLayoutHeight);
    }

    private void DrawLayoutListHero(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? defaultLayoutId)
    {
        var selectedLayout = _selectedLayoutId.HasValue
            ? layouts.FirstOrDefault(layout => layout.Id == _selectedLayoutId.Value)
            : null;

        DrawPanelCard(
            "layout-list-hero",
            EditorColors.ButtonDefault with { W = 0.30f },
            EditorColors.AccentPurple with { W = 0.24f },
            Scaled(8f),
            ResolveObjectListCardPadding(),
            () =>
            {
                DrawCardHeader(
                    "layoutListHeroHeader",
                    FontAwesomeIcon.Folder,
                    "Layouts",
                    BuildLayoutListStatus(layouts, selectedLayout, defaultLayoutId),
                    EditorColors.AccentPurple,
                    () => DrawLayoutSelectionActions(selectedLayout, defaultLayoutId),
                    wrapSubtitle: true,
                    drawAfterSubtitle: DrawLayoutFileStatus);
            });
    }

    private void DrawLayoutListCard(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? defaultLayoutId)
    {
        var padding = ResolveObjectListCardPadding();
        var itemSpacingY = ResolveObjectListItemSpacingY();
        var innerHeight = ResolveObjectListCardInnerHeight(padding, itemSpacingY);
        var background = EditorColors.ButtonDefault with { W = 0.24f };
        var rounding = Scaled(8f);

        DrawPanelCard(
            "layout-list-card",
            background,
            EditorColors.AccentPurple with { W = 0.18f },
            rounding,
            padding,
            () =>
            {
                if (layouts.Count == 0)
                {
                    DrawPlacedObjectsEmptyState("No layouts have been saved currently.", innerHeight);
                    return;
                }

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = ObjectScrollList.Begin(
                    "##layoutEntries",
                    new Vector2(0f, innerHeight),
                    CreateOverlayScrollPanelOptions(background, rounding, EditorColors.AccentPurple));
                if (!child)
                {
                    return;
                }

                ImGui.Dummy(new Vector2(0f, itemSpacingY));

                var itemHeight = ResolveObjectListEntryHeight();
                UiVirtualList.Draw(
                    layouts,
                    UiVirtualListOptions.Rows(itemHeight, itemSpacingY),
                    (layout, _) => DrawLayoutListEntry(layout, defaultLayoutId, itemHeight));
            });
    }

    private void DrawLayoutListEntry(ObjectLayoutSnapshot layout, Guid? defaultLayoutId, float itemHeight)
    {
        var isSelected = _selectedLayoutId == layout.Id;
        var isDefault = defaultLayoutId == layout.Id;
        var objectCountLabel = BuildLayoutObjectCountLabel(layout.Objects.Count);
        var detail = $"{objectCountLabel} | updated {layout.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC";
        if (isDefault)
        {
            detail += " | default";
        }

        if (DrawObjectListEntryCard(
            $"layoutEntry:{layout.Id}",
            layout.Name,
            detail,
            isDefault ? "default" : $"{layout.Objects.Count}",
            isDefault ? $"default layout | {objectCountLabel}" : objectCountLabel,
            isSelected,
            EditorColors.AccentPurple,
            itemHeight))
        {
            _selectedLayoutId = isSelected ? null : layout.Id;
        }
    }

    private void DrawLayoutSaveSection(int objectCount, int unassignedCount)
    {
        var accent = EditorColors.AccentBlue;
        var padding = ResolveObjectListCardPadding();

        DrawPanelCard(
            "layout-save-section",
            EditorColors.ButtonDefault with { W = 0.24f },
            accent with { W = 0.18f },
            Scaled(8f),
            padding,
            () =>
            {
                DrawIconTitleBlock(
                    FontAwesomeIcon.Save,
                    "Save Current Objects",
                    "Create a new layout from all currently placed objects, including ones saved in other worlds or zones.",
                    accent,
                    wrapSubtitle: true);

                ImGuiHelpers.ScaledDummy(4f);

                using (var saveTable = CompactSettingsTable("layoutSave"))
                {
                    if (saveTable)
                    {
                        DrawCompactSettingsLabelCell("Layout Name");
                        ImGui.SetNextItemWidth(-1f);
                        ImGui.InputText("##layoutName", ref _layoutName, 128);

                        DrawStateRow("Placed Objects", objectCount.ToString());
                        DrawStateRow("Without Layout", unassignedCount.ToString());
                    }
                }

                var buttonWidth = ResolveIconTextButtonMetrics(FontAwesomeIcon.Save, "Save Current Objects As Layout").Width;
                var startX = ImGui.GetCursorPosX();
                var centeredX = startX + MathF.Max(0f, (ImGui.GetContentRegionAvail().X - buttonWidth) * 0.5f);
                ImGui.SetCursorPosX(centeredX);

                if (DrawIconTextButton("##saveCurrentLayout", FontAwesomeIcon.Save, "Save Current Objects As Layout", buttonWidth))
                {
                    if (_objectManager.TrySaveCurrentObjectsAsLayout(_layoutName, out var layoutId))
                    {
                        _layoutName = string.Empty;
                        _selectedLayoutId = layoutId;
                        HandleSelectionChanged(_editorSelection.TryClear());
                    }
                }
            });
    }

    private void DrawLayoutCurrentSection(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? defaultLayoutId, IReadOnlyList<ObjectSnapshot> objects, float height)
    {
        DrawScrollableCreateSectionCard(
            "layout-current-section",
            FontAwesomeIcon.LayerGroup,
            "Current Layout",
            "New objects created while the default layout is loaded are assigned to it automatically.",
            height,
            () =>
            {
                if (!defaultLayoutId.HasValue)
                {
                    DrawPlacedObjectsEmptyState(
                        "No layout is currently selected, load a layout first to set it as the default.",
                        MathF.Max(1f, ImGui.GetContentRegionAvail().Y));
                    return;
                }

                var currentLayout = layouts.FirstOrDefault(layout => layout.Id == defaultLayoutId.Value);
                if (currentLayout is null)
                {
                    DrawPlacedObjectsEmptyState(
                        "The default layout could not be resolved.",
                        MathF.Max(1f, ImGui.GetContentRegionAvail().Y));
                    return;
                }

                using var currentTable = CompactSettingsTable("layoutCurrent");
                if (currentTable)
                {
                    DrawStateRow("Name", currentLayout.Name);
                    DrawStateRow("Objects", BuildLayoutObjectCountLabel(currentLayout.Objects.Count));
                    DrawStateRow("Loaded Now", objects.Count(snapshot => snapshot.LayoutId == currentLayout.Id).ToString());
                    DrawStateRow("Created", $"{currentLayout.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
                    DrawStateRow("Updated", $"{currentLayout.UpdatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
                    DrawStateRow("Id", currentLayout.Id.ToString());
                }
            },
            EditorColors.AccentGreen);
    }

    private void DrawLayoutSelectionActions(ObjectLayoutSnapshot? selectedLayout, Guid? defaultLayoutId)
    {
        var canUseSelected = selectedLayout is not null;
        var canUnload = defaultLayoutId.HasValue;
        var canLoadSelected = canUseSelected && !defaultLayoutId.HasValue;
        var actionButtonEdge = ResolveActionIconButtonEdge(
            FontAwesomeIcon.FileImport,
            FontAwesomeIcon.Save,
            FontAwesomeIcon.Trash,
            FontAwesomeIcon.Ban,
            FontAwesomeIcon.FolderOpen);

        var openedImportPopup = false;
        if (DrawAccentIconButton("layoutImportJson", FontAwesomeIcon.FileImport, "Import Layout From Json", EditorColors.AccentBlue, actionButtonEdge))
        {
            ImGui.OpenPopup(LayoutImportPopupId);
            openedImportPopup = true;
        }

        if (openedImportPopup)
        {
            ImGui.SetNextWindowPos(new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y));
        }

        using var popup = ImRaii.Popup(LayoutImportPopupId, ContextMenuPopupFlags);
        if (popup)
        {
            if (DrawContextMenuItem(FontAwesomeIcon.FolderOpen, "Object Layout (.json)"))
            {
                BeginLayoutImportDialog(ObjectLayoutFileImportKind.ObjectLayout);
            }

            if (DrawContextMenuItem(FontAwesomeIcon.Home, "MakePlace Layout (.json)"))
            {
                BeginLayoutImportDialog(ObjectLayoutFileImportKind.MakePlaceLayout);
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!canUseSelected))
        {
            if (DrawAccentIconButton("layoutExportJson", FontAwesomeIcon.Save, "Export Selected Layout To Json", EditorColors.AccentBlue, actionButtonEdge) && selectedLayout is not null)
            {
                BeginLayoutExportDialog(selectedLayout);
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!canUseSelected))
        {
            if (DrawAccentIconButton("layoutDeleteSelected", FontAwesomeIcon.Trash, "Delete Selected Layout", EditorColors.DimRed, actionButtonEdge) && selectedLayout is not null)
            {
                if (_objectManager.TryDeleteLayout(selectedLayout.Id))
                {
                    _selectedLayoutId = null;
                    HandleSelectionChanged(_editorSelection.TryClear());
                }
            }
        }

        ImGui.SameLine();
        if (canUnload)
        {
            if (DrawAccentIconButton("layoutUnload", FontAwesomeIcon.Ban, "Clear Default Layout", EditorColors.AccentYellow, actionButtonEdge))
            {
                if (_objectManager.TrySelectLayout(null))
                {
                    HandleSelectionChanged(_editorSelection.TryClear());
                }
            }
        }
        else
        {
            using var disabled = ImRaii.Disabled(!canLoadSelected);
            if (DrawAccentIconButton("layoutLoadSelected", FontAwesomeIcon.FolderOpen, "Set Selected As Default Layout", EditorColors.AccentGreen, actionButtonEdge) && selectedLayout is not null)
            {
                if (_objectManager.TrySelectLayout(selectedLayout.Id))
                {
                    HandleSelectionChanged(_editorSelection.TryClear());
                }
            }
        }
    }

    private void DrawLayoutFileStatus()
    {
        if (string.IsNullOrWhiteSpace(_layoutFileStatusMessage))
        {
            return;
        }

        using var textWrap = ImRaiiScope.TextWrapPos();
        ImGui.TextColored(
            _layoutFileStatusIsError
                ? EditorColors.DimRed
                : EditorColors.AccentGreen,
            _layoutFileStatusMessage);
    }

    private void BeginLayoutImportDialog(ObjectLayoutFileImportKind importKind)
    {
        var dialogTitle = importKind == ObjectLayoutFileImportKind.MakePlaceLayout
            ? "Import MakePlace layout from json"
            : "Import object layout from json";

        _uiSharedService.FileDialogManager.OpenFileDialog(
            dialogTitle,
            ".json",
            (success, paths) =>
            {
                if (!success)
                {
                    return;
                }

                if (paths.FirstOrDefault() is not string path || string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                RememberLayoutFileDialogDirectory(path);
                var result = _objectLayoutFileService.ImportLayout(path, importKind);
                if (result.Success && result.Layout is not null)
                {
                    _selectedLayoutId = result.Layout.Id;
                    HandleSelectionChanged(_editorSelection.TryClear());
                    var defaultSuccessMessage = importKind == ObjectLayoutFileImportKind.MakePlaceLayout
                        ? $"Imported MakePlace layout '{result.Layout.Name}'."
                        : $"Imported layout '{result.Layout.Name}' from json.";
                    SetLayoutFileStatus(
                        string.IsNullOrWhiteSpace(result.Message)
                            ? defaultSuccessMessage
                            : result.Message,
                        isError: false);
                    return;
                }

                SetLayoutFileStatus(result.Message, isError: true);
            },
            1,
            ResolveLayoutFileDialogDirectory());
    }

    private void BeginLayoutExportDialog(ObjectLayoutSnapshot layout)
    {
        var defaultFileName = ObjectLayoutFileUtility.BuildExportFileName(layout.Name);
        _uiSharedService.FileDialogManager.SaveFileDialog(
            "Export layout to json",
            ".json",
            defaultFileName,
            ".json",
            (success, path) =>
            {
                if (!success || string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                RememberLayoutFileDialogDirectory(path);
                var result = _objectLayoutFileService.ExportLayout(layout, path);
                if (result.Success)
                {
                    SetLayoutFileStatus(
                        string.IsNullOrWhiteSpace(result.Message)
                            ? $"Exported layout '{layout.Name}' to json."
                            : result.Message,
                        isError: false);
                    return;
                }

                SetLayoutFileStatus(result.Message, isError: true);
            },
            ResolveLayoutFileDialogDirectory());
    }

    private void SetLayoutFileStatus(string? message, bool isError)
    {
        _layoutFileStatusMessage = ObjectStringUtility.TrimOrEmpty(message);
        _layoutFileStatusIsError = isError;
    }

    private string? ResolveLayoutFileDialogDirectory()
    {
        if (Directory.Exists(_layoutFileDialogDirectory))
        {
            return _layoutFileDialogDirectory;
        }

        var documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(documentsDirectory)
            ? documentsDirectory
            : null;
    }

    private void RememberLayoutFileDialogDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            _layoutFileDialogDirectory = directory;
        }
    }

    private static string BuildLayoutListStatus(IReadOnlyList<ObjectLayoutSnapshot> layouts, ObjectLayoutSnapshot? selectedLayout, Guid? defaultLayoutId)
    {
        var countLabel = layouts.Count == 1
            ? "1 layout"
            : $"{layouts.Count} layouts";
        var defaultLabel = defaultLayoutId.HasValue
            ? "default set"
            : "no default";
        var selectedLabel = selectedLayout is null
            ? "no selection"
            : $"selected {selectedLayout.Name}";
        return $"{countLabel} | {selectedLabel} | {defaultLabel}";
    }

    private static string ResolveLayoutName(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid layoutId)
        => layouts.FirstOrDefault(layout => layout.Id == layoutId)?.Name ?? $"Layout {layoutId}";

    private static string BuildLayoutObjectCountLabel(int count)
        => count == 1 ? "1 object" : $"{count} objects";
}

