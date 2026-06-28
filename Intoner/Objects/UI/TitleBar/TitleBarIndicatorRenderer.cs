using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.TitleBar;

internal static class TitleBarIndicatorRenderer
{
    private const float HorizontalPadding = 7f;
    private const float VerticalPadding = 2f;
    private const float IndicatorSpacing = 8f;
    private const float IconSlotSize = 16f;
    private const float IconGap = 6f;
    private const float ButtonGap = 10f;
    private const float TitleGap = 6f;
    private const float Rounding = 5f;
    private const float BorderOpacity = 0.52f;
    private const float TextOpacity = 0.94f;

    public static void Draw(
        TitleBarRenderContext context,
        IReadOnlyList<TitleBarIndicator> indicators,
        float reservedLeftEdge = 0f)
    {
        if (indicators.Count == 0 || !context.HasTitleBar)
        {
            return;
        }

        ImGuiStylePtr style = ImGui.GetStyle();
        float scale = ImGuiHelpers.GlobalScale;
        float leftEdge = MathF.Max(ResolveLeftEdge(context, style, scale), reservedLeftEdge);
        float rightEdge = ResolveRightEdge(context, style, scale);
        if (rightEdge <= leftEdge)
        {
            return;
        }

        float cursorRight = rightEdge;
        for (int index = indicators.Count - 1; index >= 0; --index)
        {
            TitleBarIndicator indicator = indicators[index];
            if (!TryResolveLabel(
                    indicator,
                    cursorRight - leftEdge,
                    scale,
                    out string label,
                    out Vector2 textSize,
                    out Vector2 indicatorSize))
            {
                continue;
            }

            Vector2 indicatorMin = new(cursorRight - indicatorSize.X, context.Min.Y + ((context.Height - indicatorSize.Y) * 0.5f));
            Vector2 indicatorMax = indicatorMin + indicatorSize;
            if (indicatorMin.X < leftEdge)
            {
                continue;
            }

            DrawIndicator(context.DrawList, indicator, label, textSize, indicatorMin, indicatorMax, scale);
            cursorRight = indicatorMin.X - (IndicatorSpacing * scale);
        }
    }

    private static float ResolveLeftEdge(TitleBarRenderContext context, ImGuiStylePtr style, float scale)
    {
        string title = StripId(context.WindowTitle);
        Vector2 titleSize = ImGui.CalcTextSize(title);
        return context.Min.X + style.FramePadding.X + titleSize.X + style.ItemInnerSpacing.X + (TitleGap * scale);
    }

    private static float ResolveRightEdge(
        TitleBarRenderContext context,
        ImGuiStylePtr style,
        float scale)
    {
        int buttonCount = CountTitleBarButtons(context, style);
        float buttonWidth = ImGui.GetFrameHeight();
        float buttonSpacing = style.ItemInnerSpacing.X;
        float buttonArea = buttonCount > 0
            ? (buttonWidth * buttonCount) + (buttonSpacing * (buttonCount - 1))
            : 0f;
        float buttonGap = buttonCount > 0
            ? ButtonGap * scale
            : 0f;
        return context.Max.X - style.FramePadding.X - buttonArea - buttonGap;
    }

    private static int CountTitleBarButtons(TitleBarRenderContext context, ImGuiStylePtr style)
    {
        int count = 0;
        if (!context.WindowFlags.HasFlag(ImGuiWindowFlags.NoCollapse) && style.WindowMenuButtonPosition == ImGuiDir.Right)
        {
            count++;
        }

        if (context.ShowCloseButton)
        {
            count++;
        }

        if (context.AllowPinning || context.AllowClickthrough)
        {
            count++;
        }

        count += context.TitleBarButtons.Count;
        return count;
    }

    private static bool TryResolveLabel(
        TitleBarIndicator indicator,
        float availableWidth,
        float scale,
        out string label,
        out Vector2 textSize,
        out Vector2 indicatorSize)
        => TryResolveLabel(indicator.Label, availableWidth, scale, out label, out textSize, out indicatorSize)
           || (!string.Equals(indicator.Label, indicator.CompactLabel, StringComparison.Ordinal)
               && TryResolveLabel(indicator.CompactLabel, availableWidth, scale, out label, out textSize, out indicatorSize));

    private static bool TryResolveLabel(
        string? candidate,
        float availableWidth,
        float scale,
        out string label,
        out Vector2 textSize,
        out Vector2 indicatorSize)
    {
        label = candidate ?? string.Empty;
        if (string.IsNullOrEmpty(label))
        {
            textSize = default;
            indicatorSize = default;
            return false;
        }

        float iconSlotSize = IconSlotSize * scale;
        textSize = ImGui.CalcTextSize(label);
        indicatorSize = new Vector2(
            iconSlotSize + (IconGap * scale) + textSize.X + (HorizontalPadding * 2f * scale),
            MathF.Max(iconSlotSize, textSize.Y) + (VerticalPadding * 2f * scale));
        return indicatorSize.X <= availableWidth;
    }

    private static void DrawIndicator(
        ImDrawListPtr drawList,
        TitleBarIndicator indicator,
        string label,
        Vector2 textSize,
        Vector2 indicatorMin,
        Vector2 indicatorMax,
        float scale)
    {
        Vector4 border = EditorColors.WithAlpha(indicator.Accent, BorderOpacity);
        Vector4 textColor = EditorColors.Color(1f, 1f, 1f, TextOpacity);
        string icon = indicator.Icon.ToIconString();
        (Vector2 iconSize, float iconFontSize) = MeasureIcon(icon);
        float iconSlotSize = IconSlotSize * scale;
        Vector2 iconMin = new(
            indicatorMin.X + (HorizontalPadding * scale),
            indicatorMin.Y + ((indicatorMax.Y - indicatorMin.Y - iconSlotSize) * 0.5f));
        Vector2 iconMax = iconMin + new Vector2(iconSlotSize);
        Vector2 iconPosition = iconMin + ((new Vector2(iconSlotSize) - iconSize) * 0.5f);
        Vector2 textPosition = new(
            iconMax.X + (IconGap * scale),
            indicatorMin.Y + ((indicatorMax.Y - indicatorMin.Y - textSize.Y) * 0.5f));

        drawList.AddRect(indicatorMin, indicatorMax, ImGui.GetColorU32(border), Rounding * scale);
        drawList.AddText(UiBuilder.IconFont, iconFontSize, iconPosition, ImGui.GetColorU32(indicator.Accent), icon);
        drawList.AddText(textPosition, ImGui.GetColorU32(textColor), label);
    }

    private static (Vector2 Size, float FontSize) MeasureIcon(string icon)
    {
        using var iconFont = ImRaii.PushFont(UiBuilder.IconFont);
        return (ImGui.CalcTextSize(icon), ImGui.GetFontSize());
    }

    private static string StripId(string title)
    {
        int splitIndex = title.IndexOf("###", StringComparison.Ordinal);
        return splitIndex >= 0
            ? title[..splitIndex]
            : title;
    }
}

