using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal readonly record struct EditorOverlayArea(
    Vector2 Min,
    Vector2 Max,
    float Rounding,
    ImDrawFlags RoundingFlags)
{
    public Vector2 Size => Max - Min;
}

/// <summary> receives editor draw surfaces that late overlays may draw into </summary>
internal interface IEditorOverlayTarget
{
    /// <summary> marks the current ImGui window as the overlay draw target for this frame </summary>
    void CaptureCurrentWindow();
}

internal sealed class EditorOverlayLayer : IEditorOverlayTarget
{
    private ImDrawListPtr _targetDrawList;
    private int _targetFrame = -1;

    public void CaptureCurrentWindow()
    {
        _targetDrawList = ImGui.GetWindowDrawList();
        _targetFrame = ImGui.GetFrameCount();
    }

    public void DrawClipped(EditorOverlayArea area, Action<ImDrawListPtr> draw)
    {
        if (!EditorInputUtility.HasArea(area.Min, area.Max))
        {
            return;
        }

        ImDrawListPtr drawList = ResolveDrawList();
        drawList.PushClipRect(area.Min, area.Max, false);
        try
        {
            draw(drawList);
        }
        finally
        {
            drawList.PopClipRect();
        }
    }

    private ImDrawListPtr ResolveDrawList()
        => _targetFrame == ImGui.GetFrameCount()
            ? _targetDrawList
            : ImGui.GetWindowDrawList();
}

