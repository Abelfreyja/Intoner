using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private static readonly GizmoWheelAction[] PrimaryWheelActions = [GizmoWheelAction.Universal, GizmoWheelAction.Move,
        GizmoWheelAction.Rotate, GizmoWheelAction.Scale, GizmoWheelAction.LocalSpace, GizmoWheelAction.WorldSpace, GizmoWheelAction.Bounds];
    private static readonly GizmoWheelAction[] SecondaryWheelActions = [GizmoWheelAction.Duplicate, GizmoWheelAction.MoveToPlayer,
        GizmoWheelAction.Visibility, GizmoWheelAction.ResetTransform, GizmoWheelAction.Remove, GizmoWheelAction.Hide];

    private void DrawGizmoWheel(in GizmoContext context)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var radius = GizmoConstants.OptionWheelBaseRadius * scale;
        var innerRadius = radius * GizmoConstants.OptionWheelInnerRadiusFraction;

        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(WheelCenter - new Vector2(radius), ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(new Vector2(radius * 2f));
        using var windowRounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, radius);
        using var windowBorderSize = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 0f);
        using var windowPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        var flags = ImGuiWindowFlags.NoDecoration
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoBackground
                    | ImGuiWindowFlags.NoFocusOnAppearing
                    | ImGuiWindowFlags.NoNav;

        using var popup = ImRaii.Popup(GizmoConstants.WheelPopupId, flags);
        if (popup)
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            using var alpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f);
            var drawList = ImGui.GetForegroundDrawList();
            var windowPos = ImGui.GetWindowPos();
            var center = windowPos + new Vector2(radius);
            var mousePos = ImGui.GetIO().MousePos;

            drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(ThemeColors.Color(0.11f, 0.11f, 0.11f, 1f)), 96);
            drawList.AddCircle(center, radius, ImGui.GetColorU32(ThemeColors.Color(0.10f, 0.10f, 0.10f, 1f)), 96, 3.5f * scale);
            drawList.AddCircle(center, innerRadius, ImGui.GetColorU32(ThemeColors.Color(0.10f, 0.10f, 0.10f, 1f)), 72, 3f * scale);

            var ringInner = innerRadius * 0.92f;
            var ringOuter = radius * 0.98f;
            ReadOnlySpan<GizmoWheelAction> actions = GetWheelActions(RadialActionsPage, ImGui.GetIO().KeyShift);

            var segmentSweep = (MathF.PI * 2f) / actions.Length;
            var baseAngle = (-MathF.PI / 2f) - (segmentSweep * 0.5f);
            var hoveredIndex = -1;
            GizmoWheelSegment hoveredSegment = default;

            for (var index = 0; index < actions.Length; ++index)
            {
                GizmoWheelSegment segment = GetWheelSegment(actions[index], context);
                var startAngle = baseAngle + (segmentSweep * index);
                var endAngle = startAngle + segmentSweep;
                var isHovered = GizmoRotationMath.IsPointInRingSegment(mousePos, center, ringInner, ringOuter, startAngle, endAngle);
                if (isHovered)
                {
                    hoveredIndex = index;
                    hoveredSegment = segment;
                }

                DrawRingSegment(
                    drawList,
                    center,
                    ringOuter,
                    startAngle,
                    endAngle,
                    ImGui.GetColorU32(GetWheelFillColor(segment.Color, segment.IsActive, segment.IsEnabled, isHovered)));

                var labelAngle = (startAngle + endAngle) * 0.5f;
                var labelRadius = (ringInner + ringOuter) * 0.5f;
                var labelPosition = center + (new Vector2(MathF.Cos(labelAngle), MathF.Sin(labelAngle)) * labelRadius);
                EditorIcon.Metrics iconMetrics = EditorIcon.Measure(segment.Icon);

                var iconColor = ResolveWheelIconColor(segment);
                EditorIcon.Draw(
                    drawList,
                    segment.Icon,
                    iconMetrics,
                    labelPosition - (iconMetrics.Size * 0.5f),
                    iconColor);
            }

            drawList.AddCircleFilled(center, ringInner, ImGui.GetColorU32(ThemeColors.Color(0.11f, 0.11f, 0.11f, 1f)), 96);
            var centerButtonRadius = innerRadius * 0.42f;
            var centerHovered = Vector2.Distance(mousePos, center) <= centerButtonRadius;
            var centerColor = RadialActionsPage
                ? ThemeColors.Color(0.30f, 0.55f, 0.95f, 1f)
                : ThemeColors.Color(0.20f, 0.20f, 0.30f, 1f);
            drawList.AddCircleFilled(center, centerButtonRadius, ImGui.GetColorU32(centerColor), 64);
            drawList.AddCircle(center, centerButtonRadius, ImGui.GetColorU32(ThemeColors.Color(0.10f, 0.10f, 0.10f, 1f)), 64, 2f * scale);

            var centerIcon = RadialActionsPage ? FontAwesomeIcon.Tools : FontAwesomeIcon.LayerGroup;
            EditorIcon.Metrics centerIconMetrics = EditorIcon.Measure(centerIcon);
            EditorIcon.Draw(
                drawList,
                centerIcon,
                centerIconMetrics,
                center - (centerIconMetrics.Size * 0.5f),
                RadialActionsPage ? ThemeColors.Color(0f, 0f, 0f, 0.95f) : ThemeColors.Color(1f, 1f, 1f, 0.95f));

            if (hoveredIndex >= 0 || centerHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            if (centerHovered)
            {
                PendingRadialTooltip = new GizmoRadialTooltipInfo(mousePos, RadialActionsPage ? "Show Primary Actions" : "Show Item Actions");
            }
            else if (hoveredIndex >= 0)
            {
                PendingRadialTooltip = new GizmoRadialTooltipInfo(mousePos, hoveredSegment.Tooltip);
            }

            var activated = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            var cancel = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
            if (centerHovered && activated)
            {
                State.ToggleRadialActionsPage();
            }
            else if (activated && hoveredIndex >= 0)
            {
                if (hoveredSegment.IsEnabled)
                {
                    ExecuteWheelAction(hoveredSegment, context);
                    ImGui.CloseCurrentPopup();
                }
            }
            else if ((activated && hoveredIndex < 0) || cancel)
            {
                if (cancel)
                {
                    WheelSuppressNextToggle = true;
                }

                ImGui.CloseCurrentPopup();
            }
        }

    }

    private ReadOnlySpan<GizmoWheelAction> GetWheelActions(bool secondary, bool combine)
    {
        if (secondary)
        {
            return SecondaryWheelActions;
        }

        return PrimaryWheelActions.AsSpan(combine || Mode == GizmoTransformMode.Universal ? 0 : 1);
    }

    private GizmoWheelSegment GetWheelSegment(GizmoWheelAction action, in GizmoContext context)
    {
        bool singleSelection = context.SelectionCount == 1;
        switch (action)
        {
            case GizmoWheelAction.Universal:
                return CreateTransformWheelSegment(action, GizmoTransformMode.Universal, FontAwesomeIcon.Shapes, "Universal Gizmo");
            case GizmoWheelAction.Move:
                return CreateTransformWheelSegment(action, GizmoTransformMode.Translation, FontAwesomeIcon.ArrowsAlt, "Move Gizmo");
            case GizmoWheelAction.Rotate:
                return CreateTransformWheelSegment(action, GizmoTransformMode.Rotation, FontAwesomeIcon.SyncAlt, "Rotate Gizmo");
            case GizmoWheelAction.Scale:
                string scaleLabel = "Scale Gizmo";
                if (!context.ScaleSupported)
                {
                    scaleLabel = context.SelectionCount > 1
                        ? "Select one scalable item to use the scale gizmo"
                        : "This item does not support scaling";
                }
                return CreateTransformWheelSegment(action, GizmoTransformMode.Scale, FontAwesomeIcon.CompressArrowsAlt, scaleLabel, context.ScaleSupported);
            case GizmoWheelAction.LocalSpace:
                return new(action, FontAwesomeIcon.Cube, "Local Space", ThemeColors.AccentOrange, CurrentBoundsOverlaySpace == BoundsOverlaySpace.Local);
            case GizmoWheelAction.WorldSpace:
                return new(action, FontAwesomeIcon.Globe, "World Space", ThemeColors.AccentBlue, CurrentBoundsOverlaySpace == BoundsOverlaySpace.World);
            case GizmoWheelAction.Bounds:
                return new(action, FontAwesomeIcon.BorderAll,
                    Settings.BoundsInteractionSettings.BoundsEnabled ? "Hide Bounds Overlay" : "Show Bounds Overlay",
                    EditorColors.BoundsOverlayAccent, Settings.BoundsInteractionSettings.BoundsEnabled);
            case GizmoWheelAction.Duplicate:
                return new(action, FontAwesomeIcon.Copy, singleSelection ? "Duplicate Selected Item" : "Duplicate Selected Items",
                    ThemeColors.Color(0.35f, 0.75f, 0.95f, 1f));
            case GizmoWheelAction.MoveToPlayer:
                return new(action, FontAwesomeIcon.Running,
                    singleSelection ? "Move Selected Item To Player" : "Move to player is only available for one selected item",
                    ThemeColors.Color(0.50f, 0.90f, 0.60f, 1f), IsEnabled: singleSelection);
            case GizmoWheelAction.Visibility:
                bool anyVisible = false;
                for (int index = 0; index < context.SelectedSnapshots.Count; ++index)
                {
                    if (context.SelectedSnapshots[index].Visible)
                    {
                        anyVisible = true;
                        break;
                    }
                }
                string visibilityLabel = (anyVisible, singleSelection) switch
                {
                    (true, true) => "Hide Selected Item",
                    (true, false) => "Hide Selected Items",
                    (false, true) => "Show Selected Item",
                    (false, false) => "Show Selected Items",
                };
                return new(action, anyVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash, visibilityLabel,
                    ThemeColors.Color(0.75f, 0.75f, 0.90f, 1f), anyVisible);
            case GizmoWheelAction.ResetTransform:
                return new(action, FontAwesomeIcon.Recycle, singleSelection ? "Reset Rotation and Scale" : "Reset Rotation and Scale For Selected Items",
                    ThemeColors.Color(0.50f, 0.70f, 0.95f, 1f));
            case GizmoWheelAction.Remove:
                return new(action, FontAwesomeIcon.Trash, singleSelection ? "Remove Selected Item" : "Remove Selected Items",
                    ThemeColors.Color(0.95f, 0.40f, 0.40f, 1f));
            case GizmoWheelAction.Hide:
                return new(action, FontAwesomeIcon.TimesCircle, "Hide Gizmo", ThemeColors.Color(0.65f, 0.55f, 0.95f, 1f),
                    Mode == GizmoTransformMode.None);
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
    }

    private GizmoWheelSegment CreateTransformWheelSegment(
        GizmoWheelAction action, GizmoTransformMode mode, FontAwesomeIcon icon, string tooltip, bool enabled = true)
        => new(action, icon, tooltip, EditorColors.TransformModeAccent(mode), (Mode & mode) == mode, enabled, mode);

    private void ExecuteWheelAction(in GizmoWheelSegment segment, in GizmoContext context)
    {
        if (segment.Mode != GizmoTransformMode.None)
        {
            if (ImGui.GetIO().KeyShift && segment.Mode != GizmoTransformMode.Universal)
            {
                Settings.ToggleMode(segment.Mode, true);
            }
            else
            {
                Mode = segment.Mode;
            }
            return;
        }

        switch (segment.Action)
        {
            case GizmoWheelAction.LocalSpace:
                CurrentBoundsOverlaySpace = BoundsOverlaySpace.Local;
                break;
            case GizmoWheelAction.WorldSpace:
                CurrentBoundsOverlaySpace = BoundsOverlaySpace.World;
                break;
            case GizmoWheelAction.Bounds:
                ToggleBoundsOverlayEnabled();
                break;
            case GizmoWheelAction.Duplicate:
                _host.TryDuplicateSelectedItems(context.SelectedSnapshots);
                break;
            case GizmoWheelAction.MoveToPlayer:
                _host.TryMoveItemToPlayerWithHistory(context.PrimarySnapshot.Id);
                break;
            case GizmoWheelAction.Visibility:
                bool visible = !segment.IsActive;
                string title = $"{(visible ? "Show" : "Hide")} {(context.SelectionCount == 1 ? "Item" : "Items")}";
                _host.TryApplySelectedSnapshotUpdateWithHistory(SceneHistoryKind.Visibility, title,
                    context.SelectedSnapshots, entry => entry with { Visible = visible });
                break;
            case GizmoWheelAction.ResetTransform:
                _host.TryApplySelectedSnapshotUpdateWithHistory(SceneHistoryKind.Transform, segment.Tooltip,
                    context.SelectedSnapshots, entry => entry with
                    {
                        Transform = entry.Transform with
                        {
                            RotationDegrees = Vector3.Zero,
                            Scale = CanUseScaleGizmo(entry) ? Vector3.One : entry.Transform.Scale,
                        },
                    });
                break;
            case GizmoWheelAction.Remove:
                _host.TryRemoveSelectedItems(context.SelectedSnapshots);
                break;
            case GizmoWheelAction.Hide:
                Mode = GizmoTransformMode.None;
                break;
        }
    }

    private void HandleGizmoRadialInput(bool pointerInRegion)
    {
        if (IsGizmoWheelOpen() || HasActiveTransformDrag || SurfaceDragState.IsDragging)
        {
            return;
        }

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            if (pointerInRegion && !WheelSuppressNextToggle)
            {
                State.OpenRadialMenu(ImGui.GetIO().MousePos);
                ImGui.OpenPopup(GizmoConstants.WheelPopupId);
            }

            WheelSuppressNextToggle = false;
        }
    }

    private static void DrawRingSegment(
        ImDrawListPtr drawList,
        Vector2 center,
        float outerRadius,
        float startAngle,
        float endAngle,
        uint fillColor)
    {
        const int steps = 48;
        drawList.PathClear();
        drawList.PathLineTo(center);
        drawList.PathArcTo(center, outerRadius, startAngle, endAngle, steps);
        drawList.PathFillConvex(fillColor);
    }

    private static Vector4 GetWheelFillColor(Vector4 accentColor, bool active, bool enabled, bool hovered)
    {
        if (!enabled)
        {
            return hovered
                ? ThemeColors.Color(0.20f, 0.20f, 0.22f, 1f)
                : ThemeColors.Color(0.11f, 0.11f, 0.11f, 1f);
        }

        if (!active)
        {
            if (!hovered)
            {
                return ThemeColors.Color(0.11f, 0.11f, 0.11f, 1f);
            }

            var dim = 0.25f;
            return ThemeColors.Color(
                MathF.Min(accentColor.X * dim, 0.35f),
                MathF.Min(accentColor.Y * dim, 0.35f),
                MathF.Min(accentColor.Z * dim, 0.35f),
                1f);
        }

        return ThemeColors.Color(
            MathF.Min(accentColor.X * 1.05f, 1f),
            MathF.Min(accentColor.Y * 1.05f, 1f),
            MathF.Min(accentColor.Z * 1.05f, 1f),
            1f);
    }

    private static Vector4 ResolveWheelIconColor(in GizmoWheelSegment segment)
    {
        if (segment.IsActive)
        {
            return ThemeColors.Color(0f, 0f, 0f, 0.95f);
        }

        return segment.IsEnabled
            ? ThemeColors.Color(1f, 1f, 1f, 0.95f)
            : ThemeColors.Color(0.55f, 0.55f, 0.58f, 0.95f);
    }

    private static void DrawGizmoRadialTooltip(in GizmoRadialTooltipInfo tooltip)
    {
        float scale = ImGuiHelpers.GlobalScale;
        IntonerTooltip.DrawOverlayText(
            ImGui.GetForegroundDrawList(),
            tooltip.MousePosition + new Vector2(GizmoConstants.TooltipOffsetX * scale, GizmoConstants.TooltipOffsetY * scale),
            tooltip.Title,
            new IntonerTooltipOptions
            {
                Padding = new Vector2(9f, 6f),
                Rounding = 5f,
            });
    }
}

