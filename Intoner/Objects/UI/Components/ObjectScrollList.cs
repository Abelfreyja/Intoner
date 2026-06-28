using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal enum ObjectScrollListScrollbarLayout
{
    Overlay,
    Side,
}

internal readonly record struct ObjectScrollListOptions
{
    public Vector4? Accent { get; init; }
    public Vector4? EdgeColor { get; init; }
    public float Rounding { get; init; }
    public ObjectScrollListScrollbarLayout ScrollbarLayout { get; init; }
    public bool ShowEdgeCueArrow { get; init; }
    public IEditorOverlayTarget? OverlayTarget { get; init; }

    public static ObjectScrollListOptions Panel(Vector4 edgeColor, float rounding, Vector4? accent = null)
        => new()
        {
            Accent = accent,
            EdgeColor = edgeColor,
            Rounding = rounding,
            ScrollbarLayout = ObjectScrollListScrollbarLayout.Side,
            ShowEdgeCueArrow = true,
        };
}

internal static class ObjectScrollList
{
    private const float EdgeCueHeight = 28f;
    private const float EdgeCueMaxAlpha = 0.72f;
    private const float ScrollbarInset = 3f;
    private const float ScrollbarIdleWidth = 2f;
    private const float ScrollbarHoverWidth = 5f;
    private const float ScrollbarSideWidth = 10f;
    private const float ScrollbarOverlayHitWidth = 14f;
    private const float ScrollbarMinThumbHeight = 24f;
    private const float EdgeCueArrowWidth = 7f;
    private const float EdgeCueArrowHeight = 4f;
    private const string ScrollbarId = "##objectScrollListScrollbar";

    private static uint _overlayDragId;
    private static float _overlayDragOffset;

    public static ObjectScrollListScope Begin(
        string id,
        Vector2 size,
        bool border = false,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None,
        Vector4? accent = null,
        Vector4? edgeColor = null)
        => Begin(
            id,
            size,
            new ObjectScrollListOptions
            {
                Accent = accent,
                EdgeColor = edgeColor,
            },
            border,
            flags);

    public static ObjectScrollListScope Begin(
        string id,
        Vector2 size,
        ObjectScrollListOptions options,
        bool border = false,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        => new(id, size, border, flags, ResolveOptions(options));

    internal ref struct ObjectScrollListScope
    {
        private readonly ImRaii.ChildDisposable _child;
        private readonly ResolvedOptions _options;
        private readonly bool _success;
        private bool _disposed;

        internal ObjectScrollListScope(string id, Vector2 size, bool border, ImGuiWindowFlags flags, ResolvedOptions options)
        {
            _options = options;
            PushNativeScrollbarStyle(options);
            var child = ImRaii.Child(id, size, border, BuildChildFlags(flags, options));
            _child = child;
            _success = child.Success;
        }

        public bool Success
            => _success;

        public static bool operator true(ObjectScrollListScope scope)
            => scope.Success;

        public static bool operator false(ObjectScrollListScope scope)
            => !scope.Success;

        public static bool operator !(ObjectScrollListScope scope)
            => !scope.Success;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_success)
            {
                DrawOverlays(_options);
                _options.OverlayTarget?.CaptureCurrentWindow();
            }

            _child.Dispose();
            PopNativeScrollbarStyle(_options);
        }
    }

    internal readonly record struct ResolvedOptions(
        Vector4 Accent,
        Vector4 EdgeColor,
        float Rounding,
        ObjectScrollListScrollbarLayout ScrollbarLayout,
        bool ShowEdgeCueArrow,
        IEditorOverlayTarget? OverlayTarget);

    private readonly record struct ScrollbarGeometry(
        Vector2 TrackMin,
        Vector2 TrackMax,
        Vector2 ThumbMin,
        Vector2 ThumbMax,
        Vector2 HitMin,
        Vector2 HitMax,
        float Travel,
        float ThumbHeight);

    private static ResolvedOptions ResolveOptions(ObjectScrollListOptions options)
        => new(
            options.Accent ?? EditorColors.AccentPurple,
            options.EdgeColor ?? ResolveDefaultEdgeColor(),
            MathF.Max(0f, options.Rounding),
            options.ScrollbarLayout,
            options.ShowEdgeCueArrow,
            options.OverlayTarget);

    private static ImGuiWindowFlags BuildChildFlags(ImGuiWindowFlags flags, ResolvedOptions options)
        => options.ScrollbarLayout == ObjectScrollListScrollbarLayout.Side
            ? flags
            : flags | ImGuiWindowFlags.NoScrollbar;

    private static Vector4 ResolveDefaultEdgeColor()
    {
        var childBg = EditorColors.Style(ImGuiCol.ChildBg);
        return childBg.W > 0.01f
            ? childBg
            : EditorColors.WindowBg;
    }

    private static void PushNativeScrollbarStyle(ResolvedOptions options)
    {
        if (options.ScrollbarLayout != ObjectScrollListScrollbarLayout.Side)
        {
            return;
        }

        Vector4 accent = options.Accent;
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, ScrollbarSideWidth * ImGuiHelpers.GlobalScale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, MathF.Max(1f, options.Rounding * 0.45f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, EditorColors.Color(1f, 1f, 1f, 0.045f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, EditorColors.WithAlpha(accent, 0.58f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, EditorColors.WithAlpha(accent, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, EditorColors.WithAlpha(accent, 0.95f));
    }

    private static void PopNativeScrollbarStyle(ResolvedOptions options)
    {
        if (options.ScrollbarLayout != ObjectScrollListScrollbarLayout.Side)
        {
            return;
        }

        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(2);
    }

    private static void DrawOverlays(ResolvedOptions options)
    {
        float scrollY = ImGui.GetScrollY();
        float scrollMaxY = ImGui.GetScrollMaxY();
        if (scrollMaxY <= 0.5f)
        {
            return;
        }

        Vector2 min = ImGui.GetWindowPos();
        Vector2 max = min + ImGui.GetWindowSize();
        if (!EditorInputUtility.HasArea(min, max))
        {
            return;
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.PushClipRect(min, max, false);
        try
        {
            if (options.ScrollbarLayout == ObjectScrollListScrollbarLayout.Overlay)
            {
                ScrollbarGeometry scrollbar = ResolveScrollbarGeometry(min, max, scrollY, scrollMaxY, options);
                HandleOverlayScrollbarInput(scrollbar, scrollMaxY);
                scrollY = ImGui.GetScrollY();
                scrollbar = ResolveScrollbarGeometry(min, max, scrollY, scrollMaxY, options);
                DrawEdgeCues(drawList, min, max, scrollY, scrollMaxY, options);
                DrawScrollbar(drawList, scrollbar, options);
                return;
            }

            DrawEdgeCues(drawList, min, max, scrollY, scrollMaxY, options);
        }
        finally
        {
            drawList.PopClipRect();
        }
    }

    private static void DrawEdgeCues(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float scrollY,
        float scrollMaxY,
        ResolvedOptions options)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float edgeHeight = MathF.Min(EdgeCueHeight * scale, (max.Y - min.Y) * 0.35f);
        if (edgeHeight <= 0f)
        {
            return;
        }

        float topAlpha = Math.Clamp(scrollY / edgeHeight, 0f, 1f) * EdgeCueMaxAlpha;
        float bottomAlpha = Math.Clamp((scrollMaxY - scrollY) / edgeHeight, 0f, 1f) * EdgeCueMaxAlpha;
        DrawEdgeCue(drawList, min, max, edgeHeight, topAlpha, true, options);
        DrawEdgeCue(drawList, min, max, edgeHeight, bottomAlpha, false, options);
    }

    private static void DrawEdgeCue(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float edgeHeight,
        float alpha,
        bool top,
        ResolvedOptions options)
    {
        if (alpha <= 0.01f)
        {
            return;
        }

        Vector2 cueMin = top
            ? new Vector2(min.X, min.Y)
            : new Vector2(min.X, max.Y - edgeHeight);
        Vector2 cueMax = top
            ? new Vector2(max.X, min.Y + edgeHeight)
            : new Vector2(max.X, max.Y);
        uint solid = ImGui.GetColorU32(EditorColors.WithAlpha(options.EdgeColor, alpha));
        uint clear = ImGui.GetColorU32(EditorColors.WithAlpha(options.EdgeColor, 0f));
        drawList.AddRectFilledMultiColor(
            cueMin,
            cueMax,
            top ? solid : clear,
            top ? solid : clear,
            top ? clear : solid,
            top ? clear : solid);

        if (options.ShowEdgeCueArrow)
        {
            DrawEdgeCueArrow(drawList, min, max, alpha, top);
        }
    }

    private static void DrawEdgeCueArrow(ImDrawListPtr drawList, Vector2 min, Vector2 max, float alpha, bool top)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float width = EdgeCueArrowWidth * scale;
        float height = EdgeCueArrowHeight * scale;
        float centerX = (min.X + max.X) * 0.5f;
        float centerY = top
            ? min.Y + (10f * scale)
            : max.Y - (10f * scale);
        Vector2 left = new(centerX - width, top ? centerY + height : centerY - height);
        Vector2 center = new(centerX, top ? centerY - height : centerY + height);
        Vector2 right = new(centerX + width, top ? centerY + height : centerY - height);
        uint color = ImGui.GetColorU32(EditorColors.WithAlpha(EditorColors.Text, Math.Clamp(alpha, 0f, 0.72f)));
        float thickness = MathF.Max(1f, 1.25f * scale);
        drawList.AddLine(left, center, color, thickness);
        drawList.AddLine(center, right, color, thickness);
    }

    private static ScrollbarGeometry ResolveScrollbarGeometry(
        Vector2 min,
        Vector2 max,
        float scrollY,
        float scrollMaxY,
        ResolvedOptions options)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = max.Y - min.Y;
        float inset = MathF.Max(ScrollbarInset * scale, options.Rounding * 0.35f);
        float trackHeight = MathF.Max(1f, height - (inset * 2f));
        float width = ResolveScrollbarWidth(false);
        float hitWidth = options.ScrollbarLayout == ObjectScrollListScrollbarLayout.Side
            ? ScrollbarSideWidth * scale
            : ScrollbarOverlayHitWidth * scale;
        float xMax = max.X - inset;
        float xMin = xMax - width;
        float visibleRatio = Math.Clamp(height / (height + scrollMaxY), 0.05f, 1f);
        float thumbHeight = MathF.Min(trackHeight, MathF.Max(ScrollbarMinThumbHeight * scale, trackHeight * visibleRatio));
        float travel = MathF.Max(1f, trackHeight - thumbHeight);
        float thumbTop = min.Y + inset + (travel * Math.Clamp(scrollY / scrollMaxY, 0f, 1f));
        return new(
            new Vector2(xMin, min.Y + inset),
            new Vector2(xMax, max.Y - inset),
            new Vector2(xMin, thumbTop),
            new Vector2(xMax, thumbTop + thumbHeight),
            new Vector2(max.X - hitWidth, min.Y),
            max,
            travel,
            thumbHeight);
    }

    private static float ResolveScrollbarWidth(bool hovered)
        => (hovered ? ScrollbarHoverWidth : ScrollbarIdleWidth) * ImGuiHelpers.GlobalScale;

    private static void HandleOverlayScrollbarInput(ScrollbarGeometry scrollbar, float scrollMaxY)
    {
        uint id = ImGui.GetID(ScrollbarId);
        if (_overlayDragId != 0 && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            ClearOverlayScrollbarDrag();
        }

        bool hovered = EditorInputUtility.IsMouseInside(scrollbar.HitMin, scrollbar.HitMax);
        Vector2 mouse = ImGui.GetIO().MousePos;
        if (hovered && EditorInputUtility.IsMouseClickedInside(scrollbar.HitMin, scrollbar.HitMax))
        {
            _overlayDragId = id;
            _overlayDragOffset = mouse.Y >= scrollbar.ThumbMin.Y && mouse.Y <= scrollbar.ThumbMax.Y
                ? mouse.Y - scrollbar.ThumbMin.Y
                : scrollbar.ThumbHeight * 0.5f;
            ApplyOverlayScrollbarDrag(mouse.Y, scrollbar, scrollMaxY);
        }

        if (_overlayDragId != id)
        {
            return;
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            ApplyOverlayScrollbarDrag(mouse.Y, scrollbar, scrollMaxY);
        }
        else
        {
            ClearOverlayScrollbarDrag();
        }
    }

    private static void ClearOverlayScrollbarDrag()
    {
        _overlayDragId = 0;
        _overlayDragOffset = 0f;
    }

    private static void ApplyOverlayScrollbarDrag(float mouseY, ScrollbarGeometry scrollbar, float scrollMaxY)
    {
        float thumbTop = Math.Clamp(mouseY - _overlayDragOffset, scrollbar.TrackMin.Y, scrollbar.TrackMax.Y - scrollbar.ThumbHeight);
        float ratio = Math.Clamp((thumbTop - scrollbar.TrackMin.Y) / scrollbar.Travel, 0f, 1f);
        ImGui.SetScrollY(scrollMaxY * ratio);
    }

    private static void DrawScrollbar(ImDrawListPtr drawList, ScrollbarGeometry scrollbar, ResolvedOptions options)
    {
        bool hovered = EditorInputUtility.IsMouseInside(scrollbar.HitMin, scrollbar.HitMax)
            || (options.ScrollbarLayout == ObjectScrollListScrollbarLayout.Overlay && _overlayDragId == ImGui.GetID(ScrollbarId));
        float width = ResolveScrollbarWidth(hovered);
        float xMax = scrollbar.TrackMax.X;
        Vector2 trackMin = new(xMax - width, scrollbar.TrackMin.Y);
        Vector2 trackMax = new(xMax, scrollbar.TrackMax.Y);
        Vector2 thumbMin = new(xMax - width, scrollbar.ThumbMin.Y);
        Vector2 thumbMax = new(xMax, scrollbar.ThumbMax.Y);
        float rounding = width * 0.5f;

        Vector4 trackColor = EditorColors.Color(1f, 1f, 1f, hovered ? 0.07f : 0.035f);
        Vector4 thumbColor = EditorColors.WithAlpha(options.Accent, hovered ? 0.74f : 0.46f);
        drawList.AddRectFilled(
            trackMin,
            trackMax,
            ImGui.GetColorU32(trackColor),
            rounding);
        drawList.AddRectFilled(
            thumbMin,
            thumbMax,
            ImGui.GetColorU32(thumbColor),
            rounding);
    }
}

