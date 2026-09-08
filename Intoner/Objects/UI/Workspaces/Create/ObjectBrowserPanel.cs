using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Library;
using Intoner.Objects.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class ObjectBrowserPanel
{
    private readonly IObjectLibrary         _objectLibrary;
    private readonly EditorOverlayLayer     _editorOverlayLayer;
    private readonly EditorFilterBar        _filterBar;
    private readonly CreateBrowserSelection _browserSelection;

    public ObjectBrowserPanel(
        IObjectLibrary objectLibrary,
        EditorOverlayLayer editorOverlayLayer,
        EditorFilterBar filterBar,
        CreateBrowserSelection browserSelection)
    {
        _objectLibrary      = objectLibrary;
        _editorOverlayLayer = editorOverlayLayer;
        _filterBar          = filterBar;
        _browserSelection   = browserSelection;
    }

    internal void DrawCatalogEntriesPanel(string id, int entryCount, string emptyText, Action drawEntries)
    {
        var background = ThemeColors.ButtonDefault with { W = 0.28f };
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, background);
        using var entriesChild = EditorScrollList.Begin(
            $"{id}_entries",
            new Vector2(0f, 0f),
            _editorOverlayLayer.CreateScrollPanelOptions(background, EditorLayout.Scaled(8f), ThemeColors.AccentPrimary),
            true,
            ImGuiWindowFlags.NoScrollWithMouse);
        if (!entriesChild)
        {
            return;
        }

        if (entryCount == 0)
        {
            EditorEmptyState.Draw(emptyText, ImGui.GetContentRegionAvail().Y);
            return;
        }

        drawEntries();
        bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        _ = EditorScrollList.ScrollCurrentWindowFromMouseWheel(
            hovered && !IntonerTooltipScroll.MouseWheelClaimed);
    }

    internal void DrawCatalogHeaderCard(
        string id,
        FontAwesomeIcon icon,
        ObjectCatalogSection section,
        ref string filter,
        string searchHint,
        ref string groupFilter,
        IReadOnlyList<ObjectCatalogFilterCount> groupCounts,
        int filteredCount,
        Action? drawHeaderActions = null,
        Vector2 headerActionsSize = default)
    {
        var currentFilter = filter;
        var currentGroupFilter = groupFilter;
        DrawCatalogHeaderCardCore(
            id,
            icon,
            $"{section.DisplayName} Catalog",
            section.Count,
            filteredCount,
            section.Count,
            ref currentFilter,
            searchHint,
            drawHeaderActions,
            headerActionsSize,
            () =>
            {
                currentGroupFilter = _filterBar.DrawCatalogFilterButtons(id, currentGroupFilter, groupCounts);
            });

        filter = currentFilter;
        groupFilter = currentGroupFilter;
    }

    internal void DrawCatalogHeaderCardCore(
        string id,
        FontAwesomeIcon icon,
        string title,
        int totalCount,
        int filteredCount,
        int catalogCount,
        ref string filter,
        string searchHint,
        Action? drawHeaderActions,
        Vector2 headerActionsSize,
        Action? drawAfterFilter)
    {
        var accent = ThemeColors.AccentPrimary;
        var currentFilter = filter;
        var cardPadding = EditorLayout.ResolveObjectListCardPadding();
        EditorCard.DrawPanelCard(
            $"{id}_header",
            ThemeColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.28f },
            EditorLayout.Scaled(8f),
            cardPadding,
            () =>
            {
                Vector2 headerRowStart = ImGui.GetCursorPos();
                float headerRowWidth = ImGui.GetContentRegionAvail().X;
                float headerRowMaxX = headerRowStart.X + headerRowWidth;
                DrawCreateBrowserSourceHeading(
                    icon,
                    title,
                    filteredCount == totalCount
                        ? $"{totalCount} entries"
                        : $"{filteredCount} shown of {totalCount}",
                    catalogCount);

                float headerRowEndY = ImGui.GetCursorPosY();

                if (drawHeaderActions is not null && headerActionsSize.X > 0f && headerActionsSize.Y > 0f)
                {
                    float actionY = headerRowStart.Y + MathF.Max(0f, ((headerRowEndY - headerRowStart.Y) - headerActionsSize.Y) * 0.5f);
                    var previousCursorY = ImGui.GetCursorPosY();
                    var rightAlignedX = MathF.Max(ImGui.GetCursorPosX(), headerRowMaxX - headerActionsSize.X);
                    ImGui.SetCursorPos(new Vector2(rightAlignedX, actionY));
                    drawHeaderActions();
                    ImGui.SetCursorPos(new Vector2(headerRowStart.X, MathF.Max(headerRowEndY, previousCursorY)));
                }

                _ = EditorSearchField.Draw(
                    $"{id}Filter",
                    ref currentFilter,
                    new EditorSearchFieldOptions(searchHint, ThemeColors.AccentPrimary));
                drawAfterFilter?.Invoke();
            });
        filter = currentFilter;
    }

    private void DrawCreateBrowserSourceHeading(FontAwesomeIcon icon, string title, string summary, int catalogCount)
    {
        Vector4 accent = ThemeColors.AccentPrimary;
        string iconText = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, accent))
        {
            ImGui.TextUnformatted(iconText);
        }

        ImGui.SameLine();
        using (ImRaii.Group())
        {
            string caret = FontAwesomeIcon.ChevronDown.ToIconString();
            Vector2 titleSize = ImGui.CalcTextSize(title);
            Vector2 caretSize;
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                caretSize = ImGui.CalcTextSize(caret);
            }

            float spacing = EditorLayout.Scaled(6f);
            Vector2 buttonSize = new(titleSize.X + spacing + caretSize.X, MathF.Max(titleSize.Y, caretSize.Y));
            bool clicked = ImGui.InvisibleButton("##createBrowserSource", buttonSize);
            bool hovered = ImGui.IsItemHovered();
            Vector2 buttonMin = ImGui.GetItemRectMin();
            Vector2 buttonMax = ImGui.GetItemRectMax();
            Vector4 titleColor = hovered ? accent : ThemeColors.Text;
            ImDrawListPtr drawList = ImGui.GetWindowDrawList();
            drawList.AddText(buttonMin, ImGui.GetColorU32(titleColor), title);
            drawList.AddText(
                UiBuilder.IconFont,
                UiBuilder.IconFont.FontSize,
                new Vector2(buttonMin.X + titleSize.X + spacing, buttonMin.Y),
                ImGui.GetColorU32(accent with { W = hovered ? 1f : 0.76f }),
                caret);
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            ImGui.TextDisabled(summary);
            using EditorContextMenu.PopupScope sourceMenu = EditorContextMenu.BeginDropdown(
                "##createBrowserSourceMenu",
                clicked,
                new Vector2(buttonMin.X, buttonMax.Y),
                252f);
            if (sourceMenu)
            {
                DrawCreateBrowserSourceMenu(catalogCount);
            }
        }
    }

    private void DrawCreateBrowserSourceMenu(int catalogCount)
    {
        FontAwesomeIcon catalogIcon = SceneItemPresentation.ResolveObjectKindIcon(ObjectEditorCatalog.ToObjectKind(_browserSelection.Kind));
        string catalogLabel = $"{ObjectEditorCatalog.GetDraftKindLabel(_browserSelection.Kind)} Catalog";
        string catalogDetail = catalogCount == 1
            ? "Browse the available catalog entry"
            : $"Browse all {catalogCount} available entries";
        if (EditorChoiceMenu.DrawOption(
                "createCatalogSource",
                catalogIcon,
                catalogLabel,
                catalogDetail,
                _browserSelection.Source == CreateBrowserSelection.CreateBrowserSource.Catalog))
        {
            _browserSelection.Source = CreateBrowserSelection.CreateBrowserSource.Catalog;
        }

        ObjectLibrarySnapshot library = _objectLibrary.Current;
        string libraryDetail = $"{ObjectEditorCatalog.BuildSavedObjectCountLabel(library.Entries.Count)} in "
            + ObjectEditorCatalog.BuildLibraryGroupCountLabel(library.Folders.Count, library.Prefabs.Count);
        if (EditorChoiceMenu.DrawOption(
                "objectLibrarySource",
                FontAwesomeIcon.LayerGroup,
                "Object Library",
                libraryDetail,
                _browserSelection.Source == CreateBrowserSelection.CreateBrowserSource.Library))
        {
            _browserSelection.Source = CreateBrowserSelection.CreateBrowserSource.Library;
        }
    }
}
