using Dalamud.Bindings.ImGui;
using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal abstract class GizmoTransformDragSession
{
    private readonly GizmoDragSnapshotState _snapshots = new();

    public abstract GizmoTransformMode Mode { get; }

    public bool IsDragging { get; protected set; }

    public Guid ItemId { get; protected set; }

    public GizmoAxis ActiveAxis { get; protected set; }

    public Vector2 StartMouse { get; protected set; }

    public Vector2 AxisScreenDirection { get; protected set; }

    public float AxisScreenLength { get; protected set; }

    public Vector3 AxisWorldDirection { get; protected set; }

    public float AxisWorldLength { get; protected set; }

    public IReadOnlyList<GizmoSelectionEntry> SelectionEntries
        => _snapshots.SelectionEntries;

    public SceneItemSnapshot StartSnapshot
        => _snapshots.PrimarySnapshot;

    public Vector3 StartPosition { get; protected set; }

    public bool Matches(Guid itemId, GizmoTransformMode mode)
        => IsDragging && ItemId == itemId && Mode == mode;

    public void RecordAppliedSnapshot(SceneItemSnapshot appliedSnapshot)
        => _snapshots.Record(appliedSnapshot);

    public void RecordAppliedSnapshots(IReadOnlyList<SceneItemSnapshot> appliedSnapshots)
        => _snapshots.Record(appliedSnapshots);

    public bool TryGetHistorySnapshots(out SceneItemSnapshot[] beforeSnapshots, out SceneItemSnapshot[] afterSnapshots)
    {
        if (!IsDragging)
        {
            beforeSnapshots = [];
            afterSnapshots = [];
            return false;
        }

        return _snapshots.TryGetHistorySnapshots(out beforeSnapshots, out afterSnapshots);
    }

    public bool TryResolveAppliedSnapshot(out SceneItemSnapshot snapshot)
        => _snapshots.TryGetAppliedSnapshot(ItemId, out snapshot);

    public Vector3 ResolveReferenceRotationDegrees(Guid itemId)
        => _snapshots.ResolveReferenceRotationDegrees(itemId);

    public abstract void Reset();

    protected void BeginCore(
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        SceneItemSnapshot primarySnapshot,
        Vector3 pivotPosition,
        GizmoAxis axis,
        Vector2 axisScreenDirection,
        float axisScreenLength,
        Vector3 axisWorldDirection,
        float axisWorldLength)
    {
        IsDragging = true;
        ItemId = primarySnapshot.Id;
        ActiveAxis = axis;
        StartMouse = ImGui.GetIO().MousePos;
        AxisScreenDirection = NumericsUtility.TryNormalize(axisScreenDirection, out var normalizedAxisScreenDirection)
            ? normalizedAxisScreenDirection
            : Vector2.UnitX;
        AxisScreenLength = axisScreenLength;
        AxisWorldDirection = NumericsUtility.TryNormalize(axisWorldDirection, out var normalizedAxisWorldDirection)
            ? normalizedAxisWorldDirection
            : axisWorldDirection;
        AxisWorldLength = axisWorldLength;
        _snapshots.Begin(
            GizmoSelectionTransformUtility.CreateSelectionEntries(selectedSnapshots, pivotPosition),
            primarySnapshot);
        StartPosition = pivotPosition;
    }

    protected void ResetCore()
    {
        IsDragging = false;
        ItemId = Guid.Empty;
        ActiveAxis = GizmoAxis.None;
        StartMouse = default;
        AxisScreenDirection = default;
        AxisScreenLength = 0f;
        AxisWorldDirection = default;
        AxisWorldLength = 0f;
        _snapshots.Reset();
        StartPosition = default;
    }
}

internal sealed class GizmoTranslationDragSession : GizmoTransformDragSession
{
    public override GizmoTransformMode Mode
        => GizmoTransformMode.Translation;

    public Vector3 LastPosition { get; private set; }

    public Quaternion StartRotationQuaternion { get; private set; }

    public bool UseWorldSpace { get; private set; }

    public TranslationDragPlaneContext? TranslationPlane { get; private set; }

    public void Begin(
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        SceneItemSnapshot primarySnapshot,
        Vector3 pivotPosition,
        Quaternion pivotRotation,
        GizmoAxis axis,
        Vector2 axisScreenDirection,
        float axisScreenLength,
        Vector3 axisWorldDirection,
        float axisWorldLength,
        bool useWorldSpace)
    {
        Reset();
        BeginCore(
            selectedSnapshots,
            primarySnapshot,
            pivotPosition,
            axis,
            axisScreenDirection,
            axisScreenLength,
            axisWorldDirection,
            axisWorldLength);
        LastPosition = pivotPosition;
        StartRotationQuaternion = pivotRotation;
        UseWorldSpace = useWorldSpace;
    }

    public void RecordAppliedPosition(Vector3 position)
        => LastPosition = position;

    public void SetTranslationPlane(Vector3 planePoint, Vector3 planeNormal, Vector3 planeStartPoint, Vector2 viewportPos, Vector2 viewportSize)
        => TranslationPlane = new TranslationDragPlaneContext(planePoint, planeNormal, planeStartPoint, viewportPos, viewportSize);

    public override void Reset()
    {
        ResetCore();
        LastPosition = default;
        StartRotationQuaternion = Quaternion.Identity;
        UseWorldSpace = false;
        TranslationPlane = null;
    }
}

internal sealed class GizmoRotationDragSession : GizmoTransformDragSession
{
    public override GizmoTransformMode Mode
        => GizmoTransformMode.Rotation;

    public Quaternion StartRotationQuaternion { get; private set; }

    public bool UseWorldSpace { get; private set; }

    public RotationProjectionContext? RotationProjection { get; private set; }

    public float? RotationDragStartAngle { get; private set; }

    public float? RotationDragLastAngle { get; private set; }

    public float RotationDragAccumulatedRadians { get; private set; }

    public float RotationDragAppliedRadians { get; private set; }

    public Vector2 RotationDragLastMouse { get; private set; }

    public void Begin(
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        SceneItemSnapshot primarySnapshot,
        Vector3 pivotPosition,
        Quaternion pivotRotation,
        GizmoAxis axis,
        Vector2 tangent,
        bool useWorldSpace,
        float startAngle,
        in RotationProjectionContext projection)
    {
        Reset();
        BeginCore(
            selectedSnapshots,
            primarySnapshot,
            pivotPosition,
            axis,
            tangent,
            1f,
            GizmoAxisUtility.ToUnitVector(axis),
            1f);
        StartRotationQuaternion = pivotRotation;
        UseWorldSpace = useWorldSpace;
        RotationProjection = projection;
        RotationDragStartAngle = startAngle;
        RotationDragLastAngle = startAngle;
        RotationDragLastMouse = StartMouse;
    }

    public void RecordCurrentStep(float angle, Vector2 mousePos)
    {
        RotationDragLastAngle = angle;
        RotationDragLastMouse = mousePos;
    }

    public void AccumulateRadians(float radians)
        => RotationDragAccumulatedRadians += radians;

    public void RecordAppliedRotation(float appliedRadians)
        => RotationDragAppliedRadians = appliedRadians;

    public override void Reset()
    {
        ResetCore();
        StartRotationQuaternion = Quaternion.Identity;
        UseWorldSpace = false;
        RotationProjection = null;
        RotationDragStartAngle = null;
        RotationDragLastAngle = null;
        RotationDragAccumulatedRadians = 0f;
        RotationDragAppliedRadians = 0f;
        RotationDragLastMouse = default;
    }
}

internal sealed class GizmoScaleDragSession : GizmoTransformDragSession
{
    public override GizmoTransformMode Mode
        => GizmoTransformMode.Scale;

    public Vector3 StartScale { get; private set; }

    public Vector3 LastScale { get; private set; }

    public void Begin(
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        SceneItemSnapshot primarySnapshot,
        Vector3 pivotPosition,
        GizmoAxis axis,
        Vector2 axisScreenDirection,
        float axisScreenLength,
        Vector3 axisWorldDirection,
        float axisWorldLength)
    {
        Reset();
        BeginCore(
            selectedSnapshots,
            primarySnapshot,
            pivotPosition,
            axis,
            axisScreenDirection,
            axisScreenLength,
            axisWorldDirection,
            axisWorldLength);
        StartScale = primarySnapshot.Transform.Scale;
        LastScale = primarySnapshot.Transform.Scale;
    }

    public void RecordAppliedScale(Vector3 scale)
        => LastScale = scale;

    public override void Reset()
    {
        ResetCore();
        StartScale = Vector3.One;
        LastScale = Vector3.One;
    }
}

