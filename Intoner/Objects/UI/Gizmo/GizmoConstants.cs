using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace Intoner.Objects.UI;

/// <summary> just a bunch of constants for the gizmo </summary>
internal static class GizmoConstants
{
    public const float AxisMinScreenLength = 30f;
    public const float AxisBaseScreenLength = 100f;
    public const float AxisMaxScreenLength = 220f;
    public const float AxisMaxCompensatedHandleScale = 1.75f;
    public const float AxisLineThickness = 2.5f;
    public const float AxisArrowLength = 18f;
    public const float AxisArrowWidth = 6.5f;
    public const float AxisLabelDistance = 26f;
    public const float AxisLabelPadding = 4f;
    public const float AxisLabelRoundness = 3.5f;
    public const float AxisGlowThicknessMultiplier = 3f;
    public const float AxisCameraShiftStartAlignment = 0.80f;
    public const float AxisCameraShiftEndAlignment = 0.97f;

    public const float CenterPointRadius = 4f;
    public const float CenterInteractionRadius = 9f;
    public const float CenterGlowRadiusMultiplier = 2.2f;
    public const float CenterGlowOpacity = 0.35f;

    public const float SurfaceDragRotateStepDegrees = 90f;
    public const string SurfaceDragTooltip = "Drag along world collision surfaces";
    public static readonly SeVirtualKey[] SurfaceDragSuppressedKeys = [SeVirtualKey.R, SeVirtualKey.T];

    public const float ScaleHandleSize = 8f;
    public const float ScaleUnitsPerPixel = 0.003f;

    public const float RotationRingBaseRadius = 100f;
    public const float RotationProjectionMinReferenceWorldRadius = 0.1f;
    public const float RotationRingThickness = 2.6f;
    public const float RotationInteractionPadding = 30f;
    public const float RotationHoverTolerance = 14f;
    public const float RotationHoverIndicatorRadius = 10f;
    public const float RotationHoverIndicatorThickness = 2.2f;
    public const float RotationDragIndicatorRadius = 4f;
    public const float RotationBackgroundAlpha = 0.31f;
    public const float RotationHighlightThicknessMultiplier = 1.75f;
    public const float RotationHighlightSectorFillAlpha = 0.17f;
    public const float RotationHighlightSectorBoundaryAlpha = 0.58f;
    public const float RotationHighlightSectorBoundaryThicknessMultiplier = 1.05f;
    public const float RotationSnapTickLength = 5.5f;
    public const float RotationSnapMajorTickLength = 12f;
    public const float RotationSnapTickThickness = 1.0f;
    public const float RotationSnapTickAlpha = 0.82f;
    public const float RotationSnapTickHiddenAlpha = 0.36f;
    public const float RotationRadiansPerPixel = 1f / 150f;
    public const int RotationRingSegments = 96;
    public const int RotationProjectionSizeSolveIterations = 2;

    public const float TranslationDragPathThickness = 2.6f;
    public const float TranslationDragStartMarkerRadius = 3.5f;
    public const float SlowDragMultiplier = 1f / 5f;

    public const float IdleAlpha = 0.45f;
    public const float ActiveAlpha = 1f;

    public const float OptionWheelBaseRadius = 76f;
    public const float OptionWheelInnerRadiusFraction = 0.55f;
    public const float TooltipOffsetX = 16f;
    public const float TooltipOffsetY = 18f;
    public const string WheelPopupId = "ObjectEditorGizmoWheel";

    public const int SnapGridMaxCellsPerSide = 16;
    public const float SnapGridLineThickness = 1.15f;
    public const float SnapGridCenterLineThickness = 1.7f;
    public const float SnapGridCenterLineAlpha = 0.46f;
    public const float SnapGridLineAlpha = 0.21f;
}

