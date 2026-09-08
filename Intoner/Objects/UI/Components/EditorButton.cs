using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorButton
{
    private enum ButtonStyle
    {
        Standard,
        Primary,
        FormAction,
    }

    private const float ButtonHeight = 30f;
    private const float PrimaryButtonHeight = 32f;

    public static float Height => Scaled(ButtonHeight);
    public static float PrimaryHeight => Scaled(PrimaryButtonHeight);

    public static float MeasureWidth(
        FontAwesomeIcon icon,
        string label,
        float minimumWidth = 0f)
    {
        float contentWidth = EditorIcon.Measure(icon).Size.X
                           + Scaled(6f)
                           + ImGui.CalcTextSize(label ?? string.Empty).X
                           + Scaled(24f);
        return MathF.Max(Scaled(minimumWidth), contentWidth);
    }

    public static bool Draw(
        string id,
        FontAwesomeIcon icon,
        string label,
        Vector4 accent,
        Vector2 size,
        bool enabled = true,
        string? tooltip = null,
        Vector4? iconColor = null)
        => DrawCore(id, icon, label, accent, size, enabled, tooltip, iconColor, ButtonStyle.Standard);

    public static bool DrawPrimary(
        string id,
        FontAwesomeIcon icon,
        string label,
        Vector4 accent,
        Vector2 size,
        bool enabled = true,
        string? tooltip = null)
        => DrawCore(id, icon, label, accent, size, enabled, tooltip, accent, ButtonStyle.Primary);

    public static bool DrawFormAction(
        string id,
        FontAwesomeIcon icon,
        string label,
        Vector4 accent,
        Vector2 size,
        bool enabled = true,
        string? tooltip = null)
        => DrawCore(id, icon, label, accent, size, enabled, tooltip, accent, ButtonStyle.FormAction);

    private static bool DrawCore(
        string id,
        FontAwesomeIcon icon,
        string label,
        Vector4 accent,
        Vector2 size,
        bool enabled,
        string? tooltip,
        Vector4? iconColor,
        ButtonStyle style)
    {
        Vector4 fill = ThemeColors.ButtonDefault with { W = 0.88f };
        Vector4 hoverFill = accent with { W = 0.22f };
        Vector4 activeFill = accent with { W = 0.32f };
        Vector4 borderColor = accent with { W = 0.52f };
        if (!enabled)
        {
            fill = ThemeColors.ButtonDefault with { W = style == ButtonStyle.Standard ? 0.68f : 0.48f };
            hoverFill = fill;
            activeFill = fill;
            borderColor = ThemeColors.Border with { W = 0.26f };
        }
        else if (style == ButtonStyle.Primary)
        {
            fill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.14f) with { W = 0.86f };
            hoverFill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.23f) with { W = 0.92f };
            activeFill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.30f) with { W = 0.96f };
            borderColor = accent with { W = 0.42f };
        }
        else if (style == ButtonStyle.FormAction)
        {
            fill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.07f) with { W = 0.72f };
            hoverFill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.16f) with { W = 0.82f };
            activeFill = Vector4.Lerp(ThemeColors.ButtonDefault, accent, 0.23f) with { W = 0.88f };
            borderColor = accent with { W = 0.34f };
        }

        float rounding = Scaled(style == ButtonStyle.Standard ? 5f : 4f);
        bool customBorder = style != ButtonStyle.Standard;
        float borderSize = customBorder ? 0f : MathF.Max(1f, Scaled(1f));

        bool clicked;
        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushColor(ImGuiCol.Button, fill))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, hoverFill))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, activeFill))
        using (ImRaii.PushColor(ImGuiCol.Border, borderColor))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, borderSize))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, rounding))
        {
            clicked = ImGui.Button($"##editorButton:{id}", size);
        }

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        if (customBorder)
        {
            DrawSharpBorder(min, max, borderColor, rounding);
        }

        DrawContent(icon, label, iconColor, enabled, style != ButtonStyle.Standard, min, max);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)
            && !string.IsNullOrWhiteSpace(tooltip))
        {
            IntonerTooltip.DrawDescription(
                icon,
                tooltip,
                options: new IntonerTooltipOptions { Accent = accent });
        }

        return enabled && clicked;
    }

    private static void DrawSharpBorder(Vector2 min, Vector2 max, Vector4 color, float rounding)
    {
        float thickness = MathF.Max(1f, Scaled(0.75f));
        float inset = thickness * 0.5f;
        ImGui.GetWindowDrawList().AddRect(
            min + new Vector2(inset),
            max - new Vector2(inset),
            ImGui.GetColorU32(color),
            MathF.Max(0f, rounding - inset),
            ImDrawFlags.None,
            thickness);
    }

    private static void DrawContent(
        FontAwesomeIcon icon,
        string label,
        Vector4? iconColor,
        bool enabled,
        bool primary,
        Vector2 min,
        Vector2 max)
    {
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        float spacing = Scaled(6f);
        float paddingX = Scaled(12f);
        float availableLabelWidth = MathF.Max(
            Scaled(12f),
            max.X - min.X - (paddingX * 2f) - iconMetrics.Size.X - spacing);
        string renderedLabel = EditorTextUtility.ClipTextToWidth(label, availableLabelWidth);
        Vector2 labelSize = ImGui.CalcTextSize(renderedLabel);
        float contentWidth = iconMetrics.Size.X + spacing + labelSize.X;
        float startX = min.X + MathF.Max(0f, ((max.X - min.X) - contentWidth) * 0.5f);
        Vector4 textColor;
        Vector4 resolvedIconColor;
        if (enabled)
        {
            textColor = ThemeColors.Text;
            resolvedIconColor = iconColor ?? textColor;
        }
        else
        {
            textColor = primary
                ? ThemeColors.WithAlpha(ThemeColors.TextDisabled, 0.90f)
                : ThemeColors.TextDisabled;
            if (primary)
            {
                resolvedIconColor = ThemeColors.TextDisabled with { W = 0.72f };
            }
            else
            {
                resolvedIconColor = iconColor is { } disabledIconColor
                    ? ThemeColors.WithAlpha(disabledIconColor, 0.58f)
                    : ThemeColors.TextDisabled;
            }
        }
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        EditorIcon.Draw(
            drawList,
            icon,
            iconMetrics,
            new Vector2(startX, min.Y + MathF.Max(0f, ((max.Y - min.Y) - iconMetrics.Size.Y) * 0.5f)),
            resolvedIconColor);
        drawList.AddText(
            new Vector2(
                startX + iconMetrics.Size.X + spacing,
                min.Y + MathF.Max(0f, ((max.Y - min.Y) - labelSize.Y) * 0.5f)),
            ImGui.GetColorU32(textColor),
            renderedLabel);
    }

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}
