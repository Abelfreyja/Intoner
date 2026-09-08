using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.TitleBar;

internal static class TitleBarIndicatorRenderer
{
    private const float HorizontalPadding = 7f;
    private const float VerticalPadding = 2f;
    private const float IndicatorSpacing = 8f;
    private const float IconSlotSize = 16f;
    private const float IconScale = 0.80f;
    private const float IconGap = 6f;
    private const float ButtonGap = 10f;
    private const float TitleGap = 6f;
    private const float Rounding = 5f;
    private const float BorderOpacity = 0.52f;
    private const float TextOpacity = 0.94f;
    private const float CounterHorizontalPadding = 5f;
    private const float CounterDividerGap = 4f;
    private const float CounterDividerWidth = 1f;
    private const float CounterDividerHeight = 10f;
    private const float CounterTextScale = 0.85f;
    private const float CounterHoverOpacity = 0.10f;

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

            bool isHovered = context.IsWindowHovered
                          && ImGui.IsMouseHoveringRect(indicatorMin, indicatorMax, false);
            DrawIndicator(context.DrawList, indicator, label, textSize, indicatorMin, indicatorMax, scale, isHovered);
            if (isHovered && indicator.Tooltip is { } tooltip)
            {
                IntonerTooltip.Draw(
                    tooltip.DrawContent,
                    new IntonerTooltipOptions
                    {
                        Accent = indicator.Accent,
                        Width = tooltip.Width,
                    });
            }

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
    {
        if (indicator.Layout == TitleBarIndicatorLayout.Counter)
        {
            return TryResolveLabel(
                indicator.CompactLabel,
                availableWidth,
                scale,
                isCounter: true,
                out label,
                out textSize,
                out indicatorSize);
        }

        return TryResolveLabel(indicator.Label, availableWidth, scale, isCounter: false, out label, out textSize, out indicatorSize)
            || (!string.Equals(indicator.Label, indicator.CompactLabel, StringComparison.Ordinal)
             && TryResolveLabel(indicator.CompactLabel, availableWidth, scale, isCounter: false, out label, out textSize, out indicatorSize));
    }

    private static bool TryResolveLabel(
        string? candidate,
        float availableWidth,
        float scale,
        bool isCounter,
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
        float textScale = isCounter ? CounterTextScale : 1f;
        float horizontalPadding = isCounter ? CounterHorizontalPadding : HorizontalPadding;
        float contentGap = isCounter
            ? (CounterDividerGap * 2f) + CounterDividerWidth
            : IconGap;
        textSize = ImGui.CalcTextSize(label) * textScale;
        indicatorSize = new Vector2(
            iconSlotSize + (contentGap * scale) + textSize.X + (horizontalPadding * 2f * scale),
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
        float scale,
        bool isHovered)
    {
        bool isCounter = indicator.Layout == TitleBarIndicatorLayout.Counter;
        if (isCounter && isHovered)
        {
            drawList.AddRectFilled(
                indicatorMin,
                indicatorMax,
                ImGui.GetColorU32(ThemeColors.WithAlpha(indicator.Accent, CounterHoverOpacity)),
                Rounding * scale);
        }
        else if (!isCounter)
        {
            drawList.AddRect(
                indicatorMin,
                indicatorMax,
                ImGui.GetColorU32(ThemeColors.WithAlpha(indicator.Accent, BorderOpacity)),
                Rounding * scale);
        }

        float horizontalPadding = isCounter ? CounterHorizontalPadding : HorizontalPadding;
        float iconSlotSize = IconSlotSize * scale;
        float centerY = (indicatorMin.Y + indicatorMax.Y) * 0.5f;
        Vector2 iconMin = new(
            indicatorMin.X + (horizontalPadding * scale),
            centerY - (iconSlotSize * 0.5f));
        EditorIcon.DrawCentered(
            drawList,
            indicator.Icon,
            iconMin,
            iconMin + new Vector2(iconSlotSize),
            isCounter ? ThemeColors.WithAlpha(ThemeColors.Text, 0.92f) : indicator.Accent,
            IconScale);

        float textX = iconMin.X + iconSlotSize;
        if (isCounter)
        {
            float dividerX = textX + (CounterDividerGap * scale);
            float dividerHeight = CounterDividerHeight * scale;
            Vector2 dividerMin = new(dividerX, centerY - (dividerHeight * 0.5f));
            drawList.AddRectFilled(
                dividerMin,
                dividerMin + new Vector2(CounterDividerWidth * scale, dividerHeight),
                ImGui.GetColorU32(ThemeColors.WithAlpha(indicator.Accent, 0.72f)),
                0.5f * scale);
            textX = dividerX + ((CounterDividerWidth + CounterDividerGap) * scale);
        }
        else
        {
            textX += IconGap * scale;
        }

        Vector2 textPosition = new(
            textX,
            centerY - (textSize.Y * 0.5f));
        float textScale = isCounter ? CounterTextScale : 1f;
        Vector4 textColor = isCounter
            ? indicator.Accent
            : ThemeColors.Color(1f, 1f, 1f, TextOpacity);
        drawList.AddText(
            ImGui.GetFont(),
            ImGui.GetFontSize() * textScale,
            textPosition,
            ImGui.GetColorU32(textColor),
            label);
    }

    private static string StripId(string title)
    {
        int splitIndex = title.IndexOf("###", StringComparison.Ordinal);
        return splitIndex >= 0
            ? title[..splitIndex]
            : title;
    }
}

