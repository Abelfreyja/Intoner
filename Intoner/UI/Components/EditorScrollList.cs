using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.UI.Components;

internal enum EditorScrollListScrollbarLayout
{
    Overlay,
    Side,
}

internal readonly record struct EditorScrollListOptions
{
    public Vector4? Accent { get; init; }
    public Vector4? EdgeColor { get; init; }
    public float Rounding { get; init; }
    public ImDrawFlags? CornerFlags { get; init; }
    public EditorScrollListScrollbarLayout ScrollbarLayout { get; init; }
    public bool ShowEdgeCueArrow { get; init; }
    public IUiOverlayTarget? OverlayTarget { get; init; }

    public static EditorScrollListOptions Panel(Vector4 edgeColor, float rounding, Vector4? accent = null)
        => new()
        {
            Accent = accent,
            EdgeColor = edgeColor,
            Rounding = rounding,
            ScrollbarLayout = EditorScrollListScrollbarLayout.Side,
            ShowEdgeCueArrow = true,
        };
}

internal static class EditorScrollList
{
    private const float ScrollbarSideWidth = 10f;

    public static EditorScrollListScope Begin(
        string id,
        Vector2 size,
        bool border = false,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None,
        Vector4? accent = null,
        Vector4? edgeColor = null)
        => Begin(
            id,
            size,
            new EditorScrollListOptions
            {
                Accent = accent,
                EdgeColor = edgeColor,
            },
            border,
            flags);

    public static EditorScrollListScope Begin(
        string id,
        Vector2 size,
        EditorScrollListOptions options,
        bool border = false,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        => new(id, size, border, flags, ResolveOptions(options));

    public static float GetStableContentWidth()
    {
        ImGuiWindowPtr window = ImGuiP.GetCurrentWindow();
        return MathF.Max(1f, ImGui.GetContentRegionAvail().X + window.ScrollbarSizes.X - ImGui.GetStyle().ScrollbarSize);
    }

    public static bool ScrollCurrentWindowFromMouseWheel(bool enabled, float textLineCount = 4f)
    {
        float wheelDelta = ImGui.GetIO().MouseWheel;
        if (!enabled || MathF.Abs(wheelDelta) <= float.Epsilon)
        {
            return false;
        }

        float step = ImGui.GetTextLineHeightWithSpacing() * MathF.Max(1f, textLineCount);
        ImGui.SetScrollY(ImGui.GetScrollY() - (wheelDelta * step));
        return true;
    }

    internal ref struct EditorScrollListScope
    {
        private readonly ImRaii.ChildDisposable _child;
        private readonly ResolvedOptions _options;
        private readonly bool _success;
        private bool _disposed;

        internal EditorScrollListScope(string id, Vector2 size, bool border, ImGuiWindowFlags flags, ResolvedOptions options)
        {
            _options = options;
            PushNativeScrollbarStyle(options);
            ImRaii.ChildDisposable child = ImRaii.Child(id, size, border, BuildChildFlags(flags, options));
            _child = child;
            _success = child.Success;
        }

        public bool Success
            => _success;

        public static bool operator true(EditorScrollListScope scope)
            => scope.Success;

        public static bool operator false(EditorScrollListScope scope)
            => !scope.Success;

        public static bool operator !(EditorScrollListScope scope)
            => !scope.Success;

        private static ImGuiWindowFlags BuildChildFlags(ImGuiWindowFlags flags, ResolvedOptions options)
            => options.ScrollbarLayout == EditorScrollListScrollbarLayout.Side
                ? flags
                : flags | ImGuiWindowFlags.NoScrollbar;

        private static void PushNativeScrollbarStyle(ResolvedOptions options)
        {
            if (options.ScrollbarLayout != EditorScrollListScrollbarLayout.Side)
            {
                return;
            }

            Vector4 accent = options.Accent;
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, ScrollbarSideWidth * ImGuiHelpers.GlobalScale);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, MathF.Max(1f, options.Rounding * 0.45f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, ThemeColors.Color(1f, 1f, 1f, 0.045f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, ThemeColors.WithAlpha(accent, 0.58f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, ThemeColors.WithAlpha(accent, 0.82f));
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, ThemeColors.WithAlpha(accent, 0.95f));
        }

        private static void PopNativeScrollbarStyle(ResolvedOptions options)
        {
            if (options.ScrollbarLayout != EditorScrollListScrollbarLayout.Side)
            {
                return;
            }

            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(2);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_success)
            {
                EditorScrollListOverlay.Draw(_options);
                _options.OverlayTarget?.CaptureCurrentWindow();
            }

            _child.Dispose();
            PopNativeScrollbarStyle(_options);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct ResolvedOptions(
        Vector4 Accent,
        Vector4 EdgeColor,
        float Rounding,
        ImDrawFlags CornerFlags,
        EditorScrollListScrollbarLayout ScrollbarLayout,
        bool ShowEdgeCueArrow,
        IUiOverlayTarget? OverlayTarget);


    private static ResolvedOptions ResolveOptions(EditorScrollListOptions options)
        => new(
            options.Accent ?? ThemeColors.AccentPrimary,
            options.EdgeColor ?? ResolveDefaultEdgeColor(),
            MathF.Max(0f, options.Rounding),
            options.CornerFlags ?? ImDrawFlags.RoundCornersAll,
            options.ScrollbarLayout,
            options.ShowEdgeCueArrow,
            options.OverlayTarget);

    private static Vector4 ResolveDefaultEdgeColor()
    {
        var childBg = ThemeColors.Style(ImGuiCol.ChildBg);
        return childBg.W > 0.01f
            ? childBg
            : ThemeColors.WindowBg;
    }

}

