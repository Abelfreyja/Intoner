using System.Numerics;

namespace Intoner.Objects.UI.Docking;

/// <summary> editor dock region </summary>
internal enum EditorDockSlot
{
    Top,
    Right,
    Bottom,
    Left,
}

/// <summary> frame context passed to a docked panel/section </summary>
/// <param name="Slot">slot currently hosting the panel/section</param>
/// <param name="AvailableSize">available size for the current dock step</param>
internal readonly record struct EditorDockPanelContext(EditorDockSlot Slot, Vector2 AvailableSize);

internal readonly record struct EditorDockPanel(
    string Id,
    EditorDockSlot Slot,
    Func<EditorDockPanelContext, Vector2> ResolveSize,
    Action<EditorDockPanelContext> Draw);

