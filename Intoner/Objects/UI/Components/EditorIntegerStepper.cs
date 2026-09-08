using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Services.Input;
using Intoner.UI;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorIntegerStepper
{
    private const float ButtonWidth = 22f;
    private const float MinimumValueWidth = 30f;
    internal const KeyboardModifiers LargeStepModifier = KeyboardModifiers.Shift;
    private static readonly string DecreaseTooltip = $"Decrease by 1. Hold {KeyboardGestureFormatter.Format(LargeStepModifier)} to decrease by 10.";
    private static readonly string IncreaseTooltip = $"Increase by 1. Hold {KeyboardGestureFormatter.Format(LargeStepModifier)} to increase by 10.";

    public static bool Draw(
        string id,
        ref int value,
        Vector2 size,
        Vector4 accent,
        bool enabled)
    {
        using var pushId = ImRaii.PushId(id);
        float spacing = EditorSegmentedControl.Spacing;
        float contentWidth = MathF.Max(1f, size.X - (spacing * 2f));
        float buttonWidth = MathF.Min(
            Scaled(ButtonWidth),
            MathF.Max(1f, (contentWidth - Scaled(MinimumValueWidth)) * 0.5f));
        float valueWidth = MathF.Max(1f, contentWidth - (buttonWidth * 2f));
        int previous = value;
        int step = UiKeyboard.AreModifiersDown(LargeStepModifier) ? 10 : 1;

        if (EditorSegmentedControl.DrawCompactActionSegment(
                "##decrement",
                FontAwesomeIcon.Minus,
                enabled && value > int.MinValue,
                DecreaseTooltip,
                accent,
                new Vector2(buttonWidth, size.Y),
                0,
                3,
                emphasized: false))
        {
            value = AddClamped(value, -step);
        }

        ImGui.SameLine(0f, spacing);
        EditorSegmentedControl.DrawCompactSegment(
            "##value",
            value.ToString(CultureInfo.InvariantCulture),
            null,
            selected: false,
            enabled,
            "Current value.",
            accent,
            new Vector2(valueWidth, size.Y),
            1,
            3);

        ImGui.SameLine(0f, spacing);
        if (EditorSegmentedControl.DrawCompactActionSegment(
                "##increment",
                FontAwesomeIcon.Plus,
                enabled && value < int.MaxValue,
                IncreaseTooltip,
                accent,
                new Vector2(buttonWidth, size.Y),
                2,
                3,
                emphasized: false))
        {
            value = AddClamped(value, step);
        }

        return value != previous;
    }

    private static int AddClamped(int value, int amount)
        => (int)Math.Clamp((long)value + amount, int.MinValue, int.MaxValue);

    private static float Scaled(float value)
        => value * ImGuiHelpers.GlobalScale;
}
