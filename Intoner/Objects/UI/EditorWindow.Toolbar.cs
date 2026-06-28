using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Catalog;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Docking;
using Intoner.Objects.UI.Services.EdgeGlow;
using Intoner.UI;
using System.Globalization;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class EditorWindow
{
    private const string ToolbarDockPopupId = "##objectToolbarDockPopup";
    private const int ToolbarRailMaxColumns = 3;
    private const int ToolbarGroupSeparatorCount = 3;

    private enum ToolbarSurfaceMode
    {
        Strip,
        Rail,
    }

    private struct ToolbarSurfaceContext
    {
        private int _railColumn;

        public ToolbarSurfaceContext(ToolbarSurfaceMode mode, int railColumns)
        {
            Mode = mode;
            RailColumns = Math.Max(1, railColumns);
            _railColumn = 0;
        }

        public ToolbarSurfaceMode Mode { get; }

        public int RailColumns { get; }

        public void AdvanceItem()
        {
            if (Mode == ToolbarSurfaceMode.Strip)
            {
                ImGui.SameLine();
                return;
            }

            if (RailColumns <= 1)
            {
                return;
            }

            _railColumn++;
            if (_railColumn < RailColumns)
            {
                ImGui.SameLine();
                return;
            }

            _railColumn = 0;
        }

        public void ResetRailRow()
        {
            if (Mode == ToolbarSurfaceMode.Rail && RailColumns > 1 && _railColumn != 0)
            {
                ImGui.NewLine();
            }

            _railColumn = 0;
        }
    }

    private void DrawToolbarLayout(
        IReadOnlyList<ObjectSnapshot> objects,
        IReadOnlyList<ObjectSnapshot> activeObjects,
        IReadOnlySet<Guid> activeObjectIds,
        IReadOnlyList<ObjectKindInfo> kindInfos,
        ObjectCatalogData catalog,
        IReadOnlyList<ObjectLayoutSnapshot> layouts,
        Guid? defaultLayoutId,
        ObjectSnapshot? selected,
        IReadOnlyList<ObjectSnapshot> activeSelectedObjects)
    {
        EditorDockPanel toolbarPanel = new(
            "##intonerCommandSurfaceDockPanel",
            ResolveToolbarDockSlot(),
            ResolveToolbarPanelSize,
            context => DrawToolbarPanel(objects, selected, activeSelectedObjects, context));
        EditorDockShell.Draw(
            [toolbarPanel],
            () => DrawDockCenterContent(objects, activeObjects, activeObjectIds, kindInfos, catalog, layouts, defaultLayoutId));
    }

    private void DrawToolbarDockPopup()
    {
        if (_openToolbarDockPopupNextFrame)
        {
            var popupAnchor = ImGui.GetWindowPos() + new Vector2(ImGui.GetWindowSize().X - ImGui.GetStyle().WindowPadding.X, ImGui.GetFrameHeight());
            ImGui.SetNextWindowPos(popupAnchor, ImGuiCond.Always, new Vector2(1f, 0f));
            ImGui.OpenPopup(ToolbarDockPopupId);
            _openToolbarDockPopupNextFrame = false;
        }

        using var popup = ImRaii.Popup(ToolbarDockPopupId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!popup)
        {
            return;
        }

        var previousDockPosition = _toolbarDockPosition;
        DrawToolbarDockPad();
        if (_toolbarDockPosition != previousDockPosition)
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawToolbarPanel(IReadOnlyList<ObjectSnapshot> objects, ObjectSnapshot? selected, IReadOnlyList<ObjectSnapshot> activeSelectedObjects, EditorDockPanelContext context)
    {
        ToolbarSurfaceContext surface = ResolveToolbarSurfaceContext(context);
        using var railSpacing = surface.Mode == ToolbarSurfaceMode.Rail
            ? ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ResolveToolbarRailItemGap(), ResolveToolbarRailItemGap()))
            : default;
        DrawToolbarPanelCore(objects, selected, activeSelectedObjects, ref surface);
        MarkCurrentWindowAsEditorOverlayTarget();
    }

    private void DrawToolbarPanelCore(IReadOnlyList<ObjectSnapshot> objects, ObjectSnapshot? selected, IReadOnlyList<ObjectSnapshot> activeSelectedObjects, ref ToolbarSurfaceContext surface)
    {
        var countsByKind = BuildToolbarObjectCountsByKind(objects);

        DrawToolbarKindGroup(countsByKind, ref surface);
        DrawToolbarGroupSeparator(ref surface);
        DrawToolbarBoundsGroup(ref surface);
        DrawToolbarGroupSeparator(ref surface);
        DrawToolbarGizmoGroup(selected, activeSelectedObjects, ref surface);
        DrawToolbarGroupSeparator(ref surface);
        DrawToolbarRailFooterGap(surface);
        DrawToolbarHistoryGroup(ref surface);
    }

    private void DrawToolbarKindGroup(IReadOnlyDictionary<ObjectKind, int> countsByKind, ref ToolbarSurfaceContext surface)
    {
        DrawHeaderKindButton("##objectHeaderFurniture", FontAwesomeIcon.Home, "Furniture", countsByKind.GetValueOrDefault(ObjectKind.Furniture), DraftKind.Furniture, surface.Mode);
        if (_housingModePolicy.GetState().IsHousingMode)
        {
            return;
        }

        surface.AdvanceItem();

        DrawHeaderKindButton("##objectHeaderLight", FontAwesomeIcon.Sun, "Light", countsByKind.GetValueOrDefault(ObjectKind.Light), DraftKind.Light, surface.Mode);
        surface.AdvanceItem();

        DrawHeaderKindButton("##objectHeaderVfx", FontAwesomeIcon.Magic, "VFX", countsByKind.GetValueOrDefault(ObjectKind.Vfx), DraftKind.Vfx, surface.Mode);
        surface.AdvanceItem();

        DrawHeaderKindButton("##objectHeaderBgObject", FontAwesomeIcon.Cube, "BgObject", countsByKind.GetValueOrDefault(ObjectKind.BgObject), DraftKind.BgObject, surface.Mode);
    }

    private void DrawToolbarHistoryGroup(ref ToolbarSurfaceContext surface)
    {
        var canUndo = _objectHistoryManager.UndoActionKind is not null;
        var canRedo = _objectHistoryManager.RedoActionKind is not null;
        var undoAccent = canUndo ? EditorColors.AccentBlue : (Vector4?)null;
        var redoAccent = canRedo ? EditorColors.AccentPurple : (Vector4?)null;

        using (ImRaii.Disabled(!canUndo))
        {
            DrawHeaderActionButton(
                "##objectHistoryUndo",
                FontAwesomeIcon.Undo,
                "Undo",
                "Undo",
                false,
                () => _ = TryUndoHistory(),
                undoAccent,
                useAccentFill: false,
                useNeutralHoverFill: true,
                hoverBorderColor: undoAccent,
                drawTooltip: () => DrawToolbarHistoryTooltip(undo: true, undoAccent ?? EditorColors.AccentBlue),
                mode: surface.Mode);
        }

        surface.AdvanceItem();

        using (ImRaii.Disabled(!canRedo))
        {
            DrawHeaderActionButton(
                "##objectHistoryRedo",
                FontAwesomeIcon.Redo,
                "Redo",
                "Redo",
                false,
                () => _ = TryRedoHistory(),
                redoAccent,
                useAccentFill: false,
                useNeutralHoverFill: true,
                hoverBorderColor: redoAccent,
                drawTooltip: () => DrawToolbarHistoryTooltip(undo: false, redoAccent ?? EditorColors.AccentPurple),
                mode: surface.Mode);
        }
    }

    private void DrawToolbarBoundsGroup(ref ToolbarSurfaceContext surface)
    {
        DrawBoundsToolbarButton(surface.Mode);
        surface.AdvanceItem();

        DrawHeaderActionButton(
            "##objectBoundsSpace",
            GetBoundsSpaceToggleIcon(),
            GetBoundsSpaceToggleLabel(),
            "Toggle object space between world and local for bounds and gizmo transforms.",
            false,
            ToggleBoundsOverlaySpace,
            EditorColors.BoundsSpaceAccent(_gizmo.Settings.BoundsOverlaySpace),
            mode: surface.Mode);
    }

    private void DrawToolbarGizmoGroup(ObjectSnapshot? selected, IReadOnlyList<ObjectSnapshot> activeSelectedObjects, ref ToolbarSurfaceContext surface)
    {
        var scaleEnabled = Gizmo.CanUseScaleGizmo(activeSelectedObjects);
        var scaleTooltip = ResolveScaleGizmoTooltip(selected, activeSelectedObjects);

        DrawHeaderGizmoModeButton(
            "##objectGizmoMove",
            FontAwesomeIcon.ArrowsAlt,
            "Move",
            "Toggle the movement gizmo.",
            GizmoTransformMode.Translation,
            selected,
            enabled: true,
            surfaceMode: surface.Mode);
        surface.AdvanceItem();

        DrawHeaderGizmoModeButton(
            "##objectGizmoRotate",
            FontAwesomeIcon.SyncAlt,
            "Rotate",
            "Toggle the rotation gizmo.",
            GizmoTransformMode.Rotation,
            selected,
            enabled: true,
            surfaceMode: surface.Mode);
        surface.AdvanceItem();

        DrawHeaderGizmoModeButton(
            "##objectGizmoScale",
            FontAwesomeIcon.CompressArrowsAlt,
            "Scale",
            scaleTooltip,
            GizmoTransformMode.Scale,
            selected,
            enabled: activeSelectedObjects.Count == 0 || scaleEnabled,
            surfaceMode: surface.Mode);
        surface.AdvanceItem();

        DrawTransformSnapToolbarButton(surface.Mode);
        surface.AdvanceItem();

        DrawSurfaceAlignToolbarButton(surface.Mode);
    }

    private void DrawToolbarGroupSeparator(ref ToolbarSurfaceContext surface)
    {
        if (surface.Mode == ToolbarSurfaceMode.Rail)
        {
            surface.ResetRailRow();
            DrawVerticalToolbarGroupSeparator();
            return;
        }

        ImGui.SameLine();
        DrawHeaderVerticalSeparator(ResolveToolbarDockPadEdge());
        ImGui.SameLine();
    }

    private static string ResolveScaleGizmoTooltip(ObjectSnapshot? selected, IReadOnlyList<ObjectSnapshot> activeSelectedObjects)
        => activeSelectedObjects.Count > 1
            ? "Scale gizmo is only available for one selected bgobject or furniture object."
            : selected is not null && !Gizmo.CanUseScaleGizmo(selected)
                ? "Scale gizmo is not available for lights."
                : "Toggle the scale gizmo.";

    private static Dictionary<ObjectKind, int> BuildToolbarObjectCountsByKind(IReadOnlyList<ObjectSnapshot> objects)
    {
        var countsByKind = new Dictionary<ObjectKind, int>();
        foreach (var entry in objects)
        {
            countsByKind.TryGetValue(entry.Kind, out var count);
            countsByKind[entry.Kind] = count + 1;
        }

        return countsByKind;
    }

    private static Vector2 MeasureToolbarIcon(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            return ImGui.CalcTextSize(icon.ToIconString());
        }
    }

    private static float ResolveToolbarDockPadEdge()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var iconSize = MeasureToolbarIcon(FontAwesomeIcon.Cube);
        var labelHeight = ImGui.GetTextLineHeight();
        var verticalGap = 5f * scale;
        var verticalPadding = 9f * scale;
        var minWidth = MathF.Max(iconSize.X + (16f * scale), labelHeight + (16f * scale));
        var contentHeight = iconSize.Y + verticalGap + labelHeight;
        return MathF.Max(
            60f * scale,
            MathF.Ceiling(MathF.Max(minWidth, contentHeight + (verticalPadding * 2f))));
    }

    private EditorDockSlot ResolveToolbarDockSlot()
        => _toolbarDockPosition switch
        {
            ToolbarDockPosition.Top    => EditorDockSlot.Top,
            ToolbarDockPosition.Right  => EditorDockSlot.Right,
            ToolbarDockPosition.Bottom => EditorDockSlot.Bottom,
            ToolbarDockPosition.Left   => EditorDockSlot.Left,
            _                          => EditorDockSlot.Top,
        };

    private static ToolbarSurfaceMode ResolveToolbarSurfaceMode(EditorDockSlot slot)
        => slot is EditorDockSlot.Left or EditorDockSlot.Right
            ? ToolbarSurfaceMode.Rail
            : ToolbarSurfaceMode.Strip;

    private ToolbarSurfaceContext ResolveToolbarSurfaceContext(EditorDockPanelContext context)
    {
        ToolbarSurfaceMode mode = ResolveToolbarSurfaceMode(context.Slot);
        return new ToolbarSurfaceContext(
            mode,
            mode == ToolbarSurfaceMode.Rail
                ? ResolveToolbarRailColumnCount(context.AvailableSize.Y)
                : 1);
    }

    private Vector2 ResolveToolbarPanelSize(EditorDockPanelContext context)
    {
        ToolbarSurfaceContext surface = ResolveToolbarSurfaceContext(context);
        return surface.Mode == ToolbarSurfaceMode.Rail
            ? new Vector2(ResolveToolbarRailSlotWidth(surface.RailColumns), 0f)
            : new Vector2(0f, ResolveHorizontalToolbarRowHeight());
    }

    private static float ResolveToolbarButtonEdge(ToolbarSurfaceMode mode)
        => mode == ToolbarSurfaceMode.Rail
            ? 42f * ImGuiHelpers.GlobalScale
            : ResolveToolbarDockPadEdge();

    private static float ResolveToolbarRailSlotWidth(int columns)
    {
        var columnCount = Math.Max(1, columns);
        return (ResolveToolbarButtonEdge(ToolbarSurfaceMode.Rail) * columnCount)
            + (ResolveToolbarRailItemGap() * Math.Max(0, columnCount - 1));
    }

    private int ResolveToolbarRailColumnCount(float availableHeight)
    {
        var buttonStride = ResolveToolbarButtonEdge(ToolbarSurfaceMode.Rail) + ResolveToolbarRailItemGap();
        var separatorHeight = ResolveToolbarRailSeparatorHeight() * ToolbarGroupSeparatorCount;
        var usableHeight = MathF.Max(buttonStride, availableHeight - separatorHeight);
        var visibleRows = Math.Max(1, (int)MathF.Floor(usableHeight / buttonStride));
        var requiredColumns = (int)MathF.Ceiling(ResolveToolbarButtonCount() / (float)visibleRows);
        return Math.Clamp(requiredColumns, 1, ToolbarRailMaxColumns);
    }

    private int ResolveToolbarButtonCount()
        => (_housingModePolicy.GetState().IsHousingMode ? 1 : 4)
           + 2
           + 5
           + 2;

    private static float ResolveToolbarRailItemGap()
        => MathF.Max(ImGui.GetStyle().ItemSpacing.Y, 6f * ImGuiHelpers.GlobalScale);

    private static void DrawToolbarRailFooterGap(ToolbarSurfaceContext surface)
    {
        if (surface.Mode != ToolbarSurfaceMode.Rail)
        {
            return;
        }

        float footerHeight = ResolveToolbarRailHistoryHeight(surface);
        float targetY = ImGui.GetWindowContentRegionMax().Y - footerHeight;
        float currentY = ImGui.GetCursorPosY();
        if (targetY > currentY)
        {
            ImGui.SetCursorPosY(targetY);
        }
    }

    private static float ResolveToolbarRailHistoryHeight(ToolbarSurfaceContext surface)
    {
        float buttonEdge = ResolveToolbarButtonEdge(ToolbarSurfaceMode.Rail);
        return surface.RailColumns > 1
            ? buttonEdge
            : (buttonEdge * 2f) + ResolveToolbarRailItemGap();
    }

    private static float ResolveToolbarIconVerticalBias()
        => 2f * ImGuiHelpers.GlobalScale;

    private static float ResolveToolbarLabelVerticalBias()
        => 6f * ImGuiHelpers.GlobalScale;

    private static void DrawToolbarTooltip(Vector4 accent, Action draw)
        => UiSharedService.DrawAccentTooltip(
            draw,
            accent,
            new Vector2(12f, 10f),
            new Vector2(8f, 6f));

    private static void DrawToolbarTextTooltip(string title, string text, Vector4 accent)
        => DrawToolbarTooltip(accent, () =>
        {
            ImGui.TextColored(accent, title);
            DrawToolbarTooltipTextLines(text);
        });

    private static void DrawToolbarTooltipTextLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var wrap = ImRaiiScope.TextWrapPos(ImGui.GetFontSize() * 34f);
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                ImGui.TextDisabled(line);
            }
        }
    }

    private readonly record struct ToolbarButtonContentMetrics(Vector2 IconSize, Vector2 LabelSize, float Height);

    private static ToolbarButtonContentMetrics ResolveToolbarButtonContentMetrics(FontAwesomeIcon icon, string label, ToolbarSurfaceMode mode)
    {
        Vector2 iconSize = MeasureToolbarIcon(icon);
        Vector2 labelSize = mode == ToolbarSurfaceMode.Strip
            ? ImGui.CalcTextSize(label)
            : Vector2.Zero;
        float height = mode == ToolbarSurfaceMode.Strip
            ? iconSize.Y + (5f * ImGuiHelpers.GlobalScale) + labelSize.Y
            : iconSize.Y;
        return new ToolbarButtonContentMetrics(iconSize, labelSize, height);
    }

    private static void DrawToolbarButtonContent(
        ImDrawListPtr drawList,
        FontAwesomeIcon icon,
        string label,
        ToolbarSurfaceMode mode,
        Vector2 min,
        Vector2 size,
        ToolbarButtonContentMetrics metrics,
        Vector4 color)
    {
        float centerX = min.X + (size.X * 0.5f);
        float contentStartY = min.Y + ((size.Y - metrics.Height) * 0.5f);
        string iconText = icon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            Vector2 iconPosition = mode == ToolbarSurfaceMode.Rail
                ? ResolveCenteredSquareIconPosition(iconText, min, size)
                : new Vector2(centerX - (metrics.IconSize.X * 0.5f), contentStartY + ResolveToolbarIconVerticalBias());
            drawList.AddText(iconPosition, ImGui.GetColorU32(color), iconText);
        }

        if (mode != ToolbarSurfaceMode.Strip)
        {
            return;
        }

        drawList.AddText(
            new Vector2(
                centerX - (metrics.LabelSize.X * 0.5f),
                contentStartY + metrics.IconSize.Y + (5f * ImGuiHelpers.GlobalScale) + ResolveToolbarLabelVerticalBias()),
            ImGui.GetColorU32(color),
            label);
    }

    private static void DrawToolbarButtonBorder(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        Vector4 accent,
        bool hovered,
        bool active)
    {
        Vector4 borderColor = accent with
        {
            W = active ? 1f : hovered ? 0.95f : 0.82f,
        };
        float borderThickness = MathF.Max(1.25f * ImGuiHelpers.GlobalScale, 1f);
        float borderInset = MathF.Max(borderThickness * 0.5f, 0.5f);
        Vector2 borderOffset = new(borderInset, borderInset);
        drawList.AddRect(
            min + borderOffset,
            max - borderOffset,
            ImGui.GetColorU32(borderColor),
            ImGui.GetStyle().FrameRounding,
            ImDrawFlags.None,
            borderThickness);
    }

    private static EdgeGlowStyle CreateSelectedKindButtonEdgeGlowStyle()
        => new EdgeGlowStyle
        {
            Mode = EdgeGlowMode.FullBorder,
            ColorVariant = EdgeGlowColorVariant.Colorful,
            Theme = EdgeGlowTheme.Dark,
            BorderInset = 0.35f,
            BorderWidth = 2.25f,
            Duration = 3.35f,
            Strength = 0.98f,
            Brightness = 1.25f,
            Saturation = 1.18f,
            HueRange = 16f,
            StrokeOpacity = 0.74f,
            InnerOpacity = 0.38f,
            BloomOpacity = 0.82f,
            InnerShadowAlpha = 0.08f,
            RenderScale = 1f,
            FullBorderInnerReachScale = 1.65f,
            FullBorderSweepScale = 0.58f,
            ClipToRect = true,
            ClipPadding = 12f,
        };

    private void DrawHeaderKindButton(string id, FontAwesomeIcon icon, string label, int count, DraftKind kind, ToolbarSurfaceMode mode)
    {
        var buttonEdge = ResolveToolbarButtonEdge(mode);
        var buttonSize = new Vector2(buttonEdge, buttonEdge);
        var selected = _draftKind == kind;
        var accentColor = EditorColors.AccentPurple;
        var drawColor = selected ? accentColor : EditorColors.Text;
        var neutralButtonColor = ImGui.GetColorU32(ImGuiCol.Button);
        ToolbarButtonContentMetrics contentMetrics = ResolveToolbarButtonContentMetrics(icon, label, mode);

        using var hoveredButton = ImRaii.PushColor(ImGuiCol.ButtonHovered, neutralButtonColor);
        using var activeButton = ImRaii.PushColor(ImGuiCol.ButtonActive, neutralButtonColor);

        if (ImGui.Button(id, buttonSize))
        {
            _draftKind = kind;
            selected = true;
        }

        if (selected)
        {
            _edgeGlowRenderer.DrawAroundLastItem(
                CreateSelectedKindButtonEdgeGlowStyle());
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var itemHovered = ImGui.IsItemHovered();
        var itemActive = ImGui.IsItemActive();

        if (selected)
        {
            DrawToolbarButtonBorder(drawList, min, max, accentColor, itemHovered, itemActive);
        }

        if (mode == ToolbarSurfaceMode.Rail)
        {
            DrawToolbarRailCountBadge(drawList, max, count, accentColor);
        }

        DrawToolbarButtonContent(drawList, icon, label, mode, min, buttonSize, contentMetrics, drawColor);

        if (itemHovered)
        {
            var countLabel = count == 1 ? "1 placed" : $"{count} placed";
            DrawToolbarTextTooltip(label, countLabel, accentColor);
        }
    }

    private static void DrawToolbarRailCountBadge(ImDrawListPtr drawList, Vector2 max, int count, Vector4 accentColor)
    {
        if (count <= 0)
        {
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var label = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        var textSize = ImGui.CalcTextSize(label);
        var padding = new Vector2(4f * scale, 1.5f * scale);
        var badgeSize = new Vector2(
            MathF.Max(14f * scale, textSize.X + (padding.X * 2f)),
            MathF.Max(13f * scale, textSize.Y + (padding.Y * 2f)));
        var badgeMax = max - new Vector2(3f * scale, 3f * scale);
        var badgeMin = badgeMax - badgeSize;

        drawList.AddRectFilled(
            badgeMin,
            badgeMax,
            ImGui.GetColorU32(EditorColors.WithAlpha(accentColor, 0.88f)),
            badgeSize.Y * 0.5f);
        drawList.AddText(
            badgeMin + ((badgeSize - textSize) * 0.5f),
            ImGui.GetColorU32(EditorColors.Color(1f, 1f, 1f, 0.96f)),
            label);
    }

    private void DrawHeaderActionButton(
        string id,
        FontAwesomeIcon icon,
        string label,
        string tooltip,
        bool selected,
        Action onClick,
        Vector4? accentColor = null,
        bool useAccentFill = true,
        bool useNeutralHoverFill = false,
        Vector4? hoverBorderColor = null,
        Action<ImDrawListPtr, Vector2, Vector2, bool, bool>? drawBackground = null,
        Action? drawTooltip = null,
        ToolbarSurfaceMode mode = ToolbarSurfaceMode.Strip)
    {
        var buttonEdge = ResolveToolbarButtonEdge(mode);
        var buttonSize = new Vector2(buttonEdge, buttonEdge);
        var drawColor = accentColor ?? EditorColors.Text;
        var neutralButtonColor = ImGui.GetColorU32(ImGuiCol.Button);
        ToolbarButtonContentMetrics contentMetrics = ResolveToolbarButtonContentMetrics(icon, label, mode);

        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;
        using var hoveredButton = accentColor.HasValue && useAccentFill
            ? ImRaii.PushColor(ImGuiCol.ButtonHovered, accentColor.Value with { W = 0.25f })
            : useNeutralHoverFill
                ? ImRaii.PushColor(ImGuiCol.ButtonHovered, neutralButtonColor)
                : default;
        using var activeButton = accentColor.HasValue && useAccentFill
            ? ImRaii.PushColor(ImGuiCol.ButtonActive, accentColor.Value with { W = 0.35f })
            : useNeutralHoverFill
                ? ImRaii.PushColor(ImGuiCol.ButtonActive, neutralButtonColor)
                : default;

        if (ImGui.Button(id, buttonSize))
        {
            onClick();
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var itemHovered = ImGui.IsItemHovered();
        var itemActive = ImGui.IsItemActive();

        drawBackground?.Invoke(drawList, min, max, itemHovered, itemActive);

        var borderAccentColor = accentColor ?? (itemHovered ? hoverBorderColor : null);
        if (borderAccentColor.HasValue)
        {
            DrawToolbarButtonBorder(drawList, min, max, borderAccentColor.Value, itemHovered, itemActive);
        }

        DrawToolbarButtonContent(drawList, icon, label, mode, min, buttonSize, contentMetrics, drawColor);

        if (itemHovered)
        {
            if (drawTooltip is not null)
            {
                drawTooltip();
            }
            else if (!string.IsNullOrWhiteSpace(tooltip))
            {
                DrawToolbarTextTooltip(label, tooltip, accentColor ?? hoverBorderColor ?? EditorColors.AccentPurple);
            }
            else if (mode == ToolbarSurfaceMode.Rail)
            {
                DrawToolbarTextTooltip(label, string.Empty, accentColor ?? hoverBorderColor ?? EditorColors.AccentPurple);
            }
        }
    }

    private static void DrawHeaderVerticalSeparator(float height)
    {
        var separatorWidth = 10f * ImGuiHelpers.GlobalScale;
        var topInset = 6f * ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var x = start.X + (separatorWidth * 0.5f);
        drawList.AddLine(
            new Vector2(x, start.Y + topInset),
            new Vector2(x, start.Y + height - topInset),
            ImGui.GetColorU32(ImGuiCol.Separator));
        ImGui.Dummy(new Vector2(separatorWidth, height));
    }

    private void DrawVerticalToolbarGroupSeparator()
    {
        var separatorHeight = ResolveToolbarRailSeparatorHeight();
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = MathF.Max(0f, ImGui.GetContentRegionAvail().X);
        var color = ImGui.GetColorU32(ImGuiCol.Separator);
        var thickness = ResolveDividerThickness();
        var y = start.Y + (separatorHeight * 0.5f);
        drawList.AddLine(
            new Vector2(start.X, y),
            new Vector2(start.X + width, y),
            color,
            thickness);
        ImGui.Dummy(new Vector2(0f, separatorHeight));
    }

    private static float ResolveToolbarRailSeparatorHeight()
        => 10f * ImGuiHelpers.GlobalScale;

    private void DrawToolbarDockPad()
    {
        var buttonEdge = ResolveToolbarDockPadEdge();
        var tileSize = new Vector2(buttonEdge, buttonEdge);
        var miniEdge = MathF.Max(16f * ImGuiHelpers.GlobalScale, buttonEdge * 0.28f);
        var miniSize = new Vector2(miniEdge, miniEdge);
        var spacing = MathF.Max(3f * ImGuiHelpers.GlobalScale, 2f);
        var center = ((buttonEdge - miniEdge) * 0.5f);
        var endCursorPos = ImGui.GetCursorPos() + tileSize;

        using (ImRaii.Group())
        {
            var origin = ImGui.GetCursorPos();
            var originScreen = ImGui.GetCursorScreenPos();
            ImGui.Dummy(tileSize);

            DrawToolbarDockPadButton("##objectToolbarDockUp", FontAwesomeIcon.ArrowUp, new Vector2(origin.X + center, origin.Y), miniSize, ToolbarDockPosition.Top, "Dock toolbar to the top");
            DrawToolbarDockPadButton("##objectToolbarDockLeft", FontAwesomeIcon.ArrowLeft, new Vector2(origin.X, origin.Y + center), miniSize, ToolbarDockPosition.Left, "Dock toolbar to the left");
            DrawToolbarDockPadButton("##objectToolbarDockRight", FontAwesomeIcon.ArrowRight, new Vector2(origin.X + buttonEdge - miniEdge, origin.Y + center), miniSize, ToolbarDockPosition.Right, "Dock toolbar to the right");
            DrawToolbarDockPadButton("##objectToolbarDockDown", FontAwesomeIcon.ArrowDown, new Vector2(origin.X + center, origin.Y + buttonEdge - miniEdge), miniSize, ToolbarDockPosition.Bottom, "Dock toolbar to the bottom");

            var drawList = ImGui.GetWindowDrawList();
            var centerPoint = originScreen + new Vector2(center + (miniEdge * 0.5f), center + (miniEdge * 0.5f));
            var lineColor = EditorColors.WithAlpha(EditorColors.AccentPurple, 0.45f);
            var lineThickness = MathF.Max(1f * ImGuiHelpers.GlobalScale, 1f);
            drawList.AddLine(
                new Vector2(centerPoint.X, centerPoint.Y - ((miniEdge * 0.5f) + spacing)),
                new Vector2(centerPoint.X, centerPoint.Y + ((miniEdge * 0.5f) + spacing)),
                ImGui.GetColorU32(lineColor),
                lineThickness);
            drawList.AddLine(
                new Vector2(centerPoint.X - ((miniEdge * 0.5f) + spacing), centerPoint.Y),
                new Vector2(centerPoint.X + ((miniEdge * 0.5f) + spacing), centerPoint.Y),
                ImGui.GetColorU32(lineColor),
                lineThickness);

            ImGui.SetCursorPos(endCursorPos);
        }
    }

    private void DrawToolbarDockPadButton(
        string id,
        FontAwesomeIcon icon,
        Vector2 cursorPos,
        Vector2 size,
        ToolbarDockPosition dockPosition,
        string tooltip)
    {
        ImGui.SetCursorPos(cursorPos);

        var selected = _toolbarDockPosition == dockPosition;
        var accentColor = EditorColors.AccentPurple;
        using var button = selected
            ? ImRaii.PushColor(ImGuiCol.Button, accentColor with { W = 0.24f })
            : default;
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, accentColor with { W = selected ? 0.30f : 0.18f });
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, accentColor with { W = 0.36f });
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f * ImGuiHelpers.GlobalScale);

        if (ImGui.Button(id, size))
        {
            _toolbarDockPosition = dockPosition;
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var iconText = icon.ToIconString();
        var iconColor = selected ? accentColor : EditorColors.Text;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            drawList.AddText(
                ResolveCenteredSquareIconPosition(iconText, min, size),
                ImGui.GetColorU32(iconColor),
                iconText);
        }

        if (selected || ImGui.IsItemHovered())
        {
            var borderColor = accentColor with { W = selected ? 0.95f : 0.75f };
            drawList.AddRect(
                min,
                max,
                ImGui.GetColorU32(borderColor),
                5f * ImGuiHelpers.GlobalScale,
                ImDrawFlags.None,
                MathF.Max(1f * ImGuiHelpers.GlobalScale, 1f));
        }

        if (ImGui.IsItemHovered())
        {
            DrawToolbarTextTooltip("Toolbar Dock", tooltip, accentColor);
        }
    }

    private static float ResolveHorizontalToolbarRowHeight()
        => ResolveToolbarDockPadEdge();

    private FontAwesomeIcon GetBoundsSpaceToggleIcon()
        => _gizmo.Settings.BoundsOverlaySpace == BoundsOverlaySpace.World
            ? FontAwesomeIcon.Globe
            : FontAwesomeIcon.Cube;

    private string GetBoundsSpaceToggleLabel()
        => _gizmo.Settings.BoundsOverlaySpace == BoundsOverlaySpace.World
            ? "World"
            : "Local";

    private void ToggleBoundsOverlaySpace()
        => _gizmo.Settings.BoundsOverlaySpace = _gizmo.Settings.BoundsOverlaySpace == BoundsOverlaySpace.World
            ? BoundsOverlaySpace.Local
            : BoundsOverlaySpace.World;

    private void DrawHeaderGizmoModeButton(
        string id,
        FontAwesomeIcon icon,
        string label,
        string tooltip,
        GizmoTransformMode mode,
        ObjectSnapshot? selected,
        bool enabled,
        ToolbarSurfaceMode surfaceMode = ToolbarSurfaceMode.Strip)
    {
        var accentColor = GetGizmoModeAccentColor(mode);
        var isActive = _gizmo.Settings.Mode == mode;
        Vector4? buttonAccentColor = enabled && isActive ? accentColor : null;
        Vector4? buttonHoverBorderColor = enabled ? accentColor : null;

        using (ImRaii.Disabled(!enabled))
        {
            DrawHeaderActionButton(
                id,
                icon,
                label,
                selected is null
                    ? $"{tooltip}\nSelect a placed object to use the gizmo"
                    : tooltip,
                false,
                () => ToggleGizmoMode(mode),
                buttonAccentColor,
                useAccentFill: false,
                useNeutralHoverFill: true,
                hoverBorderColor: buttonHoverBorderColor,
                mode: surfaceMode);
        }
    }

    private static Vector4 GetGizmoModeAccentColor(GizmoTransformMode mode)
        => EditorColors.TransformModeAccent(mode);

    private void ToggleGizmoMode(GizmoTransformMode mode)
        => _gizmo.Settings.Mode = _gizmo.Settings.Mode == mode
            ? GizmoTransformMode.None
            : mode;
}
