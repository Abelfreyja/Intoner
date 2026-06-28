using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class IntegerRangeControl
{
    private const float CompactButtonEdge = 23f;
    private const float ProminentButtonEdge = 26f;
    private const float ControlSpacing = 5f;
    private const float LabelSpacing = 2f;

    public static bool Draw(
        string id,
        ref int value,
        IntegerSettingRange range,
        string valueText,
        string minimumText,
        string maximumText,
        Vector4 accent,
        bool prominentControl,
        bool enabled,
        IntegerSettingEditState editState,
        float width,
        out IntegerRangeControlUpdate update)
    {
        update = default;
        int originalValue = range.Clamp(value);
        int nextValue = originalValue;
        bool commit = false;
        bool hasUpdate = false;
        bool directControlsEnabled = enabled && !editState.IsActive;
        float buttonEdge = Scaled(prominentControl ? ProminentButtonEdge : CompactButtonEdge);
        float spacing = Scaled(ControlSpacing);
        float valuePillWidth = MathF.Max(Scaled(72f), width - (buttonEdge * 2f) - (spacing * 2f));
        Vector2 controlPos = ImGui.GetCursorPos();

        if (IconButton.DrawSquare($"##objectIntegerMinus_{id}", FontAwesomeIcon.Minus, buttonEdge, accent, directControlsEnabled && nextValue > range.Minimum))
        {
            ApplyStep(ref nextValue, -range.StepSize, range, originalValue, ref commit, ref hasUpdate);
        }

        ImGui.SameLine(0f, spacing);
        IntegerInputUpdate inputUpdate = IntegerValueControl.Draw(
            $"##objectIntegerValue_{id}",
            $"##objectIntegerInput_{id}",
            valueText,
            ref nextValue,
            range,
            editState,
            valuePillWidth,
            buttonEdge,
            accent,
            enabled);
        ApplyInputUpdate(inputUpdate, ref nextValue, originalValue, ref commit, ref hasUpdate);

        ImGui.SameLine(0f, spacing);
        bool controlsEnabledAfterValue = directControlsEnabled && !editState.IsActive;
        if (IconButton.DrawSquare($"##objectIntegerPlus_{id}", FontAwesomeIcon.Plus, buttonEdge, accent, controlsEnabledAfterValue && nextValue < range.Maximum))
        {
            ApplyStep(ref nextValue, range.StepSize, range, originalValue, ref commit, ref hasUpdate);
        }

        ImGui.SetCursorPos(new Vector2(controlPos.X, controlPos.Y + buttonEdge + spacing));
        IntegerSliderUpdate sliderUpdate = IntegerSlider.Draw(
            $"##objectIntegerSlider_{id}",
            ref nextValue,
            range,
            width,
            accent,
            controlsEnabledAfterValue);
        ApplySliderUpdate(sliderUpdate, ref nextValue, editState, originalValue, ref commit, ref hasUpdate);

        ImGui.SetCursorPos(new Vector2(
            controlPos.X,
            controlPos.Y + buttonEdge + spacing + Scaled(IntegerSlider.HitHeight) + Scaled(LabelSpacing)));
        RangeLabels.Draw(minimumText, maximumText, width, enabled);

        if (!hasUpdate)
        {
            return false;
        }

        value = range.Clamp(nextValue);
        update = new IntegerRangeControlUpdate(value, commit);
        return true;
    }

    public static float ResolveHeight(bool prominentControl)
        => Scaled(prominentControl ? ProminentButtonEdge : CompactButtonEdge)
           + Scaled(ControlSpacing)
           + Scaled(IntegerSlider.HitHeight)
           + Scaled(LabelSpacing)
           + ImGui.GetTextLineHeight();

    private static void ApplyStep(
        ref int nextValue,
        int delta,
        IntegerSettingRange range,
        int originalValue,
        ref bool commit,
        ref bool hasUpdate)
    {
        nextValue = range.Clamp(nextValue + delta);
        commit = true;
        hasUpdate = nextValue != originalValue;
    }

    private static void ApplyInputUpdate(
        IntegerInputUpdate inputUpdate,
        ref int nextValue,
        int originalValue,
        ref bool commit,
        ref bool hasUpdate)
    {
        if (inputUpdate.Canceled)
        {
            nextValue = originalValue;
            return;
        }

        if (!inputUpdate.Commit)
        {
            return;
        }

        nextValue = inputUpdate.Value;
        commit = true;
        hasUpdate = nextValue != originalValue;
    }

    private static void ApplySliderUpdate(
        IntegerSliderUpdate sliderUpdate,
        ref int nextValue,
        IntegerSettingEditState editState,
        int originalValue,
        ref bool commit,
        ref bool hasUpdate)
    {
        if (sliderUpdate.ManualInputRequested)
        {
            editState.Begin(nextValue);
            return;
        }

        if (!sliderUpdate.Changed && !sliderUpdate.Commit)
        {
            return;
        }

        commit = sliderUpdate.Commit;
        hasUpdate = sliderUpdate.Commit || nextValue != originalValue;
    }
}

