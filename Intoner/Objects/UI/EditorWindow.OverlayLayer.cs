using Intoner.Objects.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private readonly EditorOverlayLayer _editorOverlayLayer = new();

    private ObjectScrollListOptions CreateOverlayScrollPanelOptions(Vector4 edgeColor, float rounding, Vector4? accent = null)
        => ObjectScrollListOptions.Panel(edgeColor, rounding, accent) with
        {
            OverlayTarget = _editorOverlayLayer,
        };

    private void MarkCurrentWindowAsEditorOverlayTarget()
        => _editorOverlayLayer.CaptureCurrentWindow();
}

