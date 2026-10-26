using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Services.Input;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI.Components;

internal static class EditorContextMenu
{
    private const float MinimumPopupWidth = 200f;
    private const float MaximumPopupWidth = 320f;
    private const float PopupPadding = 6f;
    private const float PopupItemSpacing = 2f;
    private const float RowHeight = 28f;
    private const float RowPadding = 8f;
    private const float RowGap = 8f;
    private const float IconColumnWidth = 18f;
    private const float SeparatorSpacing = 2f;
    private const float ToggleHintSpacerHeight = 2f;
    private const float SubMenuOverlap = 1f;
    private const double SubMenuCloseDelaySeconds = 0.15;
    private const ImGuiWindowFlags PopupFlags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;
    private const ImGuiWindowFlags SubMenuPopupFlags = PopupFlags
        | ImGuiWindowFlags.ChildMenu
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoNavFocus;

    private static readonly SubMenuState SubMenu = new();

    public static float ItemHeight
        => MathF.Max(RowHeight * ImGuiHelpers.GlobalScale,
            MathF.Max(ImGui.GetTextLineHeight(), UiBuilder.IconFont.FontSize) + 10f * ImGuiHelpers.GlobalScale);

    public static float ChildIndent
        => (RowPadding * 2f + IconColumnWidth + RowGap) * ImGuiHelpers.GlobalScale;

    public static float SeparatorHeight
        => (SeparatorSpacing * 2f * ImGuiHelpers.GlobalScale) + (ImGui.GetStyle().ItemSpacing.Y * 3f);

    public static ImRaii.StyleDisposable PushStyle()
    {
        float scale = ImGuiHelpers.GlobalScale;
        return ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(PopupPadding * scale, PopupPadding * scale))
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(0f, PopupItemSpacing * scale));
    }

    private enum ChoiceMode { None, Single, Multiple }

    private readonly record struct RowInteraction(bool Activated, bool Hovered, Vector2 Min, Vector2 Max, bool CheckboxActivated = false);

    /// <summary> begins a right click menu for the previously drawn item </summary>
    public static PopupScope BeginForLastItem(string id)
    {
        ImGui.OpenPopupOnItemClick(id, ImGuiPopupFlags.MouseButtonRight);
        return BeginPopup(id, null, ImGuiCond.Appearing);
    }

    /// <summary> begins a menu below the previously drawn item and opens it when requested </summary>
    public static PopupScope BeginDropdownForLastItem(string id, bool open, float? width = null, Vector4? tooltipAccent = null)
    {
        Vector2 itemMin = ImGui.GetItemRectMin();
        Vector2 itemMax = ImGui.GetItemRectMax();
        return BeginDropdown(id, open, new Vector2(itemMin.X, itemMax.Y), width, tooltipAccent);
    }

    /// <summary> begins a menu at the supplied screen anchor with an optional width in scaled pixels </summary>
    public static PopupScope BeginDropdown(string id, bool open, Vector2 anchor, float? width = null, Vector4? tooltipAccent = null)
    {
        if (open)
        {
            ImGui.OpenPopup(id);
        }

        return BeginPopup(id, anchor, ImGuiCond.Appearing, width, tooltipAccent: tooltipAccent);
    }

    private static PopupScope BeginPopup(
        string id,
        Vector2? anchor,
        ImGuiCond positionCondition,
        float? width = null,
        ImGuiWindowFlags flags = PopupFlags,
        Vector4? tooltipAccent = null)
    {
        if (ImGui.IsPopupOpen(id))
        {
            if (anchor.HasValue)
            {
                ImGui.SetNextWindowPos(anchor.Value, positionCondition);
            }

            ApplyPopupConstraints(width);
        }

        if ((flags & ImGuiWindowFlags.ChildMenu) != ImGuiWindowFlags.None)
        {
            Vector2 itemInnerSpacing = ImGui.GetStyle().ItemInnerSpacing;
            using var placementSpacing = ImRaii.PushStyle(
                ImGuiStyleVar.ItemInnerSpacing,
                new Vector2(SubMenuOverlap * ImGuiHelpers.GlobalScale, itemInnerSpacing.Y));
            return new PopupScope(id, flags, tooltipAccent);
        }

        return new PopupScope(id, flags, tooltipAccent);
    }

    public static bool DrawItem(
        FontAwesomeIcon icon,
        string label,
        bool selected = false,
        bool enabled = true,
        string? id = null,
        Vector4? color = null,
        string? tooltip = null,
        bool constrainWidth = false,
        IReadOnlyList<EditorBadge>? badges = null,
        Vector4? accent = null,
        float? height = null,
        bool closeOnActivation = true)
    {
        RowInteraction interaction = DrawRow(id ?? label, icon, label, selected, enabled, hasSubMenu: false, color, tooltip, constrainWidth, badges, accent, height);
        return closeOnActivation ? CloseOnActivation(interaction.Activated) : interaction.Activated;
    }

    public static bool DrawToggleWithHint(
        string id, FontAwesomeIcon icon, string label, ref bool value, Vector4 accent,
        KeyboardGesture? overrideGesture, string enabledHint, string disabledHint,
        bool enabled = true, string? tooltip = null)
    {
        bool changed = DrawItem(icon, label, selected: value, enabled: enabled, id: id, tooltip: tooltip,
            constrainWidth: true, accent: accent, closeOnActivation: false);

        if (changed)
        {
            value = !value;
        }

        if (IntonerTooltipKeyHint.HoldOverride(overrideGesture, value, enabledHint, disabledHint) is { } hint)
        {
            ImGuiHelpers.ScaledDummy(0f, ToggleHintSpacerHeight);
            IntonerTooltipContent.KeyHint(hint, accent);
        }

        return changed;
    }

    public static bool DrawChoice(string id, string label, bool? selected, Vector4 accent,
        string? count = null, float? height = null, bool closeOnActivation = true)
    {
        FontAwesomeIcon icon = ResolveChoiceIcon(selected);
        EditorBadge? countBadge = string.IsNullOrEmpty(count) ? null : new EditorBadge(count, EditorBadgeStyle.Muted);
        RowInteraction interaction = DrawRow(id, icon,
            label, selected is not false, enabled: true, hasSubMenu: false, color: null, tooltip: null,
            constrainWidth: true, accent: accent, height: height,
            choice: closeOnActivation ? ChoiceMode.Single : ChoiceMode.Multiple, trailingBadge: countBadge);

        return closeOnActivation ? CloseOnActivation(interaction.Activated) : interaction.Activated;
    }

    public static bool DrawTreeChoice(string id, string label, bool? selected, ref bool expanded, Vector4 accent)
    {
        RowInteraction interaction = DrawRow(id, ResolveChoiceIcon(selected),
            label, selected is not false, enabled: true, hasSubMenu: false, color: null, tooltip: null,
            constrainWidth: true, accent: accent, choice: ChoiceMode.Multiple, expanded: expanded);

        if (!interaction.CheckboxActivated
            && (interaction.Activated || ImGui.IsItemFocused() && ImGui.IsKeyPressed(ImGuiKey.Enter)))
        {
            expanded = !expanded;
        }

        if (ImGui.IsItemFocused())
        {
            if (ImGui.IsKeyPressed(ImGuiKey.RightArrow))
            {
                expanded = true;
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow))
            {
                expanded = false;
            }
        }

        return interaction.CheckboxActivated;
    }

    private static FontAwesomeIcon ResolveChoiceIcon(bool? selected)
        => selected switch
        {
            true => FontAwesomeIcon.Check,
            null => FontAwesomeIcon.Minus,
            _ => FontAwesomeIcon.None,
        };

    private static bool CloseOnActivation(bool activated)
    {
        if (activated)
        {
            ImGui.CloseCurrentPopup();
        }

        return activated;
    }

    public static SubMenuScope BeginSubMenu(
        string id,
        FontAwesomeIcon icon,
        string label,
        bool enabled = true,
        Vector4? color = null,
        string? tooltip = null,
        Vector4? accent = null)
    {
        string popupId = $"##editorContextSubMenu:{id}";
        RowInteraction interaction = DrawRow(id, icon, label, selected: false, enabled, hasSubMenu: true, color, tooltip, accent: accent);

        if (enabled && (interaction.Hovered || interaction.Activated) && !ImGui.IsPopupOpen(popupId))
        {
            bool replacingSubMenu = HasOpenChildPopup();
            ImGui.OpenPopup(popupId);

            if (replacingSubMenu)
            {
                return default;
            }
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

    public static Vector2 ApplyPopupConstraints(float? width = null, float minimumWidth = 0f)
    {
        float scale = ImGuiHelpers.GlobalScale;
        ImGuiViewportPtr viewport = ImGui.GetWindowViewport();
        float availableWidth = MathF.Max(1f, viewport.WorkSize.X - (24f * scale));
        float maxWidth = Math.Clamp(MathF.Max(minimumWidth, width ?? MaximumPopupWidth * scale), 1f, availableWidth);
        float minWidth = width.HasValue ? maxWidth : MathF.Min(MinimumPopupWidth * scale, maxWidth);
        float maxHeight = MathF.Max(1f, MathF.Min(420f * scale, viewport.WorkSize.Y * 0.70f));
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(minWidth, 0f),
            new Vector2(maxWidth, maxHeight));
        return new Vector2(maxWidth, maxHeight);
    }

    private static RowInteraction DrawRow(
        string id,
        FontAwesomeIcon icon,
        string label,
        bool selected,
        bool enabled,
        bool hasSubMenu,
        Vector4? color,
        string? tooltip,
        bool constrainWidth = false,
        IReadOnlyList<EditorBadge>? badges = null,
        Vector4? accent = null,
        float? height = null,
        ChoiceMode choice = ChoiceMode.None,
        EditorBadge? trailingBadge = null,
        bool? expanded = null)
    {
        label ??= string.Empty;
        float scale = ImGuiHelpers.GlobalScale;
        float padding = RowPadding * scale;
        float gap = RowGap * scale;
        bool multiple = choice == ChoiceMode.Multiple;
        FontAwesomeIcon leadingIcon = icon;

        if (expanded.HasValue)
        {
            leadingIcon = expanded.Value ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight;
        }

        EditorIcon.Metrics iconMetrics = multiple && !expanded.HasValue || leadingIcon == FontAwesomeIcon.None
            ? default : EditorIcon.Measure(leadingIcon);
        FontAwesomeIcon? trailingIcon = choice switch
        {
            ChoiceMode.None => ResolveTrailingIcon(selected, hasSubMenu),
            ChoiceMode.Multiple when icon != FontAwesomeIcon.None => icon,
            _ => null,
        };
        float trailingIconScale = multiple ? 0.65f : 1f;
        EditorIcon.Metrics trailingMetrics = trailingIcon is { } value
            ? EditorIcon.Measure(value, trailingIconScale)
            : default;
        float iconColumnWidth = MathF.Max(IconColumnWidth * scale, MathF.Max(iconMetrics.Size.X, trailingMetrics.Size.X));

        string displayLabel = height.HasValue ? label.ReplaceLineEndings(" ") : label;
        Vector2 labelSize = ImGui.CalcTextSize(displayLabel);
        float rowHeight = height ?? MathF.Max(
            RowHeight * scale,
            MathF.Max(labelSize.Y, MathF.Max(iconMetrics.Size.Y, trailingMetrics.Size.Y)) + (10f * scale));
        float leadingWidth = multiple && !expanded.HasValue ? 0f : iconColumnWidth + gap;
        float naturalWidth = padding
            + leadingWidth
            + labelSize.X
            + gap
            + iconColumnWidth
            + padding;
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float maximumRowWidth = (MaximumPopupWidth * scale) - (ImGui.GetStyle().WindowPadding.X * 2f);
        float rowWidth = constrainWidth ? MathF.Max(1f, availableWidth) : MathF.Min(maximumRowWidth, MathF.Max(availableWidth, naturalWidth));

        bool activated;
        bool hovered;
        bool childPopupOpen = HasOpenChildPopup();
        using (ImRaii.Disabled(!enabled))
        {
            if (choice != ChoiceMode.None)
            {
                using ImRaii.ColorDisposable colors = ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero)
                    .Push(ImGuiCol.HeaderHovered, Vector4.Zero)
                    .Push(ImGuiCol.HeaderActive, Vector4.Zero);

                activated = ImGui.Selectable($"##editorContextMenuItem:{id}", false, ImGuiSelectableFlags.DontClosePopups, new Vector2(rowWidth, rowHeight));

                if (selected && choice == ChoiceMode.Single && ImGui.IsWindowAppearing())
                {
                    ImGui.SetKeyboardFocusHere(-1);
                }
            }
            else
            {
                activated = ImGui.InvisibleButton($"##editorContextMenuItem:{id}", new Vector2(rowWidth, rowHeight));
            }

            ImGuiHoveredFlags hoverFlags = ImGuiHoveredFlags.AllowWhenDisabled;
            if (hasSubMenu || childPopupOpen)
            {
                hoverFlags |= ImGuiHoveredFlags.AllowWhenBlockedByPopup;
            }

            hovered = ImGui.IsItemHovered(hoverFlags) || choice != ChoiceMode.None && ImGui.IsItemFocused();
        }

        Vector2 min = ImGui.GetItemRectMin();
        Vector2 max = ImGui.GetItemRectMax();
        RowInteraction interaction = new(activated && enabled, hovered, min, max);

        if (!ImGui.IsItemVisible())
        {
            return interaction;
        }

        bool active = ImGui.IsItemActive();
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        DrawRowBackground(drawList, min, max, selected && !multiple, hovered, active, enabled, accent ?? color);

        float contentHeight = max.Y - min.Y;
        Vector2 iconPosition = new(
            min.X + padding + ((iconColumnWidth - iconMetrics.Size.X) * 0.5f),
            min.Y + ((contentHeight - iconMetrics.Size.Y) * 0.5f));
        Vector2 labelPosition = new(
            min.X + padding + leadingWidth,
            min.Y + ((contentHeight - labelSize.Y) * 0.5f));
        float trailingRight = max.X - padding;
        Vector2 trailingPosition = new(
            trailingRight - trailingMetrics.Size.X - ((iconColumnWidth - trailingMetrics.Size.X) * 0.5f),
            min.Y + ((contentHeight - trailingMetrics.Size.Y) * 0.5f));
        float trailingWidth = choice == ChoiceMode.Single ? 0f : iconColumnWidth;
        float labelWidth = MathF.Max(1f, trailingRight - trailingWidth - gap - labelPosition.X);
        Vector2 tooltipMax = max;

        if (badges is { Count: > 0 } || trailingBadge.HasValue)
        {
            float badgeLeft = EditorBadgeRenderer.DrawRightAligned(drawList, badges, trailingBadge,
                choice == ChoiceMode.Single ? trailingRight : trailingRight - iconColumnWidth - gap,
                min.Y + (contentHeight - EditorBadgeRenderer.Height) * 0.5f,
                selected: false, contentAlpha: 1f, panelSurface: true);
            labelWidth = MathF.Max(1f, badgeLeft - gap - labelPosition.X);
            tooltipMax.X = badgeLeft - gap;
        }

        EditorTextUtility.ClippedText visibleLabel = EditorTextUtility.ClipTextToWidthResult(displayLabel, labelWidth);
        Vector4 textColor = ResolveRowColor(color, enabled);
        Vector4 iconColor = accent.HasValue ? ResolveRowColor(accent, enabled) : textColor;
        if (!accent.HasValue && !color.HasValue && enabled && (hovered || selected))
        {
            iconColor = ThemeColors.AccentBlue;
        }

        if ((!multiple || expanded.HasValue) && leadingIcon != FontAwesomeIcon.None)
        {
            EditorIcon.Draw(drawList, leadingIcon, iconMetrics, iconPosition,
                expanded.HasValue ? ThemeColors.TextDisabled : iconColor);
        }

        drawList.AddText(labelPosition, ImGui.GetColorU32(textColor), visibleLabel.Text);

        if (multiple)
        {
            Vector2 checkMin = new(trailingRight - iconColumnWidth, min.Y + (contentHeight - iconColumnWidth) * 0.5f);
            Vector2 checkMax = checkMin + new Vector2(iconColumnWidth);
            if (expanded.HasValue)
            {
                interaction = interaction with
                {
                    CheckboxActivated = interaction.Activated && (ImGui.IsKeyPressed(ImGuiKey.Space)
                        || ImGui.IsMouseReleased(ImGuiMouseButton.Left) && EditorInputUtility.IsMouseInside(checkMin, checkMax)),
                };
            }

            Vector4 checkAccent = accent ?? ThemeColors.AccentPrimary;
            drawList.AddRectFilled(checkMin, checkMax,
                ImGui.GetColorU32(selected ? checkAccent with { W = 0.16f } : ThemeColors.Style(ImGuiCol.FrameBg)), 3f * scale);
            drawList.AddRect(checkMin, checkMax,
                ImGui.GetColorU32(selected || hovered ? checkAccent : ThemeColors.TextDisabled with { W = 0.55f }), 3f * scale, ImDrawFlags.None, scale);
        }

        if (trailingIcon is { } resolvedTrailingIcon)
        {
            Vector4 trailingColor = multiple ? accent ?? ThemeColors.AccentPrimary : textColor;
            if (!multiple && color.HasValue)
            {
                trailingColor = ThemeColors.WithAlpha(textColor, textColor.W * 0.72f);
            }
            else if (!multiple && enabled)
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

        if (hovered && IntonerTooltip.IsAreaHovered(min, tooltipMax) && !string.IsNullOrEmpty(tooltip))
        {
            IntonerTooltip.DrawDescription(
                icon,
                label,
                tooltip,
                new IntonerTooltipOptions { Accent = accent ?? color ?? ThemeColors.AccentPrimary });
        }
        else
        {
            EditorTextUtility.AttachTooltipIfClipped(min, tooltipMax - min, label,
                visibleLabel.IsClipped || !string.Equals(displayLabel, label, StringComparison.Ordinal));
        }

        return interaction;
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
        private readonly ImRaii.StyleDisposable? _style;
        private readonly IntonerTooltip.StyleScope _tooltipStyle;
        private ImRaii.PopupDisposable _popup;
        private bool _disposed;

        internal PopupScope(string id, ImGuiWindowFlags flags, Vector4? tooltipAccent)
        {
            _style = tooltipAccent.HasValue ? null : PushStyle();
            _tooltipStyle = tooltipAccent is { } accent
                ? IntonerTooltip.PushStyle(new IntonerTooltipOptions { Accent = accent, ItemSpacing = new Vector2(0f, PopupItemSpacing) })
                : default;
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
            _tooltipStyle.Dispose();
            _style?.Dispose();
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
