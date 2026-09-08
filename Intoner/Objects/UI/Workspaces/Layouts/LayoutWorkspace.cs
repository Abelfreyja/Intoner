using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.Services.Loading;
using Intoner.UI.Performance;
using Intoner.UI;
using Microsoft.Extensions.Logging;
using System.Numerics;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed class LayoutWorkspace : IAsyncDisposable
{
    private readonly ILogger<LayoutWorkspace> _logger;
    private readonly IObjectLayoutManager _layoutManager;
    private readonly IObjectLayoutFileService _objectLayoutFileService;
    private readonly IObjectManager _objectManager;
    private readonly UiSharedService _uiSharedService;
    private readonly EditorInteraction _interaction;
    private readonly EditorListCard _listCard;
    private readonly EditorOverlayLayer _editorOverlayLayer;
    private readonly BackgroundOperation<ObjectLayoutTransferResult> _layoutFileOperation;
    private string _layoutFileDialogDirectory = string.Empty;
    private string _layoutStatusMessage = string.Empty;
    private string _layoutName = string.Empty;
    private bool _layoutStatusIsError;
    private const int LayoutNameMaxLength = 128;
    private const string LayoutMenuPopupId = "##layoutMenu";
    private const string LayoutCreationTitle = "Create Layout";
    private const string LayoutCreationDescription = "Save the objects currently placed in the scene as a new layout.";
    private const float LayoutCreationTopSpacing = 2f;
    private const float LayoutCreationHeaderSpacing = 2f;

    public Guid? SelectedLayoutId { get; set; }

    public LayoutWorkspace(
        ILogger<LayoutWorkspace> logger,
        IObjectLayoutManager layoutManager,
        IObjectLayoutFileService objectLayoutFileService,
        IObjectManager objectManager,
        UiSharedService uiSharedService,
        EditorInteraction interaction,
        EditorListCard listCard,
        EditorOverlayLayer editorOverlayLayer)
    {
        _logger                  = logger;
        _layoutManager           = layoutManager;
        _objectLayoutFileService = objectLayoutFileService;
        _objectManager           = objectManager;
        _uiSharedService         = uiSharedService;
        _interaction             = interaction;
        _listCard                = listCard;
        _editorOverlayLayer      = editorOverlayLayer;
        _layoutFileOperation     = new BackgroundOperation<ObjectLayoutTransferResult>(logger);
    }

    public void RefreshContext(IReadOnlyList<ObjectLayoutSnapshot> layouts)
    {
        if (SelectedLayoutId is { } selectedId && layouts.All(layout => layout.Id != selectedId))
        {
            SelectedLayoutId = null;
        }

        CompleteLayoutFileOperation();
    }

    public ValueTask DisposeAsync()
        => _layoutFileOperation.DisposeAsync();

    internal void DrawLayoutListPanel(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? defaultLayoutId)
    {
        DrawLayoutListHero(layouts, defaultLayoutId);
        DrawLayoutListCard(layouts, defaultLayoutId);
    }

    internal void DrawLayoutInspectorPanel(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectSnapshot> activeObjects,
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        Guid? defaultLayoutId)
    {
        ObjectLayoutSnapshot? selectedLayout = SelectedLayoutId.HasValue
            ? layouts.FirstOrDefault(layout => layout.Id == SelectedLayoutId.Value)
            : null;
        Vector2 padding = EditorLayout.ResolveObjectListCardPadding();
        float panelHeight = EditorLayout.ResolveRemainingRegionHeight();
        float contentHeight = EditorLayout.Positive(panelHeight - (padding.Y * 2f) - ImGui.GetStyle().ItemSpacing.Y);

        DrawFixedPanelCard(
            "layout-inspector",
            ThemeColors.ButtonDefault with { W = 0.24f },
            ThemeColors.AccentPrimary with { W = 0.24f },
            EditorLayout.Scaled(8f),
            padding,
            panelHeight,
            () => DrawLayoutInspectorContent(
                selectedLayout,
                defaultLayoutId,
                objects.Count,
                objects.Count(static snapshot => !snapshot.LayoutId.HasValue),
                activeObjects,
                contentHeight));
    }

    private void DrawLayoutListHero(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? defaultLayoutId)
    {
        var selectedLayout = SelectedLayoutId.HasValue
            ? layouts.FirstOrDefault(layout => layout.Id == SelectedLayoutId.Value)
            : null;
        float actionButtonEdge = EditorIconButton.MeasureMaxEdge(FontAwesomeIcon.EllipsisH, FontAwesomeIcon.Trash);
        float actionWidth = EditorLayout.ResolveActionStripWidth(actionButtonEdge, 2);

        EditorCard.DrawPanelCard(
            "layout-list-hero",
            ThemeColors.ButtonDefault with { W = 0.30f },
            ThemeColors.AccentPrimary with { W = 0.24f },
            EditorLayout.Scaled(8f),
            EditorLayout.ResolveObjectListCardPadding(),
            () =>
            {
                DrawCardHeader(
                    "layoutListHeroHeader",
                    FontAwesomeIcon.Folder,
                    "Layouts",
                    BuildLayoutListBadges(layouts, selectedLayout, defaultLayoutId),
                    ThemeColors.AccentPrimary,
                    () => DrawLayoutHeaderActions(selectedLayout, defaultLayoutId, actionButtonEdge),
                    actionWidth,
                    drawAfterBadges: DrawLayoutStatus);
            });
    }

    private void DrawLayoutListCard(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? defaultLayoutId)
    {
        var padding = EditorLayout.ResolveObjectListCardPadding();
        var itemSpacingY = EditorLayout.ResolveObjectListItemSpacingY();
        float panelHeight = EditorLayout.ResolveRemainingRegionHeight();
        float innerHeight = EditorLayout.Positive(panelHeight - (padding.Y * 2f) - (ImGui.GetStyle().ItemSpacing.Y * 2f));
        var background = ThemeColors.ButtonDefault with { W = 0.24f };
        var rounding = EditorLayout.Scaled(8f);

        DrawFixedPanelCard(
            "layout-list-card",
            background,
            ThemeColors.AccentPrimary with { W = 0.18f },
            rounding,
            padding,
            panelHeight,
            () =>
            {
                if (layouts.Count == 0)
                {
                    EditorEmptyState.Draw("No layouts have been saved yet.", innerHeight);
                    return;
                }

                using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
                using var child = EditorScrollList.Begin(
                    "##layoutEntries",
                    new Vector2(0f, innerHeight),
                    _editorOverlayLayer.CreateScrollPanelOptions(background, rounding, ThemeColors.AccentPrimary));
                if (!child)
                {
                    return;
                }

                ImGui.Dummy(new Vector2(0f, itemSpacingY));

                var itemHeight = EditorLayout.ResolveObjectListEntryHeight();
                UiVirtualList.Draw(
                    layouts,
                    UiVirtualListOptions.Rows(itemHeight, itemSpacingY),
                    (layout, _) => DrawLayoutListEntry(layout, defaultLayoutId, itemHeight));
            });
    }

    private void DrawLayoutListEntry(ObjectLayoutSnapshot layout, Guid? defaultLayoutId, float itemHeight)
    {
        var isSelected = SelectedLayoutId == layout.Id;
        var isDefault = defaultLayoutId == layout.Id;
        if (_listCard.Draw(
            $"layoutEntry:{layout.Id}",
            layout.Name,
            BuildLayoutEntryBadges(layout, isDefault),
            isSelected,
            ThemeColors.AccentPrimary,
            itemHeight,
            () => DrawLayoutListEntryContextMenu(layout, defaultLayoutId),
            panelBadgeSurface: true))
        {
            SelectedLayoutId = isSelected ? null : layout.Id;
        }
    }

    private void DrawLayoutListEntryContextMenu(ObjectLayoutSnapshot layout, Guid? defaultLayoutId)
    {
        bool isDefault = defaultLayoutId == layout.Id;
        if (EditorContextMenu.DrawItem(
                isDefault ? FontAwesomeIcon.Ban : FontAwesomeIcon.Star,
                isDefault ? "Clear Default Layout" : "Use as Default Layout"))
        {
            SetDefaultLayoutFromEditor(isDefault ? null : layout.Id);
        }

        if (DrawLayoutFileMenu(
                $"layoutExport:{layout.Id}",
                FontAwesomeIcon.Save,
                "Export Layout",
                enabled: true) is { } fileKind)
        {
            BeginLayoutExportDialog(layout, fileKind);
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, "Rename Layout"))
        {
            OpenRenameLayoutDialog(layout);
        }

        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Trash,
                "Delete Layout",
                color: ThemeColors.DimRed))
        {
            OpenDeleteLayoutDialog(layout);
        }
    }

    private void DrawLayoutInspectorContent(
        ObjectLayoutSnapshot? selectedLayout,
        Guid? defaultLayoutId,
        int objectCount,
        int unassignedCount,
        IReadOnlyList<ObjectSnapshot> activeObjects,
        float contentHeight)
    {
        float contentStartY = ImGui.GetCursorPosY();
        DrawCardHeader(
            "layoutInspectorHeader",
            FontAwesomeIcon.LayerGroup,
            "Layout Details",
            "Manage the selected saved layout.",
            ThemeColors.AccentPrimary);

        ImGui.Separator();

        float creationHeight = MeasureLayoutCreationRegionHeight(ImGui.GetContentRegionAvail().X);
        float usedHeight = ImGui.GetCursorPosY() - contentStartY;
        float detailsHeight = EditorLayout.Positive(contentHeight - usedHeight - creationHeight - ImGui.GetStyle().ItemSpacing.Y);
        DrawLayoutDetailsRegion(selectedLayout, defaultLayoutId, activeObjects, detailsHeight);

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(LayoutCreationTopSpacing);
        DrawLayoutCreationSection(objectCount, unassignedCount);
    }

    private void DrawLayoutDetailsRegion(
        ObjectLayoutSnapshot? selectedLayout,
        Guid? defaultLayoutId,
        IReadOnlyList<ObjectSnapshot> activeObjects,
        float height)
    {
        using var childBackground = ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero);
        using var child = ImRaii.Child(
            "##layoutDetailsRegion",
            new Vector2(0f, height),
            false,
            ImGuiWindowFlags.NoBackground);
        if (!child)
        {
            return;
        }

        if (selectedLayout is null)
        {
            EditorEmptyState.Draw(
                "Select a layout to inspect and manage it.",
                MathF.Max(1f, ImGui.GetContentRegionAvail().Y));
            return;
        }

        bool isDefault = defaultLayoutId == selectedLayout.Id;
        DrawSelectedLayoutSummary(selectedLayout, isDefault);
        ImGui.Separator();
        DrawSelectedLayoutMetrics(selectedLayout, activeObjects);
        DrawDefaultLayoutNote();
    }

    private void DrawSelectedLayoutSummary(ObjectLayoutSnapshot layout, bool isDefault)
    {
        FontAwesomeIcon actionIcon = isDefault ? FontAwesomeIcon.Ban : FontAwesomeIcon.Star;
        string actionLabel = isDefault ? "Unset" : "Set as Default";
        string actionTooltip = isDefault
            ? "Stop using this layout as the default."
            : "Load this layout automatically and assign new objects to it.";
        Vector4 accent = isDefault ? ThemeColors.AccentYellow : ThemeColors.AccentPrimary;
        float actionWidth = MathF.Max(
            EditorButton.MeasureWidth(FontAwesomeIcon.Star, "Set as Default"),
            EditorButton.MeasureWidth(FontAwesomeIcon.Ban, "Unset"));
        float actionHeight = EditorButton.Height;
        float paddingX = EditorLayout.Scaled(10f);
        float paddingY = EditorLayout.Scaled(6f);
        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        float contentWidth = EditorLayout.Positive(width - (paddingX * 2f));
        float textWidth = EditorLayout.Positive(contentWidth - actionWidth - ImGui.GetStyle().ItemSpacing.X);
        string subtitle = isDefault ? "Currently used as the default layout" : "Not currently used as the default";
        EditorIconTextOptions textOptions = new() { WrapTitle = true, WrapSubtitle = true };
        float textHeight = EditorIconText.MeasureHeight(
            FontAwesomeIcon.Folder,
            layout.Name,
            subtitle,
            textWidth,
            textOptions);
        float contentHeight = MathF.Max(textHeight, actionHeight);
        float rowHeight = contentHeight + (paddingY * 2f);
        Vector2 rowMin = ImGui.GetCursorScreenPos();
        Vector2 rowMax = rowMin + new Vector2(width, rowHeight);
        _listCard.DrawFrame(
            ImGui.GetWindowDrawList(),
            rowMin,
            rowMax,
            ThemeColors.ButtonDefault with { W = 0.52f },
            ThemeColors.Border with { W = 0.42f },
            accent with { W = 0.84f },
            EditorLayout.Scaled(7f),
            showEdgeGlow: false);

        ImGui.SetCursorScreenPos(rowMin + new Vector2(paddingX, paddingY));
        using (var table = ImRaii.Table(
                   "##selectedLayoutSummary",
                   2,
                   ImGuiTableFlags.SizingStretchProp
                 | ImGuiTableFlags.NoPadInnerX
                 | ImGuiTableFlags.NoPadOuterX
                 | ImGuiTableFlags.NoSavedSettings,
                new Vector2(contentWidth, contentHeight)))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Layout", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Default", ImGuiTableColumnFlags.WidthFixed, actionWidth);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, contentHeight);
                ImGui.TableNextColumn();
                EditorIconText.Draw(
                    FontAwesomeIcon.Folder,
                    layout.Name,
                    subtitle,
                    accent,
                    textOptions);

                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (contentHeight - actionHeight) * 0.5f));
                if (EditorButton.DrawPrimary(
                        "toggleDefaultLayout",
                        actionIcon,
                        actionLabel,
                        accent,
                        new Vector2(actionWidth, actionHeight),
                        tooltip: actionTooltip))
                {
                    SetDefaultLayoutFromEditor(isDefault ? null : layout.Id);
                }
            }
        }

        ImGui.SetCursorScreenPos(rowMin);
        ImGui.Dummy(new Vector2(width, rowHeight));
    }

    private static void DrawSelectedLayoutMetrics(
        ObjectLayoutSnapshot layout,
        IReadOnlyList<ObjectSnapshot> activeObjects)
    {
        int loadedCount = activeObjects.Count(snapshot => snapshot.LayoutId == layout.Id);
        EditorBadgeRenderer.DrawInline(BuildSelectedLayoutBadges(layout, loadedCount));
    }

    private void DrawDefaultLayoutNote()
    {
        const string text = "The default layout loads automatically and receives newly created objects.";
        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        float padX = EditorLayout.Scaled(10f);
        float padY = EditorLayout.Scaled(6f);
        float iconEdge = EditorLayout.Scaled(16f);
        float iconGap = EditorLayout.Scaled(8f);
        float textWidth = EditorLayout.Positive(width - (padX * 2f) - iconEdge - iconGap);
        float textHeight = ImGui.CalcTextSize(text, false, textWidth).Y;
        float height = MathF.Max(iconEdge, textHeight) + (padY * 2f);
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 max = min + new Vector2(width, height);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        _listCard.DrawFrame(
            drawList,
            min,
            max,
            ThemeColors.AccentPrimary with { W = 0.055f },
            ThemeColors.Border with { W = 0.28f },
            ThemeColors.AccentPrimary with { W = 0.84f },
            EditorLayout.Scaled(6f),
            showEdgeGlow: false);
        Vector2 iconMin = min + new Vector2(padX, padY);
        EditorIcon.DrawCentered(
            drawList,
            FontAwesomeIcon.Star,
            iconMin,
            iconMin + new Vector2(iconEdge),
            ThemeColors.AccentPrimary);

        ImGui.SetCursorScreenPos(new Vector2(iconMin.X + iconEdge + iconGap, min.Y + padY));
        using (ImRaii.PushColor(ImGuiCol.Text, ThemeColors.TextDisabled))
        using (ImRaiiScope.TextWrapPos(ImGui.GetCursorPosX() + textWidth))
        {
            ImGui.TextUnformatted(text);
        }

        ImGui.SetCursorScreenPos(new Vector2(min.X, max.Y));
    }

    private void DrawLayoutCreationSection(int objectCount, int unassignedCount)
    {
        EditorIconText.Draw(
            FontAwesomeIcon.Save,
            LayoutCreationTitle,
            LayoutCreationDescription,
            ThemeColors.AccentBlue,
            new EditorIconTextOptions { WrapSubtitle = true });

        ImGuiHelpers.ScaledDummy(LayoutCreationHeaderSpacing);
        EditorBadgeRenderer.DrawInline(BuildLayoutCreationBadges(objectCount, unassignedCount));

        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        bool submitted;
        using (ImRaii.PushColor(ImGuiCol.FrameBg, ThemeColors.ButtonDefault with { W = 0.72f }))
        {
            ImGui.SetNextItemWidth(width);
            submitted = ImGui.InputTextWithHint(
                "##layoutName",
                "Layout name",
                ref _layoutName,
                LayoutNameMaxLength,
                ImGuiInputTextFlags.EnterReturnsTrue);
        }

        bool canCreate = !string.IsNullOrWhiteSpace(_layoutName);
        bool clicked = EditorButton.DrawFormAction(
            "saveCurrentLayout",
            FontAwesomeIcon.Save,
            "Create Layout",
            ThemeColors.AccentBlue,
            new Vector2(width, EditorButton.Height),
            canCreate,
            canCreate ? null : "Enter a layout name first.");

        if ((submitted || clicked) && canCreate)
        {
            SaveCurrentObjectsAsLayout();
        }
    }

    private void SaveCurrentObjectsAsLayout()
    {
        PersistentMutationResult result = _objectManager.SaveCurrentObjectsAsLayout(_layoutName);
        if (!result.IsAccepted || !result.EntityId.HasValue)
        {
            SetLayoutStatus(
                string.IsNullOrWhiteSpace(result.Message)
                    ? "The layout could not be created."
                    : result.Message,
                isError: true);
            return;
        }

        _layoutName = string.Empty;
        SelectedLayoutId = result.EntityId.Value;
        SetLayoutStatus(string.Empty, isError: false);
        _interaction.SelectionChanged(_interaction.Selection.TryClear());
    }

    private static float MeasureLayoutCreationRegionHeight(float width)
    {
        float headerHeight = EditorIconText.MeasureHeight(
            FontAwesomeIcon.Save,
            LayoutCreationTitle,
            LayoutCreationDescription,
            width,
            new EditorIconTextOptions { WrapSubtitle = true });
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float explicitSpacing = EditorLayout.Scaled(LayoutCreationTopSpacing + LayoutCreationHeaderSpacing);

        // the separator and both spacer items also contribute item spacing
        return headerHeight
             + ImGui.GetFrameHeight()
             + EditorBadgeRenderer.Height
             + EditorButton.Height
             + explicitSpacing
             + (spacing * 7f);
    }

    private void DrawLayoutHeaderActions(
        ObjectLayoutSnapshot? selectedLayout,
        Guid? defaultLayoutId,
        float buttonEdge)
    {
        bool open = EditorIconButton.DrawAccent(
            "layoutMenu",
            FontAwesomeIcon.EllipsisH,
            "Layout Menu",
            ThemeColors.AccentPrimary,
            buttonEdge);
        Vector2 menuAnchor = new(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);

        ImGui.SameLine();
        using (ImRaii.Disabled(selectedLayout is null))
        {
            if (EditorIconButton.DrawAccent(
                    "layoutDeleteSelected",
                    FontAwesomeIcon.Trash,
                    "Delete Selected Layout",
                    ThemeColors.DimRed,
                    buttonEdge,
                    tooltipTitleColor: ThemeColors.DimRed)
                && selectedLayout is not null)
            {
                OpenDeleteLayoutDialog(selectedLayout);
            }
        }

        using EditorContextMenu.PopupScope popup = EditorContextMenu.BeginDropdown(
            LayoutMenuPopupId,
            open,
            menuAnchor);
        if (!popup)
        {
            return;
        }

        EditorContextMenu.DrawFirstSectionLabel("Layouts");
        if (DrawLayoutFileMenu(
                "layoutMenuImport",
                FontAwesomeIcon.FileImport,
                "Import Layout",
                enabled: true) is { } importFileKind)
        {
            BeginLayoutImportDialog(importFileKind);
        }

        if (DrawLayoutFileMenu(
                "layoutMenuExport",
                FontAwesomeIcon.Save,
                "Export Selected Layout",
                selectedLayout is not null,
                "Select a layout first.") is { } exportFileKind
            && selectedLayout is not null)
        {
            BeginLayoutExportDialog(selectedLayout, exportFileKind);
        }

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Edit,
                "Rename Selected Layout",
                enabled: selectedLayout is not null,
                tooltip: selectedLayout is null ? "Select a layout first." : null)
            && selectedLayout is not null)
        {
            OpenRenameLayoutDialog(selectedLayout);
        }

        EditorContextMenu.DrawSectionLabel("Default Layout");
        bool selectedIsDefault = selectedLayout is not null && selectedLayout.Id == defaultLayoutId;
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Star,
                "Use Selected as Default",
                enabled: selectedLayout is not null && !selectedIsDefault,
                tooltip: ResolveUseSelectedLayoutTooltip(selectedLayout, selectedIsDefault)))
        {
            SetDefaultLayoutFromEditor(selectedLayout!.Id);
        }

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Ban,
                "Clear Default Layout",
                enabled: defaultLayoutId.HasValue,
                tooltip: defaultLayoutId.HasValue ? null : "No default layout is set."))
        {
            SetDefaultLayoutFromEditor(null);
        }
    }

    private void SetDefaultLayoutFromEditor(Guid? layoutId)
    {
        PersistentMutationResult result = _objectManager.SelectLayout(layoutId);
        if (!result.IsAccepted)
        {
            SetLayoutStatus(
                string.IsNullOrWhiteSpace(result.Message)
                    ? "The default layout could not be changed."
                    : result.Message,
                isError: true);
            return;
        }

        if (layoutId.HasValue)
        {
            SelectedLayoutId = layoutId;
        }

        SetLayoutStatus(string.Empty, isError: false);
        _interaction.SelectionChanged(_interaction.Selection.TryClear());
    }

    private static string? ResolveUseSelectedLayoutTooltip(
        ObjectLayoutSnapshot? selectedLayout,
        bool selectedIsDefault)
    {
        if (selectedLayout is null)
        {
            return "Select a layout first.";
        }

        return selectedIsDefault
            ? "The selected layout is already the default."
            : null;
    }

    private void OpenRenameLayoutDialog(ObjectLayoutSnapshot layout)
    {
        _interaction.OpenDialog(EditorDialog.Request.TextInput(
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
            Validate = static name => TextUtility.TrimOrEmpty(name).Length == 0
                ? "Enter a layout name."
                : null,
        });
    }

    private void OpenDeleteLayoutDialog(ObjectLayoutSnapshot layout)
    {
        _interaction.OpenDialog(EditorDialog.Request.TryConfirmation(
            "layout-delete",
            "Delete Layout",
            "Delete Layout",
            () => TryDeleteLayoutFromEditor(layout.Id)) with
        {
            Icon = FontAwesomeIcon.Trash,
            ConfirmIcon = FontAwesomeIcon.Trash,
            Accent = ThemeColors.DimRed,
            Detail = layout.Name,
            Description = "This permanently deletes the saved layout. If it is active, its layout objects will be removed.",
            FailureMessage = "The layout could not be deleted.",
        });
    }

    private bool TryDeleteLayoutFromEditor(Guid layoutId)
    {
        if (!_objectManager.DeleteLayout(layoutId).IsAccepted)
        {
            return false;
        }

        if (SelectedLayoutId == layoutId)
        {
            SelectedLayoutId = null;
        }

        _interaction.SelectionChanged(_interaction.Selection.TryClear());
        return true;
    }

    private ObjectLayoutFileKind? DrawLayoutFileMenu(
        string id,
        FontAwesomeIcon icon,
        string label,
        bool enabled,
        string? disabledTooltip = null)
    {
        if (_layoutFileOperation.IsBusy)
        {
            enabled = false;
            disabledTooltip = "A layout import or export is already running.";
        }

        if (!enabled)
        {
            EditorContextMenu.DrawItem(icon, label, enabled: false, tooltip: disabledTooltip);
            return null;
        }

        using EditorContextMenu.SubMenuScope menu = EditorContextMenu.BeginSubMenu(id, icon, label);
        return menu ? DrawLayoutFileKindChoices() : null;
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

    private void DrawLayoutStatus()
    {
        if (string.IsNullOrWhiteSpace(_layoutStatusMessage))
        {
            return;
        }

        using var textWrap = ImRaiiScope.TextWrapPos();
        ImGui.TextColored(
            _layoutStatusIsError
                ? ThemeColors.DimRed
                : ThemeColors.AccentGreen,
            _layoutStatusMessage);
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
                if (_layoutFileOperation.TryStart(token => _objectLayoutFileService.ImportLayoutAsync(path, fileKind, token)))
                {
                    SetLayoutStatus("Importing layout...", isError: false);
                }
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
                if (_layoutFileOperation.TryStart(token => _objectLayoutFileService.ExportLayoutAsync(layout, path, fileKind, token)))
                {
                    SetLayoutStatus("Exporting layout...", isError: false);
                }
            },
            ResolveLayoutFileDialogDirectory());
    }

    private void CompleteLayoutFileOperation()
    {
        if (!_layoutFileOperation.TryTakeCompleted(out Task<ObjectLayoutTransferResult>? task))
        {
            return;
        }

        try
        {
            ObjectLayoutTransferResult result = task.GetAwaiter().GetResult();
            if (result.Success && result.Layout is { } layout)
            {
                SelectedLayoutId = layout.Id;
                _interaction.SelectionChanged(_interaction.Selection.TryClear());
            }

            SetLayoutStatus(result.Message, isError: !result.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Layout file operation failed");
            SetLayoutStatus("The layout file operation failed.", isError: true);
        }
    }

    private void SetLayoutStatus(string? message, bool isError)
    {
        _layoutStatusMessage = TextUtility.TrimOrEmpty(message);
        _layoutStatusIsError = isError;
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

    private static IReadOnlyList<EditorBadge> BuildLayoutEntryBadges(ObjectLayoutSnapshot layout, bool isDefault)
    {
        List<EditorBadge> badges =
        [
            EditorBadge.Count(FontAwesomeIcon.Cube, layout.Objects.Count, "object", "objects"),
        ];
        if (isDefault)
        {
            badges.Add(EditorBadge.Label("Default", FontAwesomeIcon.Star, ThemeColors.AccentGreen));
        }

        badges.Add(EditorBadge.Label(
            EditorTimestampFormatter.FormatCompact(layout.UpdatedAtUtc),
            FontAwesomeIcon.Clock));

        return badges;
    }

    private static IReadOnlyList<EditorBadge> BuildSelectedLayoutBadges(
        ObjectLayoutSnapshot layout,
        int loadedCount)
        =>
        [
            EditorBadge.Count(
                FontAwesomeIcon.Cube,
                layout.Objects.Count,
                "saved object",
                "saved objects",
                ThemeColors.AccentPrimary),
            EditorBadge.Count(
                FontAwesomeIcon.CheckCircle,
                loadedCount,
                "loaded object",
                "loaded objects",
                loadedCount > 0 ? ThemeColors.AccentGreen : ThemeColors.TextDisabled),
            EditorBadge.Label(
                EditorTimestampFormatter.FormatCompact(layout.UpdatedAtUtc),
                FontAwesomeIcon.Clock,
                tooltip: "Last updated"),
        ];

    private static IReadOnlyList<EditorBadge> BuildLayoutCreationBadges(int objectCount, int unassignedCount)
        =>
        [
            EditorBadge.Count(
                FontAwesomeIcon.Cube,
                objectCount,
                "placed object",
                "placed objects",
                ThemeColors.AccentBlue),
            EditorBadge.Count(
                FontAwesomeIcon.LayerGroup,
                unassignedCount,
                "object without layout",
                "objects without layout",
                ThemeColors.TextDisabled),
        ];

    private static IReadOnlyList<EditorBadge> BuildLayoutListBadges(
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        ObjectLayoutSnapshot? selectedLayout,
        Guid? defaultLayoutId)
    {
        ObjectLayoutSnapshot? defaultLayout = defaultLayoutId.HasValue
            ? layouts.FirstOrDefault(layout => layout.Id == defaultLayoutId.Value)
            : null;
        return
        [
            EditorBadge.Count(FontAwesomeIcon.Folder, layouts.Count, "layout", "layouts"),
            EditorBadge.Label(
                defaultLayout?.Name ?? "No default layout",
                FontAwesomeIcon.Star,
                defaultLayout is null ? ThemeColors.TextDisabled : ThemeColors.AccentYellow,
                "Default layout"),
            EditorBadge.Label(
                selectedLayout?.Name ?? "None selected",
                FontAwesomeIcon.MousePointer,
                tooltip: "Selected layout"),
        ];
    }
}
