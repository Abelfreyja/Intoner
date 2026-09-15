using Dalamud.Interface;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoDrawOptions(
    bool PointerBlocked,
    EditorScreenArea? ObscuredArea)
{
    public static GizmoDrawOptions Unobstructed { get; } = new(false, null);

    public bool CanUsePointer(Vector2 position)
        => !PointerBlocked
           && !(ObscuredArea?.Contains(position) ?? false);

    public bool CanDrawLabel(Vector2 min, Vector2 max)
        => !(ObscuredArea?.Intersects(min, max) ?? false);
}

[StructLayout(LayoutKind.Auto)]
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

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TranslationDragPlaneContext(
    Vector3 PlanePoint,
    Vector3 PlaneNormal,
    Vector3 PlaneStartPoint,
    Vector2 ViewportPos,
    Vector2 ViewportSize);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoContext(
    IReadOnlyList<SceneItemSnapshot> SelectedSnapshots,
    SceneItemSnapshot PrimarySnapshot,
    IReadOnlyList<SceneItemBoundsSnapshot> BoundsSnapshots,
    SceneItemBoundsLookup BoundsLookup,
    SceneItemBoundsSnapshot? BoundsSnapshot,
    SceneItemManipulationPolicy Manipulation,
    SceneItemManipulation ManipulationOptions,
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
    bool ScaleSupported,
    bool SurfaceDragSupported)
{
    public int SelectionCount => SelectedSnapshots.Count;
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct GizmoSelectionEntry
{
    public GizmoSelectionEntry(SceneItemSnapshot snapshot, Vector3 pivotOffset, Quaternion startRotationQuaternion)
        : this(snapshot, pivotOffset, startRotationQuaternion, false, default, default)
    {
    }

    public GizmoSelectionEntry(
        SceneItemSnapshot snapshot,
        Vector3 pivotOffset,
        Quaternion startRotationQuaternion,
        bool hasBoundsData,
        Vector3 boundsCenterLocalOffset,
        Vector3 boundsHalfExtents)
    {
        Snapshot = snapshot;
        PivotOffset = pivotOffset;
        StartRotationQuaternion = SceneTransformMath.NormalizeQuaternion(startRotationQuaternion);
        HasBoundsData = hasBoundsData;
        BoundsCenterLocalOffset = boundsCenterLocalOffset;
        BoundsHalfExtents = boundsHalfExtents;
    }

    public SceneItemSnapshot Snapshot { get; }
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

        rotation = SceneTransformMath.NormalizeQuaternion(rotation);
        return -Vector3.Transform(BoundsCenterLocalOffset, rotation);
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoAxisProjectionState(int DirectionSign, Vector2? ScreenDirection);

[StructLayout(LayoutKind.Auto)]
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

[StructLayout(LayoutKind.Auto)]
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

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoInteractionAvailability(
    bool PointerInRegion,
    bool DragActive,
    bool SurfaceDragActive,
    bool WheelOpen)
{
    public bool CanResolveHover
        => PointerInRegion && !DragActive && !SurfaceDragActive && !WheelOpen;

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

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoInteractionState(
    GizmoInteractionPhase Phase,
    bool PointerInRegion,
    bool CenterHovered,
    GizmoAxis ActiveAxis)
{
    public bool IsFocused
        => Phase != GizmoInteractionPhase.Idle;

    public bool DragActive
        => Phase == GizmoInteractionPhase.TransformDrag;

    public bool SurfaceDragActive
        => Phase == GizmoInteractionPhase.SurfaceDrag;

    public bool IsHovering
        => Phase is GizmoInteractionPhase.HoverAxis or GizmoInteractionPhase.HoverCenter;

    public bool ShouldCaptureMouse
        => Phase is GizmoInteractionPhase.HoverAxis or GizmoInteractionPhase.HoverCenter or GizmoInteractionPhase.TransformDrag;

    public bool CanStartSurfaceDrag
        => Phase == GizmoInteractionPhase.HoverCenter && CenterHovered;
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoHandleHit(
    GizmoTransformMode Operation,
    GizmoAxisVisualState LinearAxis,
    RotationHoverState Rotation,
    float Distance,
    bool IsEndpoint)
{
    public bool IsValid => Operation != GizmoTransformMode.None;

    public GizmoAxis Axis => Operation == GizmoTransformMode.Rotation ? Rotation.Axis : LinearAxis.Axis;

    public bool IsCloserThan(in GizmoHandleHit other)
        => IsValid && (!other.IsValid
            || (IsEndpoint != other.IsEndpoint ? IsEndpoint : Distance < other.Distance));
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoFrame(
    GizmoContext Context,
    GizmoTransformMode Modes,
    int TranslationAxisCount,
    int ScaleAxisCount,
    RotationProjectionContext RotationProjection,
    GizmoInteractionState Interaction,
    GizmoHandleHit HoveredHandle,
    GizmoTransformMode ActiveOperation)
{
    public bool IsCombined => Modes.IsCombined();

    public bool HasMode(GizmoTransformMode mode) => (Modes & mode) != GizmoTransformMode.None;

    public bool BlocksSelection
        => Interaction.Phase is GizmoInteractionPhase.HoverAxis or GizmoInteractionPhase.HoverCenter;

    public GizmoInteractionState ForOperation(GizmoTransformMode operation)
    {
        GizmoInteractionState interaction = Interaction;
        if ((interaction.DragActive && ActiveOperation != operation)
         || (interaction.Phase == GizmoInteractionPhase.HoverAxis && HoveredHandle.Operation != operation))
        {
            return interaction with { Phase = GizmoInteractionPhase.Idle, ActiveAxis = GizmoAxis.None };
        }

        return interaction;
    }
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoFrameRequest(
    int FrameCount,
    int InteractionRevision,
    GizmoTransformMode Mode,
    BoundsOverlaySpace BoundsOverlaySpace,
    int SelectionRevision,
    long SceneRevision,
    Vector2 MousePosition,
    bool PointerAvailable);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoCachedFrame(GizmoFrameRequest Request, GizmoFrame Frame)
{
    public bool Matches(in GizmoFrameRequest request)
        => Request == request;
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoMetricInfo(string DeltaText, string? CurrentValueText)
{
    public string Text
        => string.IsNullOrEmpty(CurrentValueText)
            ? DeltaText
            : $"{DeltaText}\n{CurrentValueText}";
}

[StructLayout(LayoutKind.Auto)]
internal readonly struct GizmoPositionSnapPolicy
{
    public GizmoPositionSnapPolicy(bool enabled, float step, in SceneSnapBasis basis, Vector3 referencePosition)
    {
        Enabled = enabled;
        Step = step;
        Basis = basis;
        ReferencePosition = referencePosition;
    }

    public bool Enabled { get; }

    public float Step { get; }

    public SceneSnapBasis Basis { get; }

    public Vector3 ReferencePosition { get; }

    public Vector3 SnapPosition(Vector3 position)
        => !Enabled
            ? position
            : SceneTransformSnapUtility.SnapPosition(position, Step, Basis);

    public Vector3 SnapAxis(Vector3 position, int axisIndex)
        => !Enabled
            ? position
            : SceneTransformSnapUtility.SnapPositionAxis(position, axisIndex, Step, Basis);

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

        return SceneTransformSnapUtility.SnapPositionAxes(
            ReferencePosition,
            primaryAxis == GizmoAxis.X || secondaryAxis == GizmoAxis.X,
            primaryAxis == GizmoAxis.Y || secondaryAxis == GizmoAxis.Y,
            primaryAxis == GizmoAxis.Z || secondaryAxis == GizmoAxis.Z,
            Step,
            Basis);
    }
}

[StructLayout(LayoutKind.Auto)]
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
            : SceneTransformSnapUtility.SnapAngleDegrees(degrees, RotationStepDegrees);

    public Vector3 SnapScale(Vector3 scale)
        => !ScaleEnabled
            ? scale
            : SceneTransformSnapUtility.SnapScale(scale, ScaleStep);
}

internal enum GizmoWheelAction
{
    Universal,
    Move,
    Rotate,
    Scale,
    LocalSpace,
    WorldSpace,
    Bounds,
    Duplicate,
    MoveToPlayer,
    Visibility,
    ResetTransform,
    Remove,
    Hide,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoWheelSegment(
    GizmoWheelAction Action,
    FontAwesomeIcon Icon,
    string Tooltip,
    Vector4 Color,
    bool IsActive = false,
    bool IsEnabled = true,
    GizmoTransformMode Mode = GizmoTransformMode.None);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoRadialTooltipInfo(
    Vector2 MousePosition,
    string Title);

