using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.UI.Components;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class EditorFilterBar
{
    private const float MinimumHeight = 30f;
    private const float Inset = 3f;
    private const float ScrollbarSize = 3f;
    private const float VerticalGap = 2f;
    private const float DividerSpacing = 5f;

    private readonly EditorOverlayLayer _editorOverlayLayer;
    private readonly Dictionary<string, FilterState> _filterStates = new(StringComparer.Ordinal);

    private sealed class FilterState
    {
        public bool Reset;
        public string? Selected;
        public float ViewportWidth;
        public IReadOnlyList<ObjectCatalogFilterCount>? Source;
        public ImFontPtr Font;
        public float FontSize;
        public float Scale;
        public FilterChip[] Chips = [];
        public float ContentWidth;
        public readonly EditorOverflowMenu<FilterChip> Menu = new(static chip => chip.Value, static chip => chip.Label,
            StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct FilterChip(string Value, string Label, string? Count, float Width, float Offset);

    public EditorFilterBar(EditorOverlayLayer editorOverlayLayer)
    {
        _editorOverlayLayer = editorOverlayLayer;
    }

    public void ResetScroll(string id)
        => GetFilterState(id).Reset = true;

    private FilterState GetFilterState(string id)
    {
        if (!_filterStates.TryGetValue(id, out FilterState? state))
        {
            state = new FilterState();
            _filterStates.Add(id, state);
        }

        return state;
    }

    internal string DrawWrappedFilterButtons(string id, string selected, IReadOnlyList<ObjectCatalogFilterCount> counts, float width)
    {
        FilterState state = GetFilterState(id);
        UpdateChips(state, counts);
        Vector2 start = ImGui.GetCursorScreenPos();
        float spacing = EditorSegmentedControl.Spacing;
        float rowSpacing = ImGui.GetStyle().ItemSpacing.Y;
        float x = start.X;
        float y = start.Y;
        float height = EditorBadgeRenderer.Height;
        width = MathF.Max(1f, width);
        using ImRaii.IdDisposable scope = ImRaii.PushId(id);

        foreach (FilterChip chip in state.Chips)
        {
            float chipWidth = MathF.Min(chip.Width, width);

            if (x > start.X && x + chipWidth > start.X + width)
            {
                x = start.X;
                y += height + rowSpacing;
            }

            ImGui.SetCursorScreenPos(new Vector2(x, y));

            if (EditorSegmentedControl.DrawFilterSegment(chip.Value.Length == 0 ? "all" : chip.Value, chip.Label, chip.Count,
                    string.Equals(selected, chip.Value, StringComparison.OrdinalIgnoreCase), ThemeColors.AccentPrimary,
                    new Vector2(chipWidth, height)))
            {
                selected = chip.Value;
            }

            x += chipWidth + spacing;
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, y + height));
        return selected;
    }

    internal string DrawCatalogFilterButtons(string id, string filterValue, IReadOnlyList<ObjectCatalogFilterCount> filterCounts)
    {
        FilterState state = GetFilterState(id);
        UpdateChips(state, filterCounts);
        Vector2 cursor = ImGui.GetCursorPos();
        Vector2 min = ImGui.GetCursorScreenPos();
        float scale = ImGuiHelpers.GlobalScale;
        float inset = Inset * scale;
        float dividerSpacing = DividerSpacing * scale;
        float width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        float height = MathF.Max(MinimumHeight * scale, ImGui.GetTextLineHeight() + 12f * scale);
        float buttonHeight = height - inset * 2f;
        float allWidth = state.Chips[0].Width;
        float actionWidth = buttonHeight;
        Vector2 max = min + new Vector2(width, height);
        Vector2 allMin = min + new Vector2(inset);
        float leftDivider = allMin.X + allWidth + dividerSpacing;
        float actionX = max.X - inset - actionWidth;
        float rightDivider = actionX - dividerSpacing - scale;
        Vector2 railMin = new(leftDivider + dividerSpacing + scale, allMin.Y);
        float railWidth = MathF.Max(1f, rightDivider - dividerSpacing - railMin.X);
        Vector4 accent = ThemeColors.AccentPrimary;
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        EditorFieldFrame.DrawFill(drawList, min, max);

        using (ImRaii.PushId(id))
        {
            ImGui.SetCursorScreenPos(allMin);

            if (EditorSegmentedControl.DrawFilterSegment("all", "All", null, string.IsNullOrWhiteSpace(filterValue),
                    accent, new Vector2(allWidth, buttonHeight)))
            {
                filterValue = string.Empty;
            }

            DrawDivider(drawList, leftDivider, allMin.Y, buttonHeight, scale);
            DrawDivider(drawList, rightDivider, allMin.Y, buttonHeight, scale);
            ImGui.SetCursorScreenPos(railMin);
            filterValue = DrawRail(state, filterValue, new Vector2(railWidth, buttonHeight), accent);
            ImGui.SetCursorScreenPos(new Vector2(actionX, allMin.Y));
            bool open = EditorSegmentedControl.DrawFilterAction("categories", FontAwesomeIcon.ChevronDown,
                ImGui.IsPopupOpen("categoryMenu"), "Choose category", accent, new Vector2(actionWidth, buttonHeight));
            filterValue = state.Menu.Draw("categoryMenu", state.Chips, filterValue, open, min, max, accent, "Search categories...",
                static (chip, selected, rowAccent, rowHeight) => EditorContextMenu.DrawChoice(
                    chip.Value, chip.Label, selected, rowAccent, count: chip.Count, height: rowHeight), _editorOverlayLayer);
            bool active = ImGui.IsItemActive() || ImGui.IsPopupOpen("categoryMenu");
            EditorFieldFrame.DrawBorder(drawList, min, max, accent, active, ImGui.IsMouseHoveringRect(min, max, true));

            ImGui.SetCursorPos(cursor);
            ImGui.InvisibleButton("background", new Vector2(width, height));
        }

        return filterValue;
    }

    private string DrawRail(FilterState state, string selected, Vector2 size, Vector4 accent)
    {
        float scale = ImGuiHelpers.GlobalScale;
        bool scrollable = state.ContentWidth > MathF.Floor(size.X);

        float chipHeight = scrollable ? size.Y - (ScrollbarSize + VerticalGap) * scale : MathF.Floor(size.Y);
        using ImRaii.StyleDisposable style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
            .Push(ImGuiStyleVar.ChildBorderSize, 0f)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(EditorSegmentedControl.Spacing, 0f))
            .Push(ImGuiStyleVar.ScrollbarSize, ScrollbarSize * scale)
            .Push(ImGuiStyleVar.ScrollbarRounding, ScrollbarSize * 0.5f * scale);
        using ImRaii.ColorDisposable colors = ImRaii.PushColor(ImGuiCol.ScrollbarBg, ThemeColors.WindowBg with { W = 0.60f })
            .Push(ImGuiCol.ScrollbarGrab, accent with { W = 0.70f })
            .Push(ImGuiCol.ScrollbarGrabHovered, accent with { W = 0.90f })
            .Push(ImGuiCol.ScrollbarGrabActive, accent with { W = 1f });

        ImGui.SetNextWindowContentSize(new Vector2(state.ContentWidth, chipHeight));
        ImGuiWindowFlags scrollbarFlags = scrollable ? ImGuiWindowFlags.AlwaysHorizontalScrollbar : ImGuiWindowFlags.NoScrollbar;
        using ImRaii.ChildDisposable child = ImRaii.Child("rail", size, false,
            scrollbarFlags | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove);

        if (!child)
        {
            return selected;
        }

        bool reveal = !string.Equals(state.Selected, selected, StringComparison.OrdinalIgnoreCase) || !state.ViewportWidth.Equals(size.X);
        state.Selected = selected;
        state.ViewportWidth = size.X;

        if (state.Reset || reveal && string.IsNullOrWhiteSpace(selected))
        {
            ImGui.SetScrollX(0f);
            state.Reset = false;
            reveal = false;
        }

        for (int index = 1; index < state.Chips.Length; ++index)
        {
            FilterChip chip = state.Chips[index];

            if (index > 1)
            {
                ImGui.SameLine();
            }

            bool isSelected = string.Equals(selected, chip.Value, StringComparison.OrdinalIgnoreCase);

            if (EditorSegmentedControl.DrawFilterSegment(chip.Value, chip.Label, chip.Count, isSelected, accent, new Vector2(chip.Width, chipHeight)))
            {
                selected = chip.Value;
                isSelected = true;
                reveal = true;
            }

            if (isSelected && reveal)
            {
                float position = ImGui.GetScrollX();
                float nearest = MathF.Max(chip.Offset + chip.Width - size.X, MathF.Min(position, chip.Offset));
                ImGui.SetScrollX(MathF.Max(0f, nearest));
            }
        }

        ImGuiIOPtr io = ImGui.GetIO();
        float wheel = MathF.Abs(io.MouseWheelH) > MathF.Abs(io.MouseWheel) ? io.MouseWheelH : io.MouseWheel;

        if (ImGui.IsWindowHovered() && !io.KeyCtrl && !IntonerTooltipScroll.MouseWheelClaimed && MathF.Abs(wheel) > float.Epsilon)
        {
            ImGui.SetScrollX(ImGui.GetScrollX() - wheel * ImGui.GetTextLineHeight() * 4f);
        }

        _editorOverlayLayer.CaptureCurrentWindow();
        return selected;
    }

    private static void UpdateChips(FilterState state, IReadOnlyList<ObjectCatalogFilterCount> counts)
    {
        ImFontPtr font = ImGui.GetFont();
        float fontSize = ImGui.GetFontSize();
        float scale = ImGuiHelpers.GlobalScale;

        if (ReferenceEquals(state.Source, counts) && state.Font == font && state.FontSize.Equals(fontSize) && state.Scale.Equals(scale))
        {
            return;
        }

        FilterChip[] chips = new FilterChip[counts.Count + 1];
        chips[0] = new FilterChip(string.Empty, "All", null, EditorSegmentedControl.MeasureFilterWidth("All"), 0f);
        float offset = 0f;

        for (int index = 0; index < counts.Count; ++index)
        {
            ObjectCatalogFilterCount count = counts[index];
            string countText = count.Count.ToString(CultureInfo.InvariantCulture);
            float width = EditorSegmentedControl.MeasureFilterWidth(count.Label, countText);
            chips[index + 1] = new FilterChip(count.Label, count.Label, countText, width, offset);
            offset += width + EditorSegmentedControl.Spacing;
        }

        state.Source = counts;
        state.Font = font;
        state.FontSize = fontSize;
        state.Scale = scale;
        state.Chips = chips;
        state.ContentWidth = MathF.Max(0f, offset - EditorSegmentedControl.Spacing);
        state.Selected = null;
    }

    private static void DrawDivider(ImDrawListPtr drawList, float x, float y, float height, float scale)
    {
        drawList.AddRectFilled(new Vector2(x, y + Inset * scale), new Vector2(x + scale, y + height - Inset * scale),
            ImGui.GetColorU32(ThemeColors.Border with { W = 0.30f }));
    }
}
