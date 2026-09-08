using Dalamud.Bindings.ImGui;
using Intoner.Objects.UI.Components;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class SegmentedChoiceRow
{
    private const float ButtonHeight = 34f;
    private const float ProminentButtonHeight = 38f;

    public static bool Draw<TValue>(
        SettingDefinition definition,
        IReadOnlyList<ChoiceOption<TValue>> options,
        ref TValue value,
        Vector4 accent,
        bool prominentControl,
        bool enabled,
        Func<TValue, bool> isOptionEnabled,
        SettingRowLayout layout)
    {
        float controlHeight = Scaled(prominentControl ? ProminentButtonHeight : ButtonHeight);
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
        if (options.Count == 0)
        {
            return false;
        }

        float controlWidth = RowChrome.ResolveControlWidth(RowChrome.AvailableControlWidth(), layout);

        RowChrome.AlignControl(rowHeight, controlHeight, controlWidth);
        bool changed = DrawSegments(definition, options, ref value, accent, enabled, controlWidth, controlHeight, isOptionEnabled);
        return changed;
    }

    private static bool DrawSegments<TValue>(
        SettingDefinition definition,
        IReadOnlyList<ChoiceOption<TValue>> options,
        ref TValue value,
        Vector4 accent,
        bool enabled,
        float controlWidth,
        float controlHeight,
        Func<TValue, bool> isOptionEnabled)
    {
        bool changed = false;
        float spacing = EditorSegmentedControl.Spacing;
        float segmentWidth = EditorSegmentedControl.ResolveSegmentWidth(controlWidth, options.Count);

        for (var index = 0; index < options.Count; ++index)
        {
            ChoiceOption<TValue> option = options[index];
            bool selected = EqualityComparer<TValue>.Default.Equals(value, option.Value);
            bool optionEnabled = enabled && isOptionEnabled(option.Value);

            if (index > 0)
            {
                ImGui.SameLine(0f, spacing);
            }

            if (EditorSegmentedControl.DrawSegment(
                    $"##segmentedChoice_{definition.Id}_{index}",
                    null,
                    option.Label,
                    null,
                    selected,
                    optionEnabled,
                    ResolveTooltip(option, definition.Description),
                    accent,
                    new Vector2(segmentWidth, controlHeight),
                    index,
                    options.Count))
            {
                value = option.Value;
                changed = true;
            }
        }

        return changed;
    }

    private static string ResolveTooltip<TValue>(ChoiceOption<TValue> option, string fallback)
        => string.IsNullOrWhiteSpace(option.Tooltip)
            ? fallback
            : option.Tooltip;
}

