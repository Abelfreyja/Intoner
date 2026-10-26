using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Docking;
using Intoner.Objects.UI.Services.EdgeGlow;
using Intoner.Scene;
using Intoner.Services.Configuration;
using Intoner.Services.Input;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

internal sealed partial class EditorToolbar
{
    private readonly ISceneHistoryManager         _sceneHistoryManager;
    private readonly IObjectHousingModePolicy     _housingModePolicy;
    private readonly IIntonerConfigurationService _configurationService;
    private readonly IShortcutService             _shortcuts;
    private readonly Gizmo                        _gizmo;
    private readonly HistoryWorkspace             _history;
    private readonly EditorOverlayLayer           _editorOverlayLayer;
    private readonly EdgeGlowRenderer             _edgeGlowRenderer;
    private readonly CreateBrowserSelection       _browserSelection;

    private IReadOnlyList<ObjectSnapshot>? _countedObjects;
    private readonly Dictionary<ObjectKind, int> _objectCountsByKind = [];

    private readonly Action _toggleTransformSpace;
    private readonly Action _drawTransformSpaceTooltip;
    private readonly Action _drawScaleTooltip;
    private ToolbarDockPosition _toolbarDockPosition = ToolbarDockPosition.Top;
    private bool _openToolbarDockPopupNextFrame;
    private const string ToolbarDockPopupId = "##objectToolbarDockPopup";
    private const int ToolbarRailMaxColumns = 3;
    private const int ToolbarGroupSeparatorCount = 3;
    private const int ToolbarBoundsButtonCount = 2;
    private const int ToolbarGizmoButtonCount = 5;
    private const int ToolbarHistoryButtonCount = 2;
    private static readonly IReadOnlyList<IntonerTooltipKeyHint> GizmoModeKeyHints =
    [
        new(KeyboardModifiers.Shift, "and click to toggle independently"),
    ];

    public EditorToolbar(
        ISceneHistoryManager sceneHistoryManager,
        IObjectHousingModePolicy housingModePolicy,
        IIntonerConfigurationService configurationService,
        IShortcutService shortcuts,
        Gizmo gizmo,
        HistoryWorkspace history,
        EditorOverlayLayer editorOverlayLayer,
        EdgeGlowRenderer edgeGlowRenderer,
        CreateBrowserSelection browserSelection)
    {
        _sceneHistoryManager  = sceneHistoryManager;
        _housingModePolicy    = housingModePolicy;
        _configurationService = configurationService;
        _shortcuts            = shortcuts;
        _toolbarDockPosition  = configurationService.Current.Ui.ToolbarPosition;
        _gizmo                = gizmo;
        _history              = history;
        _editorOverlayLayer   = editorOverlayLayer;
        _edgeGlowRenderer     = edgeGlowRenderer;
        _browserSelection     = browserSelection;

        _toggleTransformSpace      = gizmo.ToggleTransformSpace;
        _drawTransformSpaceTooltip = DrawTransformSpaceTooltip;
        _drawScaleTooltip          = DrawScaleToolbarTooltip;
    }

    public void OpenDockMenu()
        => _openToolbarDockPopupNextFrame = true;

    internal enum ToolbarSurfaceMode
    {
        Strip,
        Rail,
    }

    [StructLayout(LayoutKind.Auto)]
    internal struct ToolbarSurfaceContext
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
                ImGui.SameLine(0f, ResolveToolbarRailItemGap());
                return;
            }

            _railColumn = 0;
        }

        public void ResetRailRow()
        {
            if (Mode == ToolbarSurfaceMode.Rail && RailColumns > 1 && _railColumn != 0)
            {
                using var spacing = PushToolbarItemSpacing(Mode);
                ImGui.NewLine();
            }

            _railColumn = 0;
        }
    }

    internal void DrawToolbarDockPopup()
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

    internal void DrawToolbarPanel(IReadOnlyList<ObjectSnapshot> objects, SceneItemSnapshot? selected, IReadOnlyList<SceneItemSnapshot> activeSelectedItems, EditorDockPanelContext context)
    {
        ToolbarSurfaceContext surface = ResolveToolbarSurfaceContext(context);
        DrawToolbarPanelCore(objects, selected, activeSelectedItems, ref surface);
        _editorOverlayLayer.CaptureCurrentWindow();
    }

    private void DrawToolbarPanelCore(IReadOnlyList<ObjectSnapshot> objects, SceneItemSnapshot? selected, IReadOnlyList<SceneItemSnapshot> activeSelectedItems, ref ToolbarSurfaceContext surface)
    {
        var countsByKind = GetObjectCountsByKind(objects);

        DrawToolbarKindGroup(countsByKind, ref surface);
        DrawToolbarGroupSeparator(ref surface);
        DrawToolbarBoundsGroup(ref surface);
        DrawToolbarGroupSeparator(ref surface);
        DrawToolbarGizmoGroup(selected, activeSelectedItems, ref surface);
        DrawToolbarGroupSeparator(ref surface);
        DrawToolbarHistoryGap(surface);
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
        SceneHistorySnapshot snapshot = _sceneHistoryManager.GetSnapshot();
        var canUndo = snapshot.UndoActionKind is not null && snapshot.Undo.IsSuccess;
        var canRedo = snapshot.RedoActionKind is not null && snapshot.Redo.IsSuccess;
        var undoAccent = canUndo ? ThemeColors.AccentBlue : (Vector4?)null;
        var redoAccent = canRedo ? ThemeColors.AccentPrimary : (Vector4?)null;

        using (ImRaii.Disabled(!canUndo))
        {
            DrawHeaderActionButton(
                "##objectHistoryUndo",
                FontAwesomeIcon.Undo,
                "Undo",
                "Undo",
                false,
                () => _ = _history.TryUndoHistory(),
                undoAccent,
                useNeutralHoverFill: true,
                hoverBorderColor: undoAccent,
                drawTooltip: () => _history.DrawToolbarHistoryTooltip(undo: true, undoAccent ?? ThemeColors.AccentBlue),
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
                () => _ = _history.TryRedoHistory(),
                redoAccent,
                useNeutralHoverFill: true,
                hoverBorderColor: redoAccent,
                drawTooltip: () => _history.DrawToolbarHistoryTooltip(undo: false, redoAccent ?? ThemeColors.AccentPrimary),
                mode: surface.Mode);
        }
    }

    private void DrawToolbarBoundsGroup(ref ToolbarSurfaceContext surface)
    {
        DrawBoundsToolbarButton(surface.Mode);
        surface.AdvanceItem();

        using (ImRaii.Disabled(!_gizmo.CanToggleTransformSpace))
        {
            DrawHeaderActionButton(
                "##objectBoundsSpace",
                GetBoundsSpaceToggleIcon(),
                GetBoundsSpaceToggleLabel(),
                string.Empty,
                false,
                _toggleTransformSpace,
                EditorColors.BoundsSpaceAccent(_gizmo.Settings.BoundsOverlaySpace),
                drawTooltip: _drawTransformSpaceTooltip,
                mode: surface.Mode);
        }
    }

    private void DrawToolbarGizmoGroup(SceneItemSnapshot? selected, IReadOnlyList<SceneItemSnapshot> activeSelectedItems, ref ToolbarSurfaceContext surface)
    {
        var scaleEnabled = _gizmo.CanUseScaleGizmo(activeSelectedItems);
        _scaleUniformRequired = selected is not null && GizmoScaling.RequiresUniform(selected, activeSelectedItems.Count);
        _scaleUnavailableReason = selected switch
        {
            null => "Select a placed item to use the gizmo",
            _ when activeSelectedItems.Count > 0 && !scaleEnabled => "The selection contains an item that does not support scaling.",
            _ => string.Empty,
        };

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
            string.Empty,
            GizmoTransformMode.Scale,
            selected,
            enabled: activeSelectedItems.Count == 0 || scaleEnabled,
            surfaceMode: surface.Mode,
            drawTooltip: _drawScaleTooltip);
        DrawScaleOptionsPopup();
        surface.AdvanceItem();

        DrawTransformSnapToolbarButton(surface.Mode);
        surface.AdvanceItem();

        DrawSurfaceAlignToolbarButton(surface.Mode);
    }

    private static void DrawToolbarGroupSeparator(ref ToolbarSurfaceContext surface)
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

    private IReadOnlyDictionary<ObjectKind, int> GetObjectCountsByKind(IReadOnlyList<ObjectSnapshot> objects)
    {
        if (ReferenceEquals(_countedObjects, objects))
        {
            return _objectCountsByKind;
        }

        _objectCountsByKind.Clear();
        foreach (ObjectSnapshot entry in objects)
        {
            _objectCountsByKind.TryGetValue(entry.Kind, out int count);
            _objectCountsByKind[entry.Kind] = count + 1;
        }

        _countedObjects = objects;
        return _objectCountsByKind;
    }

    private static float ResolveToolbarDockPadEdge()
    {
        float scale = ImGuiHelpers.GlobalScale;
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(FontAwesomeIcon.Cube);
        float labelHeight = ImGui.GetTextLineHeight();
        float verticalGap = 5f * scale;
        float verticalPadding = 9f * scale;
        float minWidth = MathF.Max(iconMetrics.Size.X + (16f * scale), labelHeight + (16f * scale));
        float contentHeight = iconMetrics.Size.Y + verticalGap + labelHeight;
        return MathF.Max(
            60f * scale,
            MathF.Ceiling(MathF.Max(minWidth, contentHeight + (verticalPadding * 2f))));
    }

    internal EditorDockSlot ResolveToolbarDockSlot()
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

    internal Vector2 ResolveToolbarPanelSize(EditorDockPanelContext context)
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
        for (int columns = 1; columns < ToolbarRailMaxColumns; ++columns)
        {
            if (ResolveToolbarRailRequiredHeight(columns) <= availableHeight)
            {
                return columns;
            }
        }

        return ToolbarRailMaxColumns;
    }

    private float ResolveToolbarRailRequiredHeight(int columns)
    {
        int kindButtonCount = _housingModePolicy.GetState().IsHousingMode ? 1 : 4;
        int bodyRows = ResolveToolbarRailRowCount(kindButtonCount, columns)
                     + ResolveToolbarRailRowCount(ToolbarBoundsButtonCount, columns)
                     + ResolveToolbarRailRowCount(ToolbarGizmoButtonCount, columns);
        float itemGap = ResolveToolbarRailItemGap();
        float bodyHeight = bodyRows * (ResolveToolbarButtonEdge(ToolbarSurfaceMode.Rail) + itemGap);
        float separatorHeight = ToolbarGroupSeparatorCount * (ResolveToolbarRailSeparatorHeight() + itemGap);
        return bodyHeight + separatorHeight + ResolveToolbarRailHistoryHeight(columns);
    }

    private static int ResolveToolbarRailRowCount(int buttonCount, int columns)
    {
        int columnCount = Math.Max(1, columns);
        return (buttonCount + columnCount - 1) / columnCount;
    }

    private static float ResolveToolbarRailItemGap()
        => MathF.Max(ImGui.GetStyle().ItemSpacing.Y, 6f * ImGuiHelpers.GlobalScale);

    private static void DrawToolbarHistoryGap(ToolbarSurfaceContext surface)
    {
        if (surface.Mode == ToolbarSurfaceMode.Strip)
        {
            float historyWidth = (ResolveToolbarButtonEdge(surface.Mode) * ToolbarHistoryButtonCount)
                               + (ImGui.GetStyle().ItemSpacing.X * (ToolbarHistoryButtonCount - 1));
            float availableWidth = ImGui.GetContentRegionAvail().X;
            if (availableWidth > historyWidth)
            {
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availableWidth - historyWidth);
            }

            return;
        }

        float footerHeight = ResolveToolbarRailHistoryHeight(surface.RailColumns);
        float targetY = ImGui.GetWindowContentRegionMax().Y - footerHeight;
        float currentY = ImGui.GetCursorPosY();
        if (targetY > currentY)
        {
            ImGui.SetCursorPosY(targetY);
        }
    }

    private static float ResolveToolbarRailHistoryHeight(int columns)
    {
        float buttonEdge = ResolveToolbarButtonEdge(ToolbarSurfaceMode.Rail);
        int rows = ResolveToolbarRailRowCount(ToolbarHistoryButtonCount, columns);
        return (buttonEdge * rows) + (ResolveToolbarRailItemGap() * Math.Max(0, rows - 1));
    }

    private static void DrawToolbarTextTooltip(
        FontAwesomeIcon icon,
        string title,
        string text,
        Vector4 accent,
        IReadOnlyList<IntonerTooltipKeyHint> keyHints)
        => EditorToolbarTooltip.Draw(icon, title, accent, [], [], keyHints, text);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct ToolbarButtonContentMetrics(
        EditorIcon.Metrics Icon,
        Vector2 LabelSize,
        float Height);

    private static ToolbarButtonContentMetrics ResolveToolbarButtonContentMetrics(FontAwesomeIcon icon, string label, ToolbarSurfaceMode mode)
    {
        EditorIcon.Metrics iconMetrics = EditorIcon.Measure(icon);
        Vector2 labelSize = mode == ToolbarSurfaceMode.Strip
            ? ImGui.CalcTextSize(label)
            : Vector2.Zero;
        float height = mode == ToolbarSurfaceMode.Strip
            ? iconMetrics.Size.Y + (5f * ImGuiHelpers.GlobalScale) + labelSize.Y
            : iconMetrics.Size.Y;
        return new ToolbarButtonContentMetrics(iconMetrics, labelSize, height);
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
        if (mode == ToolbarSurfaceMode.Rail)
        {
            EditorIcon.DrawCentered(drawList, icon, min, min + size, color);
        }
        else
        {
            Vector2 iconPosition = new(
                centerX - (metrics.Icon.Size.X * 0.5f),
                contentStartY);
            EditorIcon.Draw(drawList, icon, metrics.Icon, iconPosition, color);
        }

        if (mode != ToolbarSurfaceMode.Strip)
        {
            return;
        }

        drawList.AddText(
            new Vector2(
                centerX - (metrics.LabelSize.X * 0.5f),
                contentStartY + metrics.Icon.Size.Y + (5f * ImGuiHelpers.GlobalScale)),
            ImGui.GetColorU32(color),
            label);
    }

    private static void DrawToolbarStateBands(ImDrawListPtr drawList, Vector2 min, Vector2 max,
        ReadOnlySpan<(Vector4 Accent, bool Enabled)> bands, float peakAlpha)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float inset = 3f * scale;
        Vector2 innerMin = min + new Vector2(inset);
        Vector2 innerMax = max - new Vector2(inset);
        float bandGap = MathF.Max(1f * scale, 1f);
        float bandWidth = (innerMax.X - innerMin.X - bandGap * (bands.Length - 1)) / bands.Length;

        if (bandWidth <= 0f)
        {
            return;
        }

        float left = innerMin.X;
        for (int index = 0; index < bands.Length; ++index)
        {
            float right = index == bands.Length - 1 ? innerMax.X : left + bandWidth;
            (Vector4 accent, bool enabled) = bands[index];
            DrawToolbarStateBand(drawList, new Vector2(left, innerMin.Y), new Vector2(right, innerMax.Y), accent, enabled, peakAlpha);
            left = right + bandGap;
        }
    }

    private static void DrawToolbarStateBand(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 accent, bool enabled, float peakAlpha)
    {
        if (!enabled || !EditorInputUtility.HasArea(min, max))
        {
            return;
        }

        var midY = min.Y + ((max.Y - min.Y) * 0.56f);
        var middleAlpha = ThemeColors.WithAlpha(accent, peakAlpha);
        var topAlpha = ThemeColors.WithAlpha(accent, peakAlpha * 0.22f);
        var fadeAlpha = ThemeColors.WithAlpha(accent, peakAlpha * 0.18f);

        drawList.AddRectFilledMultiColor(
            min,
            new Vector2(max.X, midY),
            ImGui.GetColorU32(topAlpha),
            ImGui.GetColorU32(topAlpha),
            ImGui.GetColorU32(middleAlpha),
            ImGui.GetColorU32(middleAlpha));

        drawList.AddRectFilledMultiColor(
            new Vector2(min.X, midY),
            max,
            ImGui.GetColorU32(middleAlpha),
            ImGui.GetColorU32(middleAlpha),
            ImGui.GetColorU32(fadeAlpha),
            ImGui.GetColorU32(fadeAlpha));
    }

    private static void DrawToolbarButtonBorder(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        Vector4 accent,
        bool hovered,
        bool active)
    {
        float borderAlpha = 0.82f;
        if (active)
        {
            borderAlpha = 1f;
        }
        else if (hovered)
        {
            borderAlpha = 0.95f;
        }

        Vector4 borderColor = accent with { W = borderAlpha };
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
        var selected = _browserSelection.Kind == kind;
        var accentColor = ThemeColors.AccentPrimary;
        var drawColor = selected ? accentColor : ThemeColors.Text;
        var neutralButtonColor = ImGui.GetColorU32(ImGuiCol.Button);
        ToolbarButtonContentMetrics contentMetrics = ResolveToolbarButtonContentMetrics(icon, label, mode);

        using var hoveredButton = ImRaii.PushColor(ImGuiCol.ButtonHovered, neutralButtonColor);
        using var activeButton = ImRaii.PushColor(ImGuiCol.ButtonActive, neutralButtonColor);

        if (DrawToolbarButton(id, buttonSize, mode))
        {
            _browserSelection.SetDraftKind(kind);
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

        DrawToolbarButtonContent(drawList, icon, label, mode, min, buttonSize, contentMetrics, drawColor);

        if (mode == ToolbarSurfaceMode.Rail)
        {
            DrawToolbarRailCountBadge(drawList, min, max, count, accentColor);
        }

        if (itemHovered)
        {
            var countLabel = count == 1 ? "1 placed" : $"{count} placed";
            DrawToolbarTextTooltip(icon, label, countLabel, accentColor, []);
        }
    }

    private static void DrawToolbarRailCountBadge(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        int count,
        Vector4 accentColor)
    {
        if (count <= 0)
        {
            return;
        }

        float scale = ImGuiHelpers.GlobalScale;
        string label = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        ImFontPtr font = ImGui.GetFont();
        float sourceFontSize = ImGui.GetFontSize();
        float fontSize = sourceFontSize * (label.Length > 2 ? 0.58f : 0.64f);
        Vector2 textSize = ImGui.CalcTextSize(label) * (fontSize / sourceFontSize);
        Vector2 padding = new(2.5f * scale, 0.75f * scale);
        float badgeHeight = MathF.Max(12f * scale, textSize.Y + (padding.Y * 2f));
        Vector2 badgeSize = new(
            MathF.Max(badgeHeight, textSize.X + (padding.X * 2f)),
            badgeHeight);
        Vector2 inset = new(3f * scale, 3f * scale);
        Vector2 badgeMin = new(max.X - inset.X - badgeSize.X, min.Y + inset.Y);
        Vector2 badgeMax = badgeMin + badgeSize;
        float rounding = badgeHeight * 0.5f;

        drawList.AddRectFilled(
            badgeMin,
            badgeMax,
            ImGui.GetColorU32(ThemeColors.WithAlpha(accentColor, 0.90f)),
            rounding);
        drawList.AddRect(
            badgeMin,
            badgeMax,
            ImGui.GetColorU32(ThemeColors.Color(1f, 1f, 1f, 0.20f)),
            rounding,
            ImDrawFlags.None,
            MathF.Max(scale, 1f));
        drawList.AddText(
            font,
            fontSize,
            badgeMin + ((badgeSize - textSize) * 0.5f),
            ImGui.GetColorU32(ThemeColors.Color(1f, 1f, 1f, 0.96f)),
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
        bool useNeutralHoverFill = false,
        Vector4? hoverBorderColor = null,
        Action<ImDrawListPtr, Vector2, Vector2, bool, bool>? drawBackground = null,
        Action? drawTooltip = null,
        ToolbarSurfaceMode mode = ToolbarSurfaceMode.Strip,
        IReadOnlyList<IntonerTooltipKeyHint>? keyHints = null)
    {
        var buttonEdge = ResolveToolbarButtonEdge(mode);
        var buttonSize = new Vector2(buttonEdge, buttonEdge);
        var drawColor = accentColor ?? ThemeColors.Text;
        var neutralButtonColor = ImGui.GetColorU32(ImGuiCol.Button);
        ToolbarButtonContentMetrics contentMetrics = ResolveToolbarButtonContentMetrics(icon, label, mode);

        using var selectedButton = selected
            ? ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive))
            : default;
        bool useAccentButtonFill = accentColor.HasValue && !useNeutralHoverFill;
        uint hoveredColor = useAccentButtonFill
            ? ImGui.GetColorU32(accentColor!.Value with { W = 0.25f })
            : neutralButtonColor;
        uint activeColor = useAccentButtonFill
            ? ImGui.GetColorU32(accentColor!.Value with { W = 0.35f })
            : neutralButtonColor;
        using var hoveredButton = useAccentButtonFill || useNeutralHoverFill
            ? ImRaii.PushColor(ImGuiCol.ButtonHovered, hoveredColor)
            : default;
        using var activeButton = useAccentButtonFill || useNeutralHoverFill
            ? ImRaii.PushColor(ImGuiCol.ButtonActive, activeColor)
            : default;

        if (DrawToolbarButton(id, buttonSize, mode))
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

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (drawTooltip is not null)
            {
                drawTooltip();
            }
            else if (!string.IsNullOrWhiteSpace(tooltip))
            {
                DrawToolbarTextTooltip(icon, label, tooltip, accentColor ?? hoverBorderColor ?? ThemeColors.AccentPrimary, keyHints ?? []);
            }
            else if (mode == ToolbarSurfaceMode.Rail)
            {
                DrawToolbarTextTooltip(icon, label, string.Empty, accentColor ?? hoverBorderColor ?? ThemeColors.AccentPrimary, keyHints ?? []);
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

    private static void DrawVerticalToolbarGroupSeparator()
    {
        using var spacing = PushToolbarItemSpacing(ToolbarSurfaceMode.Rail);
        var separatorHeight = ResolveToolbarRailSeparatorHeight();
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = MathF.Max(0f, ImGui.GetContentRegionAvail().X);
        var color = ImGui.GetColorU32(ImGuiCol.Separator);
        var thickness = EditorLayout.ResolveDividerThickness();
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

    private static ImRaii.StyleDisposable? PushToolbarItemSpacing(ToolbarSurfaceMode mode)
        => mode == ToolbarSurfaceMode.Rail
            ? ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(ResolveToolbarRailItemGap()))
            : default;

    private static bool DrawToolbarButton(string id, Vector2 size, ToolbarSurfaceMode mode)
    {
        using var spacing = PushToolbarItemSpacing(mode);
        return ImGui.Button(id, size);
    }

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
            var lineColor = ThemeColors.WithAlpha(ThemeColors.AccentPrimary, 0.45f);
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
        var accentColor = ThemeColors.AccentPrimary;
        using var button = selected
            ? ImRaii.PushColor(ImGuiCol.Button, accentColor with { W = 0.24f })
            : default;
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, accentColor with { W = selected ? 0.30f : 0.18f });
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, accentColor with { W = 0.36f });
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f * ImGuiHelpers.GlobalScale);

        if (ImGui.Button(id, size) && !selected)
        {
            _toolbarDockPosition = dockPosition;
            _ = _configurationService.TryUpdate(
                configuration => configuration.Ui.ToolbarPosition = dockPosition);
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var iconColor = selected ? accentColor : ThemeColors.Text;
        EditorIcon.DrawCentered(drawList, icon, min, max, iconColor);

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
            DrawToolbarTextTooltip(icon, "Toolbar Dock", tooltip, accentColor, []);
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

    private void DrawTransformSpaceTooltip()
    {
        KeyboardGesture? gesture = _shortcuts.GetGesture(EditorShortcuts.ToggleTransformSpace);
        DrawToolbarTextTooltip(
            GetBoundsSpaceToggleIcon(),
            GetBoundsSpaceToggleLabel(),
            EditorShortcuts.ToggleTransformSpace.Description,
            EditorColors.BoundsSpaceAccent(_gizmo.Settings.BoundsOverlaySpace),
            gesture is { } binding ? [new IntonerTooltipKeyHint(binding, "to switch transform space")] : []);
    }

    private void DrawHeaderGizmoModeButton(
        string id,
        FontAwesomeIcon icon,
        string label,
        string tooltip,
        GizmoTransformMode mode,
        SceneItemSnapshot? selected,
        bool enabled,
        ToolbarSurfaceMode surfaceMode = ToolbarSurfaceMode.Strip,
        Action? drawTooltip = null)
    {
        var accentColor = EditorColors.TransformModeAccent(mode);
        var isActive = (_gizmo.Settings.Mode & mode) != GizmoTransformMode.None;
        Vector4? buttonAccentColor = enabled && isActive ? accentColor : null;
        Vector4? buttonHoverBorderColor = enabled ? accentColor : null;

        using (ImRaii.Disabled(!enabled))
        {
            DrawHeaderActionButton(
                id,
                icon,
                label,
                drawTooltip is null && selected is null
                    ? $"{tooltip}\nSelect a placed item to use the gizmo"
                    : tooltip,
                mode == GizmoTransformMode.Scale && ImGui.IsPopupOpen(ScaleOptionsPopupId),
                () => ToggleGizmoMode(mode),
                buttonAccentColor,
                useNeutralHoverFill: true,
                hoverBorderColor: buttonHoverBorderColor,
                drawTooltip: drawTooltip,
                mode: surfaceMode,
                keyHints: GizmoModeKeyHints);
        }
    }

    private void ToggleGizmoMode(GizmoTransformMode mode)
        => _gizmo.Settings.ToggleMode(mode, ImGui.GetIO().KeyShift);
}
