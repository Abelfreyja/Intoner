using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorPropertyTable
{
    private static readonly Vector4 AxisXColor = new(0.95f, 0.42f, 0.42f, 0.95f);
    private static readonly Vector4 AxisYColor = new(0.46f, 0.84f, 0.52f, 0.95f);
    private static readonly Vector4 AxisZColor = new(0.46f, 0.66f, 1.00f, 0.95f);

    public static ImRaiiScope.TableScope Begin(string id)
    {
        const ImGuiTableFlags flags =
            ImGuiTableFlags.SizingStretchProp
          | ImGuiTableFlags.RowBg
          | ImGuiTableFlags.BordersInnerV
          | ImGuiTableFlags.BordersInnerH
          | ImGuiTableFlags.NoPadOuterX
          | ImGuiTableFlags.NoSavedSettings;

        ImRaiiScope.TableScope table = ImRaiiScope.Table($"##{id}", 2, flags, new Vector2(8f, 3f) * ImGuiHelpers.GlobalScale);
        if (!table)
        {
            return table;
        }

        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthFixed, 156f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        return table;
    }

    public static void NextRow(string title, float actionWidth = 0f, Action? drawActions = null, string? originalValue = null)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + (2f * ImGuiHelpers.GlobalScale));
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawLabel(title, actionWidth, drawActions, originalValue);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
    }

    private static void DrawLabel(string title, float actionWidth, Action? drawActions, string? originalValue)
    {
        float startX = ImGui.GetCursorPosX();
        float availableWidth = ImGui.GetContentRegionAvail().X;

        if (originalValue is null)
        {
            ImGui.TextUnformatted(title);
        }
        else
        {
            ImGui.TextColored(ThemeColors.AccentPrimary, $"{title} *");

            if (ImGui.IsItemHovered())
            {
                IntonerTooltip.DrawText($"Original: {originalValue}");
            }
        }

        if (drawActions is null || actionWidth <= 0f)
        {
            return;
        }

        float actionX = startX + MathF.Max(0f, availableWidth - actionWidth);
        ImGui.SameLine();
        ImGui.SetCursorPosX(actionX);
        drawActions();
    }

    public static bool Checkbox(string id, string title, ref bool value)
    {
        NextRow(title);
        return ImGui.Checkbox($"##{id}", ref value);
    }

    public static bool InputText(string id, string title, ref string value, int maxLength)
    {
        NextRow(title);
        value ??= string.Empty;
        return ImGui.InputText($"##{id}", ref value, maxLength);
    }

    public static bool SliderFloat(string id, string title, ref float value, float min, float max, string format)
    {
        NextRow(title);
        return ImGui.SliderFloat($"##{id}", ref value, min, max, format);
    }

    public static bool DragFloat(string id, string title, ref float value, float speed, float min, float max, string format,
        float actionWidth = 0f, Action? drawActions = null, Vector4? borderColor = null, Action<int>? onActivated = null)
    {
        NextRow(title, actionWidth, drawActions);
        return DrawFloatInput(id, ref value, speed, min, max, format, MathF.Max(1f, ImGui.GetContentRegionAvail().X), borderColor, onActivated);
    }

    public static bool DragFloat3(
        string id,
        string title,
        ref Vector3 value,
        float speed,
        float min,
        float max,
        string format,
        float actionWidth = 0f,
        Action? drawActions = null,
        Action<int>? onActivated = null)
    {
        NextRow(title, actionWidth, drawActions);
        return DrawDragFloat3Inputs(id, ref value, speed, min, max, format, onActivated);
    }

    private static bool DrawDragFloat3Inputs(string id, ref Vector3 value, float speed, float min, float max, string format, Action<int>? onActivated)
    {
        using var group = ImRaii.Group();
        float x = value.X;
        float y = value.Y;
        float z = value.Z;
        bool changed = false;
        float axisGap = 6f * ImGuiHelpers.GlobalScale;
        float axisValueGap = 3f * ImGuiHelpers.GlobalScale;
        float axisLabelWidth = MathF.Ceiling(ImGui.CalcTextSize("X").X);
        float availableWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        float inputWidth = MathF.Max(1f, (availableWidth - (axisLabelWidth * 3f) - (axisGap * 2f) - (axisValueGap * 3f)) / 3f);
        float minimumInputWidth = MathF.Max(56f * ImGuiHelpers.GlobalScale, ImGui.CalcTextSize("0.000").X + ImGui.GetStyle().FramePadding.X * 2f);
        if (inputWidth < minimumInputWidth)
        {
            inputWidth = MathF.Max(1f, availableWidth - axisLabelWidth - axisValueGap);
            axisGap = 0f;
        }

        DrawAxis($"{id}_x", "X", 0, AxisXColor, ref x, 0f);
        DrawAxis($"{id}_y", "Y", 1, AxisYColor, ref y, axisGap);
        DrawAxis($"{id}_z", "Z", 2, AxisZColor, ref z, axisGap);

        if (!changed)
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;

        void DrawAxis(string inputId, string axis, int axisIndex, Vector4 color, ref float axisValue, float leadingGap)
        {
            if (leadingGap > 0f)
            {
                ImGui.SameLine(0f, leadingGap);
            }

            ImGui.AlignTextToFramePadding();
            using (ImRaii.PushColor(ImGuiCol.Text, color))
            {
                ImGui.TextUnformatted(axis);
            }

            ImGui.SameLine(0f, axisValueGap);
            changed |= DrawFloatInput(inputId, ref axisValue, speed, min, max, format, inputWidth, color, onActivated, axisIndex);
        }
    }

    private static bool DrawFloatInput(string id, ref float value, float speed, float min, float max, string format,
        float inputWidth, Vector4? borderColor, Action<int>? onActivated, int axis = -1)
    {
        ImGui.SetNextItemWidth(inputWidth);
        using var border = ImRaii.PushColor(ImGuiCol.Border, (borderColor ?? ThemeColors.Border) with { W = 0.90f }, borderColor.HasValue);
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, ImGuiHelpers.GlobalScale, borderColor.HasValue);
        bool changed = ImGui.DragFloat($"##{id}", ref value, speed, min, max, format);

        if (ImGui.IsItemActivated())
        {
            onActivated?.Invoke(axis);
        }

        return changed;
    }

    public static bool EnumRow<T>(string id, string title, ref T current) where T : struct, Enum
    {
        NextRow(title);
        using var combo = ImRaii.Combo($"##{id}", current.ToString());
        if (!combo)
        {
            return false;
        }

        bool changed = false;
        foreach (T value in Enum.GetValues<T>())
        {
            bool selected = EqualityComparer<T>.Default.Equals(current, value);
            if (ImGui.Selectable(value.ToString(), selected))
            {
                current = value;
                changed = true;
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }
        return changed;
    }
}
