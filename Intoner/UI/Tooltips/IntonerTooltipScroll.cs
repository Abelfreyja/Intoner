using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Tooltips;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct IntonerTooltipScrollOptions
{
    public Vector4? Accent { get; init; }
    public Vector4? EdgeColor { get; init; }
    public float? MaxHeight { get; init; }
    public float MaxViewportHeightRatio { get; init; }
    public float WheelLineCount { get; init; }
    public ImGuiKey ModifierKey { get; init; }
}

internal static class IntonerTooltipScroll
{
    private const float DefaultMaxHeightRatio = 0.58f;
    private const float DefaultWheelLineCount = 4f;

    private static int _mouseWheelClaimFrame = -1;

    public static bool MouseWheelClaimed
        => _mouseWheelClaimFrame == ImGui.GetFrameCount();

    public static void Draw(
        string id,
        float contentHeight,
        Action content,
        IntonerTooltipScrollOptions options = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(content);

        float scale = ImGuiHelpers.GlobalScale;
        float viewportRatio = options.MaxViewportHeightRatio > 0f
            ? Math.Clamp(options.MaxViewportHeightRatio, 0.1f, 1f)
            : DefaultMaxHeightRatio;
        float maxHeight = options.MaxHeight is > 0f
            ? options.MaxHeight.Value * scale
            : ImGui.GetMainViewport().WorkSize.Y * viewportRatio;
        float height = MathF.Min(MathF.Max(1f, contentHeight), MathF.Max(1f, maxHeight));
        Vector4 accent = options.Accent ?? ThemeColors.AccentPrimary;

        using var child = EditorScrollList.Begin(
            id,
            new Vector2(0f, height),
            new EditorScrollListOptions
            {
                Accent = accent,
                EdgeColor = options.EdgeColor ?? IntonerTooltip.ResolveBackground(),
                Rounding = 5f * scale,
                ScrollbarLayout = EditorScrollListScrollbarLayout.Side,
                ShowEdgeCueArrow = true,
            },
            false,
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollWithMouse);
        if (!child)
        {
            return;
        }

        content();

        float wheelDelta = ImGui.GetIO().MouseWheel;
        if (!IsModifierDown(options.ModifierKey) || MathF.Abs(wheelDelta) <= float.Epsilon)
        {
            return;
        }

        float lineCount = options.WheelLineCount > 0f ? options.WheelLineCount : DefaultWheelLineCount;
        _ = EditorScrollList.ScrollCurrentWindowFromMouseWheel(enabled: true, lineCount);
        _mouseWheelClaimFrame = ImGui.GetFrameCount();
    }

    private static bool IsModifierDown(ImGuiKey key)
        => key == ImGuiKey.None || ImGui.IsKeyDown(key);
}
