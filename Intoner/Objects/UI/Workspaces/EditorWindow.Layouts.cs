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
    private const int LayoutNameMaxLength = 128;
    private const string LayoutImportPopupId = "##layoutImportPopup";
    private const string LayoutExportPopupId = "##layoutExportPopup";

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

        if (_listCard.Draw(
            $"layoutEntry:{layout.Id}",
            layout.Name,
            detail,
            isDefault ? "default" : $"{layout.Objects.Count}",
            isDefault ? $"default layout | {objectCountLabel}" : objectCountLabel,
            isSelected,
            EditorColors.AccentPurple,
            itemHeight,
            () => DrawLayoutListEntryContextMenu(layout, defaultLayoutId)))
        {
            _selectedLayoutId = isSelected ? null : layout.Id;
        }
    }

    private void DrawLayoutListEntryContextMenu(ObjectLayoutSnapshot layout, Guid? defaultLayoutId)
    {
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, "Edit Name"))
        {
            OpenRenameLayoutDialog(layout);
        }

        bool isDefault = defaultLayoutId == layout.Id;
        if (EditorContextMenu.DrawItem(
                isDefault ? FontAwesomeIcon.Ban : FontAwesomeIcon.FolderOpen,
                isDefault ? "Clear Default Layout" : "Set As Default Layout",
                enabled: isDefault || !defaultLayoutId.HasValue))
        {
            SelectLayoutFromEditor(isDefault ? null : layout.Id);
        }

        using (var exportMenu = EditorContextMenu.BeginSubMenu(
                   $"layoutExport:{layout.Id}",
                   FontAwesomeIcon.Save,
                   "Export Layout"))
        {
            if (exportMenu && DrawLayoutFileKindChoices() is { } fileKind)
            {
                BeginLayoutExportDialog(layout, fileKind);
            }
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Trash, "Delete Layout"))
        {
            OpenDeleteLayoutDialog(layout);
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
                        ImGui.InputText("##layoutName", ref _layoutName, LayoutNameMaxLength);

                        DrawStateRow("Placed Objects", objectCount.ToString());
                        DrawStateRow("Without Layout", unassignedCount.ToString());
                    }
                }

                var buttonWidth = ResolveIconTextButtonMetrics(FontAwesomeIcon.Save, "Save Current Objects As Layout").Width;
                var startX = ImGui.GetCursorPosX();
                var centeredX = startX + MathF.Max(0f, (ImGui.GetContentRegionAvail().X - buttonWidth) * 0.5f);
                ImGui.SetCursorPosX(centeredX);

                if (DrawIconTextButton("##saveCurrentLayout", FontAwesomeIcon.Save, "Save Current Objects As Layout", buttonWidth)
                    && _objectManager.TrySaveCurrentObjectsAsLayout(_layoutName, out var layoutId))
                {
                    _layoutName = string.Empty;
                    _selectedLayoutId = layoutId;
                    HandleSelectionChanged(_editorSelection.TryClear());
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

        bool importMenuRequested = DrawAccentIconButton(
            "layoutImportJson",
            FontAwesomeIcon.FileImport,
            "Import Layout From Json",
            EditorColors.AccentBlue,
            actionButtonEdge);
        if (DrawLayoutFileKindDropdown(LayoutImportPopupId, importMenuRequested) is { } importFileKind)
        {
            BeginLayoutImportDialog(importFileKind);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!canUseSelected))
        {
            bool exportMenuRequested = DrawAccentIconButton(
                "layoutExportJson",
                FontAwesomeIcon.Save,
                "Export Selected Layout To Json",
                EditorColors.AccentBlue,
                actionButtonEdge);
            ObjectLayoutSnapshot? exportLayout = selectedLayout;
            if (exportLayout is not null
                && DrawLayoutFileKindDropdown(LayoutExportPopupId, exportMenuRequested) is { } exportFileKind)
            {
                BeginLayoutExportDialog(exportLayout, exportFileKind);
            }
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!canUseSelected))
        {
            if (DrawAccentIconButton("layoutDeleteSelected", FontAwesomeIcon.Trash, "Delete Selected Layout", EditorColors.DimRed, actionButtonEdge)
                && selectedLayout is not null)
            {
                OpenDeleteLayoutDialog(selectedLayout);
            }
        }

        ImGui.SameLine();
        if (canUnload)
        {
            if (DrawAccentIconButton("layoutUnload", FontAwesomeIcon.Ban, "Clear Default Layout", EditorColors.AccentYellow, actionButtonEdge))
            {
                SelectLayoutFromEditor(null);
            }
        }
        else
        {
            using var disabled = ImRaii.Disabled(!canLoadSelected);
            if (DrawAccentIconButton("layoutLoadSelected", FontAwesomeIcon.FolderOpen, "Set Selected As Default Layout", EditorColors.AccentGreen, actionButtonEdge)
                && selectedLayout is not null)
            {
                SelectLayoutFromEditor(selectedLayout.Id);
            }
        }
    }

    private void SelectLayoutFromEditor(Guid? layoutId)
    {
        if (!_objectManager.TrySelectLayout(layoutId))
        {
            return;
        }

        HandleSelectionChanged(_editorSelection.TryClear());
    }

    private void OpenRenameLayoutDialog(ObjectLayoutSnapshot layout)
    {
        OpenDialog(EditorDialog.Request.TextInput(
            "layout-rename",
            "Rename Layout",
            "Rename Layout",
            name => _layoutManager.TryRenameLayout(layout.Id, name)) with
        {
            Icon = FontAwesomeIcon.Edit,
            InitialValue = layout.Name,
            Placeholder = "layout name",
            FailureMessage = "The layout could not be renamed.",
            MaxLength = LayoutNameMaxLength,
            Validate = static name => ObjectStringUtility.TrimOrEmpty(name).Length == 0
                ? "Enter a layout name."
                : null,
        });
    }

    private void OpenDeleteLayoutDialog(ObjectLayoutSnapshot layout)
    {
        OpenDialog(EditorDialog.Request.TryConfirmation(
            "layout-delete",
            "Delete Layout",
            "Delete Layout",
            () => TryDeleteLayoutFromEditor(layout.Id)) with
        {
            Icon = FontAwesomeIcon.Trash,
            ConfirmIcon = FontAwesomeIcon.Trash,
            Accent = EditorColors.DimRed,
            Detail = layout.Name,
            Description = "This permanently deletes the saved layout. If it is active, its layout objects will be removed.",
            FailureMessage = "The layout could not be deleted.",
        });
    }

    private bool TryDeleteLayoutFromEditor(Guid layoutId)
    {
        if (!_objectManager.TryDeleteLayout(layoutId))
        {
            return false;
        }

        if (_selectedLayoutId == layoutId)
        {
            _selectedLayoutId = null;
        }

        HandleSelectionChanged(_editorSelection.TryClear());
        return true;
    }

    private static ObjectLayoutFileKind? DrawLayoutFileKindDropdown(string popupId, bool open)
    {
        using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdownForLastItem(popupId, open);
        if (!popup)
        {
            return null;
        }

        return DrawLayoutFileKindChoices();
    }

    private static ObjectLayoutFileKind? DrawLayoutFileKindChoices()
    {
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.FolderOpen, "Object Layout (.json)"))
        {
            return ObjectLayoutFileKind.ObjectLayout;
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Home, "MakePlace Layout (.json)"))
        {
            return ObjectLayoutFileKind.MakePlaceLayout;
        }

        return null;
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

    private void BeginLayoutImportDialog(ObjectLayoutFileKind fileKind)
    {
        string dialogTitle = fileKind == ObjectLayoutFileKind.MakePlaceLayout
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
                ObjectLayoutFileImportResult result = _objectLayoutFileService.ImportLayout(path, fileKind);
                if (result.Success && result.Layout is not null)
                {
                    _selectedLayoutId = result.Layout.Id;
                    HandleSelectionChanged(_editorSelection.TryClear());
                    string defaultSuccessMessage = fileKind == ObjectLayoutFileKind.MakePlaceLayout
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

    private void BeginLayoutExportDialog(ObjectLayoutSnapshot layout, ObjectLayoutFileKind fileKind)
    {
        string defaultFileName = ObjectLayoutFileUtility.BuildExportFileName(layout.Name);
        string dialogTitle = fileKind == ObjectLayoutFileKind.MakePlaceLayout
            ? "Export MakePlace layout to json"
            : "Export layout to json";
        _uiSharedService.FileDialogManager.SaveFileDialog(
            dialogTitle,
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
                ObjectLayoutFileExportResult result = _objectLayoutFileService.ExportLayout(layout, path, fileKind);
                if (result.Success)
                {
                    string defaultSuccessMessage = fileKind == ObjectLayoutFileKind.MakePlaceLayout
                        ? $"Exported MakePlace layout '{layout.Name}' to json."
                        : $"Exported layout '{layout.Name}' to json.";
                    SetLayoutFileStatus(
                        string.IsNullOrWhiteSpace(result.Message)
                            ? defaultSuccessMessage
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

