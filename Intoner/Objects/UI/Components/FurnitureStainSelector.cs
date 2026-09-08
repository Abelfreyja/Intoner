using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

/// <summary> draws searchable furniture stains and their color previews </summary>
internal static class FurnitureStainSelector
{
    /// <summary> draws a stain row in the current property table and returns whether the selection changed </summary>
    public static bool Draw(string label, string idSuffix, IReadOnlyList<FurnitureStainOption> stains, ref byte currentStainId, ref string filter)
    {
        bool changed = false;
        FurnitureStainOption current = Find(stains, currentStainId);
        string previewLabel = $"{current.Id:000} | {current.Name}";

        EditorPropertyTable.NextRow(label);
        float scale = ImGuiHelpers.GlobalScale;
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float previewWidth = (32f * scale) + MathF.Max(ImGui.CalcTextSize("glossy").X, ImGui.CalcTextSize("matte").X);
        bool inlinePreview = availableWidth >= (120f * scale) + previewWidth;
        float comboWidth = MathF.Max(1f, availableWidth - (inlinePreview ? previewWidth : 0f));
        ImGui.SetNextItemWidth(comboWidth);
        using (var combo = ImRaii.Combo($"##{label}_{idSuffix}", previewLabel))
        {
            if (combo)
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint($"##{idSuffix}_stain_filter", "filter stains", ref filter, 128);
                ImGui.Spacing();

                int visibleEntries = 0;
                foreach (FurnitureStainOption stain in stains)
                {
                    if (!MatchesFilter(stain, filter))
                    {
                        continue;
                    }

                    visibleEntries++;
                    ImGui.ColorButton(
                        $"##{idSuffix}_stain_color_{stain.Id}",
                        stain.PreviewColor,
                        ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
                        new Vector2(16f * ImGuiHelpers.GlobalScale, 16f * ImGuiHelpers.GlobalScale));
                    ImGui.SameLine();

                    string optionLabel = stain.IsMetallic
                        ? $"{stain.Id:000} | {stain.Name} | glossy"
                        : $"{stain.Id:000} | {stain.Name}";
                    bool isSelected = stain.Id == currentStainId;
                    if (ImGui.Selectable($"{optionLabel}##{idSuffix}_stain_{stain.Id}", isSelected))
                    {
                        currentStainId = stain.Id;
                        current = stain;
                        changed = true;
                    }

                    if (isSelected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                if (visibleEntries == 0)
                {
                    ImGui.TextDisabled("No stains match the current filter.");
                }
            }
        }

        if (inlinePreview)
        {
            ImGui.SameLine(0f, 8f * scale);
        }
        ImGui.ColorButton(
            $"##{idSuffix}_stain_preview",
            current.PreviewColor,
            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
            new Vector2(18f * ImGuiHelpers.GlobalScale, 18f * ImGuiHelpers.GlobalScale));
        ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled(current.IsMetallic ? "glossy" : "matte");

        return changed;
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

        return new FurnitureStainOption(stainId, $"Unknown {stainId}", ThemeColors.Color(0.25f, 0.25f, 0.25f, 1f), false);
    }

    private static bool MatchesFilter(FurnitureStainOption stain, string filter)
        => string.IsNullOrWhiteSpace(filter)
            || stain.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || stain.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);
}
