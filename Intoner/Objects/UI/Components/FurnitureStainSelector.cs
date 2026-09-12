using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.UI.Performance;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

/// <summary> draws searchable furniture stains and their color previews </summary>
internal sealed class FurnitureStainSelector
{
    private readonly List<FurnitureStainOption> _filteredStains = [];
    private IReadOnlyList<FurnitureStainOption>? _source;
    private string _filter = string.Empty;
    private string _appliedFilter = string.Empty;

    public string Filter => _filter;

    /// <summary> draws a stain row in the current property table and returns whether the selection changed </summary>
    public bool Draw(string label, string idSuffix, IReadOnlyList<FurnitureStainOption> stains, ref byte currentStainId)
    {
        EditorPropertyTable.NextRow(label);
        FurnitureStainOption current = Find(stains, currentStainId);
        float scale = ImGuiHelpers.GlobalScale;
        float comboWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        Vector2 comboMin = ImGui.GetCursorScreenPos();
        Vector2 comboSize = new(comboWidth, ImGui.GetFrameHeight());
        float inset = 16f * scale;
        float gap = 4f * scale;
        Vector2 workSize = ImGui.GetMainViewport().WorkSize;
        float popupWidth = MathF.Min(MathF.Max(comboWidth, 316f * scale), MathF.Max(1f, workSize.X - inset));
        float gridWidth = MathF.Max(1f, popupWidth - inset * 2f);
        int columns = Math.Max(1, (int)((gridWidth + gap) / (32f * scale + gap)));
        float edge = (gridWidth - gap * (columns - 1)) / columns;
        float paletteHeight = edge * 7f + gap * 6f;
        Vector2 popupSize = new(popupWidth,
            MathF.Min(inset * 2f + EditorSearchField.Height + 8f * scale + paletteHeight, MathF.Max(1f, workSize.Y - inset)));

        ImGui.SetNextItemWidth(comboWidth);
        ImGui.SetNextWindowSizeConstraints(popupSize, popupSize);
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(inset, ImGui.GetStyle().FramePadding.Y)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(inset)))
        using (var combo = ImRaii.Combo($"##{label}_{idSuffix}", string.Empty))
        {
            if (combo)
            {
                current = DrawPopup(idSuffix, stains, columns, edge, current.DuplicateOf ?? current.Id) ?? current;
            }
        }

        bool changed = currentStainId != current.Id;
        currentStainId = current.Id;
        DrawPreview(comboMin, comboSize, current);
        return changed;
    }

    private FurnitureStainOption? DrawPopup(string idSuffix, IReadOnlyList<FurnitureStainOption> stains, int columns, float edge, byte selectedStainId)
    {
        bool appearing = ImGui.IsWindowAppearing();
        if (appearing)
        {
            ImGui.SetKeyboardFocusHere();
        }

        _ = EditorSearchField.Draw(
            $"{idSuffix}_stain_filter", ref _filter,
            new EditorSearchFieldOptions("Search dyes", ThemeColors.AccentPrimary));
        bool filterChanged = UpdateFilter(stains);
        ImGui.SetCursorPosY(ImGui.GetItemRectMax().Y - ImGui.GetWindowPos().Y + 8f * ImGuiHelpers.GlobalScale);
        Vector2 paletteSize = ImGui.GetContentRegionAvail();
        paletteSize.X += 8f * ImGuiHelpers.GlobalScale;

        using var scrollbar = ImRaii.PushStyle(ImGuiStyleVar.ScrollbarSize, 6f * ImGuiHelpers.GlobalScale);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        using var child = ImRaii.Child($"##{idSuffix}_stain_list", paletteSize, false);
        if (!child)
        {
            return null;
        }

        if (filterChanged)
        {
            ImGui.SetScrollY(0f);
        }

        return DrawPalette(appearing, columns, edge, selectedStainId);
    }

    private FurnitureStainOption? DrawPalette(bool appearing, int columns, float edge, byte selectedStainId)
    {
        if (_filteredStains.Count == 0)
        {
            Vector2 textSize = ImGui.CalcTextSize("No matching dyes");
            ImGui.SetCursorPos(ImGui.GetCursorPos() + Vector2.Max(Vector2.Zero, (ImGui.GetContentRegionAvail() - textSize) * 0.5f));
            ImGui.TextDisabled("No matching dyes");
            return null;
        }

        float scale = ImGuiHelpers.GlobalScale;
        float gap = 4f * scale;
        int rowCount = (_filteredStains.Count + columns - 1) / columns;
        int focusRow = -1;
        if (appearing)
        {
            for (int index = 0; index < _filteredStains.Count; ++index)
            {
                if (_filteredStains[index].Id == selectedStainId)
                {
                    focusRow = index / columns;
                    break;
                }
            }
        }

        using UiVirtualList.Scope list = UiVirtualList.Begin(rowCount,
            UiVirtualListOptions.Rows(edge, gap) with { FocusIndex = focusRow });
        while (list.Step())
        {
            for (int row = list.DisplayStart; row < list.DisplayEnd; ++row)
            {
                int start = row * columns;
                int end = Math.Min(start + columns, _filteredStains.Count);
                for (int index = start; index < end; ++index)
                {
                    if (index > start)
                    {
                        ImGui.SameLine(0f, gap);
                    }

                    FurnitureStainOption stain = _filteredStains[index];
                    using var id = ImRaii.PushId(stain.Id);
                    bool clicked = ImGui.ColorButton("##dye", stain.PreviewColor,
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop | ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoBorder,
                        new Vector2(edge));
                    Vector2 swatchMin = ImGui.GetItemRectMin();
                    Vector2 swatchMax = ImGui.GetItemRectMax();
                    EditorColorSwatch.Draw(ImGui.GetWindowDrawList(), swatchMin, swatchMax,
                        stain.Id == 0 ? ThemeColors.TextDisabled : stain.PreviewColor, stain.Id == 0, stain.Id == selectedStainId);
                    if (stain.Id == selectedStainId)
                    {
                        ImGui.SetItemDefaultFocus();
                    }

                    IntonerTooltip.Attach(stain.Name);
                    if (clicked)
                    {
                        ImGui.CloseCurrentPopup();
                        return stain;
                    }
                }

                list.FinishItem(row);
            }
        }

        return null;
    }

    private bool UpdateFilter(IReadOnlyList<FurnitureStainOption> stains)
    {
        string filter = _filter.Trim();
        if (ReferenceEquals(_source, stains) && string.Equals(_appliedFilter, filter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _source = stains;
        _appliedFilter = filter;
        _filteredStains.Clear();
        foreach (FurnitureStainOption stain in stains)
        {
            if (stain.IsAvailable && MatchesFilter(stain, filter))
            {
                _filteredStains.Add(stain);
            }
        }

        return true;
    }

    private static void DrawPreview(Vector2 min, Vector2 size, FurnitureStainOption stain)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inset = 6f * scale;
        float edge = MathF.Max(1f, size.Y - 8f * scale);
        float availableWidth = size.X - ImGui.GetFrameHeight() - inset * 2f;
        if (availableWidth < edge)
        {
            return;
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        Vector2 swatchMin = min + new Vector2(inset, (size.Y - edge) * 0.5f);
        EditorColorSwatch.Draw(drawList, swatchMin, swatchMin + new Vector2(edge),
            stain.Id == 0 ? ThemeColors.TextDisabled : stain.PreviewColor, stain.Id == 0);
        float textOffset = edge + 8f * scale;
        string name = EditorTextUtility.ClipTextToWidth(stain.Name, MathF.Max(1f, availableWidth - textOffset));
        drawList.AddText(new Vector2(swatchMin.X + textOffset, min.Y + (size.Y - ImGui.GetTextLineHeight()) * 0.5f),
            ImGui.GetColorU32(ThemeColors.Text), name);
        if (IntonerTooltip.IsAreaHovered(min, min + size))
        {
            IntonerTooltip.DrawText(stain.Name);
        }
    }

    /// <summary> resolves a stain or returns a neutral preview for an unknown stain id </summary>
    public static FurnitureStainOption Find(IReadOnlyList<FurnitureStainOption> stains, byte stainId)
    {
        foreach (FurnitureStainOption stain in stains)
        {
            if (stain.Id == stainId)
            {
                return stain;
            }
        }

        return new FurnitureStainOption(stainId, $"Unknown {stainId}", ThemeColors.Color(0.25f, 0.25f, 0.25f, 1f), false, false);
    }

    private static bool MatchesFilter(FurnitureStainOption stain, string filter)
        => filter.Length == 0
            || stain.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || stain.Id.ToString("D3").Contains(filter, StringComparison.Ordinal);
}
