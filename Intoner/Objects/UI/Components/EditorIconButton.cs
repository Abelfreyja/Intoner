using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal enum EditorAccentIconButtonStyle
{
    Neutral,
    Tinted,
    Subtle,
}

internal static class EditorIconButton
{
    private const float MinimumEdge = 28f;
    private const float IconPadding = 12f;

    public static float MeasureEdge(FontAwesomeIcon icon)
    {
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        return MathF.Max(
            Scaled(MinimumEdge),
            MathF.Ceiling(MathF.Max(iconMetrics.Size.X, iconMetrics.Size.Y) + Scaled(IconPadding)));
    }

    public static float MeasureMaxEdge(params FontAwesomeIcon[] icons)
    {
        float edge = 0f;
        foreach (FontAwesomeIcon icon in icons)
        {
            edge = MathF.Max(edge, MeasureEdge(icon));
        }

        return edge;
    }

    public static float MeasureCompactEdge()
        => MathF.Max(ImGui.GetFrameHeight(), Scaled(22f));

    public static bool DrawCompact(string id, FontAwesomeIcon icon, string tooltip, Vector4 accent, bool enabled = true, float? edgeOverride = null)
    {
        Vector4 textColor = enabled
            ? accent
            : ThemeColors.TextDisabled;
        Vector4 fill = ThemeColors.ButtonDefault with { W = enabled ? 0.62f : 0.34f };

        using var button = ImRaii.PushColor(ImGuiCol.Button, fill)
            .Push(ImGuiCol.ButtonHovered, accent with { W = enabled ? 0.16f : 0.06f })
            .Push(ImGuiCol.ButtonActive, accent with { W = enabled ? 0.22f : 0.06f });
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Scaled(5f));
        return DrawDefault(id, icon, tooltip, edgeOverride: edgeOverride ?? MeasureCompactEdge(), iconColor: textColor, tooltipAccent: accent, enabled: enabled);
    }

    public static bool DrawDefault(
        string id,
        FontAwesomeIcon icon,
        string tooltip,
        bool selected = false,
        float? edgeOverride = null,
        Vector4? iconColor = null,
        Vector4? tooltipTitleColor = null,
        Vector4? tooltipAccent = null,
        bool enabled = true)
    {
        float edge = edgeOverride ?? MeasureEdge(icon);
        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;

        bool clicked;
        using (ImRaii.Disabled(!enabled))
        {
            clicked = ImGui.Button($"##{id}", new Vector2(edge));
        }

        EditorIcon.DrawCentered(
            ImGui.GetWindowDrawList(),
            icon,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            iconColor ?? (enabled ? ThemeColors.Text : ThemeColors.TextDisabled));

        DrawTooltip(icon, tooltip, tooltipAccent ?? iconColor, tooltipTitleColor);
        return enabled && clicked;
    }

    public static bool DrawAccent(
        string id,
        FontAwesomeIcon icon,
        string tooltip,
        Vector4 accent,
        float? edgeOverride = null,
        Vector4? tooltipTitleColor = null,
        EditorAccentIconButtonStyle style = EditorAccentIconButtonStyle.Neutral,
        Action? drawTooltip = null)
    {
        float edge = edgeOverride ?? MeasureEdge(icon);
        Vector2 size = new(edge);
        ResolveAccentColors(accent, style, out Vector4 fill, out Vector4 hoverFill, out Vector4 activeFill);
        float rounding = Scaled(6f);
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, fill))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverFill))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, activeFill))
        using (ImRaii.PushColor(ImGuiCol.Border, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, rounding))
        using (ImRaii.PushStyle(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f)))
        {
            clicked = ImGui.Button($"##{id}", size);
        }

        if (style != EditorAccentIconButtonStyle.Subtle || ImGui.IsItemHovered())
        {
            float borderAlpha = style switch
            {
                EditorAccentIconButtonStyle.Tinted => 0.62f,
                EditorAccentIconButtonStyle.Subtle => 0.46f,
                _                                  => 1f,
            };
            ImGui.GetWindowDrawList().AddRect(
                ImGui.GetItemRectMin(),
                ImGui.GetItemRectMax(),
                ImGui.GetColorU32(accent with { W = borderAlpha }),
                rounding,
                ImDrawFlags.None,
                Scaled(1f));
        }

        EditorIcon.DrawCentered(
            ImGui.GetWindowDrawList(),
            icon,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            accent);

        DrawTooltip(icon, tooltip, accent, tooltipTitleColor, drawTooltip);
        return clicked;
    }

    public static bool DrawToggle(
        string id,
        FontAwesomeIcon icon,
        Vector2 size,
        bool selected,
        Vector4 selectedAccent,
        Vector4 inactiveAccent,
        string? tooltip = null,
        bool enabled = true)
    {
        Vector4 accent = selected ? selectedAccent : inactiveAccent;
        ResolveToggleColors(selected, enabled, accent, out Vector4 fill, out Vector4 hoverFill, out Vector4 activeFill, out Vector4 border);
        float rounding = Scaled(4f);
        float borderThickness = MathF.Max(1f, Scaled(0.75f));
        float borderInset = borderThickness * 0.5f;

        bool clicked;
        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushColor(ImGuiCol.Button, fill))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverFill))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, activeFill))
        using (ImRaii.PushColor(ImGuiCol.Border, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, rounding))
        {
            clicked = ImGui.Button($"##{id}", size);
        }

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddRect(
            min + new Vector2(borderInset),
            max - new Vector2(borderInset),
            ImGui.GetColorU32(border),
            MathF.Max(0f, rounding - borderInset),
            ImDrawFlags.None,
            borderThickness);
        EditorIcon.DrawCentered(
            drawList,
            icon,
            min,
            max,
            enabled ? accent : ThemeColors.TextDisabled with { W = 0.72f });

        if (!string.IsNullOrWhiteSpace(tooltip))
        {
            DrawTooltip(icon, tooltip, accent, null);
        }

        return enabled && clicked;
    }

    private static void ResolveAccentColors(
        Vector4 accent,
        EditorAccentIconButtonStyle style,
        out Vector4 fill,
        out Vector4 hoverFill,
        out Vector4 activeFill)
    {
        switch (style)
        {
            case EditorAccentIconButtonStyle.Subtle:
                fill = Vector4.Zero;
                hoverFill = accent with { W = 0.16f };
                activeFill = accent with { W = 0.24f };
                return;
            case EditorAccentIconButtonStyle.Tinted:
                fill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.12f) with { W = 0.94f };
                hoverFill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.22f) with { W = 0.96f };
                activeFill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.30f) with { W = 0.98f };
                return;
            default:
                fill = ThemeColors.ButtonDefault with { W = 0.88f };
                hoverFill = accent with { W = 0.16f };
                activeFill = accent with { W = 0.10f };
                return;
        }
    }

    private static void ResolveToggleColors(
        bool selected,
        bool enabled,
        Vector4 accent,
        out Vector4 fill,
        out Vector4 hoverFill,
        out Vector4 activeFill,
        out Vector4 border)
    {
        if (!enabled)
        {
            fill = ThemeColors.ButtonDefault with { W = 0.48f };
            hoverFill = fill;
            activeFill = fill;
            border = ThemeColors.Border with { W = 0.26f };
            return;
        }

        fill = selected
            ? accent with { W = 0.18f }
            : ThemeColors.ButtonDefault with { W = 0.88f };
        hoverFill = accent with { W = selected ? 0.24f : 0.16f };
        activeFill = accent with { W = selected ? 0.20f : 0.10f };
        border = accent with { W = selected ? 0.50f : 0.42f };
    }

    private static void DrawTooltip(
        FontAwesomeIcon icon,
        string tooltip,
        Vector4? accent,
        Vector4? titleColor,
        Action? drawTooltip = null)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            return;
        }

        if (drawTooltip is not null)
        {
            drawTooltip();
            return;
        }

        IntonerTooltip.DrawDescription(
            icon,
            tooltip,
            options: new IntonerTooltipOptions
            {
                Accent = accent,
                TitleColor = titleColor,
            });
    }

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}
