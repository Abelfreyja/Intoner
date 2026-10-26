using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Objects.Utils;
using Intoner.UI;
using Intoner.UI.Performance;
using System.Numerics;
using static Intoner.Objects.UI.Components.EditorCard;

namespace Intoner.Objects.UI;

internal sealed class LayoutWorkspace
{
    private readonly IObjectLayoutManager _layoutManager;
    private readonly LayoutActions        _actions;
    private readonly EditorInteraction    _interaction;
    private readonly EditorListCard       _listCard;
    private readonly EditorOverlayLayer   _editorOverlayLayer;
    private readonly EditorColumnPanel    _columnPanel;
    private string _layoutName = string.Empty;
    private string _layoutFilter = string.Empty;
    private (IReadOnlyList<ObjectLayoutSnapshot> Source, string Filter, IReadOnlyList<ObjectLayoutSnapshot> Matches)? _layoutSearch;
    private const string LayoutMenuPopupId = "##layoutMenu";
    private const string LayoutCreationTitle = "Create Layout";
    private const string LayoutCreationDescription = "Save the objects currently placed in the scene as a new layout.";
    private const float LayoutCreationTopSpacing = 2f;
    private const float LayoutCreationHeaderSpacing = 2f;

    public Guid? SelectedLayoutId
    {
        get => _actions.SelectedLayoutId;
        set => _actions.SelectedLayoutId = value;
    }

    public LayoutWorkspace(
        IObjectLayoutManager layoutManager,
        LayoutActions actions,
        EditorInteraction interaction,
        EditorListCard listCard,
        EditorOverlayLayer editorOverlayLayer,
        EditorColumnPanel columnPanel)
    {
        _layoutManager      = layoutManager;
        _actions            = actions;
        _interaction        = interaction;
        _listCard           = listCard;
        _editorOverlayLayer = editorOverlayLayer;
        _columnPanel        = columnPanel;
    }

    public void RefreshContext(IReadOnlyList<ObjectLayoutSnapshot> layouts)
        => _actions.RefreshContext(layouts);

    internal void DrawLayoutListPanel(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? creationLayoutId)
    {
        ObjectLayoutSnapshot? selectedLayout = SelectedLayoutId.HasValue
            ? layouts.FirstOrDefault(layout => layout.Id == SelectedLayoutId.Value)
            : null;
        IReadOnlyList<EditorBadge> badges = BuildLayoutListBadges(layouts, selectedLayout, creationLayoutId);
        _columnPanel.DrawPanel(
            "layouts", FontAwesomeIcon.Folder, "Layouts", string.Empty, ThemeColors.AccentPrimary,
            EditorLayout.Positive(ImGui.GetContentRegionAvail().X), EditorLayout.ResolveRemainingRegionHeight(),
            header =>
            {
                float edge = EditorLayout.Scaled(30f);
                float right = header.Max.X - (header.Height - edge) * 0.5f;
                float left = right - EditorLayout.ResolveActionStripWidth(edge, 2);
                float textRight = EditorColumnPanel.DrawHeaderBadges(header, "Layouts", badges, left, [badges[0]]);
                ImGui.SetCursorScreenPos(new Vector2(left, header.Min.Y + (header.Height - edge) * 0.5f));
                DrawLayoutHeaderActions(selectedLayout, edge);
                return textRight;
            },
            contentHeight => DrawLayoutList(layouts, creationLayoutId, contentHeight),
            DrawLayoutNavigation,
            contentHorizontalInset: EditorLayout.Scaled(4f));
    }

    internal void DrawLayoutInspectorPanel(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectSnapshot> activeObjects,
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        Guid? creationLayoutId)
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
                creationLayoutId,
                objects.Count,
                objects.Count(static snapshot => !snapshot.LayoutId.HasValue),
                activeObjects,
                contentHeight));
    }

    private float DrawLayoutNavigation(float width)
    {
        Vector2 start = ImGui.GetCursorScreenPos();
        float inset = EditorLayout.Scaled(8f);
        float gap = EditorLayout.Scaled(4f);
        ImGui.SetCursorScreenPos(start + new Vector2(inset));
        _ = EditorSearchField.Draw("layoutListFilter", ref _layoutFilter,
            new EditorSearchFieldOptions("Search layouts", ThemeColors.AccentPrimary,
                MaxLength: EditorColumnPanel.FilterMaxLength, Width: MathF.Max(1f, width - inset * 2f)));
        float bottom = start.Y + inset + EditorSearchField.Height + gap;

        if (!string.IsNullOrWhiteSpace(_actions.StatusMessage))
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + inset, bottom));
            DrawLayoutStatus();
            bottom = ImGui.GetCursorScreenPos().Y + gap;
        }

        return bottom - start.Y;
    }

    private void DrawLayoutList(IReadOnlyList<ObjectLayoutSnapshot> layouts, Guid? creationLayoutId, float height)
    {
        string filter = _layoutFilter.Trim();

        if (_layoutSearch is not { } cached || !ReferenceEquals(cached.Source, layouts)
            || !string.Equals(cached.Filter, filter, StringComparison.OrdinalIgnoreCase))
        {
            _layoutSearch = (layouts, filter, filter.Length == 0
                ? layouts
                : layouts.Where(layout => layout.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray());
        }

        IReadOnlyList<ObjectLayoutSnapshot> visibleLayouts = _layoutSearch.Value.Matches;

        if (visibleLayouts.Count == 0)
        {
            EditorEmptyState.Draw(layouts.Count == 0
                ? "No layouts have been saved yet."
                : "No layouts match your search.", height);
            return;
        }

        float itemSpacingY = EditorLayout.ResolveObjectListItemSpacingY();
        ImGui.Dummy(new Vector2(0f, itemSpacingY));
        float itemHeight = EditorLayout.ResolveObjectListEntryHeight();
        UiVirtualList.Draw(
            visibleLayouts,
            UiVirtualListOptions.Rows(itemHeight, itemSpacingY),
            (layout, _) => DrawLayoutListEntry(layout, creationLayoutId, itemHeight));
    }

    private void DrawLayoutListEntry(ObjectLayoutSnapshot layout, Guid? creationLayoutId, float itemHeight)
    {
        var isSelected = SelectedLayoutId == layout.Id;
        var isCreation = creationLayoutId == layout.Id;
        if (_listCard.Draw(
            $"layoutEntry:{layout.Id}",
            layout.Name,
            BuildLayoutEntryBadges(layout, isCreation),
            isSelected,
            ThemeColors.AccentPrimary,
            itemHeight,
            () => DrawLayoutListEntryContextMenu(layout),
            panelBadgeSurface: true))
        {
            SelectedLayoutId = isSelected ? null : layout.Id;
        }
    }

    private void DrawLayoutListEntryContextMenu(ObjectLayoutSnapshot layout)
    {
        bool enabled = _layoutManager.IsLayoutEnabled(layout.Id);
        SceneSelectionMenu.DrawChildren(_interaction, enabled ? layout.Objects : []);
        EditorContextMenu.DrawSeparator();

        if (EditorContextMenu.DrawItem(enabled ? FontAwesomeIcon.ToggleOn : FontAwesomeIcon.ToggleOff,
                enabled ? "Disable Layout" : "Enable Layout"))
        {
            _actions.SetEnabled(layout.Id, !enabled);
        }

        if (DrawLayoutFileMenu(
                $"layoutExport:{layout.Id}",
                FontAwesomeIcon.Save,
                "Export Layout",
                enabled: true) is { } fileKind)
        {
            _actions.Export(layout, fileKind);
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Edit, "Rename Layout"))
        {
            _actions.Rename(layout);
        }

        DrawPlacementDestinationMenu(layout);
        EditorContextMenu.DrawSeparator();
        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Trash,
                "Delete Layout",
                color: ThemeColors.DimRed))
        {
            _actions.Delete(layout);
        }
    }

    private void DrawLayoutInspectorContent(
        ObjectLayoutSnapshot? selectedLayout,
        Guid? creationLayoutId,
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
        DrawLayoutDetailsRegion(selectedLayout, creationLayoutId, activeObjects, detailsHeight);

        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(LayoutCreationTopSpacing);
        DrawLayoutCreationSection(objectCount, unassignedCount);
    }

    private void DrawLayoutDetailsRegion(
        ObjectLayoutSnapshot? selectedLayout,
        Guid? creationLayoutId,
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

        _editorOverlayLayer.CaptureCurrentWindow();

        if (selectedLayout is null)
        {
            EditorEmptyState.Draw(
                "Select a layout to inspect and manage it.",
                MathF.Max(1f, ImGui.GetContentRegionAvail().Y));
            return;
        }

        bool isCreation = creationLayoutId == selectedLayout.Id;
        DrawSelectedLayoutSummary(selectedLayout, isCreation);
        ImGui.Separator();
        DrawSelectedLayoutMetrics(selectedLayout, activeObjects);
    }

    private void DrawSelectedLayoutSummary(ObjectLayoutSnapshot layout, bool isCreation)
    {
        bool enabled = _layoutManager.IsLayoutEnabled(layout.Id);
        FontAwesomeIcon actionIcon = enabled ? FontAwesomeIcon.Ban : FontAwesomeIcon.Star;
        string actionLabel = enabled ? "Unset" : "Set";
        string actionTooltip = enabled
            ? "Unload this layout without changing other enabled layouts."
            : "Load this layout alongside other enabled layouts.";
        Vector4 accent = enabled ? ThemeColors.AccentYellow : ThemeColors.AccentPrimary;
        float actionWidth = MathF.Max(
            EditorButton.MeasureWidth(FontAwesomeIcon.Star, "Set"),
            EditorButton.MeasureWidth(FontAwesomeIcon.Ban, "Unset"));
        float actionHeight = EditorButton.Height;
        float paddingX = EditorLayout.Scaled(10f);
        float paddingY = EditorLayout.Scaled(6f);
        float width = EditorLayout.Positive(ImGui.GetContentRegionAvail().X);
        float contentWidth = EditorLayout.Positive(width - (paddingX * 2f));
        float textWidth = EditorLayout.Positive(contentWidth - actionWidth - ImGui.GetStyle().ItemSpacing.X);
        string subtitle = enabled ? "Enabled" : "Disabled";
        if (enabled && isCreation)
        {
            subtitle = "Enabled / New Objects";
        }
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
                ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, actionWidth);
                ImGui.TableNextRow(ImGuiTableRowFlags.None, contentHeight);
                ImGui.TableNextColumn();
                EditorIconText.Draw(FontAwesomeIcon.Folder, layout.Name, subtitle, accent, textOptions);

                ImGui.TableNextColumn();
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0f, (contentHeight - actionHeight) * 0.5f));
                if (EditorButton.DrawPrimary(
                        "toggleLayoutEnabled",
                        actionIcon,
                        actionLabel,
                        accent,
                        new Vector2(actionWidth, actionHeight),
                        tooltip: actionTooltip))
                {
                    _actions.SetEnabled(layout.Id, !enabled);
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
                LayoutActions.NameMaxLength,
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

        if ((submitted || clicked) && canCreate && _actions.SaveCurrent(_layoutName))
        {
            _layoutName = string.Empty;
        }
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
        float buttonEdge)
    {
        bool open = EditorIconButton.DrawToggle(
            "layoutMenu", FontAwesomeIcon.EllipsisH, new Vector2(buttonEdge), false,
            ThemeColors.AccentPrimary, ThemeColors.AccentPrimary, "Layout Menu");
        Vector2 menuAnchor = new(ImGui.GetItemRectMin().X, ImGui.GetItemRectMax().Y);

        ImGui.SameLine();

        if (EditorIconButton.DrawToggle(
                "layoutDeleteSelected", FontAwesomeIcon.Trash, new Vector2(buttonEdge), false,
                ThemeColors.DimRed, ThemeColors.DimRed, "Delete Selected Layout", enabled: selectedLayout is not null)
            && selectedLayout is not null)
        {
            _actions.Delete(selectedLayout);
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
            _actions.Import(importFileKind);
        }

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.FileImport, "Recover Layout...", enabled: !_actions.IsBusy,
                tooltip: "Recover valid entries into a separate layout. The original file is not changed in any way."))
        {
            _actions.Recover();
        }

        if (DrawLayoutFileMenu(
                "layoutMenuExport",
                FontAwesomeIcon.Save,
                "Export Selected Layout",
                selectedLayout is not null,
                "Select a layout first.") is { } exportFileKind
            && selectedLayout is not null)
        {
            _actions.Export(selectedLayout, exportFileKind);
        }

        if (EditorContextMenu.DrawItem(
                FontAwesomeIcon.Edit,
                "Rename Selected Layout",
                enabled: selectedLayout is not null,
                tooltip: selectedLayout is null ? "Select a layout first." : null)
            && selectedLayout is not null)
        {
            _actions.Rename(selectedLayout);
        }

        DrawPlacementDestinationMenu(selectedLayout);
    }

    private void DrawPlacementDestinationMenu(ObjectLayoutSnapshot? layout)
    {
        Guid? creationLayoutId = _layoutManager.GetCreationLayoutId();
        bool selected = layout is not null && layout.Id == creationLayoutId;
        string? unavailable = null;
        if (layout is null)
            unavailable = "Select a layout first.";
        else if (!_layoutManager.IsLayoutEnabled(layout.Id))
            unavailable = "Enable this layout first.";

        EditorContextMenu.DrawSectionLabel("New Objects Go To");
        if (EditorContextMenu.DrawItem(FontAwesomeIcon.MapMarkerAlt, "This Layout",
                selected: selected, enabled: unavailable is null && !selected)
            && layout is not null)
        {
            _actions.SetCreation(layout.Id);
        }

        if (unavailable is not null)
            IntonerTooltip.Attach(unavailable);

        if (EditorContextMenu.DrawItem(FontAwesomeIcon.Cubes, "Unassigned",
                selected: !creationLayoutId.HasValue, enabled: creationLayoutId.HasValue, accent: ThemeColors.AccentRose))
        {
            _actions.SetCreation(null);
        }
    }

    private ObjectLayoutFileKind? DrawLayoutFileMenu(
        string id,
        FontAwesomeIcon icon,
        string label,
        bool enabled,
        string? disabledTooltip = null)
    {
        if (_actions.IsBusy)
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
        if (string.IsNullOrWhiteSpace(_actions.StatusMessage))
        {
            return;
        }

        using var textWrap = ImRaiiScope.TextWrapPos();
        ImGui.TextColored(
            _actions.StatusIsError
                ? ThemeColors.DimRed
                : ThemeColors.AccentGreen,
            _actions.StatusMessage);
    }

    private IReadOnlyList<EditorBadge> BuildLayoutEntryBadges(ObjectLayoutSnapshot layout, bool isCreation)
    {
        List<EditorBadge> badges =
        [
            EditorBadge.Count(FontAwesomeIcon.Cube, layout.Objects.Count, "object", "objects"),
        ];
        if (_layoutManager.IsLayoutEnabled(layout.Id))
        {
            badges.Add(EditorBadge.Label("Enabled", FontAwesomeIcon.Check, ThemeColors.AccentGreen));
        }

        if (isCreation)
        {
            badges.Add(EditorBadge.Label("New Objects", FontAwesomeIcon.Plus, ThemeColors.AccentYellow));
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
        Guid? creationLayoutId)
    {
        ObjectLayoutSnapshot? creationLayout = creationLayoutId.HasValue
            ? layouts.FirstOrDefault(layout => layout.Id == creationLayoutId.Value)
            : null;
        return
        [
            EditorBadge.Count(FontAwesomeIcon.Folder, layouts.Count, "layout", "layouts"),
            EditorBadge.Label(
                creationLayout?.Name ?? ObjectDestinationMenu.UnassignedLabel,
                FontAwesomeIcon.Star,
                creationLayout is null ? ThemeColors.TextDisabled : ThemeColors.AccentYellow,
                "New object destination"),
            EditorBadge.Label(
                selectedLayout?.Name ?? "None selected",
                FontAwesomeIcon.MousePointer,
                tooltip: "Selected layout"),
        ];
    }
}
