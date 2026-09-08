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

    public static void NextRow(string title, float actionWidth = 0f, Action? drawActions = null)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.None, ImGui.GetFrameHeight() + (2f * ImGuiHelpers.GlobalScale));
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        DrawLabel(title, actionWidth, drawActions);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1f);
    }

    private static void DrawLabel(string title, float actionWidth, Action? drawActions)
    {
        if (drawActions is null || actionWidth <= 0f)
        {
            ImGui.TextUnformatted(title);
            return;
        }

        float startX = ImGui.GetCursorPosX();
        float availableWidth = ImGui.GetContentRegionAvail().X;
        ImGui.TextUnformatted(title);

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

    public static bool SliderFloat(string id, string title, ref float value, float min, float max, string format)
    {
        NextRow(title);
        return ImGui.SliderFloat($"##{id}", ref value, min, max, format);
    }

    public static bool DragFloat(string id, string title, ref float value, float speed, float min, float max, string format)
    {
        NextRow(title);
        return ImGui.DragFloat($"##{id}", ref value, speed, min, max, format);
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
        Action? drawActions = null)
    {
        NextRow(title, actionWidth, drawActions);
        return DrawDragFloat3Inputs(id, ref value, speed, min, max, format);
    }

    private static bool DrawDragFloat3Inputs(string id, ref Vector3 value, float speed, float min, float max, string format)
    {
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

        DrawAxisFloatInline($"{id}_x", "X", AxisXColor, ref x, speed, min, max, format, inputWidth, 0f, axisValueGap, ref changed);
        DrawAxisFloatInline($"{id}_y", "Y", AxisYColor, ref y, speed, min, max, format, inputWidth, axisGap, axisValueGap, ref changed);
        DrawAxisFloatInline($"{id}_z", "Z", AxisZColor, ref z, speed, min, max, format, inputWidth, axisGap, axisValueGap, ref changed);

        if (!changed)
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }

    private static void DrawAxisFloatInline(string id, string axis, Vector4 color, ref float value, float speed, float min, float max, string format, float inputWidth, float leadingGap, float valueGap, ref bool changed)
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

        ImGui.SameLine(0f, valueGap);
        ImGui.SetNextItemWidth(inputWidth);
        using var border = ImRaii.PushColor(ImGuiCol.Border, color with { W = 0.90f });
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, ImGuiHelpers.GlobalScale);
        if (ImGui.DragFloat($"##{id}", ref value, speed, min, max, format))
        {
            changed = true;
        }
    }

    public static bool EnumRow<T>(string id, string title, T current, Func<T, string> labelFunc, ref T target) where T : struct, Enum
    {
        NextRow(title);
        using var combo = ImRaii.Combo($"##{id}", labelFunc(current));
        if (!combo)
        {
            return false;
        }

        bool changed = false;
        foreach (T value in Enum.GetValues<T>())
        {
            bool selected = EqualityComparer<T>.Default.Equals(target, value);
            if (ImGui.Selectable(labelFunc(value), selected))
            {
                target = value;
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
