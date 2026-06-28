using Dalamud.Interface;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal readonly record struct RotationProjectionContext(
    Vector2 Center,
    float ScreenRadius,
    float VisualRadius,
    float WorldRadius,
    Vector3 WorldPosition,
    Matrix4x4 ViewProjection,
    Vector2 ViewportPos,
    Vector2 ViewportSize,
    Quaternion Rotation,
    bool UseWorldSpace,
    Vector3? CameraViewDirection,
    Vector3? CameraRight,
    Vector3? CameraUp);

internal readonly record struct TranslationDragPlaneContext(
    Vector3 PlanePoint,
    Vector3 PlaneNormal,
    Vector3 PlaneStartPoint,
    Vector2 ViewportPos,
    Vector2 ViewportSize);

internal readonly record struct GizmoContext(
    IReadOnlyList<ObjectSnapshot> SelectedSnapshots,
    ObjectSnapshot PrimarySnapshot,
    IReadOnlyList<ObjectBoundsSnapshot> BoundsSnapshots,
    ObjectBoundsSnapshot? BoundsSnapshot,
    Vector3 PivotPosition,
    Vector2 ScreenPos,
    Vector2 ViewportPos,
    Vector2 ViewportSize,
    Matrix4x4 ViewProjection,
    Quaternion Rotation,
    Vector3? CameraViewDirection,
    Vector3? CameraRight,
    Vector3? CameraUp,
    float AxisWorldLength,
    bool UseWorldSpace,
    bool ScaleSupported)
{
    public int SelectionCount => SelectedSnapshots.Count;
}

internal readonly struct GizmoSelectionEntry
{
    public GizmoSelectionEntry(ObjectSnapshot snapshot, Vector3 pivotOffset, Quaternion startRotationQuaternion)
        : this(snapshot, pivotOffset, startRotationQuaternion, false, default, default)
    {
    }

    public GizmoSelectionEntry(
        ObjectSnapshot snapshot,
        Vector3 pivotOffset,
        Quaternion startRotationQuaternion,
        bool hasBoundsData,
        Vector3 boundsCenterLocalOffset,
        Vector3 boundsHalfExtents)
    {
        Snapshot = snapshot;
        PivotOffset = pivotOffset;
        StartRotationQuaternion = ObjectTransformMath.NormalizeQuaternion(startRotationQuaternion);
        HasBoundsData = hasBoundsData;
        BoundsCenterLocalOffset = boundsCenterLocalOffset;
        BoundsHalfExtents = boundsHalfExtents;
    }

    public ObjectSnapshot Snapshot { get; }
    public Vector3 PivotOffset { get; }
    public Quaternion StartRotationQuaternion { get; }
    public bool HasBoundsData { get; }
    public Vector3 BoundsCenterLocalOffset { get; }
    public Vector3 BoundsHalfExtents { get; }

    public Vector3 ResolvePivotOffset(Quaternion rotation)
    {
        if (!HasBoundsData)
        {
            return PivotOffset;
        }

        rotation = ObjectTransformMath.NormalizeQuaternion(rotation);
        return -Vector3.Transform(BoundsCenterLocalOffset, rotation);
    }
}

internal readonly record struct GizmoAxisVisualState(
    GizmoAxis Axis,
    Vector2 ScreenStart,
    Vector2 ScreenEnd,
    Vector3 WorldDirection,
    float WorldLength,
    Vector2 ScreenDirection,
    float ScreenLength,
    float VisualScale)
{
    public static readonly GizmoAxisVisualState None = new(
        GizmoAxis.None,
        default,
        default,
        default,
        0f,
        default,
        0f,
        1f);

    public bool IsValid
        => Axis != GizmoAxis.None;
}

internal readonly record struct RotationHoverState(
    GizmoAxis Axis,
    float Distance,
    Vector2 Tangent,
    Vector2 ScreenPoint,
    bool HasPoint,
    float Angle)
{
    public bool IsValid
        => Axis != GizmoAxis.None;

    public static RotationHoverState None(float distanceTolerance)
        => new(GizmoAxis.None, distanceTolerance, Vector2.UnitX, default, false, 0f);
}

internal enum GizmoInteractionPhase
{
    Idle,
    HoverAxis,
    HoverCenter,
    TransformDrag,
    SurfaceDrag,
    RadialMenu,
}

internal static class GizmoInteractionPhaseExtensions
{
    public static bool IsFocused(this GizmoInteractionPhase phase)
        => phase != GizmoInteractionPhase.Idle;

    public static bool IsTransformDrag(this GizmoInteractionPhase phase)
        => phase == GizmoInteractionPhase.TransformDrag;

    public static bool IsSurfaceDrag(this GizmoInteractionPhase phase)
        => phase == GizmoInteractionPhase.SurfaceDrag;

    public static bool ShouldCaptureMouse(this GizmoInteractionPhase phase)
        => phase is GizmoInteractionPhase.HoverAxis or GizmoInteractionPhase.HoverCenter or GizmoInteractionPhase.TransformDrag;

    public static bool CanStartSurfaceDrag(this GizmoInteractionPhase phase, bool centerHovered)
        => phase == GizmoInteractionPhase.HoverCenter && centerHovered;
}

internal readonly record struct GizmoInteractionAvailability(
    bool PointerInRegion,
    bool DragActive,
    bool SurfaceDragActive,
    bool WheelOpen)
{
    public bool CanResolveHover
        => !DragActive && !SurfaceDragActive && !WheelOpen;

    public GizmoInteractionPhase ResolvePhase(bool centerHovered, bool axisHovered)
    {
        if (DragActive)
        {
            return GizmoInteractionPhase.TransformDrag;
        }

        if (SurfaceDragActive)
        {
            return GizmoInteractionPhase.SurfaceDrag;
        }

        if (WheelOpen)
        {
            return GizmoInteractionPhase.RadialMenu;
        }

        if (centerHovered)
        {
            return GizmoInteractionPhase.HoverCenter;
        }

        return axisHovered
            ? GizmoInteractionPhase.HoverAxis
            : GizmoInteractionPhase.Idle;
    }
}

internal readonly record struct GizmoInteractionState(
    GizmoInteractionPhase Phase,
    bool PointerInRegion,
    bool CenterHovered,
    GizmoAxis ActiveAxis)
{
    public bool IsFocused
        => Phase.IsFocused();

    public bool DragActive
        => Phase.IsTransformDrag();

    public bool SurfaceDragActive
        => Phase.IsSurfaceDrag();

    public bool ShouldCaptureMouse
        => Phase.ShouldCaptureMouse();

    public bool CanStartSurfaceDrag
        => Phase.CanStartSurfaceDrag(CenterHovered);
}

internal readonly record struct GizmoLinearInteractionState(
    GizmoInteractionState Common,
    GizmoAxis HoveredAxis,
    GizmoAxisVisualState HoveredAxisState,
    bool AxisHovered)
{
    public bool CanStartAxisDrag
        => Common.Phase == GizmoInteractionPhase.HoverAxis && HoveredAxisState.IsValid;
}

internal readonly record struct GizmoRotationInteractionState(
    GizmoInteractionState Common,
    RotationHoverState HoverState,
    bool AxisHovered)
{
    public bool CanStartRotationDrag
        => Common.Phase == GizmoInteractionPhase.HoverAxis && HoverState.IsValid && HoverState.HasPoint;
}

internal readonly struct GizmoFrame
{
    public GizmoFrame(
        in GizmoContext context,
        int axisCount,
        in GizmoLinearInteractionState linearInteraction)
    {
        Context = context;
        AxisCount = axisCount;
        LinearInteraction = linearInteraction;
        RotationProjection = default;
        RotationInteraction = default;
        HasRotationFrame = false;
    }

    public GizmoFrame(
        in GizmoContext context,
        in RotationProjectionContext rotationProjection,
        in GizmoRotationInteractionState rotationInteraction)
    {
        Context = context;
        AxisCount = 0;
        LinearInteraction = default;
        RotationProjection = rotationProjection;
        RotationInteraction = rotationInteraction;
        HasRotationFrame = true;
    }

    public GizmoContext Context { get; }

    public int AxisCount { get; }

    public GizmoLinearInteractionState LinearInteraction { get; }

    public RotationProjectionContext RotationProjection { get; }

    public GizmoRotationInteractionState RotationInteraction { get; }

    public bool HasRotationFrame { get; }

    public bool BlocksSelection
        => HasRotationFrame
            ? RotationInteraction.Common.Phase is GizmoInteractionPhase.HoverAxis or GizmoInteractionPhase.HoverCenter
            : LinearInteraction.Common.Phase is GizmoInteractionPhase.HoverAxis or GizmoInteractionPhase.HoverCenter;
}

internal readonly record struct GizmoFrameRequest(
    int FrameCount,
    int InteractionRevision,
    GizmoTransformMode Mode,
    BoundsOverlaySpace BoundsOverlaySpace,
    int SelectionRevision,
    long SceneRevision,
    Vector2 MousePosition);

internal readonly record struct GizmoCachedFrame(GizmoFrameRequest Request, GizmoFrame Frame)
{
    public bool Matches(in GizmoFrameRequest request)
        => Request == request;
}

internal readonly record struct GizmoMetricInfo(string DeltaText, string? CurrentValueText)
{
    public string Text
        => string.IsNullOrEmpty(CurrentValueText)
            ? DeltaText
            : $"{DeltaText}\n{CurrentValueText}";
}

internal readonly struct GizmoPositionSnapPolicy
{
    public GizmoPositionSnapPolicy(bool enabled, float step, in ObjectSnapBasis basis, Vector3 referencePosition)
    {
        Enabled = enabled;
        Step = step;
        Basis = basis;
        ReferencePosition = referencePosition;
    }

    public bool Enabled { get; }

    public float Step { get; }

    public ObjectSnapBasis Basis { get; }

    public Vector3 ReferencePosition { get; }

    public Vector3 SnapPosition(Vector3 position)
        => !Enabled
            ? position
            : ObjectTransformSnapUtility.SnapPosition(position, Step, Basis);

    public Vector3 SnapAxis(Vector3 position, int axisIndex)
        => !Enabled
            ? position
            : ObjectTransformSnapUtility.SnapPositionAxis(position, axisIndex, Step, Basis);

    public Vector3 ResolveGridOrigin(GizmoAxis primaryAxis, GizmoAxis secondaryAxis, GizmoAxis preferredAxis)
    {
        if (!Enabled)
        {
            return ReferencePosition;
        }

        if (preferredAxis != GizmoAxis.None)
        {
            return SnapAxis(ReferencePosition, GizmoAxisUtility.ToIndex(preferredAxis));
        }

        return ObjectTransformSnapUtility.SnapPositionAxes(
            ReferencePosition,
            primaryAxis == GizmoAxis.X || secondaryAxis == GizmoAxis.X,
            primaryAxis == GizmoAxis.Y || secondaryAxis == GizmoAxis.Y,
            primaryAxis == GizmoAxis.Z || secondaryAxis == GizmoAxis.Z,
            Step,
            Basis);
    }
}

internal readonly struct GizmoTransformSnapPolicy
{
    public GizmoTransformSnapPolicy(
        in GizmoPositionSnapPolicy position,
        bool rotationEnabled,
        float rotationStepDegrees,
        bool scaleEnabled,
        float scaleStep)
    {
        Position = position;
        RotationEnabled = rotationEnabled;
        RotationStepDegrees = rotationStepDegrees;
        ScaleEnabled = scaleEnabled;
        ScaleStep = scaleStep;
    }

    public GizmoPositionSnapPolicy Position { get; }

    public bool RotationEnabled { get; }

    public float RotationStepDegrees { get; }

    public bool ScaleEnabled { get; }

    public float ScaleStep { get; }

    public bool PositionEnabled
        => Position.Enabled;

    public float PositionStep
        => Position.Step;

    public Vector3 SnapPosition(Vector3 position)
        => Position.SnapPosition(position);

    public Vector3 SnapPositionAxis(Vector3 position, int axisIndex)
        => Position.SnapAxis(position, axisIndex);

    public Vector3 ResolveGridOrigin(GizmoAxis primaryAxis, GizmoAxis secondaryAxis, GizmoAxis preferredAxis)
        => Position.ResolveGridOrigin(primaryAxis, secondaryAxis, preferredAxis);

    public float SnapRotationDegrees(float degrees)
        => !RotationEnabled
            ? degrees
            : ObjectTransformSnapUtility.SnapAngleDegrees(degrees, RotationStepDegrees);

    public Vector3 SnapScale(Vector3 scale)
        => !ScaleEnabled
            ? scale
            : ObjectTransformSnapUtility.SnapScale(scale, ScaleStep);
}

internal readonly record struct GizmoWheelSegment(
    FontAwesomeIcon Icon,
    string Tooltip,
    Vector4 Color,
    bool IsActive,
    bool IsEnabled,
    Action OnClick);

internal readonly record struct GizmoRadialTooltipInfo(
    Vector2 MousePosition,
    string Title);

