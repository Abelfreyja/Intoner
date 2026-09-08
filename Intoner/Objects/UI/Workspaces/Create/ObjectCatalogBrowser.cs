using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Intoner.Objects.Catalog;
using Intoner.Objects.Library;
using Intoner.Objects.Models;
using Intoner.Objects.Preview;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.UI.Performance;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class ObjectCatalogBrowser
{
    private readonly IObjectHousingModePolicy _housingModePolicy;
    private readonly PreviewService _previewService;
    private readonly EditorFilterBar _filterBar;
    private readonly ObjectBrowserRowRenderer _browserRows;
    private readonly CreateBrowserSelection _browserSelection;
    private readonly ObjectCreationDraft _draft;
    private readonly ObjectLibraryBrowser _libraryBrowser;
    private readonly ObjectBrowserPanel _browserPanel;
    private readonly CatalogLibraryStatus _libraryStatus;
    private readonly FurnitureCatalogView _furnitureView = new();
    private string _bgObjectFilter = string.Empty;
    private string _bgObjectSourceFilter = string.Empty;
    private string _furnitureFilter = string.Empty;
    private string _furnitureCategoryFilter = string.Empty;
    private string _vfxFilter = string.Empty;
    private string _vfxSourceFilter = string.Empty;
    private string _lightFilter = string.Empty;
    private CatalogLayoutMode _bgObjectLayout = CatalogLayoutMode.List;
    private CatalogLayoutMode _furnitureLayout = CatalogLayoutMode.List;
    private bool _focusCurrentCatalogEntry;

    public ObjectCatalogBrowser(
        IObjectHousingModePolicy housingModePolicy,
        PreviewService previewService,
        EditorFilterBar filterBar,
        ObjectBrowserRowRenderer browserRows,
        CreateBrowserSelection browserSelection,
        ObjectCreationDraft draft,
        ObjectLibraryBrowser libraryBrowser,
        ObjectBrowserPanel browserPanel,
        CatalogLibraryStatus libraryStatus)
    {
        _housingModePolicy = housingModePolicy;
        _previewService    = previewService;
        _filterBar         = filterBar;
        _browserRows       = browserRows;
        _browserSelection  = browserSelection;
        _draft             = draft;
        _libraryBrowser    = libraryBrowser;
        _browserPanel      = browserPanel;
        _libraryStatus     = libraryStatus;
    }

    internal enum CatalogLayoutMode
    {
        List,
        Grid,
    }

    internal enum CatalogGridTileImageSource
    {
        Preview,
        ItemIcon,
    }

    internal void DrawCatalogPanel(ObjectCatalogData catalog)
    {
        if (_browserSelection.Source == CreateBrowserSelection.CreateBrowserSource.Library)
        {
            _libraryBrowser.DrawObjectLibraryBrowser(ResolveCatalogEntryCount(catalog, _browserSelection.Kind));
            return;
        }

        switch (_browserSelection.Kind)
        {
            case DraftKind.BgObject:
                DrawBgObjectCatalogBrowser(
                    "##bgobjectCatalog",
                    FontAwesomeIcon.Cube,
                    catalog.BgObjects,
                    ref _bgObjectFilter,
                    ref _bgObjectSourceFilter,
                    _draft.BgObject.Model.ModelPath,
                    entry => _draft.BgObject.Model = _draft.BgObject.Model with
                    {
                        ModelPath = ObjectCreationDraft.ToggleCatalogSelectionPath(_draft.BgObject.Model.ModelPath, entry.PlacementPath),
                    },
                    "No bgobject entries match the current filter.");
                break;
            case DraftKind.Furniture:
                DrawFurnitureCatalogBrowser(
                    "##furnitureCatalog",
                    FontAwesomeIcon.Home,
                    catalog.Furniture,
                    ref _furnitureFilter,
                    ref _furnitureCategoryFilter,
                    _draft.ToggleFurnitureCatalogSelection,
                    "No furniture entries match the current filter.");
                break;
            case DraftKind.Vfx:
                DrawCatalogBrowser(
                    "##vfxCatalog",
                    FontAwesomeIcon.Magic,
                    catalog.Vfx,
                    ref _vfxFilter,
                    ref _vfxSourceFilter,
                    _draft.Vfx.Model.VfxPath,
                    entry => _draft.Vfx.Model = _draft.Vfx.Model with
                    {
                        VfxPath = ObjectCreationDraft.ToggleCatalogSelectionPath(_draft.Vfx.Model.VfxPath, entry.PlacementPath),
                    },
                    "No VFX entries match the current filter.");
                break;
            case DraftKind.Light:
                DrawLightCatalogBrowser();
                break;
        }
    }

    private static int ResolveCatalogEntryCount(ObjectCatalogData catalog, DraftKind kind)
        => kind switch
        {
            DraftKind.BgObject => catalog.BgObjects.Count,
            DraftKind.Furniture => catalog.Furniture.Count,
            DraftKind.Vfx => catalog.Vfx.Count,
            DraftKind.Light => ObjectEditorCatalog.LightCatalogEntries.Count,
            _ => 0,
        };

    private void DrawLightCatalogBrowser()
    {
        var currentFilter = _lightFilter;
        var filteredEntries = FilterLightCatalogEntries(currentFilter);
        _browserPanel.DrawCatalogHeaderCardCore(
            "##lightCatalog",
            FontAwesomeIcon.Sun,
            "Light Catalog",
            ObjectEditorCatalog.LightCatalogEntries.Count,
            filteredEntries.Count,
            ObjectEditorCatalog.LightCatalogEntries.Count,
            ref currentFilter,
            "search light type or description",
            null,
            Vector2.Zero,
            null);
        _lightFilter = currentFilter;
        filteredEntries = FilterLightCatalogEntries(_lightFilter);
        int focusIndex = _focusCurrentCatalogEntry
            ? ConsumeCatalogFocusIndex(filteredEntries, entry => entry.Type == _draft.Light.Model.LightType)
            : -1;

        DrawCatalogEntriesListCore(
            "##lightCatalog",
            filteredEntries,
            "No light types match the current filter.",
            (entry, itemHeight) =>
            {
                ObjectLibraryPreset libraryPreset = ObjectEditorCatalog.CreateCatalogLibraryPreset(entry);
                _ = _browserRows.DrawObjectBrowserRow(
                    new ObjectBrowserRowRenderer.ObjectBrowserRow
                    {
                        Id = $"lightCatalog:{entry.Type}",
                        Title = entry.Name,
                        Detail = entry.Description,
                        TrailingBadge = ObjectEditorCatalog.ResolveLightTypeBadge(entry),
                        Badges = _libraryStatus.ResolveCatalogBadges(libraryPreset),
                        Selected = _draft.Light.Model.LightType == entry.Type,
                    },
                    itemHeight,
                    () => _draft.Light.Model = _draft.Light.Model with { LightType = entry.Type });
                _libraryBrowser.DrawCatalogEntryLibraryContext(
                    $"light:{entry.Type}",
                    entry.Name,
                    libraryPreset);
            },
            focusIndex);
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

        _browserPanel.DrawCatalogHeaderCard(
            id,
            icon,
            section,
            ref currentFilter,
            "search name, row, source, or path",
            ref currentSourceFilter,
            section.SourceFilters,
            filteredEntries.Count,
            () => DrawCatalogLayoutToggleButtons($"{id}_layout", ref _bgObjectLayout),
            MeasureCatalogLayoutToggleButtonsSize());

        filter = currentFilter;
        sourceFilter = currentSourceFilter;
        filteredEntries = section.FilterBySource(filter, sourceFilter);
        int focusIndex = _focusCurrentCatalogEntry
            ? ConsumeCatalogFocusIndex(
                filteredEntries,
                entry => string.Equals(selectedPath, entry.PlacementPath, StringComparison.OrdinalIgnoreCase))
            : -1;

        DrawCatalogEntries(
            id,
            _bgObjectLayout,
            filteredEntries,
            emptyText,
            (entry, itemHeight) => DrawCatalogEntryCard(
                entry,
                string.Equals(selectedPath, entry.PlacementPath, StringComparison.OrdinalIgnoreCase),
                () => onSelect(entry),
                itemHeight),
            (entry, tileSize) =>
            {
                DrawCatalogGridTile(
                    $"bgobjectCatalogGrid:{entry.Source}:{entry.RowId}:{entry.PlacementPath}",
                    entry,
                    entry.Name,
                    null,
                    string.Equals(selectedPath, entry.PlacementPath, StringComparison.OrdinalIgnoreCase),
                    () => onSelect(entry),
                    tileSize);
                _libraryBrowser.DrawCatalogEntryLibraryContext(
                    $"bgobject-grid:{entry.Source}:{entry.RowId}:{entry.PlacementPath}",
                    entry.Name,
                    ObjectEditorCatalog.CreateCatalogLibraryPreset(entry));
            },
            focusIndex);
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

        _browserPanel.DrawCatalogHeaderCard(
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
        int focusIndex = _focusCurrentCatalogEntry
            ? ConsumeCatalogFocusIndex(
                filteredEntries,
                entry => string.Equals(selectedPath, entry.PlacementPath, StringComparison.OrdinalIgnoreCase))
            : -1;
        DrawCatalogEntriesListCore(
            id,
            filteredEntries,
            emptyText,
            (entry, itemHeight) => DrawCatalogEntryCard(
                entry,
                string.Equals(selectedPath, entry.PlacementPath, StringComparison.OrdinalIgnoreCase),
                () => onSelect(entry),
                itemHeight),
            focusIndex);
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
        ObjectHousingModeState housing = _housingModePolicy.GetState();
        IReadOnlyList<ObjectCatalogFurnitureResult> filteredEntries = _furnitureView.GetEntries(
            section,
            currentFilter,
            currentCategoryFilter,
            housing,
            _draft.Furniture.Model);

        _browserPanel.DrawCatalogHeaderCard(
            id,
            icon,
            section,
            ref currentFilter,
            "search name, row, category, or path",
            ref currentCategoryFilter,
            section.CategoryFilters,
            filteredEntries.Count,
            () => DrawCatalogLayoutToggleButtons($"{id}_layout", ref _furnitureLayout),
            MeasureCatalogLayoutToggleButtonsSize());

        filter = currentFilter;
        categoryFilter = currentCategoryFilter;
        filteredEntries = _furnitureView.GetEntries(
            section,
            filter,
            categoryFilter,
            housing,
            _draft.Furniture.Model);
        int focusIndex = _focusCurrentCatalogEntry
            ? ConsumeCatalogFocusIndex(filteredEntries, _draft.IsFurnitureCatalogSelection)
            : -1;
        DrawCatalogEntries(
            id,
            _furnitureLayout,
            filteredEntries,
            emptyText,
            (entry, itemHeight) => DrawFurnitureCatalogEntryCard(
                entry,
                _draft.IsFurnitureCatalogSelection(entry),
                () => onSelect(entry),
                itemHeight),
            (entry, tileSize) =>
            {
                DrawCatalogGridTile(
                    $"furnitureCatalogGrid:{entry.Entry.Source}:{entry.Variant.HousingRowId}:{entry.Variant.ItemRowId}:{entry.Entry.PlacementPath}",
                    entry.Entry,
                    entry.Variant.Name,
                    entry.Variant.HousingRowId,
                    _draft.IsFurnitureCatalogSelection(entry),
                    () => onSelect(entry),
                    tileSize,
                    imageSource: CatalogGridTileImageSource.ItemIcon,
                    itemIconId: GetFurnitureCatalogItemIconId(entry));
                _libraryBrowser.DrawCatalogEntryLibraryContext(
                    $"furniture-grid:{entry.Entry.Source}:{entry.Variant.HousingRowId}:{entry.Variant.ItemRowId}:{entry.Entry.PlacementPath}",
                    entry.Variant.Name,
                    ObjectEditorCatalog.CreateCatalogLibraryPreset(entry));
            },
            focusIndex);
    }

    private static uint? GetFurnitureCatalogItemIconId(ObjectCatalogFurnitureResult result)
        => result.Variant.IconId > 0 ? result.Variant.IconId : null;

    private static void DrawCatalogLayoutToggleButtons(string id, ref CatalogLayoutMode layoutMode)
    {
        var buttonEdge = ResolveCatalogLayoutToggleButtonEdge();
        var buttonSize = new Vector2(buttonEdge, buttonEdge);
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        using (ImRaii.Group())
        {
            if (EditorIconButton.DrawToggle(
                    $"{id}_grid",
                    FontAwesomeIcon.BorderAll,
                    buttonSize,
                    layoutMode == CatalogLayoutMode.Grid,
                    ThemeColors.AccentPrimary,
                    ThemeColors.AccentOrange,
                    "grid layout"))
            {
                layoutMode = CatalogLayoutMode.Grid;
            }

            ImGui.SameLine(0f, spacing);
            if (EditorIconButton.DrawToggle(
                    $"{id}_list",
                    FontAwesomeIcon.Bars,
                    buttonSize,
                    layoutMode == CatalogLayoutMode.List,
                    ThemeColors.AccentPrimary,
                    ThemeColors.AccentOrange,
                    "list layout"))
            {
                layoutMode = CatalogLayoutMode.List;
            }
        }
    }

    private static Vector2 MeasureCatalogLayoutToggleButtonsSize()
    {
        float edge = ResolveCatalogLayoutToggleButtonEdge();
        return new Vector2((edge * 2f) + ImGui.GetStyle().ItemSpacing.X, edge);
    }

    private static float ResolveCatalogLayoutToggleButtonEdge()
    {
        return MathF.Max(
            EditorIconButton.MeasureEdge(FontAwesomeIcon.BorderAll),
            EditorIconButton.MeasureEdge(FontAwesomeIcon.Bars));
    }

    private void DrawCatalogEntries<TEntry>(
        string id,
        CatalogLayoutMode layout,
        IReadOnlyList<TEntry> filteredEntries,
        string emptyText,
        Action<TEntry, float> drawListEntry,
        Action<TEntry, Vector2> drawGridEntry,
        int focusIndex = -1)
    {
        if (layout == CatalogLayoutMode.Grid)
        {
            DrawCatalogEntriesGridCore(id, filteredEntries, emptyText, drawGridEntry, focusIndex);
            return;
        }

        DrawCatalogEntriesListCore(id, filteredEntries, emptyText, drawListEntry, focusIndex);
    }

    private void DrawCatalogEntriesListCore<TEntry>(
        string id,
        IReadOnlyList<TEntry> filteredEntries,
        string emptyText,
        Action<TEntry, float> drawEntry,
        int focusIndex = -1)
    {
        _browserPanel.DrawCatalogEntriesPanel(
            id,
            filteredEntries.Count,
            emptyText,
            () =>
            {
                var itemHeight = EditorLayout.Scaled(48f);
                var itemSpacingY = EditorLayout.ResolveObjectListItemSpacingY();
                UiVirtualList.Draw(
                    filteredEntries,
                    UiVirtualListOptions.Rows(itemHeight, itemSpacingY) with
                    {
                        DrawTrailingSpacing = true,
                        FocusIndex = focusIndex,
                    },
                    (entry, _) => drawEntry(entry, itemHeight));
            });
    }

    private void DrawCatalogEntriesGridCore<TEntry>(
        string id,
        IReadOnlyList<TEntry> filteredEntries,
        string emptyText,
        Action<TEntry, Vector2> drawEntry,
        int focusIndex = -1)
    {
        _browserPanel.DrawCatalogEntriesPanel(
            id,
            filteredEntries.Count,
            emptyText,
            () =>
            {
                float tileSpacing = ImGui.GetStyle().ItemSpacing.X;
                float availableWidth = EditorScrollList.GetStableContentWidth();
                int columns = ResolveCatalogGridColumnCount(availableWidth, tileSpacing);
                float tileEdge = EditorLayout.Positive((availableWidth - ((columns - 1) * tileSpacing)) / columns);
                int rowCount = (filteredEntries.Count + columns - 1) / columns;
                Vector2 tileSize = new(tileEdge, tileEdge);

                UiVirtualList.DrawIndices(
                    rowCount,
                    UiVirtualListOptions.Rows(tileEdge, tileSpacing) with
                    {
                        DrawTrailingSpacing = true,
                        FocusIndex = focusIndex >= 0 ? focusIndex / columns : -1,
                    },
                    row =>
                    {
                        for (int column = 0; column < columns; column++)
                        {
                            int entryIndex = (row * columns) + column;
                            if (entryIndex >= filteredEntries.Count)
                            {
                                break;
                            }

                            drawEntry(filteredEntries[entryIndex], tileSize);

                            if (column + 1 < columns && entryIndex + 1 < filteredEntries.Count)
                            {
                                ImGui.SameLine(0f, tileSpacing);
                            }
                        }
                    });
            });
    }

    private int ConsumeCatalogFocusIndex<TEntry>(IReadOnlyList<TEntry> entries, Predicate<TEntry> isSelected)
    {
        if (!_focusCurrentCatalogEntry)
        {
            return -1;
        }

        _focusCurrentCatalogEntry = false;
        for (int index = 0; index < entries.Count; ++index)
        {
            if (isSelected(entries[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static int ResolveCatalogGridColumnCount(float availableWidth, float tileSpacing)
    {
        float minimumTileEdge = EditorLayout.Scaled(72f);
        int columns = (int)MathF.Floor((availableWidth + tileSpacing) / (minimumTileEdge + tileSpacing));
        return Math.Clamp(columns, 1, 5);
    }

    private static IReadOnlyList<LightCatalogEntry> FilterLightCatalogEntries(string filter)
    {
        string[] searchTokens = SearchTermUtility.BuildSearchTokens(filter);
        if (searchTokens.Length == 0)
        {
            return ObjectEditorCatalog.LightCatalogEntries;
        }

        return ObjectEditorCatalog.LightCatalogEntries
            .Where(entry => SearchTermUtility.MatchesSearchText(BuildLightCatalogSearchText(entry), searchTokens))
            .ToList();
    }

    private static string BuildLightCatalogSearchText(LightCatalogEntry entry)
        => SearchTermUtility.BuildSearchText(
        [
            entry.Name,
            entry.Description,
            entry.Type.ToString(),
        ]);

    private void DrawCatalogGridTile(
        string id,
        ObjectCatalogEntry entry,
        string displayName,
        uint? displayRowId,
        bool selected,
        Action onSelect,
        Vector2 size,
        CatalogGridTileImageSource imageSource = CatalogGridTileImageSource.Preview,
        uint? itemIconId = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float innerPadding = 8f * scale;
        PreviewRender.Result? preview = null;
        if (imageSource == CatalogGridTileImageSource.Preview)
        {
            preview = string.IsNullOrWhiteSpace(entry.PlacementPath)
                ? new PreviewRender.Result(null, false, "Preview unavailable")
                : _previewService.GetPreview(
                    CatalogPreviewAssetFactory.Create(entry),
                    CreateCatalogThumbnailRequest(size, innerPadding));
        }

        ImGui.InvisibleButton($"##{id}", size);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            onSelect();
        }

        bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem | ImGuiHoveredFlags.AllowWhenOverlapped);
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            DrawCatalogEntryTooltip(entry, displayName, displayRowId, preview);
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var accent = ThemeColors.AccentPrimary;
        var fill = selected
            ? accent with { W = 0.16f }
            : ThemeColors.ButtonDefault with { W = 0.24f };
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
            border = ThemeColors.Border with { W = 0.34f };
        }
        float rounding = 10f * scale;
        var previewMin = min + new Vector2(innerPadding);
        var previewMax = max - new Vector2(innerPadding);
        float imageRounding = MathF.Max(6f * scale, rounding - (2f * scale));

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, hovered || selected ? 1.35f * scale : 1f * scale);

        if (imageSource == CatalogGridTileImageSource.ItemIcon)
        {
            DrawCatalogItemIconTileContent(drawList, previewMin, previewMax, imageRounding, itemIconId, accent);
        }
        else if (preview is { } resolvedPreview && resolvedPreview.Texture is not null)
        {
            drawList.AddImageRounded(resolvedPreview.Texture.Handle, previewMin, previewMax, Vector2.Zero, Vector2.One, 0xFFFFFFFF, imageRounding);
        }
        else
        {
            drawList.AddRectFilled(
                previewMin,
                previewMax,
                ImGui.GetColorU32(PreviewRender.BackgroundPalette.GetPlaceholderFill(PreviewRender.BackgroundStyle.White)),
                imageRounding);
            if (preview is { IsLoading: true })
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

    private void DrawCatalogItemIconTileContent(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float rounding,
        uint? itemIconId,
        Vector4 accent)
    {
        if (itemIconId is > 0
         && _browserRows.TryGetObjectItemIcon(itemIconId.Value, out IDalamudTextureWrap? itemIcon)
         && itemIcon is not null)
        {
            drawList.AddImageRounded(itemIcon.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFF, rounding);
            return;
        }

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(PreviewRender.BackgroundPalette.GetPlaceholderFill(PreviewRender.BackgroundStyle.White)),
            rounding);
        DrawCenteredCatalogTileIcon(drawList, min, max, FontAwesomeIcon.Home, accent with { W = 0.88f });
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

    private static void DrawCatalogEntryTooltip(
        ObjectCatalogEntry entry,
        string displayName,
        uint? displayRowId,
        PreviewRender.Result? preview)
    {
        (FontAwesomeIcon icon, string kind) = entry.Kind switch
        {
            ObjectCatalogKind.Furniture => (FontAwesomeIcon.Home, "Furniture"),
            ObjectCatalogKind.BgObject  => (FontAwesomeIcon.Cube, "Background object"),
            ObjectCatalogKind.Vfx       => (FontAwesomeIcon.Magic, "VFX"),
            _                           => (FontAwesomeIcon.Cube, "Object"),
        };

        IntonerTooltip.Draw(
            () =>
            {
                IntonerTooltipContent.Header(icon, displayName, kind);
                IntonerTooltipContent.Separator();

                if (displayRowId is { } rowId)
                {
                    IntonerTooltipContent.Property(
                        "Game data",
                        $"Row {rowId.ToString(CultureInfo.InvariantCulture)}");
                }

                IntonerTooltipContent.Property("Source", entry.Source);
                IntonerTooltipContent.Property("Asset", entry.DisplayPath);
                if (preview is { IsLoading: true })
                {
                    IntonerTooltipContent.Notice(FontAwesomeIcon.SyncAlt, "Preview is loading", ThemeColors.AccentBlue);
                }
                else if (preview is { Error: { } error })
                {
                    IntonerTooltipContent.Notice(FontAwesomeIcon.ExclamationTriangle, error, ThemeColors.AccentOrange);
                }
            },
            new IntonerTooltipOptions
            {
                Accent = ThemeColors.AccentPrimary,
                Width = 360f,
            });
    }

    private static void DrawCenteredCatalogTileMessage(ImDrawListPtr drawList, Vector2 min, Vector2 max, string text, Vector4 color)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = MathF.Max(1f, (max.X - min.X) - (16f * scale));
        string renderedText = EditorTextUtility.ClipTextToWidth(text, width);
        Vector2 textSize = ImGui.CalcTextSize(renderedText);
        var textPosition = new Vector2(
            min.X + MathF.Max(0f, ((max.X - min.X) - textSize.X) * 0.5f),
            min.Y + MathF.Max(0f, ((max.Y - min.Y) - textSize.Y) * 0.5f));
        drawList.AddText(textPosition, ImGui.GetColorU32(color), renderedText);
    }

    private static void DrawCenteredCatalogTileIcon(ImDrawListPtr drawList, Vector2 min, Vector2 max, FontAwesomeIcon icon, Vector4 color)
        => EditorIcon.DrawCentered(drawList, icon, min, max, color);

    private void DrawCatalogEntryCard(ObjectCatalogEntry entry, bool selected, Action onSelect, float height)
    {
        if (entry.Kind == ObjectCatalogKind.Vfx && entry.VfxInfo is not null)
        {
            DrawVfxCatalogEntryCard(entry, entry.VfxInfo, selected, onSelect, height);
            return;
        }

        ObjectLibraryPreset libraryPreset = ObjectEditorCatalog.CreateCatalogLibraryPreset(entry);
        _ = _browserRows.DrawObjectBrowserRow(
            new ObjectBrowserRowRenderer.ObjectBrowserRow
            {
                Id = $"{entry.Source}:{entry.RowId}:{entry.PlacementPath}",
                Title = entry.Name,
                Detail = entry.DisplayPath,
                Badges = _libraryStatus.ResolveCatalogBadges(libraryPreset, entry.BgObjectInfo),
                Selected = selected,
            },
            height,
            onSelect);
        _libraryBrowser.DrawCatalogEntryLibraryContext(
            $"entry:{entry.Source}:{entry.RowId}:{entry.PlacementPath}",
            entry.Name,
            libraryPreset);
    }

    private void DrawFurnitureCatalogEntryCard(ObjectCatalogFurnitureResult result, bool selected, Action onSelect, float height)
    {
        ObjectCatalogEntry entry = result.Entry;
        ObjectCatalogFurnitureVariant variant = result.Variant;
        FontAwesomeIcon? titleIcon = null;
        string? titleIconTooltip = null;
        Vector4? titleIconColor = null;
        uint? itemIconId = GetFurnitureCatalogItemIconId(result);

        if (variant.DyeCount > 0)
        {
            titleIcon = FontAwesomeIcon.Palette;
            titleIconTooltip = variant.DyeCount == 1
                ? "1 dye channel"
                : $"{variant.DyeCount} dye channels";
            titleIconColor = ThemeColors.AccentOrange with { W = selected ? 0.95f : 0.82f };
        }

        ObjectLibraryPreset libraryPreset = ObjectEditorCatalog.CreateCatalogLibraryPreset(result);
        _ = _browserRows.DrawObjectBrowserRow(
            new ObjectBrowserRowRenderer.ObjectBrowserRow
            {
                Id = $"{entry.Source}:{variant.HousingRowId}:{variant.ItemRowId}:{entry.PlacementPath}",
                Title = result.Variant.Name,
                Detail = entry.DisplayPath,
                TrailingBadge = FurnitureDataTooltip.CreateBadge(variant),
                ItemIconId = itemIconId,
                TitleIcon = titleIcon,
                TitleIconTooltip = titleIconTooltip,
                TitleIconColor = titleIconColor,
                Badges = _libraryStatus.ResolveCatalogBadges(libraryPreset),
                Selected = selected,
            },
            height,
            onSelect);
        _libraryBrowser.DrawCatalogEntryLibraryContext(
            $"furniture:{entry.Source}:{variant.HousingRowId}:{variant.ItemRowId}:{entry.PlacementPath}",
            result.Variant.Name,
            libraryPreset);
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
            titleIconColor = ThemeColors.AccentGreen with { W = selected ? 0.95f : 0.82f };
        }

        ObjectLibraryPreset libraryPreset = ObjectEditorCatalog.CreateCatalogLibraryPreset(entry);
        _ = _browserRows.DrawObjectBrowserRow(
            new ObjectBrowserRowRenderer.ObjectBrowserRow
            {
                Id = $"{entry.Source}:{entry.RowId}:{entry.PlacementPath}",
                Title = entry.Name,
                Detail = entry.DisplayPath,
                TitleIcon = titleIcon,
                TitleIconTooltip = titleIconTooltip,
                TitleIconColor = titleIconColor,
                Badges = _libraryStatus.ResolveCatalogBadges(libraryPreset),
                Selected = selected,
            },
            height,
            onSelect);
        _libraryBrowser.DrawCatalogEntryLibraryContext(
            $"vfx:{entry.Source}:{entry.RowId}:{entry.PlacementPath}",
            entry.Name,
            libraryPreset);
    }

    internal void OpenCurrentDraftInCatalog()
    {
        string? filterStripId = null;
        switch (_browserSelection.Kind)
        {
            case DraftKind.BgObject:
                _bgObjectFilter = string.Empty;
                _bgObjectSourceFilter = string.Empty;
                filterStripId = "##bgobjectCatalog";
                break;
            case DraftKind.Furniture:
                _furnitureFilter = string.Empty;
                _furnitureCategoryFilter = string.Empty;
                filterStripId = "##furnitureCatalog";
                break;
            case DraftKind.Vfx:
                _vfxFilter = string.Empty;
                _vfxSourceFilter = string.Empty;
                filterStripId = "##vfxCatalog";
                break;
            case DraftKind.Light:
                _lightFilter = string.Empty;
                break;
        }

        if (filterStripId is not null)
        {
            _filterBar.ResetScroll(filterStripId);
        }

        _focusCurrentCatalogEntry = true;
        _browserSelection.Source = CreateBrowserSelection.CreateBrowserSource.Catalog;
    }
}
