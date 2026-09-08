using Dalamud.Bindings.ImGui;
using Intoner.Services.Input;
using Intoner.UI;

namespace Intoner.Objects.UI;

internal static class GizmoInputUtility
{
    public const KeyboardModifiers SlowDragModifier = KeyboardModifiers.Shift;
    public const KeyboardModifiers PrecisionSnapModifier = KeyboardModifiers.Control;

    public static bool HasActiveModifierIndicators()
        => IsSlowDragModifierActive() || IsPrecisionSnapModifierActive();

    public static bool IsSlowDragModifierActive()
        => UiKeyboard.AreModifiersDown(SlowDragModifier);

    public static bool IsPrecisionSnapModifierActive()
        => UiKeyboard.AreModifiersDown(PrecisionSnapModifier);

    public static float GetModifierIndicatorFontSize()
        => ImGui.GetFontSize() * 0.72f;

    public static float GetGizmoDragSpeedMultiplier(float slowDragMultiplier)
        => IsSlowDragModifierActive() ? slowDragMultiplier : 1f;
}

