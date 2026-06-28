using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private static Vector2 MeasureToolbarIcon(FontAwesomeIcon icon)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            return ImGui.CalcTextSize(icon.ToIconString());
        }
    }

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

            drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(EditorColors.Color(0.11f, 0.11f, 0.11f, 1f)), 96);
            drawList.AddCircleFilled(center, innerRadius, ImGui.GetColorU32(EditorColors.Color(0.11f, 0.11f, 0.11f, 1f)), 72);
            drawList.AddCircle(center, radius, ImGui.GetColorU32(EditorColors.Color(0.10f, 0.10f, 0.10f, 1f)), 96, 3.5f * scale);
            drawList.AddCircle(center, innerRadius, ImGui.GetColorU32(EditorColors.Color(0.10f, 0.10f, 0.10f, 1f)), 72, 3f * scale);

            var ringInner = innerRadius * 0.92f;
            var ringOuter = radius * 0.98f;
            var segments = RadialActionsPage
                ? BuildSecondaryWheelSegments(context)
                : BuildPrimaryWheelSegments(context);

            var segmentSweep = (MathF.PI * 2f) / segments.Length;
            var baseAngle = (-MathF.PI / 2f) - (segmentSweep * 0.5f);
            var hoveredIndex = -1;

            for (var index = 0; index < segments.Length; ++index)
            {
                var segment = segments[index];
                var startAngle = baseAngle + (segmentSweep * index);
                var endAngle = startAngle + segmentSweep;
                var isHovered = GizmoRotationMath.IsPointInRingSegment(mousePos, center, ringInner, ringOuter, startAngle, endAngle);
                if (isHovered)
                {
                    hoveredIndex = index;
                }

                DrawRingSegment(
                    drawList,
                    center,
                    ringInner,
                    ringOuter,
                    startAngle,
                    endAngle,
                    ImGui.GetColorU32(GetWheelFillColor(segment.Color, segment.IsActive, segment.IsEnabled, isHovered)));

                var labelAngle = (startAngle + endAngle) * 0.5f;
                var labelRadius = (ringInner + ringOuter) * 0.5f;
                var labelPosition = center + (new Vector2(MathF.Cos(labelAngle), MathF.Sin(labelAngle)) * labelRadius);
                var iconText = segment.Icon.ToIconString();
                var iconSize = MeasureToolbarIcon(segment.Icon);

                var iconColor = segment.IsActive
                    ? EditorColors.Color(0f, 0f, 0f, 0.95f)
                    : segment.IsEnabled
                        ? EditorColors.Color(1f, 1f, 1f, 0.95f)
                        : EditorColors.Color(0.55f, 0.55f, 0.58f, 0.95f);
                using (ImRaii.PushFont(UiBuilder.IconFont))
                {
                    drawList.AddText(
                        labelPosition - (iconSize * 0.5f),
                        ImGui.GetColorU32(iconColor),
                        iconText);
                }
            }

            var centerButtonRadius = innerRadius * 0.42f;
            var centerHovered = Vector2.Distance(mousePos, center) <= centerButtonRadius;
            var centerColor = RadialActionsPage
                ? EditorColors.Color(0.30f, 0.55f, 0.95f, 1f)
                : EditorColors.Color(0.20f, 0.20f, 0.30f, 1f);
            drawList.AddCircleFilled(center, centerButtonRadius, ImGui.GetColorU32(centerColor), 64);
            drawList.AddCircle(center, centerButtonRadius, ImGui.GetColorU32(EditorColors.Color(0.10f, 0.10f, 0.10f, 1f)), 64, 2f * scale);

            var centerIcon = RadialActionsPage ? FontAwesomeIcon.Tools : FontAwesomeIcon.LayerGroup;
            var centerText = centerIcon.ToIconString();
            var centerIconSize = MeasureToolbarIcon(centerIcon);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                drawList.AddText(
                    center - (centerIconSize * 0.5f),
                    ImGui.GetColorU32(RadialActionsPage ? EditorColors.Color(0f, 0f, 0f, 0.95f) : EditorColors.Color(1f, 1f, 1f, 0.95f)),
                    centerText);
            }

            if (hoveredIndex >= 0 || centerHovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            }

            if (centerHovered)
            {
                PendingRadialTooltip = new GizmoRadialTooltipInfo(mousePos, RadialActionsPage ? "Show Primary Actions" : "Show Object Actions");
            }
            else if (hoveredIndex >= 0)
            {
                PendingRadialTooltip = new GizmoRadialTooltipInfo(mousePos, segments[hoveredIndex].Tooltip);
            }

            var activated = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            var cancel = ImGui.IsMouseClicked(ImGuiMouseButton.Right);
            if (centerHovered && activated)
            {
                State.ToggleRadialActionsPage();
            }
            else if (activated && hoveredIndex >= 0)
            {
                var segment = segments[hoveredIndex];
                if (segment.IsEnabled)
                {
                    segment.OnClick();
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

    private GizmoWheelSegment[] BuildPrimaryWheelSegments(in GizmoContext context)
    {
        var scaleEnabled = context.ScaleSupported;
        return
        [
            new GizmoWheelSegment(
                FontAwesomeIcon.ArrowsAlt,
                "Move Gizmo",
                EditorColors.TransformModeAccent(GizmoTransformMode.Translation),
                Mode == GizmoTransformMode.Translation,
                true,
                () => Mode = GizmoTransformMode.Translation),
            new GizmoWheelSegment(
                FontAwesomeIcon.SyncAlt,
                "Rotate Gizmo",
                EditorColors.TransformModeAccent(GizmoTransformMode.Rotation),
                Mode == GizmoTransformMode.Rotation,
                true,
                () => Mode = GizmoTransformMode.Rotation),
            new GizmoWheelSegment(
                FontAwesomeIcon.CompressArrowsAlt,
                scaleEnabled
                    ? "Scale Gizmo"
                    : context.SelectionCount > 1
                        ? "Scale gizmo is only available for one selected bgobject or furniture object"
                        : "Scale gizmo is not available for lights",
                EditorColors.TransformModeAccent(GizmoTransformMode.Scale),
                Mode == GizmoTransformMode.Scale,
                scaleEnabled,
                () => Mode = GizmoTransformMode.Scale),
            new GizmoWheelSegment(
                FontAwesomeIcon.Cube,
                "Local Space",
                EditorColors.AccentOrange,
                CurrentBoundsOverlaySpace == BoundsOverlaySpace.Local,
                true,
                () => CurrentBoundsOverlaySpace = BoundsOverlaySpace.Local),
            new GizmoWheelSegment(
                FontAwesomeIcon.Globe,
                "World Space",
                EditorColors.AccentBlue,
                CurrentBoundsOverlaySpace == BoundsOverlaySpace.World,
                true,
                () => CurrentBoundsOverlaySpace = BoundsOverlaySpace.World),
            new GizmoWheelSegment(
                FontAwesomeIcon.BorderAll,
                Settings.BoundsInteractionSettings.BoundsEnabled ? "Hide Bounds Overlay" : "Show Bounds Overlay",
                EditorColors.BoundsOverlayAccent,
                Settings.BoundsInteractionSettings.BoundsEnabled,
                true,
                ToggleBoundsOverlayEnabled),
        ];
    }

    private GizmoWheelSegment[] BuildSecondaryWheelSegments(in GizmoContext context)
    {
        var snapshot = context.PrimarySnapshot;
        var selectedSnapshots = context.SelectedSnapshots;
        var selectionCount = context.SelectionCount;
        var canMoveToPlayer = context.SelectionCount == 1;
        var anyVisible = false;
        for (var index = 0; index < selectedSnapshots.Count; ++index)
        {
            if (!selectedSnapshots[index].Visible)
            {
                continue;
            }

            anyVisible = true;
            break;
        }

        var duplicateLabel = selectionCount == 1 ? "Duplicate Selected Object" : "Duplicate Selected Objects";
        var visibilityLabel = anyVisible
            ? selectionCount == 1 ? "Hide Selected Object" : "Hide Selected Objects"
            : selectionCount == 1 ? "Show Selected Object" : "Show Selected Objects";
        var visibilityHistoryTitle = anyVisible
            ? selectionCount == 1 ? "Hide Object" : "Hide Objects"
            : selectionCount == 1 ? "Show Object" : "Show Objects";
        var resetLabel = selectionCount == 1 ? "Reset Rotation and Scale" : "Reset Rotation and Scale For Selected Objects";
        var removeLabel = selectionCount == 1 ? "Remove Selected Object" : "Remove Selected Objects";
        return
        [
            new GizmoWheelSegment(
                FontAwesomeIcon.Copy,
                duplicateLabel,
                EditorColors.Color(0.35f, 0.75f, 0.95f, 1f),
                false,
                true,
                () => _host.TryDuplicateSelectedObjects(selectedSnapshots)),
            new GizmoWheelSegment(
                FontAwesomeIcon.Running,
                canMoveToPlayer ? "Move Selected Object To Player" : "Move to player is only available for one selected object",
                EditorColors.Color(0.50f, 0.90f, 0.60f, 1f),
                false,
                canMoveToPlayer,
                () => _host.TryMoveObjectToPlayerWithHistory(snapshot.Id)),
            new GizmoWheelSegment(
                anyVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                visibilityLabel,
                EditorColors.Color(0.75f, 0.75f, 0.90f, 1f),
                anyVisible,
                true,
                () => _host.TryApplySelectedSnapshotUpdateWithHistory(
                    ObjectHistoryKind.Visibility,
                    visibilityHistoryTitle,
                    selectedSnapshots,
                    entry => entry with { Visible = !anyVisible })),
            new GizmoWheelSegment(
                FontAwesomeIcon.Recycle,
                resetLabel,
                EditorColors.Color(0.50f, 0.70f, 0.95f, 1f),
                false,
                true,
                () => _host.TryApplySelectedSnapshotUpdateWithHistory(
                    ObjectHistoryKind.Transform,
                    resetLabel,
                    selectedSnapshots,
                    entry =>
                    {
                        var transform = entry.Transform with
                        {
                            RotationDegrees = Vector3.Zero,
                            Scale = CanUseScaleGizmo(entry) ? Vector3.One : entry.Transform.Scale,
                        };
                        return entry with { Transform = transform };
                    })),
            new GizmoWheelSegment(
                FontAwesomeIcon.Trash,
                removeLabel,
                EditorColors.Color(0.95f, 0.40f, 0.40f, 1f),
                false,
                true,
                () => _host.TryRemoveSelectedObjects(selectedSnapshots)),
            new GizmoWheelSegment(
                FontAwesomeIcon.TimesCircle,
                "Hide Gizmo",
                EditorColors.Color(0.65f, 0.55f, 0.95f, 1f),
                Mode == GizmoTransformMode.None,
                true,
                () => Mode = GizmoTransformMode.None),
        ];
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
        float innerRadius,
        float outerRadius,
        float startAngle,
        float endAngle,
        uint fillColor)
    {
        const int steps = 48;
        drawList.PathClear();
        drawList.PathArcTo(center, outerRadius, startAngle, endAngle, steps);
        drawList.PathArcTo(center, innerRadius, endAngle, startAngle, steps);
        drawList.PathFillConvex(fillColor);
    }

    private static Vector4 GetWheelFillColor(Vector4 accentColor, bool active, bool enabled, bool hovered)
    {
        if (!enabled)
        {
            return hovered
                ? EditorColors.Color(0.20f, 0.20f, 0.22f, 1f)
                : EditorColors.Color(0.11f, 0.11f, 0.11f, 1f);
        }

        if (!active)
        {
            if (!hovered)
            {
                return EditorColors.Color(0.11f, 0.11f, 0.11f, 1f);
            }

            var dim = 0.25f;
            return EditorColors.Color(
                MathF.Min(accentColor.X * dim, 0.35f),
                MathF.Min(accentColor.Y * dim, 0.35f),
                MathF.Min(accentColor.Z * dim, 0.35f),
                1f);
        }

        return EditorColors.Color(
            MathF.Min(accentColor.X * 1.05f, 1f),
            MathF.Min(accentColor.Y * 1.05f, 1f),
            MathF.Min(accentColor.Z * 1.05f, 1f),
            1f);
    }

    private static void DrawGizmoRadialTooltip(in GizmoRadialTooltipInfo tooltip)
    {
        var drawList = ImGui.GetForegroundDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(8f * scale, 5f * scale);
        var rectMin = tooltip.MousePosition + new Vector2(GizmoConstants.TooltipOffsetX * scale, GizmoConstants.TooltipOffsetY * scale);
        var textSize = ImGui.CalcTextSize(tooltip.Title);
        var rectMax = rectMin + textSize + (padding * 2f);

        drawList.AddRectFilled(rectMin, rectMax, ImGui.GetColorU32(EditorColors.Color(0.08f, 0.08f, 0.08f, 0.95f)), 0f);
        drawList.AddRect(rectMin, rectMax, ImGui.GetColorU32(EditorColors.Color(0.35f, 0.35f, 0.35f, 1f)), 0f, ImDrawFlags.None, 1.4f * scale);
        drawList.AddText(rectMin + padding, ImGui.GetColorU32(ImGuiCol.Text), tooltip.Title);
    }
}

