using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Assets;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.Preview;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.UI;
using Intoner.UI.Performance;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private void DrawCatalogPanel(ObjectCatalogData catalog)
    {
        switch (_draftKind)
        {
            case DraftKind.BgObject:
                DrawBgObjectCatalogBrowser(
                    "##bgobjectCatalog",
                    FontAwesomeIcon.Cube,
                    catalog.BgObjects,
                    ref _bgObjectCreate.CatalogFilter,
                    ref _bgObjectCreate.SourceFilter,
                    _bgObjectCreate.ModelPath,
                    entry => _bgObjectCreate.ModelPath = ToggleCatalogSelectionPath(_bgObjectCreate.ModelPath, entry.PlacementPath),
                    "No bgobject entries match the current filter.");
                break;
            case DraftKind.Furniture:
                DrawFurnitureCatalogBrowser(
                    "##furnitureCatalog",
                    FontAwesomeIcon.Home,
                    catalog.Furniture,
                    ref _furnitureCreate.CatalogFilter,
                    ref _furnitureCreate.CategoryFilter,
                    ToggleFurnitureCatalogSelection,
                    "No furniture entries match the current filter.");
                break;
            case DraftKind.Vfx:
                DrawCatalogBrowser(
                    "##vfxCatalog",
                    FontAwesomeIcon.Magic,
                    catalog.Vfx,
                    ref _vfxCreate.CatalogFilter,
                    ref _vfxCreate.SourceFilter,
                    _vfxCreate.VfxPath,
                    entry => _vfxCreate.VfxPath = ToggleCatalogSelectionPath(_vfxCreate.VfxPath, entry.PlacementPath),
                    "No VFX entries match the current filter.");
                break;
            case DraftKind.Light:
                DrawLightCatalogBrowser();
                break;
        }
    }

    private static string ToggleCatalogSelectionPath(string currentPath, string nextPath)
        => string.Equals(currentPath, nextPath, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : nextPath;

    private bool IsFurnitureCatalogSelection(ObjectCatalogFurnitureResult entry)
        => string.Equals(_furnitureCreate.SharedGroupPath, entry.Entry.PlacementPath, StringComparison.OrdinalIgnoreCase)
        && _furnitureCreate.HousingRowId == entry.Variant.HousingRowId
        && _furnitureCreate.ItemRowId == entry.Variant.ItemRowId;

    private void ToggleFurnitureCatalogSelection(ObjectCatalogFurnitureResult entry)
    {
        if (IsFurnitureCatalogSelection(entry))
        {
            ClearFurnitureCatalogSelection();
            return;
        }

        _furnitureCreate.SharedGroupPath = entry.Entry.PlacementPath;
        _furnitureCreate.HousingRowId = entry.Variant.HousingRowId;
        _furnitureCreate.ItemRowId = entry.Variant.ItemRowId;
    }

    private void ClearFurnitureCatalogSelection()
    {
        _furnitureCreate.SharedGroupPath = string.Empty;
        _furnitureCreate.HousingRowId = 0;
        _furnitureCreate.ItemRowId = 0;
    }

    private void DrawLightCatalogBrowser()
    {
        var currentFilter = _lightCreate.CatalogFilter;
        var filteredEntries = FilterLightCatalogEntries(currentFilter);
        DrawCatalogHeaderCardCore(
            "##lightCatalog",
            FontAwesomeIcon.Sun,
            "Light Types",
            LightCatalogEntries.Count,
            filteredEntries.Count,
            ref currentFilter,
            "search light type or description",
            null,
            0f,
            null);
        _lightCreate.CatalogFilter = currentFilter;
        filteredEntries = FilterLightCatalogEntries(_lightCreate.CatalogFilter);

        var background = EditorColors.ButtonDefault with { W = 0.28f };
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, background);
        using var child = ObjectScrollList.Begin(
            "##lightCatalog_entries",
            new Vector2(0f, 0f),
            CreateOverlayScrollPanelOptions(background, Scaled(8f), EditorColors.AccentPurple),
            true);
        if (!child)
        {
            return;
        }

        if (filteredEntries.Count == 0)
        {
            DrawCatalogEmptyState("No light types match the current filter.");
            return;
        }

        var itemHeight   = Scaled(48f);
        var itemSpacingY = ResolveObjectListItemSpacingY();
        UiVirtualList.Draw(
            filteredEntries,
            UiVirtualListOptions.Rows(itemHeight, itemSpacingY),
            (entry, _) =>
            {
                DrawCatalogSelectionCardCore(
                    $"lightCatalog:{entry.Type}",
                    entry.Name,
                    entry.Description,
                    entry.BadgeIcon.ToIconString(),
                    _lightCreate.Model.LightType == entry.Type,
                    () => _lightCreate.Model = _lightCreate.Model with { LightType = entry.Type },
                    itemHeight,
                    entry.BadgeTooltip,
                    badgeUsesIconFont: true);
            });
    }

    private void DrawBgObjectCatalogBrowser(
        string id,
        FontAwesomeIcon icon,
        ObjectCatalogSection section,
        ref string filter,
        ref string sourceFilter,
        string selectedPath,
        Action<ObjectCatalogEntry> onSelect,
        string emptyText)
    {
        var currentFilter = filter;
        var currentSourceFilter = sourceFilter;
        var filteredEntries = section.FilterBySource(currentFilter, currentSourceFilter);

        DrawCatalogHeaderCardCore(
            id,
            icon,
            section.DisplayName,
            section.Count,
            filteredEntries.Count,
            ref currentFilter,
            "search name, row, source, or path",
            () => DrawCatalogLayoutToggleButtons($"{id}_layout", ref _bgObjectCreate.CatalogLayout),
            MeasureCatalogLayoutToggleButtonsWidth(),
            () =>
            {
                currentSourceFilter = DrawCatalogFilterButtons(id, currentSourceFilter, section.SourceFilters);
            });

        filter = currentFilter;
        sourceFilter = currentSourceFilter;
        filteredEntries = section.FilterBySource(filter, sourceFilter);

        switch (_bgObjectCreate.CatalogLayout)
        {
            case CatalogLayoutMode.Grid:
                DrawBgObjectCatalogEntriesGrid(id, filteredEntries, selectedPath, onSelect, emptyText);
                break;
            default:
                DrawCatalogEntriesList(id, filteredEntries, selectedPath, onSelect, emptyText);
                break;
        }
    }

    private void DrawCatalogBrowser(
        string id,
        FontAwesomeIcon icon,
        ObjectCatalogSection section,
        ref string filter,
        ref string sourceFilter,
        string selectedPath,
        Action<ObjectCatalogEntry> onSelect,
        string emptyText)
    {
        var currentFilter = filter;
        var currentSourceFilter = sourceFilter;
        var filteredEntries = section.FilterBySource(currentFilter, currentSourceFilter);

        DrawCatalogHeaderCard(
            id,
            icon,
            section,
            ref currentFilter,
            "search name, row, source, or path",
            ref currentSourceFilter,
            section.SourceFilters,
            filteredEntries.Count);

        filter = currentFilter;
        sourceFilter = currentSourceFilter;
        filteredEntries = section.FilterBySource(filter, sourceFilter);
        DrawCatalogEntriesList(id, filteredEntries, selectedPath, onSelect, emptyText);
    }

    private void DrawFurnitureCatalogBrowser(
        string id,
        FontAwesomeIcon icon,
        ObjectCatalogSection section,
        ref string filter,
        ref string categoryFilter,
        Action<ObjectCatalogFurnitureResult> onSelect,
        string emptyText)
    {
        var currentFilter = filter;
        var currentCategoryFilter = categoryFilter;
        var filteredEntries = ResolveSelectedFurnitureCatalogEntry(
            FilterHousingFurnitureCatalogEntries(section.FilterFurniture(currentFilter, currentCategoryFilter)),
            currentFilter,
            currentCategoryFilter);

        DrawCatalogHeaderCard(
            id,
            icon,
            section,
            ref currentFilter,
            "search name, row, category, or path",
            ref currentCategoryFilter,
            section.CategoryFilters,
            filteredEntries.Count);

        filter = currentFilter;
        categoryFilter = currentCategoryFilter;
        filteredEntries = ResolveSelectedFurnitureCatalogEntry(
            FilterHousingFurnitureCatalogEntries(section.FilterFurniture(filter, categoryFilter)),
            filter,
            categoryFilter);
        DrawFurnitureCatalogEntriesList(id, filteredEntries, onSelect, emptyText);
    }

    private IReadOnlyList<ObjectCatalogFurnitureResult> FilterHousingFurnitureCatalogEntries(IReadOnlyList<ObjectCatalogFurnitureResult> entries)
    {
        ObjectHousingModeState state = _housingModePolicy.GetState();
        if (!state.IsHousingMode)
        {
            return entries;
        }

        return entries
            .Where(entry => HousingFurnitureAreaPolicy.AllowsArea(entry.Variant.HousingMetadata, state.Area))
            .ToList();
    }

    private IReadOnlyList<ObjectCatalogFurnitureResult> ResolveSelectedFurnitureCatalogEntry(
        IReadOnlyList<ObjectCatalogFurnitureResult> entries,
        string filter,
        string categoryFilter)
    {
        if (!string.IsNullOrWhiteSpace(filter)
         || !string.IsNullOrWhiteSpace(categoryFilter)
         || string.IsNullOrWhiteSpace(_furnitureCreate.SharedGroupPath))
        {
            return entries;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            ObjectCatalogFurnitureResult entry = entries[i];
            if (!string.Equals(entry.Entry.PlacementPath, _furnitureCreate.SharedGroupPath, StringComparison.OrdinalIgnoreCase)
             || !TryResolveSelectedFurnitureVariant(entry.Entry, out ObjectCatalogFurnitureVariant? selectedVariant))
            {
                continue;
            }

            if (entry.Variant.HousingRowId == selectedVariant.HousingRowId
             && entry.Variant.ItemRowId == selectedVariant.ItemRowId)
            {
                return entries;
            }

            ObjectCatalogFurnitureResult[] resolvedEntries = entries.ToArray();
            resolvedEntries[i] = new ObjectCatalogFurnitureResult(entry.Entry, selectedVariant);
            return resolvedEntries;
        }

        return entries;
    }

    private bool TryResolveSelectedFurnitureVariant(
        ObjectCatalogEntry entry,
        [NotNullWhen(true)] out ObjectCatalogFurnitureVariant? variant)
    {
        if (entry.FurnitureInfo is not { } furnitureInfo)
        {
            variant = null;
            return false;
        }

        return furnitureInfo.TryResolveVariant(
            _furnitureCreate.HousingRowId,
            _furnitureCreate.ItemRowId,
            out variant);
    }

    private static void DrawCatalogLayoutToggleButtons(string id, ref CatalogLayoutMode layoutMode)
    {
        var buttonEdge = ResolveCatalogLayoutToggleButtonEdge();
        var buttonSize = new Vector2(buttonEdge, buttonEdge);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        using (ImRaii.Group())
        {
            if (DrawAccentToggleIconButton($"{id}_grid", FontAwesomeIcon.BorderAll, buttonSize, layoutMode == CatalogLayoutMode.Grid, "grid layout"))
            {
                layoutMode = CatalogLayoutMode.Grid;
            }

            ImGui.SameLine(0f, spacing);
            if (DrawAccentToggleIconButton($"{id}_list", FontAwesomeIcon.Bars, buttonSize, layoutMode == CatalogLayoutMode.List, "list layout"))
            {
                layoutMode = CatalogLayoutMode.List;
            }
        }
    }

    private static float MeasureCatalogLayoutToggleButtonsWidth()
    {
        return (ResolveCatalogLayoutToggleButtonEdge() * 2f) + ImGui.GetStyle().ItemSpacing.X;
    }

    private static float ResolveCatalogLayoutToggleButtonEdge()
    {
        return MathF.Max(
            ResolveSquareIconButtonMetrics(FontAwesomeIcon.BorderAll.ToIconString()).Edge,
            ResolveSquareIconButtonMetrics(FontAwesomeIcon.Bars.ToIconString()).Edge);
    }

    private void DrawCatalogEntriesList(
        string id,
        IReadOnlyList<ObjectCatalogEntry> filteredEntries,
        string selectedPath,
        Action<ObjectCatalogEntry> onSelect,
        string emptyText)
        => DrawCatalogEntriesListCore(
            id,
            filteredEntries,
            emptyText,
            (entry, itemHeight) =>
            {
                DrawCatalogEntryCard(
                    entry,
                    string.Equals(selectedPath, entry.PlacementPath, StringComparison.OrdinalIgnoreCase),
                    () => onSelect(entry),
                    itemHeight);
            });

    private void DrawFurnitureCatalogEntriesList(
        string id,
        IReadOnlyList<ObjectCatalogFurnitureResult> filteredEntries,
        Action<ObjectCatalogFurnitureResult> onSelect,
        string emptyText)
        => DrawCatalogEntriesListCore(
            id,
            filteredEntries,
            emptyText,
            (entry, itemHeight) =>
            {
                DrawFurnitureCatalogEntryCard(
                    entry,
                    IsFurnitureCatalogSelection(entry),
                    () => onSelect(entry),
                    itemHeight);
            });

    private void DrawCatalogEntriesListCore<TEntry>(
        string id,
        IReadOnlyList<TEntry> filteredEntries,
        string emptyText,
        Action<TEntry, float> drawEntry)
    {
        var background = EditorColors.ButtonDefault with { W = 0.28f };
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, background);
        using var entriesChild = ObjectScrollList.Begin(
            $"{id}_entries",
            new Vector2(0f, 0f),
            CreateOverlayScrollPanelOptions(background, Scaled(8f), EditorColors.AccentPurple),
            true);
        if (!entriesChild)
        {
            return;
        }

        if (filteredEntries.Count == 0)
        {
            DrawCatalogEmptyState(emptyText);
            return;
        }

        var itemHeight   = Scaled(48f);
        var itemSpacingY = ResolveObjectListItemSpacingY();
        UiVirtualList.Draw(
            filteredEntries,
            UiVirtualListOptions.Rows(itemHeight, itemSpacingY) with { DrawTrailingSpacing = true },
            (entry, _) => drawEntry(entry, itemHeight));
    }

    private void DrawBgObjectCatalogEntriesGrid(
        string id,
        IReadOnlyList<ObjectCatalogEntry> filteredEntries,
        string selectedPath,
        Action<ObjectCatalogEntry> onSelect,
        string emptyText)
    {
        var background = EditorColors.ButtonDefault with { W = 0.28f };
        using var childBg = ImRaii.PushColor(ImGuiCol.ChildBg, background);
        using var entriesChild = ObjectScrollList.Begin(
            $"{id}_entries",
            new Vector2(0f, 0f),
            CreateOverlayScrollPanelOptions(background, Scaled(8f), EditorColors.AccentPurple),
            true);
        if (!entriesChild)
        {
            return;
        }

        if (filteredEntries.Count == 0)
        {
            DrawCatalogEmptyState(emptyText);
            return;
        }

        const int columns = 5;
        float tileSpacing = ImGui.GetStyle().ItemSpacing.X;
        float availableWidth = Positive(ImGui.GetContentRegionAvail().X);
        float tileEdge = Positive((availableWidth - ((columns - 1) * tileSpacing)) / columns);
        int rowCount = (filteredEntries.Count + columns - 1) / columns;
        Vector2 tileSize = new(tileEdge, tileEdge);

        UiVirtualList.Draw(
            rowCount,
            UiVirtualListOptions.Rows(tileEdge, tileSpacing) with { DrawTrailingSpacing = true },
            row =>
            {
                for (int column = 0; column < columns; column++)
                {
                    int entryIndex = (row * columns) + column;
                    if (entryIndex >= filteredEntries.Count)
                    {
                        break;
                    }

                    ObjectCatalogEntry entry = filteredEntries[entryIndex];
                    DrawBgObjectCatalogGridTile(
                        entry,
                        string.Equals(selectedPath, entry.PlacementPath, StringComparison.OrdinalIgnoreCase),
                        () => onSelect(entry),
                        tileSize);

                    if (column + 1 < columns && entryIndex + 1 < filteredEntries.Count)
                    {
                        ImGui.SameLine(0f, tileSpacing);
                    }
                }
            });
    }

    private static IReadOnlyList<LightCatalogEntry> FilterLightCatalogEntries(string filter)
    {
        string[] searchTokens = ObjectSearchTermUtility.BuildSearchTokens(filter);
        if (searchTokens.Length == 0)
        {
            return LightCatalogEntries;
        }

        return LightCatalogEntries
            .Where(entry => ObjectSearchTermUtility.MatchesSearchText(BuildLightCatalogSearchText(entry), searchTokens))
            .ToList();
    }

    private static string BuildLightCatalogSearchText(LightCatalogEntry entry)
        => ObjectSearchTermUtility.BuildSearchText(
        [
            entry.Name,
            entry.BadgeTooltip,
            entry.Description,
            entry.Type.ToString(),
        ]);

    private void DrawCatalogHeaderCard(
        string id,
        FontAwesomeIcon icon,
        ObjectCatalogSection section,
        ref string filter,
        string searchHint,
        ref string groupFilter,
        IReadOnlyList<ObjectCatalogFilterCount> groupCounts,
        int filteredCount)
    {
        var currentFilter = filter;
        var currentGroupFilter = groupFilter;
        DrawCatalogHeaderCardCore(
            id,
            icon,
            section.DisplayName,
            section.Count,
            filteredCount,
            ref currentFilter,
            searchHint,
            null,
            0f,
            () =>
            {
                currentGroupFilter = DrawCatalogFilterButtons(id, currentGroupFilter, groupCounts);
            });

        filter = currentFilter;
        groupFilter = currentGroupFilter;
    }

    private static void DrawCatalogHeaderCardCore(
        string id,
        FontAwesomeIcon icon,
        string title,
        int totalCount,
        int filteredCount,
        ref string filter,
        string searchHint,
        Action? drawHeaderActions,
        float headerActionsWidth,
        Action? drawAfterFilter)
    {
        var accent = EditorColors.AccentPurple;
        var currentFilter = filter;
        var cardPadding = ResolveObjectListCardPadding();
        DrawPanelCard(
            $"{id}_header",
            EditorColors.ButtonDefault with { W = 0.30f },
            accent with { W = 0.28f },
            Scaled(8f),
            cardPadding,
            () =>
            {
                Vector2 headerRowStart = ImGui.GetCursorPos();
                float headerRowWidth = ImGui.GetContentRegionAvail().X;
                float headerRowMaxX = headerRowStart.X + headerRowWidth;
                using (ImRaii.PushFont(UiBuilder.IconFont))
                using (ImRaii.PushColor(ImGuiCol.Text, accent))
                {
                    ImGui.TextUnformatted(icon.ToIconString());
                }

                ImGui.SameLine();
                using (ImRaii.Group())
                {
                    ImGui.TextUnformatted(title);
                    ImGui.TextDisabled(filteredCount == totalCount
                        ? $"{totalCount} entries"
                        : $"{filteredCount} shown of {totalCount}");
                }

                float headerRowEndY = ImGui.GetCursorPosY();

                if (drawHeaderActions is not null && headerActionsWidth > 0f)
                {
                    float actionHeight = ResolveCatalogLayoutToggleButtonEdge();
                    float actionY = headerRowStart.Y + MathF.Max(0f, ((headerRowEndY - headerRowStart.Y) - actionHeight) * 0.5f);
                    var previousCursorY = ImGui.GetCursorPosY();
                    var rightAlignedX = MathF.Max(ImGui.GetCursorPosX(), headerRowMaxX - headerActionsWidth);
                    ImGui.SetCursorPos(new Vector2(rightAlignedX, actionY));
                    drawHeaderActions();
                    ImGui.SetCursorPos(new Vector2(headerRowStart.X, MathF.Max(headerRowEndY, previousCursorY)));
                }

                ImGuiHelpers.ScaledDummy(6f);
                DrawCatalogFilterInputRow(id, searchHint, ref currentFilter);
                drawAfterFilter?.Invoke();
            });
        filter = currentFilter;
    }

    private static void DrawCatalogFilterInputRow(string id, string searchHint, ref string filter)
    {
        var clearButtonWidth = Scaled(28f);
        using (ImRaii.PushColor(ImGuiCol.FrameBg, EditorColors.ButtonDefault with { W = 0.75f }))
        {
            ImGui.SetNextItemWidth(Positive(ImGui.GetContentRegionAvail().X - clearButtonWidth - ImGui.GetStyle().ItemSpacing.X));
            ImGui.InputTextWithHint($"{id}_filter", searchHint, ref filter, 128);
        }

        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            if (ImGui.Button($"{FontAwesomeIcon.Times.ToIconString()}##clear{id}", new Vector2(clearButtonWidth, 0f)))
            {
                filter = string.Empty;
            }
        }
    }

    private string DrawCatalogFilterButtons(string id, string filterValue, IReadOnlyList<ObjectCatalogFilterCount> filterCounts)
    {
        var currentFilterValue = filterValue;
        ImGuiStylePtr style = ImGui.GetStyle();
        Vector2 baseFramePadding = style.FramePadding;
        float available = ImGui.GetContentRegionAvail().X;
        float buttonHeight = ImGui.GetFrameHeight();
        float arrowWidth = buttonHeight;
        float scrollWidth = Math.Max(0f, available - (arrowWidth * 2f + style.ItemSpacing.X * 2f));
        scrollWidth = Math.Max(scrollWidth, 120f * ImGuiHelpers.GlobalScale);

        float totalWidth = GetCatalogFilterButtonWidth("All", baseFramePadding);
        foreach (var filterCount in filterCounts)
        {
            totalWidth += style.ItemSpacing.X + GetCatalogFilterButtonWidth($"{filterCount.Label} ({filterCount.Count})", baseFramePadding);
        }

        bool showScrollbar = totalWidth > scrollWidth;
        float childHeight = buttonHeight + style.FramePadding.Y * 2f + (showScrollbar ? style.ScrollbarSize : 0f);
        float scrollStep = scrollWidth > 0f ? scrollWidth * 0.9f : 120f * ImGuiHelpers.GlobalScale;
        _catalogFilterScroll.TryGetValue(id, out float prevScroll);
        _catalogFilterScrollMax.TryGetValue(id, out float prevMax);
        float currentScroll = prevScroll;
        float maxScroll = prevMax;

        using var idScope = ImRaii.PushId($"{id}_catalog_filters");
        using (ImRaii.Group())
        {
            if (DrawCatalogFilterArrow("##catalog_filter_left", ImGuiDir.Left, prevScroll <= 0.5f))
            {
                _pendingCatalogFilterScroll[id] = Math.Max(0f, currentScroll - scrollStep);
            }

            ImGui.SameLine(0f, style.ItemSpacing.X);

            using (ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f)))
            using (var child = ImRaii.Child($"{id}_filter_scroll", new Vector2(scrollWidth, childHeight), false, ImGuiWindowFlags.HorizontalScrollbar))
            {
                if (child)
                {
                    var first = true;
                    if (DrawCatalogFilterButton($"{id}_all", "All", string.IsNullOrWhiteSpace(currentFilterValue)))
                    {
                        currentFilterValue = string.Empty;
                    }

                    first = false;

                    foreach (var filterCount in filterCounts)
                    {
                        if (!first)
                        {
                            ImGui.SameLine();
                        }

                        var label = $"{filterCount.Label} ({filterCount.Count})";
                        if (DrawCatalogFilterButton($"{id}_{filterCount.Label}", label, string.Equals(currentFilterValue, filterCount.Label, StringComparison.OrdinalIgnoreCase)))
                        {
                            currentFilterValue = filterCount.Label;
                        }

                        first = false;
                    }

                    if (_pendingCatalogFilterScroll.Remove(id, out var pendingScroll))
                    {
                        ImGui.SetScrollX(pendingScroll);
                    }

                    currentScroll = ImGui.GetScrollX();
                    maxScroll = ImGui.GetScrollMaxX();
                    MarkCurrentWindowAsEditorOverlayTarget();
                }
            }

            ImGui.SameLine(0f, style.ItemSpacing.X);

            if (DrawCatalogFilterArrow("##catalog_filter_right", ImGuiDir.Right, prevScroll >= prevMax - 0.5f))
            {
                _pendingCatalogFilterScroll[id] = Math.Min(prevScroll + scrollStep, prevMax);
            }
        }

        _catalogFilterScroll[id] = currentScroll;
        _catalogFilterScrollMax[id] = maxScroll;

        return currentFilterValue;
    }

    private static bool DrawCatalogFilterArrow(string id, ImGuiDir direction, bool disabled)
    {
        using var disabledScope = ImRaii.Disabled(disabled);
        using var normalColor = ImRaii.PushColor(ImGuiCol.Button, EditorColors.ButtonDefault);
        using var hoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, EditorColors.AccentPurple with { W = 0.85f });
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, EditorColors.AccentPurpleDefault with { W = 0.75f });
        return ImGui.ArrowButton(id, direction);
    }

    private static float GetCatalogFilterButtonWidth(string label, Vector2 framePadding)
    {
        return ImGui.CalcTextSize(label).X + (framePadding.X * 2f);
    }

    private static bool DrawCatalogFilterButton(string id, string label, bool selected)
    {
        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;

        return ImGui.Button($"{label}##{id}");
    }

    private static void DrawCatalogEmptyState(string text)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = MathF.Max(Scaled(120f), ImGui.GetContentRegionAvail().Y);
        var accent = EditorColors.AccentPurple;
        var icon = FontAwesomeIcon.Search.ToIconString();
        var iconColor = accent with { W = 0.8f };
        var textWrapWidth = Positive(width - Scaled(36f));
        var iconSize = Vector2.Zero;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(icon);
        }

        var messageSize = ImGui.CalcTextSize(text, wrapWidth: textWrapWidth);
        var blockSpacing = Scaled(12f);
        var blockHeight = iconSize.Y + blockSpacing + messageSize.Y;
        var blockStartY = start.Y + MathF.Max(0f, (height - blockHeight) * 0.5f);

        ImGui.SetCursorScreenPos(new Vector2(start.X + ((width - iconSize.X) * 0.5f), blockStartY));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, iconColor))
        {
            ImGui.TextUnformatted(icon);
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X + ((width - messageSize.X) * 0.5f), blockStartY + iconSize.Y + blockSpacing));
        ImGui.TextWrapped(text);
        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawBgObjectCatalogGridTile(ObjectCatalogEntry entry, bool selected, Action onSelect, Vector2 size)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float innerPadding = 8f * scale;
        var preview = string.IsNullOrWhiteSpace(entry.PlacementPath)
            ? new PreviewRender.Result(null, false, "Preview unavailable")
            : _previewService.GetPreview(
                CatalogPreviewAssetFactory.Create(entry),
                CreateCatalogThumbnailRequest(size, innerPadding));

        ImGui.InvisibleButton($"##bgobjectCatalogGrid:{entry.Source}:{entry.RowId}:{entry.PlacementPath}", size);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            onSelect();
        }

        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenOverlapped);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiSharedService.AttachToolTip(BuildCatalogEntryTooltip(entry, preview));
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var accent = EditorColors.AccentPurple;
        var fill = selected
            ? accent with { W = 0.16f }
            : EditorColors.ButtonDefault with { W = 0.24f };
        Vector4 border;
        if (selected)
        {
            border = accent with { W = 0.88f };
        }
        else if (hovered)
        {
            border = accent with { W = 0.54f };
        }
        else
        {
            border = EditorColors.Border with { W = 0.34f };
        }
        float rounding = 10f * scale;
        var previewMin = min + new Vector2(innerPadding);
        var previewMax = max - new Vector2(innerPadding);
        float imageRounding = MathF.Max(6f * scale, rounding - (2f * scale));

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, hovered || selected ? 1.35f * scale : 1f * scale);

        if (preview.Texture is not null)
        {
            drawList.AddImageRounded(preview.Texture.Handle, previewMin, previewMax, Vector2.Zero, Vector2.One, 0xFFFFFFFF, imageRounding);
        }
        else
        {
            drawList.AddRectFilled(
                previewMin,
                previewMax,
                ImGui.GetColorU32(PreviewRender.BackgroundPalette.GetPlaceholderFill(PreviewRender.BackgroundStyle.White)),
                imageRounding);
            if (preview.IsLoading)
            {
                DrawCenteredCatalogTileMessage(
                    drawList,
                    previewMin,
                    previewMax,
                    "Loading...",
                    accent with { W = 0.88f });
            }
            else
            {
                DrawCenteredCatalogTileIcon(
                    drawList,
                    previewMin,
                    previewMax,
                    FontAwesomeIcon.Cube,
                    accent with { W = 0.88f });
            }
        }
    }

    private static PreviewRender.Request CreateCatalogThumbnailRequest(Vector2 tileSize, float innerPadding)
    {
        float previewWidth = MathF.Max(1f, tileSize.X - (innerPadding * 2f));
        float previewHeight = MathF.Max(1f, tileSize.Y - (innerPadding * 2f));

        return new(
            (int)MathF.Round(previewWidth),
            (int)MathF.Round(previewHeight),
            -85,
            34,
            100,
            PreviewRender.BackgroundStyle.White,
            PreviewRender.Mode.Thumbnail);
    }

    private static string BuildCatalogEntryTooltip(ObjectCatalogEntry entry, PreviewRender.Result preview)
    {
        string status = string.Empty;
        if (preview.IsLoading)
        {
            status = "\nPreview: loading";
        }
        else if (!string.IsNullOrWhiteSpace(preview.Error))
        {
            status = $"\nPreview: {preview.Error}";
        }

        return $"{entry.Name}\n#{entry.RowId} | {entry.Source}\n{entry.DisplayPath}{status}";
    }

    private static void DrawCenteredCatalogTileMessage(ImDrawListPtr drawList, Vector2 min, Vector2 max, string text, Vector4 color)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = MathF.Max(1f, (max.X - min.X) - (16f * scale));
        string renderedText = ClipTextToWidth(text, width);
        Vector2 textSize = ImGui.CalcTextSize(renderedText);
        var textPosition = new Vector2(
            min.X + MathF.Max(0f, ((max.X - min.X) - textSize.X) * 0.5f),
            min.Y + MathF.Max(0f, ((max.Y - min.Y) - textSize.Y) * 0.5f));
        drawList.AddText(textPosition, ImGui.GetColorU32(color), renderedText);
    }

    private static void DrawCenteredCatalogTileIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max, FontAwesomeIcon icon, Vector4 color)
    {
        string iconText = icon.ToIconString();
        Vector2 iconSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var iconPosition = new Vector2(
            min.X + MathF.Max(0f, ((max.X - min.X) - iconSize.X) * 0.5f),
            min.Y + MathF.Max(0f, ((max.Y - min.Y) - iconSize.Y) * 0.5f));

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(iconPosition, ImGui.GetColorU32(color), iconText);
        }
    }

    private void DrawCatalogEntryCard(ObjectCatalogEntry entry, bool selected, Action onSelect, float height)
    {
        if (entry.Kind == ObjectCatalogKind.Furniture && entry.FurnitureInfo is { } furnitureInfo)
        {
            DrawFurnitureCatalogEntryCard(new ObjectCatalogFurnitureResult(entry, furnitureInfo.PrimaryVariant), selected, onSelect, height);
            return;
        }

        if (entry.Kind == ObjectCatalogKind.Vfx && entry.VfxInfo is not null)
        {
            DrawVfxCatalogEntryCard(entry, entry.VfxInfo, selected, onSelect, height);
            return;
        }

        DrawCatalogSelectionCardCore(
            $"{entry.Source}:{entry.RowId}:{entry.PlacementPath}",
            entry.Name,
            entry.DisplayPath,
            $"#{entry.RowId}",
            selected,
            onSelect,
            height,
            null,
            badgeUsesIconFont: false);
    }

    private void DrawFurnitureCatalogEntryCard(ObjectCatalogFurnitureResult result, bool selected, Action onSelect, float height)
    {
        ObjectCatalogEntry entry = result.Entry;
        ObjectCatalogFurnitureVariant variant = result.Variant;
        FontAwesomeIcon? titleIcon = null;
        string? titleIconTooltip = null;
        Vector4? titleIconColor = null;
        uint? itemIconId = variant.IconId > 0 ? variant.IconId : null;

        if (variant.DyeCount > 0)
        {
            titleIcon = FontAwesomeIcon.Palette;
            titleIconTooltip = variant.DyeCount == 1
                ? "1 dye channel"
                : $"{variant.DyeCount} dye channels";
            titleIconColor = EditorColors.AccentOrange with { W = selected ? 0.95f : 0.82f };
        }

        uint rowId = variant.HousingRowId != 0 ? variant.HousingRowId : entry.RowId;
        string name = string.IsNullOrWhiteSpace(variant.Name) ? entry.Name : variant.Name;
        DrawCatalogSelectionCardCore(
            $"{entry.Source}:{variant.HousingRowId}:{variant.ItemRowId}:{entry.PlacementPath}",
            name,
            entry.DisplayPath,
            $"#{rowId}",
            selected,
            onSelect,
            height,
            null,
            badgeUsesIconFont: false,
            itemIconId,
            titleIcon,
            titleIconTooltip,
            titleIconColor);
    }

    private void DrawVfxCatalogEntryCard(ObjectCatalogEntry entry, ObjectCatalogVfxInfo vfxInfo, bool selected, Action onSelect, float height)
    {
        FontAwesomeIcon? titleIcon = null;
        string? titleIconTooltip = null;
        Vector4? titleIconColor = null;
        if (vfxInfo.IsPermanentLoop)
        {
            titleIcon = FontAwesomeIcon.Repeat;
            titleIconTooltip = "permanent loop";
            titleIconColor = EditorColors.AccentGreen with { W = selected ? 0.95f : 0.82f };
        }

        DrawCatalogSelectionCardCore(
            $"{entry.Source}:{entry.RowId}:{entry.PlacementPath}",
            entry.Name,
            entry.DisplayPath,
            null,
            selected,
            onSelect,
            height,
            null,
            badgeUsesIconFont: false,
            titleIcon: titleIcon,
            titleIconTooltip: titleIconTooltip,
            titleIconColor: titleIconColor);
    }

    private void DrawCatalogSelectionCardCore(
        string id,
        string title,
        string detail,
        string? badgeText,
        bool selected,
        Action onSelect,
        float height,
        string? badgeTooltip,
        bool badgeUsesIconFont,
        uint? itemIconId = null,
        FontAwesomeIcon? titleIcon = null,
        string? titleIconTooltip = null,
        Vector4? titleIconColor = null)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var accent = EditorColors.AccentPurple;

        ListEntryCardInteraction interaction = DrawListEntryCardInteraction($"catalogEntry:{id}", selected, new Vector2(width, height));
        if (interaction.Clicked)
        {
            onSelect();
        }

        var drawList = ImGui.GetWindowDrawList();
        var min = interaction.Min;
        var max = interaction.Max;
        var text = EditorColors.Text;
        var disabledText = EditorColors.TextDisabled;
        var padX = Scaled(10f);
        var padY = Scaled(8f);
        var itemIconSize = Scaled(36f);
        var itemIconSpacing = Scaled(9f);

        DrawListEntryCardChrome(drawList, min, max, selected, interaction.Hovered, accent);

        var badgePaddingX = Scaled(7f);
        var badgePaddingY = Scaled(3f);
        var contentRight = max.X - padX;
        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            var badgeTextSize = badgeUsesIconFont
                ? MeasureCatalogBadgeText(badgeText)
                : ImGui.CalcTextSize(badgeText);
            var badgeMin = new Vector2(max.X - badgeTextSize.X - (badgePaddingX * 2f) - padX, min.Y + padY);
            var badgeMax = new Vector2(max.X - padX, badgeMin.Y + badgeTextSize.Y + (badgePaddingY * 2f));
            drawList.AddRectFilled(badgeMin, badgeMax, ImGui.GetColorU32(accent with { W = selected ? 0.36f : 0.22f }), 999f);
            if (badgeUsesIconFont)
            {
                drawList.AddText(
                    UiBuilder.IconFont,
                    UiBuilder.IconFont.FontSize,
                    new Vector2(badgeMin.X + badgePaddingX, badgeMin.Y + badgePaddingY),
                    ImGui.GetColorU32(text),
                    badgeText);
            }
            else
            {
                drawList.AddText(new Vector2(badgeMin.X + badgePaddingX, badgeMin.Y + badgePaddingY), ImGui.GetColorU32(text), badgeText);
            }

            if (!string.IsNullOrWhiteSpace(badgeTooltip) && EditorInputUtility.IsMouseInside(badgeMin, badgeMax))
            {
                UiSharedService.DrawAccentTooltipText(badgeTooltip, accent, wrapEms: 35f);
            }

            contentRight = badgeMin.X - padX;
        }

        var contentMinX = min.X + padX;
        if (itemIconId is > 0)
        {
            var iconMin = new Vector2(contentMinX, min.Y + MathF.Max(0f, ((max.Y - min.Y) - itemIconSize) * 0.5f));
            var iconMax = iconMin + new Vector2(itemIconSize);
            var iconRounding = Scaled(6f);
            var iconFill = selected
                ? accent with { W = 0.20f }
                : EditorColors.WindowBg with { W = 0.28f };
            var iconBorder = selected
                ? accent with { W = 0.52f }
                : EditorColors.Border with { W = 0.28f };
            drawList.AddRectFilled(iconMin, iconMax, ImGui.GetColorU32(iconFill), iconRounding);
            drawList.AddRect(iconMin, iconMax, ImGui.GetColorU32(iconBorder), iconRounding);

            if (TryGetCatalogItemIcon(itemIconId.Value, out var itemIconWrap) && itemIconWrap is not null)
            {
                var iconInset = 0f;
                drawList.AddImage(
                    itemIconWrap.Handle,
                    iconMin + new Vector2(iconInset),
                    iconMax - new Vector2(iconInset));
            }

            contentMinX = iconMax.X + itemIconSpacing;
        }

        Vector2 titleIconSize = Vector2.Zero;
        var titleIconSpacing = 0f;
        var titleIconText = string.Empty;
        if (titleIcon is { } resolvedTitleIcon)
        {
            titleIconText = resolvedTitleIcon.ToIconString();
            titleIconSpacing = Scaled(7f);
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                titleIconSize = ImGui.CalcTextSize(titleIconText);
            }
        }

        var nameWidth = MathF.Max(ResolveMinimumCardTextWidth(), contentRight - contentMinX - titleIconSize.X - titleIconSpacing);
        var nameText = ClipTextToWidth(title, nameWidth);
        var titlePosition = new Vector2(contentMinX, min.Y + padY);
        drawList.AddText(titlePosition, ImGui.GetColorU32(text), nameText);

        if (titleIcon is not null)
        {
            var renderedNameWidth = ImGui.CalcTextSize(nameText).X;
            var titleIconPosition = new Vector2(titlePosition.X + renderedNameWidth + titleIconSpacing, titlePosition.Y);
            var iconColor = titleIconColor ?? accent with { W = selected ? 0.92f : 0.76f };
            drawList.AddText(
                UiBuilder.IconFont,
                UiBuilder.IconFont.FontSize,
                titleIconPosition,
                ImGui.GetColorU32(iconColor),
                titleIconText);

            if (!string.IsNullOrWhiteSpace(titleIconTooltip))
            {
                var titleIconMin = titleIconPosition;
                var titleIconMax = new Vector2(titleIconPosition.X + titleIconSize.X, titleIconPosition.Y + titleIconSize.Y);
                if (EditorInputUtility.IsMouseInside(titleIconMin, titleIconMax))
                {
                    UiSharedService.DrawAccentTooltipText(titleIconTooltip, iconColor, wrapEms: 35f);
                }
            }
        }

        var metaY = min.Y + padY + ImGui.GetTextLineHeight() + Scaled(4f);
        drawList.AddText(
            new Vector2(contentMinX, metaY),
            ImGui.GetColorU32(disabledText with { W = 0.88f }),
            ClipTextToWidth(detail, max.X - contentMinX - padX));
    }

    private bool TryGetCatalogItemIcon(uint iconId, out IDalamudTextureWrap? wrap)
    {
        wrap = null;
        try
        {
            return _uiSharedService.TryGetIcon(iconId, out wrap) && wrap is not null;
        }
        catch
        {
            wrap = null;
            return false;
        }
    }

    private static Vector2 MeasureCatalogBadgeText(string badgeText)
    {
        using var iconFont = ImRaii.PushFont(UiBuilder.IconFont);
        return ImGui.CalcTextSize(badgeText);
    }

}
