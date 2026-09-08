using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.UI.Components;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
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
            drawList.AddCircleFilled(center, innerRadius, ImGui.GetColorU32(ThemeColors.Color(0.11f, 0.11f, 0.11f, 1f)), 72);
            drawList.AddCircle(center, radius, ImGui.GetColorU32(ThemeColors.Color(0.10f, 0.10f, 0.10f, 1f)), 96, 3.5f * scale);
            drawList.AddCircle(center, innerRadius, ImGui.GetColorU32(ThemeColors.Color(0.10f, 0.10f, 0.10f, 1f)), 72, 3f * scale);

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
                EditorIcon.Metrics iconMetrics = EditorIcon.Measure(segment.Icon);

                var iconColor = ResolveWheelIconColor(segment);
                EditorIcon.Draw(
                    drawList,
                    segment.Icon,
                    iconMetrics,
                    labelPosition - (iconMetrics.Size * 0.5f),
                    iconColor);
            }

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
        string scaleLabel;
        if (scaleEnabled)
        {
            scaleLabel = "Scale Gizmo";
        }
        else if (context.SelectionCount > 1)
        {
            scaleLabel = "Select one scalable item to use the scale gizmo";
        }
        else
        {
            scaleLabel = "This item does not support scaling";
        }

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
                scaleLabel,
                EditorColors.TransformModeAccent(GizmoTransformMode.Scale),
                Mode == GizmoTransformMode.Scale,
                scaleEnabled,
                () => Mode = GizmoTransformMode.Scale),
            new GizmoWheelSegment(
                FontAwesomeIcon.Cube,
                "Local Space",
                ThemeColors.AccentOrange,
                CurrentBoundsOverlaySpace == BoundsOverlaySpace.Local,
                true,
                () => CurrentBoundsOverlaySpace = BoundsOverlaySpace.Local),
            new GizmoWheelSegment(
                FontAwesomeIcon.Globe,
                "World Space",
                ThemeColors.AccentBlue,
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

        bool singleSelection = selectionCount == 1;
        string visibilityAction = anyVisible ? "Hide" : "Show";
        var duplicateLabel = singleSelection ? "Duplicate Selected Item" : "Duplicate Selected Items";
        var visibilityLabel = $"{visibilityAction} Selected {(singleSelection ? "Item" : "Items")}";
        var visibilityHistoryTitle = $"{visibilityAction} {(singleSelection ? "Item" : "Items")}";
        var resetLabel = singleSelection ? "Reset Rotation and Scale" : "Reset Rotation and Scale For Selected Items";
        var removeLabel = singleSelection ? "Remove Selected Item" : "Remove Selected Items";
        return
        [
            new GizmoWheelSegment(
                FontAwesomeIcon.Copy,
                duplicateLabel,
                ThemeColors.Color(0.35f, 0.75f, 0.95f, 1f),
                false,
                true,
                () => _host.TryDuplicateSelectedItems(selectedSnapshots)),
            new GizmoWheelSegment(
                FontAwesomeIcon.Running,
                canMoveToPlayer ? "Move Selected Item To Player" : "Move to player is only available for one selected item",
                ThemeColors.Color(0.50f, 0.90f, 0.60f, 1f),
                false,
                canMoveToPlayer,
                () => _host.TryMoveItemToPlayerWithHistory(snapshot.Id)),
            new GizmoWheelSegment(
                anyVisible ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash,
                visibilityLabel,
                ThemeColors.Color(0.75f, 0.75f, 0.90f, 1f),
                anyVisible,
                true,
                () => _host.TryApplySelectedSnapshotUpdateWithHistory(
                    SceneHistoryKind.Visibility,
                    visibilityHistoryTitle,
                    selectedSnapshots,
                    entry => entry with { Visible = !anyVisible })),
            new GizmoWheelSegment(
                FontAwesomeIcon.Recycle,
                resetLabel,
                ThemeColors.Color(0.50f, 0.70f, 0.95f, 1f),
                false,
                true,
                () => _host.TryApplySelectedSnapshotUpdateWithHistory(
                    SceneHistoryKind.Transform,
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
                ThemeColors.Color(0.95f, 0.40f, 0.40f, 1f),
                false,
                true,
                () => _host.TryRemoveSelectedItems(selectedSnapshots)),
            new GizmoWheelSegment(
                FontAwesomeIcon.TimesCircle,
                "Hide Gizmo",
                ThemeColors.Color(0.65f, 0.55f, 0.95f, 1f),
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

