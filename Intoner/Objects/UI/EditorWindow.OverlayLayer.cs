using Dalamud.Bindings.ImGui;
using Intoner.Objects.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private readonly EditorOverlayLayer _editorOverlayLayer = new();

    private void BeginEditorOverlayFrame()
        => _editorOverlayLayer.BeginFrame();

    private ObjectScrollListOptions CreateOverlayScrollPanelOptions(Vector4 edgeColor, float rounding, Vector4? accent = null)
        => ObjectScrollListOptions.Panel(edgeColor, rounding, accent) with
        {
            OverlayTarget = _editorOverlayLayer,
        };

    private void MarkCurrentWindowAsEditorOverlayTarget()
        => _editorOverlayLayer.CaptureCurrentWindow();

    private void DrawEditorOverlayClipped(Vector2 min, Vector2 max, Action<ImDrawListPtr> draw)
        => _editorOverlayLayer.DrawClipped(min, max, draw);
}

