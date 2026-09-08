using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.UI;
using System.Numerics;
using System.Runtime.InteropServices;

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
    private const float SeparatorSpacing = 2f;
    private const float SubMenuOverlap = 1f;
    private const double SubMenuCloseDelaySeconds = 0.15;
    private const ImGuiWindowFlags PopupFlags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;
    private const ImGuiWindowFlags SubMenuPopupFlags = PopupFlags
        | ImGuiWindowFlags.ChildMenu
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoNavFocus;

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
        return BeginDropdown(id, open, new Vector2(itemMin.X, itemMax.Y));
    }

    /// <summary> begins a menu at the supplied anchor and opens it when requested </summary>
    public static PopupScope BeginDropdown(string id, bool open, Vector2 anchor, float? minimumWidth = null)
    {
        if (open)
        {
            ImGui.OpenPopup(id);
        }

        return BeginPopup(id, anchor, ImGuiCond.Appearing, minimumWidth);
    }

    private static PopupScope BeginPopup(
        string id,
        Vector2? anchor,
        ImGuiCond positionCondition,
        float? minimumWidth = null,
        ImGuiWindowFlags flags = PopupFlags)
    {
        if (ImGui.IsPopupOpen(id))
        {
            if (anchor.HasValue)
            {
                ImGui.SetNextWindowPos(anchor.Value, positionCondition);
            }

            ApplyPopupConstraints(minimumWidth);
        }

        if ((flags & ImGuiWindowFlags.ChildMenu) != ImGuiWindowFlags.None)
        {
            Vector2 itemInnerSpacing = ImGui.GetStyle().ItemInnerSpacing;
            using var placementSpacing = ImRaii.PushStyle(
                ImGuiStyleVar.ItemInnerSpacing,
                new Vector2(SubMenuOverlap * ImGuiHelpers.GlobalScale, itemInnerSpacing.Y));
            return new PopupScope(id, flags);
        }

        return new PopupScope(id, flags);
    }

    public static bool DrawItem(
        FontAwesomeIcon icon,
        string label,
        bool selected = false,
        bool enabled = true,
        string? id = null,
        Vector4? color = null,
        string? tooltip = null)
    {
        RowInteraction interaction = DrawRow(id ?? label, icon, label, selected, enabled, hasSubMenu: false, color, tooltip);
        if (!interaction.Activated)
        {
            return false;
        }

        ImGui.CloseCurrentPopup();
        return true;
    }

    public static SubMenuScope BeginSubMenu(
        string id,
        FontAwesomeIcon icon,
        string label,
        bool enabled = true,
        Vector4? color = null,
        string? tooltip = null)
    {
        string popupId = $"##editorContextSubMenu:{id}";
        RowInteraction interaction = DrawRow(id, icon, label, selected: false, enabled, hasSubMenu: true, color, tooltip);
        if (enabled && (interaction.Hovered || interaction.Activated) && !ImGui.IsPopupOpen(popupId))
        {
            ImGui.OpenPopup(popupId);
        }

        Vector2 popupPosition = new(
            ImGui.GetWindowPos().X + ImGui.GetWindowSize().X - (SubMenuOverlap * ImGuiHelpers.GlobalScale),
            interaction.Min.Y - ImGui.GetStyle().WindowPadding.Y);
        ImGuiWindowFlags popupFlags = SubMenuPopupFlags;
        if (SubMenu.Depth > 0)
        {
            popupFlags |= ImGuiWindowFlags.ChildWindow;
        }

        int depth = SubMenu.Depth + 1;
        PopupScope popup = BeginPopup(popupId, popupPosition, ImGuiCond.Always, flags: popupFlags);
        if (!popup)
        {
            SubMenu.ResetIfTracked(depth, popupId);
        }

        return new SubMenuScope(popupId, depth, interaction.Min, interaction.Max, popup);
    }

    public static void DrawFirstSectionLabel(string label)
        => DrawSectionLabel(label, false);

    public static void DrawSectionLabel(string label)
        => DrawSectionLabel(label, true);

    public static void DrawSeparator()
    {
        ImGuiHelpers.ScaledDummy(SeparatorSpacing);
        using (ImRaii.PushColor(
                   ImGuiCol.Separator,
                   ThemeColors.WithAlpha(ThemeColors.Separator, 0.70f)))
        {
            ImGui.Separator();
        }

        ImGuiHelpers.ScaledDummy(SeparatorSpacing);
    }

    private static void DrawSectionLabel(string label, bool drawSeparator)
    {
        if (drawSeparator)
        {
            DrawSeparator();
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (RowPadding * ImGuiHelpers.GlobalScale));
        ImGui.TextDisabled(label);
    }

    public static void DrawHint(string text)
    {
        using var indent = ImRaii.PushIndent(RowPadding + IconColumnWidth + RowGap);
        ImGui.TextDisabled(text);
    }

    private static void ApplyPopupConstraints(float? minimumWidth)
    {
        float scale = ImGuiHelpers.GlobalScale;
        ImGuiViewportPtr viewport = ImGui.GetWindowViewport();
        float maxWidth = MathF.Max(1f, MathF.Min(MaximumPopupWidth * scale, viewport.WorkSize.X - (24f * scale)));
        float minWidth = MathF.Min(MathF.Max(MinimumPopupWidth, minimumWidth ?? 0f) * scale, maxWidth);
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
        bool hasSubMenu,
        Vector4? color,
        string? tooltip)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float padding = RowPadding * scale;
        float gap = RowGap * scale;
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        FontAwesomeIcon? trailingIcon = ResolveTrailingIcon(selected, hasSubMenu);
        EditorIcon.Metrics trailingMetrics = trailingIcon is { } value
            ? EditorIcon.Measure(value)
            : default;

        Vector2 labelSize = ImGui.CalcTextSize(label);
        float rowHeight = MathF.Max(
            RowHeight * scale,
            MathF.Max(labelSize.Y, MathF.Max(iconMetrics.Size.Y, trailingMetrics.Size.Y)) + (10f * scale));
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
        bool childPopupOpen = HasOpenChildPopup();
        using (ImRaii.Disabled(!enabled))
        {
            activated = ImGui.InvisibleButton($"##editorContextMenuItem:{id}", new Vector2(rowWidth, rowHeight));
            ImGuiHoveredFlags hoverFlags = ImGuiHoveredFlags.AllowWhenDisabled;
            if (hasSubMenu || childPopupOpen)
            {
                hoverFlags |= ImGuiHoveredFlags.AllowWhenBlockedByPopup;
            }

            hovered = ImGui.IsItemHovered(hoverFlags);
        }

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        bool active = ImGui.IsItemActive();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        DrawRowBackground(drawList, min, max, selected, hovered, active, enabled, color);

        float contentHeight = max.Y - min.Y;
        Vector2 iconPosition = new(
            min.X + padding + (((IconColumnWidth * scale) - iconMetrics.Size.X) * 0.5f),
            min.Y + ((contentHeight - iconMetrics.Size.Y) * 0.5f));
        Vector2 labelPosition = new(
            min.X + padding + (IconColumnWidth * scale) + gap,
            min.Y + ((contentHeight - labelSize.Y) * 0.5f));
        float trailingRight = max.X - padding;
        Vector2 trailingPosition = new(
            trailingRight - trailingMetrics.Size.X - (((IconColumnWidth * scale) - trailingMetrics.Size.X) * 0.5f),
            min.Y + ((contentHeight - trailingMetrics.Size.Y) * 0.5f));
        float labelWidth = MathF.Max(1f, trailingRight - (IconColumnWidth * scale) - gap - labelPosition.X);
        EditorTextUtility.ClippedText visibleLabel = EditorTextUtility.ClipTextToWidthResult(label, labelWidth);
        Vector4 textColor = ResolveRowColor(color, enabled);
        Vector4 iconColor = textColor;
        if (!color.HasValue && enabled && (hovered || selected))
        {
            iconColor = ThemeColors.AccentBlue;
        }

        EditorIcon.Draw(drawList, icon, iconMetrics, iconPosition, iconColor);
        drawList.AddText(labelPosition, ImGui.GetColorU32(textColor), visibleLabel.Text);
        if (trailingIcon is { } resolvedTrailingIcon)
        {
            Vector4 trailingColor = textColor;
            if (color.HasValue)
            {
                trailingColor = ThemeColors.WithAlpha(textColor, textColor.W * 0.72f);
            }
            else if (enabled)
            {
                trailingColor = ThemeColors.TextDisabled;
            }

            EditorIcon.Draw(
                drawList,
                resolvedTrailingIcon,
                trailingMetrics,
                trailingPosition,
                trailingColor);
        }

        if (hovered && !string.IsNullOrEmpty(tooltip))
        {
            IntonerTooltip.DrawDescription(
                icon,
                label,
                tooltip,
                new IntonerTooltipOptions { Accent = color ?? ThemeColors.AccentPrimary });
        }
        else
        {
            EditorTextUtility.AttachTooltipIfClipped(min, max - min, label, visibleLabel.IsClipped);
        }

        return new RowInteraction(activated && enabled, hovered, min, max);
    }

    private static bool HasOpenChildPopup()
    {
        ImGuiContextPtr context = ImGui.GetCurrentContext();
        return context.OpenPopupStack.Size > context.BeginPopupStack.Size;
    }

    private static void DrawRowBackground(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        bool selected,
        bool hovered,
        bool active,
        bool enabled,
        Vector4? color)
    {
        Vector4 highlight = color ?? ThemeColors.AccentPrimary;
        Vector4 fill = selected
            ? ThemeColors.WithAlpha(highlight, 0.14f)
            : Vector4.Zero;
        if (enabled && hovered)
        {
            fill = ThemeColors.WithAlpha(highlight, active ? 0.34f : 0.24f);
        }

        if (fill.W > 0f)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(fill), 4f * ImGuiHelpers.GlobalScale);
        }
    }

    private static Vector4 ResolveRowColor(Vector4? color, bool enabled)
    {
        if (!enabled)
        {
            return color.HasValue
                ? ThemeColors.WithAlpha(color.Value, color.Value.W * 0.46f)
                : ThemeColors.TextDisabled;
        }

        return color ?? ThemeColors.Text;
    }

    private static FontAwesomeIcon? ResolveTrailingIcon(bool selected, bool hasSubMenu)
    {
        if (hasSubMenu)
        {
            return FontAwesomeIcon.ChevronRight;
        }

        return selected ? FontAwesomeIcon.Check : null;
    }

    public ref struct PopupScope
    {
        private readonly ImRaii.StyleDisposable _style;
        private ImRaii.PopupDisposable _popup;
        private bool _disposed;

        internal PopupScope(string id, ImGuiWindowFlags flags)
        {
            float scale = ImGuiHelpers.GlobalScale;
            _style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PopupPadding * scale, PopupPadding * scale))
                .Push(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 2f * scale));
            _popup = ImRaii.Popup(id, flags);
        }

        public bool Success => _popup.Success;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _popup.Dispose();
            _style.Dispose();
            _disposed = true;
        }

        public static implicit operator bool(PopupScope value)
            => value.Success;
    }

    public ref struct SubMenuScope
    {
        private readonly string _id;
        private readonly int _depth;
        private readonly Vector2 _parentMin;
        private readonly Vector2 _parentMax;
        private PopupScope _popup;
        private bool _disposed;

        internal SubMenuScope(string id, int depth, Vector2 parentMin, Vector2 parentMax, PopupScope popup)
        {
            _id = id;
            _depth = depth;
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

            if (Success && SubMenu.ShouldClose(_depth, _id, _parentMin, _parentMax))
            {
                CloseCurrentSubMenu();
                SubMenu.Reset(_depth);
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

        private static void CloseCurrentSubMenu()
        {
            ImGuiContextPtr context = ImGui.GetCurrentContext();
            int remaining = context.BeginPopupStack.Size - 1;
            if (remaining >= 0 && remaining < context.OpenPopupStack.Size)
            {
                ImGuiP.ClosePopupToLevel(remaining, true);
            }
        }
    }

    private sealed class SubMenuState
    {
        private readonly SubMenuLeaveTracker _leaveTracker = new();
        private int _depth;

        public int Depth => _depth;

        public void Enter()
            => _depth++;

        public void Exit()
            => _depth--;

        public void Reset(int depth)
            => _leaveTracker.Reset(depth);

        public void ResetIfTracked(int depth, string id)
            => _leaveTracker.ResetIfTracked(depth, id);

        public bool ShouldClose(int depth, string id, Vector2 parentMin, Vector2 parentMax)
        {
            ImGuiHoveredFlags hoverFlags = ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByPopup;
            bool hovered = ImGui.IsWindowHovered(hoverFlags)
                || EditorInputUtility.IsMouseInside(parentMin, parentMax);
            return _leaveTracker.ShouldClose(depth, id, hovered, ImGui.GetTime(), SubMenuCloseDelaySeconds);
        }
    }
}

internal sealed class SubMenuLeaveTracker
{
    private readonly Dictionary<int, LeaveState> _states = [];

    public bool ShouldClose(int depth, string id, bool hovered, double now, double delay)
    {
        if (hovered)
        {
            Reset(depth);
            return false;
        }

        if (!_states.TryGetValue(depth, out LeaveState state)
         || !string.Equals(state.Id, id, StringComparison.Ordinal))
        {
            _states[depth] = new LeaveState(id, now);
            return false;
        }

        return now - state.StartedAt >= delay;
    }

    public void Reset(int depth)
        => _states.Remove(depth);

    public void ResetIfTracked(int depth, string id)
    {
        if (_states.TryGetValue(depth, out LeaveState state)
         && string.Equals(state.Id, id, StringComparison.Ordinal))
        {
            Reset(depth);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LeaveState(string Id, double StartedAt);
}
