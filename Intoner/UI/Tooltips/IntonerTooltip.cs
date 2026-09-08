using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Tooltips;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct IntonerTooltipOptions
{
    public Vector4? Accent { get; init; }
    public Vector4? TitleColor { get; init; }
    public Vector4? Background { get; init; }
    public Vector4? Border { get; init; }
    public Vector2? Padding { get; init; }
    public Vector2? ItemSpacing { get; init; }
    public float? Width { get; init; }
    public float? MaxWidth { get; init; }
    public float? Rounding { get; init; }
    public float? BorderWidth { get; init; }
    public float? WrapWidthEms { get; init; }
}

internal static class IntonerTooltip
{
    private const float DefaultRichWidth = 320f;
    private const float DefaultMaxWidth = 420f;
    private const float DefaultRounding = 6f;
    private const float DefaultBorderWidth = 1f;
    private const float DefaultWrapWidthEms = 34f;
    private const float DefaultPadding = 10f;

    private static int _suppressionDepth;

    public static bool IsSuppressed => _suppressionDepth > 0;

    public static bool IsAreaHovered(Vector2 min, Vector2 max)
        => !IsSuppressed
        && max.X > min.X
        && max.Y > min.Y
        && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
        && ImGui.IsMouseHoveringRect(min, max);

    /// <summary> suppresses tooltips while drawing UI covered by an overlay </summary>
    public static SuppressionScope Suppress(bool suppress = true)
    {
        if (suppress)
        {
            ++_suppressionDepth;
        }

        return new SuppressionScope(suppress);
    }

    public static void Attach(
        string text,
        IntonerTooltipOptions options = default,
        ImGuiHoveredFlags hoverFlags = ImGuiHoveredFlags.AllowWhenDisabled)
    {
        if (!IsSuppressed && ImGui.IsItemHovered(hoverFlags))
        {
            DrawText(text, options);
        }
    }

    public static void Attach(
        FontAwesomeIcon icon,
        string title,
        string detail = "",
        IntonerTooltipOptions options = default,
        ImGuiHoveredFlags hoverFlags = ImGuiHoveredFlags.AllowWhenDisabled)
    {
        if (!IsSuppressed && ImGui.IsItemHovered(hoverFlags))
        {
            DrawDescription(icon, title, detail, options);
        }
    }

    public static void DrawText(string text, IntonerTooltipOptions options = default)
    {
        if (IsSuppressed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ResolvedStyle style = ResolveStyle(options, false);
        DrawResolved(
            () =>
            {
                float wrapWidth = style.Width.HasValue
                    ? ImGui.GetContentRegionAvail().X
                    : MathF.Min(
                        ImGui.GetFontSize() * style.WrapWidthEms,
                        style.MaxWidth - (style.Padding.X * 2f));
                float wrapPosition = ImGui.GetCursorPosX() + MathF.Max(1f, wrapWidth);
                using var wrap = ImRaii.TextWrapPos(wrapPosition);
                ImGui.TextUnformatted(text.Trim());
            },
            style);
    }

    public static void DrawText(
        string text,
        Vector4 accent,
        float wrapWidthEms = DefaultWrapWidthEms,
        float width = 0f)
        => DrawText(
            text,
            new IntonerTooltipOptions
            {
                Accent = accent,
                WrapWidthEms = wrapWidthEms,
                Width = width > 0f ? width : null,
            });

    public static void DrawDescription(
        FontAwesomeIcon icon,
        string title,
        string detail = "",
        IntonerTooltipOptions options = default)
    {
        if (IsSuppressed || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        string normalizedTitle = title.Trim();
        string normalizedDetail = string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim();
        DrawContentSized(
            () => IntonerTooltipContent.Header(
                icon,
                normalizedTitle,
                normalizedDetail,
                options.Accent,
                options.TitleColor),
            IntonerTooltipContent.MeasureHeaderWidth(icon, normalizedTitle, normalizedDetail),
            options);
    }

    public static void DrawContentSized(
        Action content,
        float measuredContentWidth,
        IntonerTooltipOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (IsSuppressed)
        {
            return;
        }

        ResolvedStyle style = ResolveStyle(options, true);
        if (!options.Width.HasValue)
        {
            // imgui truncates window sizes, so keep fractional scaling from reducing the measured content width
            float width = MathF.Ceiling(MathF.Max(1f, measuredContentWidth) + (style.Padding.X * 2f));
            style = style with { Width = MathF.Min(width, style.MaxWidth) };
        }

        DrawResolved(content, style);
    }

    public static void Draw(Action content, IntonerTooltipOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (IsSuppressed)
        {
            return;
        }

        DrawResolved(content, ResolveStyle(options, true));
    }

    private static void DrawResolved(Action content, ResolvedStyle style)
    {
        if (IsSuppressed)
        {
            return;
        }

        using var enabled = ImRaii.Enabled();
        ApplySizeConstraints(style);

        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, style.Rounding);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, style.Padding);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, style.ItemSpacing);
        using var borderWidth = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, style.BorderWidth);
        using var background = ImRaii.PushColor(ImGuiCol.PopupBg, style.Background);
        using var border = ImRaii.PushColor(ImGuiCol.Border, style.Border);
        using var text = ImRaii.PushColor(ImGuiCol.Text, ThemeColors.TextDefault);
        using var disabledText = ImRaii.PushColor(ImGuiCol.TextDisabled, ThemeColors.TextDisabledDefault);
        using var tooltip = ImRaii.Tooltip();
        if (!tooltip.Alive)
        {
            return;
        }

        content();
    }

    public static void DrawOverlayText(
        ImDrawListPtr drawList,
        Vector2 anchor,
        string text,
        IntonerTooltipOptions options = default)
    {
        if (IsSuppressed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        ResolvedStyle style = ResolveStyle(options, false);
        Vector2 textSize = ImGui.CalcTextSize(text);
        Vector2 size = textSize + (style.Padding * 2f);
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        Vector2 min = Vector2.Clamp(anchor, viewport.WorkPos, viewport.WorkPos + viewport.WorkSize - size);
        Vector2 max = min + size;

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(style.Background), style.Rounding);
        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(style.Border),
            style.Rounding,
            ImDrawFlags.None,
            style.BorderWidth);
        drawList.AddText(min + style.Padding, ImGui.GetColorU32(ThemeColors.Text), text);
    }

    private static ResolvedStyle ResolveStyle(IntonerTooltipOptions options, bool useDefaultWidth)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float viewportWidth = MathF.Max(1f, ImGui.GetMainViewport().WorkSize.X - (24f * scale));
        Vector4 accent = options.Accent ?? ThemeColors.AccentPrimary;
        Vector4 background = options.Background ?? ResolveBackground();
        float maxWidth = MathF.Min(
            (options.MaxWidth is > 0f ? options.MaxWidth.Value : DefaultMaxWidth) * scale,
            viewportWidth);
        float? requestedWidth = null;
        if (options.Width is > 0f)
        {
            requestedWidth = options.Width.Value * scale;
        }
        else if (useDefaultWidth)
        {
            requestedWidth = DefaultRichWidth * scale;
        }
        float? width = requestedWidth.HasValue
            ? MathF.Min(requestedWidth.Value, maxWidth)
            : null;
        return new ResolvedStyle(
            background,
            options.Border ?? Vector4.Lerp(ThemeColors.Border, accent, 0.28f) with { W = 0.58f },
            (options.Padding ?? new Vector2(DefaultPadding)) * scale,
            (options.ItemSpacing ?? new Vector2(7f, 5f)) * scale,
            width,
            maxWidth,
            (options.Rounding ?? DefaultRounding) * scale,
            (options.BorderWidth ?? DefaultBorderWidth) * scale,
            options.WrapWidthEms is > 0f ? options.WrapWidthEms.Value : DefaultWrapWidthEms);
    }

    internal static Vector4 ResolveBackground()
        => Vector4.Lerp(ThemeColors.WindowBg, ThemeColors.ButtonDefault, 0.48f) with { W = 0.99f };

    private static void ApplySizeConstraints(ResolvedStyle style)
    {
        if (style.Width is { } width)
        {
            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, float.MaxValue));
            return;
        }

        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new Vector2(style.MaxWidth, float.MaxValue));
    }

    [StructLayout(LayoutKind.Auto)]
    public ref struct SuppressionScope
    {
        private bool _active;

        internal SuppressionScope(bool suppress)
        {
            _active = suppress;
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            EndSuppression();
        }

        private static void EndSuppression()
            => --_suppressionDepth;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct ResolvedStyle(
        Vector4 Background,
        Vector4 Border,
        Vector2 Padding,
        Vector2 ItemSpacing,
        float? Width,
        float MaxWidth,
        float Rounding,
        float BorderWidth,
        float WrapWidthEms);
}
