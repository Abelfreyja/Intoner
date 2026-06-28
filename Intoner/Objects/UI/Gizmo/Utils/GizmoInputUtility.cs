using Dalamud.Bindings.ImGui;

namespace Intoner.Objects.UI;

internal static class GizmoInputUtility
{
    public static bool HasActiveModifierIndicators()
        => IsSlowDragModifierActive() || IsPrecisionSnapModifierActive();

    public static bool IsSlowDragModifierActive()
        => ImGui.GetIO().KeyShift;

    public static bool IsPrecisionSnapModifierActive()
        => ImGui.GetIO().KeyCtrl;

    public static float GetModifierIndicatorFontSize()
        => ImGui.GetFontSize() * 0.72f;

    public static float GetGizmoDragSpeedMultiplier(float slowDragMultiplier)
        => IsSlowDragModifierActive() ? slowDragMultiplier : 1f;
}

