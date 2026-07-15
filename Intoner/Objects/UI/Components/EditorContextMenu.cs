using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace Intoner.Objects.UI.Components;

internal static class EditorContextMenu
{
    private const float MinimumPopupWidth = 200f;
    private const float MaximumPopupWidth = 320f;
    private const float PopupPadding = 6f;
    private const float RowHeight = 28f;
    private const float RowPadding = 8f;
    private const float RowGap = 8f;
    private const float IconColumnWidth = 18f;
    private const double SubMenuCloseDelaySeconds = 0.15;
    private const ImGuiWindowFlags PopupFlags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;

    private static readonly SubMenuState SubMenu = new();

    private readonly record struct RowInteraction(bool Activated, bool Hovered, Vector2 Min, Vector2 Max);

    /// <summary> begins a right click menu for the previously drawn item </summary>
    public static PopupScope BeginForLastItem(string id)
    {
        ImGui.OpenPopupOnItemClick(id, ImGuiPopupFlags.MouseButtonRight);
        return BeginPopup(id, null, ImGuiCond.Appearing);
    }

    /// <summary> begins a menu below the previously drawn item and opens it when requested </summary>
    public static PopupScope BeginDropdownForLastItem(string id, bool open)
    {
        Vector2 itemMin = ImGui.GetItemRectMin();
        Vector2 itemMax = ImGui.GetItemRectMax();
        if (open)
        {
            ImGui.OpenPopup(id);
        }

        return BeginPopup(id, new Vector2(itemMin.X, itemMax.Y), ImGuiCond.Appearing);
    }

    private static PopupScope BeginPopup(string id, Vector2? anchor, ImGuiCond positionCondition)
    {
        if (ImGui.IsPopupOpen(id))
        {
            if (anchor.HasValue)
            {
                ImGui.SetNextWindowPos(anchor.Value, positionCondition);
            }

            ApplyPopupConstraints();
        }

        return new PopupScope(id);
    }

    public static bool DrawItem(
        FontAwesomeIcon icon,
        string label,
        bool selected = false,
        bool enabled = true,
        string? id = null)
    {
        RowInteraction interaction = DrawRow(id ?? label, icon, label, selected, enabled, hasSubMenu: false);
        if (!interaction.Activated)
        {
            return false;
        }

        ImGui.CloseCurrentPopup();
        SubMenu.RequestParentClose();
        return true;
    }

    public static SubMenuScope BeginSubMenu(string id, FontAwesomeIcon icon, string label, bool enabled = true)
    {
        string popupId = $"##editorContextSubMenu:{id}";
        RowInteraction interaction = DrawRow(id, icon, label, selected: false, enabled, hasSubMenu: true);
        if (enabled && (interaction.Hovered || interaction.Activated) && !ImGui.IsPopupOpen(popupId))
        {
            ImGui.OpenPopup(popupId);
        }

        Vector2 popupPosition = new(
            ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - ImGuiHelpers.GlobalScale,
            interaction.Min.Y - ImGui.GetStyle().WindowPadding.Y);
        PopupScope popup = BeginPopup(popupId, popupPosition, ImGuiCond.Always);
        if (!popup)
        {
            SubMenu.ResetIfTracked(popupId);
        }

        return new SubMenuScope(popupId, interaction.Min, interaction.Max, popup);
    }

    public static void DrawSectionLabel(string label)
    {
        ImGui.Separator();
        float offset = MathF.Max(0f, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(label).X) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextDisabled(label);
    }

    public static void DrawHint(string text)
    {
        float indent = (RowPadding + IconColumnWidth + RowGap) * ImGuiHelpers.GlobalScale;
        ImGui.Indent(indent);
        ImGui.TextDisabled(text);
        ImGui.Unindent(indent);
    }

    private static void ApplyPopupConstraints()
    {
        float scale = ImGuiHelpers.GlobalScale;
        ImGuiViewportPtr viewport = ImGui.GetWindowViewport();
        float maxWidth = MathF.Max(1f, MathF.Min(MaximumPopupWidth * scale, viewport.WorkSize.X - (24f * scale)));
        float minWidth = MathF.Min(MinimumPopupWidth * scale, maxWidth);
        float maxHeight = MathF.Max(1f, MathF.Min(420f * scale, viewport.WorkSize.Y * 0.70f));
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(minWidth, 0f),
            new Vector2(maxWidth, maxHeight));
    }

    private static RowInteraction DrawRow(
        string id,
        FontAwesomeIcon icon,
        string label,
        bool selected,
        bool enabled,
        bool hasSubMenu)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float padding = RowPadding * scale;
        float gap = RowGap * scale;
        string iconText = icon.ToIconString();
        string trailingText = ResolveTrailingIcon(selected, hasSubMenu);
        Vector2 iconSize;
        Vector2 trailingSize;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(iconText);
            trailingSize = string.IsNullOrEmpty(trailingText) ? Vector2.Zero : ImGui.CalcTextSize(trailingText);
        }

        Vector2 labelSize = ImGui.CalcTextSize(label);
        float rowHeight = MathF.Max(
            RowHeight * scale,
            MathF.Max(labelSize.Y, MathF.Max(iconSize.Y, trailingSize.Y)) + (10f * scale));
        float naturalWidth = padding
            + (IconColumnWidth * scale)
            + gap
            + labelSize.X
            + gap
            + (IconColumnWidth * scale)
            + padding;
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float maximumRowWidth = (MaximumPopupWidth * scale) - (ImGui.GetStyle().WindowPadding.X * 2f);
        float rowWidth = MathF.Min(maximumRowWidth, MathF.Max(availableWidth, naturalWidth));

        bool activated;
        bool hovered;
        using (ImRaii.Disabled(!enabled))
        {
            activated = ImGui.InvisibleButton($"##editorContextMenuItem:{id}", new Vector2(rowWidth, rowHeight));
            hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        }

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        bool active = ImGui.IsItemActive();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        DrawRowBackground(drawList, min, max, selected, hovered, active, enabled);

        float contentHeight = max.Y - min.Y;
        Vector2 iconPosition = new(
            min.X + padding + (((IconColumnWidth * scale) - iconSize.X) * 0.5f),
            min.Y + ((contentHeight - iconSize.Y) * 0.5f));
        Vector2 labelPosition = new(
            min.X + padding + (IconColumnWidth * scale) + gap,
            min.Y + ((contentHeight - labelSize.Y) * 0.5f));
        float trailingRight = max.X - padding;
        Vector2 trailingPosition = new(
            trailingRight - trailingSize.X - (((IconColumnWidth * scale) - trailingSize.X) * 0.5f),
            min.Y + ((contentHeight - trailingSize.Y) * 0.5f));
        float labelWidth = MathF.Max(1f, trailingRight - (IconColumnWidth * scale) - gap - labelPosition.X);
        EditorTextUtility.ClippedText visibleLabel = EditorTextUtility.ClipTextToWidthResult(label, labelWidth);
        Vector4 textColor = enabled ? EditorColors.Text : EditorColors.TextDisabled;
        Vector4 iconColor = enabled && (hovered || selected) ? EditorColors.AccentBlue : textColor;

        DrawIcon(drawList, iconPosition, iconText, iconColor);
        drawList.AddText(labelPosition, ImGui.GetColorU32(textColor), visibleLabel.Text);
        if (!string.IsNullOrEmpty(trailingText))
        {
            DrawIcon(drawList, trailingPosition, trailingText, enabled ? EditorColors.TextDisabled : textColor);
        }

        EditorTextUtility.AttachTooltipIfClipped(min, max - min, label, visibleLabel.IsClipped);
        return new RowInteraction(activated && enabled, hovered, min, max);
    }

    private static void DrawRowBackground(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool selected,
        bool hovered,
        bool active,
        bool enabled)
    {
        Vector4 fill = selected
            ? EditorColors.WithAlpha(EditorColors.AccentPurple, 0.14f)
            : Vector4.Zero;
        if (enabled && hovered)
        {
            fill = EditorColors.WithAlpha(EditorColors.AccentPurple, active ? 0.34f : 0.24f);
        }

        if (fill.W > 0f)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 4f * ImGuiHelpers.GlobalScale);
        }
    }

    private static string ResolveTrailingIcon(bool selected, bool hasSubMenu)
    {
        if (hasSubMenu)
        {
            return FontAwesomeIcon.ChevronRight.ToIconString();
        }

        return selected ? FontAwesomeIcon.Check.ToIconString() : string.Empty;
    }

    private static void DrawIcon(ImDrawListPtr drawList, Vector2 position, string text, Vector4 color)
        => drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize(), position, ImGui.GetColorU32(color), text);

    public ref struct PopupScope
    {
        private readonly ImRaii.StyleDisposable _style;
        private bool _disposed;

        internal PopupScope(string id)
        {
            float scale = ImGuiHelpers.GlobalScale;
            _style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PopupPadding * scale, PopupPadding * scale))
                .Push(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 2f * scale));
            Success = ImGui.BeginPopup(id, PopupFlags);
        }

        public bool Success { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (Success)
            {
                ImGui.EndPopup();
            }

            _style.Dispose();
            _disposed = true;
        }

        public static implicit operator bool(PopupScope value)
            => value.Success;
    }

    public ref struct SubMenuScope
    {
        private readonly string _id;
        private readonly Vector2 _parentMin;
        private readonly Vector2 _parentMax;
        private PopupScope _popup;
        private bool _disposed;

        internal SubMenuScope(string id, Vector2 parentMin, Vector2 parentMax, PopupScope popup)
        {
            _id = id;
            _parentMin = parentMin;
            _parentMax = parentMax;
            _popup = popup;
            if (Success)
            {
                SubMenu.Enter();
            }
        }

        public bool Success => _popup.Success;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (Success && SubMenu.ShouldClose(_id, _parentMin, _parentMax))
            {
                ImGui.CloseCurrentPopup();
                SubMenu.Reset();
            }

            _popup.Dispose();
            if (Success)
            {
                SubMenu.Exit();
            }

            _disposed = true;
        }

        public static implicit operator bool(SubMenuScope value)
            => value.Success;
    }

    private sealed class SubMenuState
    {
        private string? _trackedId;
        private double _leaveStartedAt = double.NaN;
        private int _depth;
        private bool _closeStack;

        public void RequestParentClose()
            => _closeStack = _depth > 0;

        public void Enter()
            => _depth++;

        public void Exit()
        {
            _depth--;
            if (!_closeStack)
            {
                return;
            }

            ImGui.CloseCurrentPopup();
            if (_depth == 0)
            {
                _closeStack = false;
            }
        }

        public void Reset()
        {
            _trackedId = null;
            _leaveStartedAt = double.NaN;
        }

        public void ResetIfTracked(string id)
        {
            if (string.Equals(_trackedId, id, StringComparison.Ordinal))
            {
                Reset();
            }
        }

        public bool ShouldClose(string id, Vector2 parentMin, Vector2 parentMax)
        {
            if (ImGui.IsWindowHovered() || EditorInputUtility.IsMouseInside(parentMin, parentMax))
            {
                _trackedId = id;
                _leaveStartedAt = double.NaN;
                return false;
            }

            double now = ImGui.GetTime();
            if (!string.Equals(_trackedId, id, StringComparison.Ordinal) || double.IsNaN(_leaveStartedAt))
            {
                _trackedId = id;
                _leaveStartedAt = now;
                return false;
            }

            return now - _leaveStartedAt >= SubMenuCloseDelaySeconds;
        }
    }
}
