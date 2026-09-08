using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct EditorOverlayArea(
    Vector2 Min,
    Vector2 Max,
    float Rounding,
    ImDrawFlags RoundingFlags)
{
    public Vector2 Size => Max - Min;
}

internal sealed class EditorOverlayLayer : IUiOverlayTarget
{
    private ImDrawListPtr _targetDrawList;
    private int _targetFrame = -1;

    public Vector4 BackgroundColor { get; set; }

    public void DrawChildPanel(string id, Vector2 size, bool border, ImGuiWindowFlags flags, Action draw, bool transparentBackground = true)
    {
        using var childBg = transparentBackground
            ? ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero)
            : default;
        ImGuiWindowFlags childFlags = transparentBackground
            ? flags | ImGuiWindowFlags.NoBackground
            : flags;
        using var child = ImRaii.Child(id, size, border, childFlags);
        if (child)
        {
            CaptureCurrentWindow();
            draw();
        }
    }

    public EditorScrollListOptions CreateScrollPanelOptions(Vector4 edgeColor, float rounding, Vector4? accent = null)
        => EditorScrollListOptions.Panel(edgeColor, rounding, accent) with
        {
            OverlayTarget = this,
        };

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

