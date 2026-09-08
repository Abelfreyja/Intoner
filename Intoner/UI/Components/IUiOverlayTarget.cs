namespace Intoner.UI.Components;

/// <summary> receives an ImGui window that late UI overlays may draw into </summary>
internal interface IUiOverlayTarget
{
    /// <summary> marks the current ImGui window as the overlay target for this frame </summary>
    void CaptureCurrentWindow();
}
