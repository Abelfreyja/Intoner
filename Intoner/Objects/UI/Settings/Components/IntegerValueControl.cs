using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.UI;
using System.Numerics;
using static Intoner.Objects.UI.Settings.Components.SettingsChrome;

namespace Intoner.Objects.UI.Settings.Components;

internal static class IntegerValueControl
{
    public static IntegerInputUpdate Draw(
        string valueId,
        string inputId,
        string valueText,
        ref int value,
        IntegerSettingRange range,
        IntegerSettingEditState editState,
        float width,
        float height,
        Vector4 accent,
        bool enabled)
    {
        if (editState.IsActive)
        {
            IntegerInputUpdate update = DrawValueInput(inputId, editState, range, width, height, accent, enabled);
            if (!update.Canceled)
            {
                value = update.Commit
                    ? update.Value
                    : range.Clamp(editState.Value);
            }

            return update;
        }

        if (DrawValuePill(valueId, valueText, width, height, accent, enabled))
        {
            editState.Begin(value);
        }

        return default;
    }

    private static bool DrawValuePill(string id, string valueText, float width, float height, Vector4 accent, bool enabled)
    {
        Vector2 min = ImGui.GetCursorScreenPos();
        Vector2 size = new(width, height);
        Vector2 max = min + size;
        using (ImRaii.Disabled(!enabled))
        {
            ImGui.InvisibleButton(id, size);
        }

        bool hovered = enabled && ImGui.IsItemHovered();
        bool manualInputRequested = InputGesture.ManualInputRequested(hovered, enabled);
        var rounding = height * 0.45f;
        Vector4 fill = enabled
            ? accent with { W = hovered ? 0.20f : 0.13f }
            : EditorColors.ButtonDefault with { W = 0.20f };
        Vector4 border = enabled
            ? accent with { W = hovered ? 0.42f : 0.28f }
            : EditorColors.Border with { W = 0.14f };
        Vector4 textColor = enabled
            ? EditorColors.Text with { W = 0.92f }
            : EditorColors.TextDisabled with { W = 0.56f };
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), rounding);
        drawList.AddRect(min, max, ImGui.GetColorU32(border), rounding, ImDrawFlags.None, Scaled(1f));
        DrawCenteredText(drawList, min, max, valueText, textColor);
        return manualInputRequested;
    }

    private static IntegerInputUpdate DrawValueInput(
        string id,
        IntegerSettingEditState editState,
        IntegerSettingRange range,
        float width,
        float height,
        Vector4 accent,
        bool enabled)
    {
        if (editState.ConsumeFocusRequest())
        {
            ImGui.SetKeyboardFocusHere();
        }

        int editValue = editState.Value;
        float verticalPadding = MathF.Max(0f, (height - ImGui.GetTextLineHeight()) * 0.5f);
        using (ImRaii.ItemWidth(width))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, height * 0.45f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(Scaled(8f), verticalPadding)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, accent with { W = enabled ? 0.16f : 0.20f }))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, accent with { W = 0.22f }))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, accent with { W = 0.26f }))
        using (ImRaii.PushColor(ImGuiCol.Border, accent with { W = enabled ? 0.42f : 0.14f }))
        using (ImRaii.Disabled(!enabled))
        {
            ImGui.InputInt(id, ref editValue, 0, 0);
        }

        editState.Value = editValue;
        bool focused = ImGui.IsItemFocused();
        if (focused && ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            editState.End();
            return new IntegerInputUpdate(default, false, true);
        }

        bool submitted = focused && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));
        if (submitted || ImGui.IsItemDeactivatedAfterEdit())
        {
            int committedValue = range.Clamp(editState.Value);
            editState.End();
            return new IntegerInputUpdate(committedValue, true, false);
        }

        if (ImGui.IsItemDeactivated())
        {
            editState.End();
        }

        return default;
    }
}

