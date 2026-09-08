using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class EditorFilterBar
{
    private readonly EditorOverlayLayer _editorOverlayLayer;
    private readonly Dictionary<string, float>    _catalogFilterScroll = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float>    _catalogFilterScrollMax = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _pendingCatalogFilterScroll = new(StringComparer.Ordinal);

    public EditorFilterBar(EditorOverlayLayer editorOverlayLayer)
    {
        _editorOverlayLayer = editorOverlayLayer;
    }

    public void ResetScroll(string id)
        => _pendingCatalogFilterScroll[id] = 0f;

    internal string DrawCatalogFilterButtons(string id, string filterValue, IReadOnlyList<ObjectCatalogFilterCount> filterCounts)
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
                    _editorOverlayLayer.CaptureCurrentWindow();
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
        using var normalColor = ImRaii.PushColor(ImGuiCol.Button, ThemeColors.ButtonDefault);
        using var hoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, ThemeColors.AccentPrimary with { W = 0.85f });
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, ThemeColors.AccentPrimaryMuted with { W = 0.75f });
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
}
