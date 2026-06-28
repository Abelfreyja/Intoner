using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

/// <summary> receives editor draw surfaces that late overlays may draw into </summary>
internal interface IEditorOverlayTarget
{
    /// <summary> marks the current ImGui window as the latest valid overlay draw target </summary>
    void CaptureCurrentWindow();
}

internal sealed class EditorOverlayLayer : IEditorOverlayTarget
{
    private ImDrawListPtr _targetDrawList;
    private bool _hasTarget;

    public void BeginFrame()
    {
        _targetDrawList = default;
        _hasTarget = false;
    }

    public void CaptureCurrentWindow()
    {
        _targetDrawList = ImGui.GetWindowDrawList();
        _hasTarget = true;
    }

    public void DrawClipped(Vector2 min, Vector2 max, Action<ImDrawListPtr> draw)
    {
        if (!EditorInputUtility.HasArea(min, max))
        {
            return;
        }

        ImDrawListPtr drawList = ResolveDrawList();
        drawList.PushClipRect(min, max, false);
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
        => _hasTarget
            ? _targetDrawList
            : ImGui.GetWindowDrawList();
}

