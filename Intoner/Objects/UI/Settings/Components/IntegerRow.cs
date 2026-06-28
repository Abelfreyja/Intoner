using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal static class IntegerRow
{
    private const float RowMinHeight = 74f;

    public static bool Draw(
        SettingDefinition definition,
        ref int value,
        IntegerSettingRange range,
        string valueText,
        string minimumText,
        string maximumText,
        Vector4 accent,
        bool prominentControl,
        bool enabled,
        IntegerSettingEditState editState,
        SettingRowLayout layout,
        out IntegerSettingUpdate update)
    {
        update = default;
        float controlHeight = IntegerRangeControl.ResolveHeight(prominentControl);
        float rowHeight = MathF.Max(
            RowMinHeight * ImGuiHelpers.GlobalScale,
            RowChrome.ResolveRowHeight(controlHeight));

        RowChrome.BeginRow(definition, rowHeight);
        return DrawControl(
            definition,
            ref value,
            range,
            valueText,
            minimumText,
            maximumText,
            accent,
            prominentControl,
            enabled,
            editState,
            rowHeight,
            controlHeight,
            layout,
            out update);
    }

    private static bool DrawControl(
        SettingDefinition definition,
        ref int value,
        IntegerSettingRange range,
        string valueText,
        string minimumText,
        string maximumText,
        Vector4 accent,
        bool prominentControl,
        bool enabled,
        IntegerSettingEditState editState,
        float rowHeight,
        float controlHeight,
        SettingRowLayout layout,
        out IntegerSettingUpdate update)
    {
        update = default;
        float controlWidth = RowChrome.ResolveControlWidth(RowChrome.AvailableControlWidth(), layout);

        RowChrome.AlignControl(rowHeight, controlHeight, controlWidth);
        bool changed = IntegerRangeControl.Draw(
            definition.Id,
            ref value,
            range,
            valueText,
            minimumText,
            maximumText,
            accent,
            prominentControl,
            enabled,
            editState,
            controlWidth,
            out IntegerRangeControlUpdate controlUpdate);

        RowChrome.DrawDescriptionTooltip(definition);
        if (!changed)
        {
            return false;
        }

        update = new IntegerSettingUpdate(controlUpdate.Value, controlUpdate.Commit);
        return true;
    }
}

