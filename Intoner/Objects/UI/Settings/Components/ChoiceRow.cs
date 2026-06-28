using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class ChoiceRow
{
    private const float ComboMinWidth = 148f;

    public static bool Draw<TValue>(
        SettingDefinition definition,
        IReadOnlyList<ChoiceOption<TValue>> options,
        ref TValue value,
        Vector4 accent,
        bool enabled,
        Func<TValue, bool> isOptionEnabled,
        SettingRowLayout layout)
    {
        float controlHeight = ImGui.GetFrameHeight();
        float rowHeight = RowChrome.ResolveRowHeight(controlHeight);

        RowChrome.BeginRow(definition, rowHeight);
        return DrawControl(definition, options, ref value, accent, enabled, isOptionEnabled, rowHeight, controlHeight, layout);
    }

    private static bool DrawControl<TValue>(
        SettingDefinition definition,
        IReadOnlyList<ChoiceOption<TValue>> options,
        ref TValue value,
        Vector4 accent,
        bool enabled,
        Func<TValue, bool> isOptionEnabled,
        float rowHeight,
        float controlHeight,
        SettingRowLayout layout)
    {
        string preview = ResolveOptionLabel(options, value);
        float availableControlWidth = RowChrome.AvailableControlWidth();
        float controlWidth = MathF.Max(
            Scaled(ComboMinWidth),
            RowChrome.ResolveControlWidth(availableControlWidth, layout));

        RowChrome.AlignControl(rowHeight, controlHeight, controlWidth, alignRight: true);

        bool changed = false;
        using (ImRaii.ItemWidth(controlWidth))
        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, EditorColors.ButtonDefault with { W = enabled ? 0.42f : 0.20f }))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, EditorColors.ButtonDefault with { W = 0.54f }))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, accent with { W = 0.22f }))
        using (ImRaii.PushColor(ImGuiCol.Border, accent with { W = 0.24f }))
        {
            using var combo = UiSharedService.BeginCombo($"##objectChoice_{definition.Id}", preview);
            if (combo)
            {
                foreach (ChoiceOption<TValue> option in options)
                {
                    bool optionEnabled = enabled && isOptionEnabled(option.Value);
                    bool selected = EqualityComparer<TValue>.Default.Equals(value, option.Value);
                    using (ImRaii.Disabled(!optionEnabled))
                    {
                        if (ImGui.Selectable(option.Label, selected) && optionEnabled)
                        {
                            value = option.Value;
                            changed = true;
                        }
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }
            }
        }

        RowChrome.DrawDescriptionTooltip(definition);
        return changed;
    }

    private static string ResolveOptionLabel<TValue>(IReadOnlyList<ChoiceOption<TValue>> options, TValue value)
    {
        foreach (ChoiceOption<TValue> option in options)
        {
            if (EqualityComparer<TValue>.Default.Equals(option.Value, value))
            {
                return option.Label;
            }
        }

        return "Unknown";
    }
}

