using Intoner.Objects.Utils;
using Intoner.Scene;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Intoner.Objects.UI;

internal enum GizmoSurfaceDragRotationAxis
{
    Yaw,
    Pitch,
}

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GizmoSurfaceDragRotationStep(GizmoSurfaceDragRotationAxis Axis, int StepCount);

/// <summary> runtime session for one active surface drag </summary>
internal sealed class GizmoSurfaceDragSession
{
    private readonly GizmoDragSnapshotState _snapshots = new();
    private readonly List<GizmoSurfaceDragRotationStep> _rotationSteps = [];
    private readonly HashSet<Guid> _selectedItemIds = [];

    public bool IsDragging { get; private set; }

    public Guid ItemId { get; private set; }

    public IReadOnlyList<GizmoSelectionEntry> SelectionEntries
        => _snapshots.SelectionEntries;

    public IReadOnlySet<Guid> SelectedItemIds
        => _selectedItemIds;

    public SceneItemSnapshot StartSnapshot
        => _snapshots.PrimarySnapshot;

    public SceneItemSnapshot CurrentSingleSnapshot
        => _snapshots.TryGetAppliedSnapshot(ItemId, out SceneItemSnapshot appliedSnapshot)
            ? appliedSnapshot
            : StartSnapshot;

    public Quaternion StartRotationQuaternion { get; private set; } = Quaternion.Identity;

    public SceneSurfaceTargetSnapshot SurfaceTargets { get; private set; } = SceneSurfaceTargetSnapshot.Empty;

    public bool ItemTargetsEnabled { get; private set; }

    public SceneSurfaceTargetShape TargetShape { get; private set; } = SceneSurfaceTargetShape.Bounds;

    public IReadOnlyList<GizmoSurfaceDragRotationStep> RotationSteps
        => _rotationSteps;

    public Vector3 LastResolvedPosition { get; private set; }

    public Vector3 LastResolvedRotationDegrees { get; private set; }

    public bool Matches(Guid itemId)
        => IsDragging && ItemId == itemId;

    public bool IsSingleSelection
        => SelectionEntries.Count == 1;

    public bool IsMultiSelection
        => SelectionEntries.Count > 1;

    public GizmoSelectionEntry PrimaryEntry
        => SelectionEntries[0];

    public void Begin(
        IReadOnlyList<SceneItemSnapshot> selectedSnapshots,
        SceneItemBoundsLookup boundsLookup,
        SceneItemSnapshot snapshot,
        Vector3 pivotPosition,
        IReadOnlySet<Guid> selectedItemIds,
        SceneSurfaceTargetSnapshot surfaceTargets,
        bool itemTargetsEnabled,
        SceneSurfaceTargetShape targetShape)
    {
        Reset();
        IsDragging = true;
        ItemId = snapshot.Id;
        _snapshots.Begin(
            GizmoSelectionTransformUtility.CreateSelectionEntries(selectedSnapshots, boundsLookup, pivotPosition),
            snapshot);
        _selectedItemIds.UnionWith(selectedItemIds);
        StartRotationQuaternion = SceneTransformMath.CreateRotationQuaternion(snapshot.Transform.RotationDegrees);
        SurfaceTargets = surfaceTargets;
        ItemTargetsEnabled = itemTargetsEnabled;
        TargetShape = targetShape;
        LastResolvedPosition = pivotPosition;
        LastResolvedRotationDegrees = snapshot.Transform.RotationDegrees;
    }

    public void AddRotationSteps(int yawSteps, int pitchSteps)
    {
        AppendRotationStep(GizmoSurfaceDragRotationAxis.Yaw, yawSteps);
        AppendRotationStep(GizmoSurfaceDragRotationAxis.Pitch, pitchSteps);
    }

    public void RecordSingleApply(Vector3 resolvedPosition, Vector3 resolvedRotationDegrees, SceneItemSnapshot appliedSnapshot)
    {
        _snapshots.Record(appliedSnapshot);
        LastResolvedPosition = resolvedPosition;
        LastResolvedRotationDegrees = resolvedRotationDegrees;
    }

    public void RecordSelectionApply(Vector3 resolvedPivotPosition, Vector3 resolvedRotationDegrees, IReadOnlyList<SceneItemSnapshot> appliedSnapshots)
    {
        _snapshots.Record(appliedSnapshots);
        LastResolvedPosition = resolvedPivotPosition;
        LastResolvedRotationDegrees = resolvedRotationDegrees;
    }

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

    public Vector3 ResolveReferenceRotationDegrees(Guid itemId)
        => _snapshots.ResolveReferenceRotationDegrees(itemId);

    public void Reset()
    {
        IsDragging = false;
        ItemId = Guid.Empty;
        _snapshots.Reset();
        _selectedItemIds.Clear();
        _rotationSteps.Clear();
        StartRotationQuaternion = Quaternion.Identity;
        SurfaceTargets = SceneSurfaceTargetSnapshot.Empty;
        ItemTargetsEnabled = false;
        TargetShape = SceneSurfaceTargetShape.Bounds;
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

