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

internal enum EditorToggleIconButtonStyle
{
    Subtle,
    Outlined,
    MutedOutline,
}

internal static class EditorIconButton
{
    public readonly record struct Item(string Id, FontAwesomeIcon Icon, string Tooltip, bool Enabled = true,
        bool Selected = false, Vector4? IconColor = null, Vector4? TooltipTitleColor = null);

    private const float MinimumEdge = 28f;
    private const float IconPadding = 12f;

    public static Vector2 MeasureStrip(IReadOnlyList<Item> items)
    {
        float edge = items.Count == 0 ? 0f : MathF.Ceiling(MathF.Max(Scaled(MinimumEdge),
            MathF.Max(ImGui.GetFrameHeight(), UiBuilder.IconFont.FontSize + Scaled(IconPadding))));
        return new Vector2(EditorLayout.ResolveActionStripWidth(edge, items.Count), edge);
    }

    public static string? DrawStrip(IReadOnlyList<Item> items)
    {
        if (items.Count == 0)
            return null;

        float edge = MeasureStrip(items).Y;
        float gap = ImGui.GetStyle().ItemSpacing.X;
        int columns = Math.Max(1, (int)MathF.Floor((ImGui.GetContentRegionAvail().X + gap) / (edge + gap)));
        string? activatedId = null;
        for (int index = 0; index < items.Count; ++index)
        {
            if (index % columns != 0)
                ImGui.SameLine();
            Item item = items[index];
            EditorIcon.Metrics metrics = EditorIcon.Measure(item.Icon);
            float iconScale = MathF.Min(1f, MathF.Max(1f, edge - Scaled(IconPadding))
                / MathF.Max(1f, MathF.Max(metrics.Size.X, metrics.Size.Y)));
            if (DrawDefault(item.Id, item.Icon, item.Tooltip, item.Selected, edge, item.IconColor,
                    item.TooltipTitleColor, enabled: item.Enabled, iconScale: iconScale))
                activatedId = item.Id;
        }
        return activatedId;
    }

    public static float MeasureEdge(FontAwesomeIcon icon)
    {
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        return MathF.Max(
            Scaled(MinimumEdge),
            MathF.Ceiling(MathF.Max(iconMetrics.Size.X, iconMetrics.Size.Y) + Scaled(IconPadding)));
    }

    public static float MeasureMaxEdge(params ReadOnlySpan<FontAwesomeIcon> icons)
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
        bool enabled = true,
        float iconScale = 1f)
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
            enabled ? iconColor ?? ThemeColors.Text : ThemeColors.TextDisabled,
            iconScale);

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
        bool enabled = true,
        Action? drawTooltip = null,
        EditorToggleIconButtonStyle style = EditorToggleIconButtonStyle.Subtle)
    {
        Vector4 accent = selected ? selectedAccent : inactiveAccent;
        ResolveToggleColors(selected, enabled, accent, style, out Vector4 fill, out Vector4 hoverFill, out Vector4 activeFill, out Vector4 border);
        bool subtle = style == EditorToggleIconButtonStyle.Subtle;
        float rounding = Scaled(subtle ? 4f : 7f);
        float borderThickness = MathF.Max(1f, Scaled(subtle ? 0.75f : 1f));
        float borderInset = subtle ? borderThickness * 0.5f : 0f;

        bool clicked;
        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushColor(ImGuiCol.Button, fill))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverFill))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, activeFill))
        using (ImRaii.PushColor(ImGuiCol.Border, Vector4.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, subtle ? 0f : borderThickness))
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
        float iconAlpha = (selected, style) switch
        {
            (_, EditorToggleIconButtonStyle.Subtle) => accent.W,
            (true, _) => 1f,
            (_, EditorToggleIconButtonStyle.Outlined) => 0.88f,
            _ => 0.72f,
        };
        EditorIcon.DrawCentered(
            drawList,
            icon,
            min,
            max,
            enabled ? accent with { W = iconAlpha } : ThemeColors.TextDisabled with { W = 0.72f });

        if (drawTooltip is not null || !string.IsNullOrWhiteSpace(tooltip))
        {
            DrawTooltip(icon, tooltip ?? string.Empty, accent, null, drawTooltip);
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
        EditorToggleIconButtonStyle style,
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

        if (style != EditorToggleIconButtonStyle.Subtle)
        {
            (float hoverAlpha, float activeAlpha, float borderAlpha) = (selected, style) switch
            {
                (true, _) => (0.30f, 0.36f, 0.88f),
                (_, EditorToggleIconButtonStyle.MutedOutline) => (0.08f, 0.12f, 0.28f),
                _ => (0.12f, 0.18f, 0.42f),
            };
            fill = selected ? accent with { W = 0.20f } : ThemeColors.ButtonDefault with { W = 0.80f };
            hoverFill = accent with { W = hoverAlpha };
            activeFill = accent with { W = activeAlpha };
            border = accent with { W = borderAlpha };
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
