using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private bool DrawFurnitureColorEditor(
        string label,
        string idSuffix,
        ref bool useCustomColor,
        ref byte stainId,
        ref Vector4 customColor,
        ref string filter,
        Action<string, string, bool, byte, Vector4, bool>? onChanged = null)
    {
        var changed = false;

        if (DrawCheckboxRow($"{idSuffix}_furnitureCustomColor", "Custom Color", ref useCustomColor))
        {
            changed = true;
            onChanged?.Invoke("FurnitureCustomColor", "Toggle Furniture Custom Color", useCustomColor, stainId, customColor, true);
        }

        if (useCustomColor)
        {
            var rgbColor = new Vector3(customColor.X, customColor.Y, customColor.Z);
            DrawCompactSettingsLabelCell(label);
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.ColorEdit3($"##{label}_{idSuffix}", ref rgbColor))
            {
                customColor = EditorColors.Color(rgbColor, 1f);
                changed = true;
                onChanged?.Invoke("FurnitureColor", "Change Furniture Color", useCustomColor, stainId, customColor, false);
            }
        }
        else if (DrawFurnitureStainSelector(label, idSuffix, ref stainId, ref filter))
        {
            changed = true;
            onChanged?.Invoke("FurnitureStain", "Change Furniture Stain", useCustomColor, stainId, customColor, true);
        }

        return changed;
    }

    private bool DrawFurnitureStainSelector(string label, string idSuffix, ref byte currentStainId, ref string filter)
    {
        var changed = false;
        var currentId = currentStainId;
        var currentFilter = filter;
        var current = FindFurnitureStain(currentId);
        var previewLabel = $"{current.Id:000} | {current.Name}";

        DrawCompactSettingsLabelCell(label);
        var comboWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - (88f * ImGuiHelpers.GlobalScale));
        ImGui.SetNextItemWidth(comboWidth);
        using var combo = ImRaii.Combo($"##{label}_{idSuffix}", previewLabel);
        if (combo)
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint($"##{idSuffix}_stain_filter", "filter stains", ref currentFilter, 128);
            ImGui.Spacing();

            var visibleEntries = 0;
            foreach (var stain in _furnitureStains)
            {
                if (!MatchesFurnitureStainFilter(stain, currentFilter))
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

                var optionLabel = stain.IsMetallic
                    ? $"{stain.Id:000} | {stain.Name} | glossy"
                    : $"{stain.Id:000} | {stain.Name}";
                var isSelected = stain.Id == currentId;
                if (ImGui.Selectable($"{optionLabel}##{idSuffix}_stain_{stain.Id}", isSelected))
                {
                    currentId = stain.Id;
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

        ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
        ImGui.ColorButton(
            $"##{idSuffix}_stain_preview",
            current.PreviewColor,
            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoDragDrop,
            new Vector2(18f * ImGuiHelpers.GlobalScale, 18f * ImGuiHelpers.GlobalScale));
        ImGui.SameLine(0f, 6f * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled(current.IsMetallic ? "glossy" : "matte");

        currentStainId = currentId;
        filter = currentFilter;
        return changed;
    }

    private FurnitureStainOption FindFurnitureStain(byte stainId)
    {
        for (var index = 0; index < _furnitureStains.Count; ++index)
        {
            var stain = _furnitureStains[index];
            if (stain.Id == stainId)
            {
                return stain;
            }
        }

        return new FurnitureStainOption(stainId, $"Unknown {stainId}", EditorColors.Color(0.25f, 0.25f, 0.25f, 1f), false);
    }

    private static bool MatchesFurnitureStainFilter(FurnitureStainOption stain, string filter)
        => string.IsNullOrWhiteSpace(filter)
            || stain.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || stain.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase);
}

