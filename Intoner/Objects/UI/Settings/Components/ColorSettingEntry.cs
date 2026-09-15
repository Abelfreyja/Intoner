using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Settings.Components;

internal sealed class ColorSettingEntry : ISettingEntry
{
    private readonly Func<Vector3> _readValue;
    private readonly Action<Vector3> _writeValue;
    private readonly Action<Vector3> _previewValue;
    private readonly Func<bool> _isEnabled;
    private readonly SettingRowLayout _layout;

    private Vector3 _pendingValue;
    private Vector3 _pickerReference;
    private bool _hasPendingValue;

    public ColorSettingEntry(
        SettingDefinition definition,
        Func<Vector3> readValue,
        Action<Vector3> writeValue,
        Action<Vector3>? previewValue = null,
        Func<bool>? isEnabled = null,
        SettingRowLayout layout = default)
    {
        Definition = definition;
        _readValue = readValue;
        _writeValue = writeValue;
        _previewValue = previewValue ?? (static _ => { });
        _isEnabled = isEnabled ?? (static () => true);
        _layout = layout;
    }

    public SettingDefinition Definition { get; }

    public SettingRowLayout Layout
        => _layout;

    public void DrawRow(Vector4 accent, bool prominentControl)
    {
        bool enabled = _isEnabled();
        Vector3 savedValue = _readValue();
        if (!enabled)
        {
            _hasPendingValue = false;
        }

        Vector3 value = _hasPendingValue ? _pendingValue : savedValue;
        bool changed = ColorSettingRow.Draw(
            Definition,
            ref value,
            ref _pickerReference,
            accent,
            enabled,
            _layout,
            out bool commit);
        if (changed)
        {
            _pendingValue = value;
            _hasPendingValue = true;
        }

        if (_hasPendingValue)
        {
            _previewValue(_pendingValue);
        }

        if (commit)
        {
            _hasPendingValue = false;
            if (value != savedValue)
            {
                _writeValue(value);
            }
        }

        _ = prominentControl;
    }
}

internal static class ColorSettingRow
{
    private const float PickerPopupWidth = 420f;
    private const float PickerPopupMargin = 8f;
    private const float PickerPopupPadding = 8f;
    private const ImGuiColorEditFlags HexEditorFlags =
        ImGuiColorEditFlags.DisplayHex
      | ImGuiColorEditFlags.InputRgb
      | ImGuiColorEditFlags.NoDragDrop
      | ImGuiColorEditFlags.NoOptions
      | ImGuiColorEditFlags.NoSmallPreview
      | ImGuiColorEditFlags.NoTooltip;
    private const ImGuiColorEditFlags PickerFlags =
        ImGuiColorEditFlags.InputRgb
      | ImGuiColorEditFlags.NoAlpha;
    private const ImGuiColorEditFlags SwatchFlags =
        ImGuiColorEditFlags.NoDragDrop
      | ImGuiColorEditFlags.NoTooltip;
    private const ImGuiWindowFlags PopupFlags =
        ImGuiWindowFlags.AlwaysAutoResize
      | ImGuiWindowFlags.NoSavedSettings
      | ImGuiWindowFlags.NoScrollbar
      | ImGuiWindowFlags.NoScrollWithMouse;

    public static bool Draw(
        SettingDefinition definition,
        ref Vector3 value,
        ref Vector3 pickerReference,
        Vector4 accent,
        bool enabled,
        SettingRowLayout layout,
        out bool commit)
    {
        float controlHeight = ImGui.GetFrameHeight();
        float rowHeight = RowChrome.ResolveRowHeight(controlHeight);
        RowChrome.BeginRow(definition, rowHeight);
        float controlWidth = RowChrome.ResolveControlWidth(RowChrome.AvailableControlWidth(), layout);
        RowChrome.AlignControl(rowHeight, controlHeight, controlWidth);

        float swatchSize = controlHeight;
        float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        float editorWidth = MathF.Max(1f, controlWidth - swatchSize - spacing);

        bool changed;
        bool editorCommit;
        using (ImRaii.ItemWidth(editorWidth))
        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, ThemeColors.ButtonDefault with { W = enabled ? 0.42f : 0.20f }))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, ThemeColors.ButtonDefault with { W = 0.54f }))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, accent with { W = 0.22f }))
        {
            changed = ImGui.ColorEdit3(
                $"##settingColor_{definition.Id}",
                ref value,
                HexEditorFlags);
            editorCommit = ImGui.IsItemDeactivatedAfterEdit();
        }

        ImGui.SameLine(0f, spacing);
        string popupId = $"##settingColorPicker_{definition.Id}";
        bool openPopup;
        using (ImRaii.Disabled(!enabled))
        {
            openPopup = ImGui.ColorButton(
                $"##settingColorSwatch_{definition.Id}",
                new Vector4(value, 1f),
                SwatchFlags,
                new Vector2(swatchSize));
        }

        Vector2 swatchMin = ImGui.GetItemRectMin();
        Vector2 swatchMax = ImGui.GetItemRectMax();
        if (openPopup)
        {
            pickerReference = value;
            ImGui.OpenPopup(popupId);
        }

        PreparePickerPopup(popupId, swatchMin, swatchMax);
        using var popupPadding = ImRaii.PushStyle(
            ImGuiStyleVar.WindowPadding,
            new Vector2(PickerPopupPadding * ImGuiHelpers.GlobalScale));
        using var popupRounding = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, 6f * ImGuiHelpers.GlobalScale);
        using var popupBorderSize = ImRaii.PushStyle(
            ImGuiStyleVar.PopupBorderSize,
            MathF.Max(1f, ImGuiHelpers.GlobalScale));
        using var popupBorder = ImRaii.PushColor(ImGuiCol.Border, accent with { W = 0.55f });
        using var popup = ImRaii.Popup(popupId, PopupFlags);
        bool pickerCommit = false;
        if (popup)
        {
            float sidePreviewWidth = (ImGui.GetFrameHeight() * 3f) + ImGui.GetStyle().ItemInnerSpacing.X;
            ImGui.SetNextItemWidth(MathF.Max(1f, ImGui.GetContentRegionAvail().X - sidePreviewWidth));

            Vector4 pickerValue = new(value, 1f);
            Vector4 originalValue = new(pickerReference, 1f);
            changed |= ImGui.ColorPicker4(
                $"##settingColorPickerControl_{definition.Id}",
                ref pickerValue,
                PickerFlags,
                originalValue);
            value = new Vector3(pickerValue.X, pickerValue.Y, pickerValue.Z);
            pickerCommit = ImGui.IsItemDeactivatedAfterEdit();
        }

        commit = editorCommit || pickerCommit;
        return changed;
    }

    private static void PreparePickerPopup(string popupId, Vector2 anchorMin, Vector2 anchorMax)
    {
        if (!ImGui.IsPopupOpen(popupId))
        {
            return;
        }

        float scale = ImGuiHelpers.GlobalScale;
        float margin = PickerPopupMargin * scale;
        ImGuiViewportPtr viewport = ImGui.GetWindowViewport();
        Vector2 workMin = viewport.WorkPos + new Vector2(margin);
        Vector2 workMax = viewport.WorkPos + viewport.WorkSize - new Vector2(margin);
        float availableWidth = MathF.Max(1f, workMax.X - workMin.X);
        float availableHeight = MathF.Max(1f, workMax.Y - workMin.Y);
        float popupWidth = MathF.Min(PickerPopupWidth * scale, MathF.Min(availableWidth, availableHeight));
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float popupX = Math.Clamp(anchorMax.X - popupWidth, workMin.X, MathF.Max(workMin.X, workMax.X - popupWidth));
        float popupY = anchorMax.Y + spacing;
        if (popupY + popupWidth > workMax.Y)
        {
            popupY = anchorMin.Y - popupWidth - spacing;
        }

        popupY = Math.Clamp(popupY, workMin.Y, MathF.Max(workMin.Y, workMax.Y - popupWidth));
        ImGui.SetNextWindowPos(new Vector2(popupX, popupY), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(popupWidth, 0f),
            new Vector2(popupWidth, availableHeight));
    }
}
