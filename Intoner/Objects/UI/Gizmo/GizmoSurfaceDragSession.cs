using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal enum GizmoSurfaceDragRotationAxis
{
    Yaw,
    Pitch,
}

internal readonly record struct GizmoSurfaceDragRotationStep(GizmoSurfaceDragRotationAxis Axis, int StepCount);

/// <summary> runtime session for one active surface drag </summary>
internal sealed class GizmoSurfaceDragSession
{
    private GizmoSelectionEntry[] _selectionEntries = [];
    private ObjectSnapshot? _lastAppliedSnapshot;
    private IReadOnlyList<ObjectSnapshot> _lastAppliedSnapshots = [];
    private readonly List<GizmoSurfaceDragRotationStep> _rotationSteps = [];

    public bool IsDragging { get; private set; }

    public Guid ObjectId { get; private set; }

    public IReadOnlyList<GizmoSelectionEntry> SelectionEntries
        => _selectionEntries;

    public ObjectSnapshot StartSnapshot { get; private set; } = null!;

    public ObjectSnapshot CurrentSingleSnapshot
        => _lastAppliedSnapshot ?? StartSnapshot;

    public Quaternion StartRotationQuaternion { get; private set; } = Quaternion.Identity;

    public ObjectSurfaceTargetSnapshot SurfaceTargets { get; private set; } = ObjectSurfaceTargetSnapshot.Empty;

    public bool ObjectTargetsEnabled { get; private set; }

    public SurfaceObjectTargetShape ObjectTargetShape { get; private set; } = SurfaceObjectTargetShape.Bounds;

    public IReadOnlyList<GizmoSurfaceDragRotationStep> RotationSteps
        => _rotationSteps;

    public Vector3 LastResolvedPosition { get; private set; }

    public Vector3 LastResolvedRotationDegrees { get; private set; }

    public bool Matches(Guid objectId)
        => IsDragging && ObjectId == objectId;

    public bool IsSingleSelection
        => _selectionEntries.Length == 1;

    public bool IsMultiSelection
        => _selectionEntries.Length > 1;

    public GizmoSelectionEntry PrimaryEntry
        => _selectionEntries[0];

    public void Begin(
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        ObjectSnapshot snapshot,
        Vector3 pivotPosition,
        ObjectSurfaceTargetSnapshot surfaceTargets,
        bool objectTargetsEnabled,
        SurfaceObjectTargetShape objectTargetShape)
    {
        Reset();
        IsDragging = true;
        ObjectId = snapshot.Id;
        _selectionEntries = GizmoSelectionTransformUtility.CreateSelectionEntries(selectedSnapshots, boundsSnapshots, pivotPosition);
        _lastAppliedSnapshot = null;
        _lastAppliedSnapshots = [];
        StartSnapshot = snapshot;
        StartRotationQuaternion = ObjectTransformMath.CreateRotationQuaternion(snapshot.Transform.RotationDegrees);
        SurfaceTargets = surfaceTargets;
        ObjectTargetsEnabled = objectTargetsEnabled;
        ObjectTargetShape = objectTargetShape;
        LastResolvedPosition = pivotPosition;
        LastResolvedRotationDegrees = snapshot.Transform.RotationDegrees;
    }

    public void AddRotationSteps(int yawSteps, int pitchSteps)
    {
        AppendRotationStep(GizmoSurfaceDragRotationAxis.Yaw, yawSteps);
        AppendRotationStep(GizmoSurfaceDragRotationAxis.Pitch, pitchSteps);
    }

    public void RecordSingleApply(Vector3 resolvedPosition, Vector3 resolvedRotationDegrees, ObjectSnapshot appliedSnapshot)
    {
        _lastAppliedSnapshot = appliedSnapshot;
        _lastAppliedSnapshots = [];
        LastResolvedPosition = resolvedPosition;
        LastResolvedRotationDegrees = resolvedRotationDegrees;
    }

    public void RecordSelectionApply(Vector3 resolvedPivotPosition, Vector3 resolvedRotationDegrees, IReadOnlyList<ObjectSnapshot> appliedSnapshots)
    {
        _lastAppliedSnapshot = null;
        _lastAppliedSnapshots = appliedSnapshots;
        LastResolvedPosition = resolvedPivotPosition;
        LastResolvedRotationDegrees = resolvedRotationDegrees;
    }

    public bool TryGetHistorySnapshots(out ObjectSnapshot[] beforeSnapshots, out ObjectSnapshot[] afterSnapshots)
    {
        if (!IsDragging)
        {
            beforeSnapshots = [];
            afterSnapshots = [];
            return false;
        }

        return GizmoSessionSnapshotUtility.TryGetHistorySnapshots(_selectionEntries, _lastAppliedSnapshot, _lastAppliedSnapshots, out beforeSnapshots, out afterSnapshots);
    }

    public Vector3 ResolveReferenceRotationDegrees(Guid objectId)
        => GizmoSessionSnapshotUtility.ResolveReferenceRotationDegrees(objectId, _selectionEntries, _lastAppliedSnapshot, _lastAppliedSnapshots, StartSnapshot);

    public void Reset()
    {
        IsDragging = false;
        ObjectId = Guid.Empty;
        _selectionEntries = [];
        _lastAppliedSnapshot = null;
        _lastAppliedSnapshots = [];
        _rotationSteps.Clear();
        StartSnapshot = null!;
        StartRotationQuaternion = Quaternion.Identity;
        SurfaceTargets = ObjectSurfaceTargetSnapshot.Empty;
        ObjectTargetsEnabled = false;
        ObjectTargetShape = SurfaceObjectTargetShape.Bounds;
        LastResolvedPosition = default;
        LastResolvedRotationDegrees = default;
    }

    private void AppendRotationStep(GizmoSurfaceDragRotationAxis axis, int stepCount)
    {
        if (stepCount == 0)
        {
            return;
        }

        var lastIndex = _rotationSteps.Count - 1;
        if (lastIndex >= 0 && _rotationSteps[lastIndex].Axis == axis)
        {
            var mergedStepCount = _rotationSteps[lastIndex].StepCount + stepCount;
            if (mergedStepCount == 0)
            {
                _rotationSteps.RemoveAt(lastIndex);
            }
            else
            {
                _rotationSteps[lastIndex] = new GizmoSurfaceDragRotationStep(axis, mergedStepCount);
            }

            return;
        }

        _rotationSteps.Add(new GizmoSurfaceDragRotationStep(axis, stepCount));
    }
}

