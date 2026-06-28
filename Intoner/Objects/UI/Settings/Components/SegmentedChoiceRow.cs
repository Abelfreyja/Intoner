using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class SegmentedChoiceRow
{
    private const float ButtonHeight = 34f;
    private const float ProminentButtonHeight = 38f;
    private const float ButtonSpacing = 2f;

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
        var scale = ImGuiHelpers.GlobalScale;
        float spacing = ButtonSpacing * scale;
        float segmentWidth = MathF.Max(1f, (controlWidth - (spacing * (options.Count - 1))) / options.Count);

        for (var index = 0; index < options.Count; ++index)
        {
            ChoiceOption<TValue> option = options[index];
            bool selected = EqualityComparer<TValue>.Default.Equals(value, option.Value);
            bool optionEnabled = enabled && isOptionEnabled(option.Value);

            if (index > 0)
            {
                ImGui.SameLine(0f, spacing);
            }

            if (DrawSegment(
                    $"##segmentedChoice_{definition.Id}_{index}",
                    option.Label,
                    selected,
                    optionEnabled,
                    ResolveTooltip(option, definition.Description),
                    accent,
                    new Vector2(segmentWidth, controlHeight),
                    ResolveRoundingFlags(index, options.Count)))
            {
                value = option.Value;
                changed = true;
            }
        }

        return changed;
    }

    private static bool DrawSegment(
        string id,
        string label,
        bool selected,
        bool enabled,
        string tooltip,
        Vector4 accent,
        Vector2 size,
        ImDrawFlags roundingFlags)
    {
        bool clicked;
        using (ImRaii.Disabled(!enabled))
        {
            clicked = ImGui.InvisibleButton(id, size);
        }

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        bool hovered = ImGui.IsItemHovered();
        var scale = ImGuiHelpers.GlobalScale;
        float rounding = 7f * scale;
        Vector4 fill = ResolveFillColor(selected, enabled, hovered, accent);
        Vector4 border = ResolveBorderColor(selected, enabled, hovered, accent);
        Vector4 text = ResolveTextColor(selected, enabled, hovered);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding, roundingFlags);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, roundingFlags, 1f * scale);

        if (selected)
        {
            DrawSelectionIndicator(drawList, min, max, size, accent, enabled);
        }

        DrawCenteredText(drawList, min, max, label, text);
        RowChrome.DrawTooltip(tooltip);
        return clicked && enabled;
    }

    private static string ResolveTooltip<TValue>(ChoiceOption<TValue> option, string fallback)
        => string.IsNullOrWhiteSpace(option.Tooltip)
            ? fallback
            : option.Tooltip;

    private static void DrawSelectionIndicator(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector2 size, Vector4 accent, bool enabled)
    {
        var scale = ImGuiHelpers.GlobalScale;
        float indicatorWidth = MathF.Min(46f * scale, MathF.Max(1f, size.X - (28f * scale)));
        float indicatorHeight = MathF.Max(2f * scale, 1f);
        float centerX = (min.X + max.X) * 0.5f;
        float indicatorY = max.Y - (6f * scale);
        Vector2 indicatorMin = new(centerX - (indicatorWidth * 0.5f), indicatorY);
        Vector2 indicatorMax = new(centerX + (indicatorWidth * 0.5f), indicatorY + indicatorHeight);

        drawList.AddRectFilled(
            indicatorMin,
            indicatorMax,
            ImGui.GetColorU32(accent with { W = enabled ? 0.92f : 0.32f }),
            indicatorHeight * 0.5f);
    }

    private static ImDrawFlags ResolveRoundingFlags(int index, int count)
    {
        if (count <= 1)
        {
            return ImDrawFlags.RoundCornersAll;
        }

        return index switch
        {
            0             => ImDrawFlags.RoundCornersLeft,
            var last when last == count - 1 => ImDrawFlags.RoundCornersRight,
            _             => ImDrawFlags.RoundCornersNone,
        };
    }

    private static Vector4 ResolveFillColor(bool selected, bool enabled, bool hovered, Vector4 accent)
    {
        if (!enabled)
        {
            return EditorColors.ButtonDefault with { W = 0.18f };
        }

        if (selected)
        {
            return accent with { W = hovered ? 0.42f : 0.30f };
        }

        return EditorColors.ButtonDefault with { W = hovered ? 0.56f : 0.38f };
    }

    private static Vector4 ResolveBorderColor(bool selected, bool enabled, bool hovered, Vector4 accent)
    {
        if (!enabled)
        {
            return EditorColors.Border with { W = 0.14f };
        }

        if (selected)
        {
            return accent with { W = hovered ? 0.70f : 0.54f };
        }

        return EditorColors.Border with { W = hovered ? 0.42f : 0.26f };
    }

    private static Vector4 ResolveTextColor(bool selected, bool enabled, bool hovered)
    {
        if (!enabled)
        {
            return EditorColors.TextDisabled with { W = 0.48f };
        }

        if (selected)
        {
            return EditorColors.Text with { W = 0.98f };
        }

        return hovered
            ? EditorColors.Text with { W = 0.88f }
            : EditorColors.TextDisabled with { W = 0.82f };
    }
}

