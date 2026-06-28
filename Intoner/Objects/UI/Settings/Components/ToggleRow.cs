using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal static class ToggleRow
{
    private const float CompactToggleWidth = 38f;
    private const float CompactToggleHeight = 20f;
    private const float ProminentToggleWidth = 54f;
    private const float ProminentToggleHeight = 28f;

    private readonly record struct ControlMetrics(Vector2 BadgeSize, Vector2 BadgePadding, float BadgeRounding, Vector2 ToggleSize, float Spacing)
    {
        public float Width
            => BadgeSize.X + Spacing + ToggleSize.X;

        public float Height
            => MathF.Max(BadgeSize.Y, ToggleSize.Y);
    }

    public static bool Draw(
        SettingDefinition definition,
        ref bool value,
        SettingStatus status,
        Vector4 accent,
        bool prominentControl,
        bool enabled = true)
    {
        ControlMetrics metrics = ResolveControlMetrics(status.Text, prominentControl);
        float rowHeight = RowChrome.ResolveRowHeight(metrics.Height);

        RowChrome.BeginRow(definition, rowHeight);
        return DrawSettingControl(definition, ref value, status, accent, metrics, rowHeight, enabled);
    }

    private static bool DrawSettingControl(
        SettingDefinition definition,
        ref bool value,
        SettingStatus status,
        Vector4 accent,
        ControlMetrics metrics,
        float rowHeight,
        bool enabled)
    {
        RowChrome.AlignControl(rowHeight, metrics.Height, metrics.Width, alignRight: true);
        RowChrome.DrawStatusBadge(status.Text, status.Color, metrics.BadgeSize, metrics.BadgePadding, metrics.BadgeRounding);
        ImGui.SameLine(0f, metrics.Spacing);
        bool changed = DrawToggleSwitch($"##objectSetting_{definition.Id}", ref value, accent, enabled, metrics.ToggleSize);
        RowChrome.DrawDescriptionTooltip(definition);
        return changed;
    }

    private static bool DrawToggleSwitch(string id, ref bool value, Vector4 accent, bool enabled, Vector2 size)
    {
        var scale = ImGuiHelpers.GlobalScale;
        bool changed;
        using (ImRaii.Disabled(!enabled))
        {
            changed = ImGui.InvisibleButton(id, size);
        }

        if (changed)
        {
            value = !value;
        }

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        bool hovered = ImGui.IsItemHovered();
        Vector4 trackColor = ResolveToggleTrackColor(value, enabled, hovered, accent);
        Vector4 knobColor = enabled
            ? EditorColors.Text
            : EditorColors.TextDisabled;
        float radius = size.Y * 0.5f;
        float knobRadius = MathF.Max(1f, radius - (3f * scale));
        float knobX = value
            ? max.X - radius
            : min.X + radius;
        Vector2 knobCenter = new(knobX, min.Y + radius);

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(trackColor), radius);
        drawList.AddRect(min, max, ImGui.GetColorU32(EditorColors.Border with { W = enabled ? 0.42f : 0.18f }), radius, ImDrawFlags.None, 1f * scale);
        drawList.AddCircleFilled(knobCenter, knobRadius, ImGui.GetColorU32(knobColor with { W = enabled ? 0.95f : 0.45f }));
        return changed;
    }

    private static Vector4 ResolveToggleTrackColor(bool value, bool enabled, bool hovered, Vector4 accent)
    {
        if (!enabled)
        {
            return EditorColors.ButtonDefault with { W = 0.24f };
        }

        if (value)
        {
            return accent with { W = hovered ? 0.74f : 0.58f };
        }

        return EditorColors.ButtonDefault with { W = hovered ? 0.66f : 0.48f };
    }

    private static ControlMetrics ResolveControlMetrics(string statusText, bool prominentControl)
    {
        var scale = ImGuiHelpers.GlobalScale;
        Vector2 toggleSize = prominentControl
            ? new Vector2(ProminentToggleWidth * scale, ProminentToggleHeight * scale)
            : new Vector2(CompactToggleWidth * scale, CompactToggleHeight * scale);
        Vector2 badgePadding = prominentControl
            ? new Vector2(10f * scale, 5f * scale)
            : new Vector2(7f * scale, 2f * scale);
        Vector2 badgeSize = ImGui.CalcTextSize(statusText) + (badgePadding * 2f);
        float badgeRounding = (prominentControl ? 6f : 4f) * scale;
        float spacing = prominentControl
            ? 8f * scale
            : ImGui.GetStyle().ItemSpacing.X;
        return new ControlMetrics(badgeSize, badgePadding, badgeRounding, toggleSize, spacing);
    }
}

