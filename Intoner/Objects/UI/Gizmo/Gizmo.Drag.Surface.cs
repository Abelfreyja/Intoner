using FFXIVClientStructs.FFXIV.Client.System.Input;
using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private void BeginGizmoSurfaceDrag(in GizmoContext context)
    {
        var objectTargetsEnabled = Settings.SurfaceObjectTargetsEnabled;
        var objectTargetShape = Settings.SurfaceObjectTargetShape;
        var surfaceTargets = CaptureSurfaceTargets(context, objectTargetsEnabled, objectTargetShape);
        State.BeginSurfaceDrag(
            context.SelectedSnapshots,
            context.BoundsSnapshots,
            context.PrimarySnapshot,
            context.PivotPosition,
            surfaceTargets,
            objectTargetsEnabled,
            objectTargetShape);
        DisposeSurfaceDragInputSuppressionLease();
        SurfaceDragInputSuppressionLease = _gameInputSuppressionService.BeginKeyboardSuppression(GizmoConstants.SurfaceDragSuppressedKeys);
    }

    private bool HandleGizmoSurfaceDragLifecycle(in GizmoContext context)
    {
        var matchesCurrentTarget = SurfaceDragState.Matches(context.PrimarySnapshot.Id);
        var currentContext = context;
        if (!HandleActiveGizmoDragLifecycle(
                matchesCurrentTarget,
                true,
                () =>
                {
                    HandleGizmoSurfaceDragKeyboardShortcuts(currentContext);
                    UpdateGizmoSurfaceDrag(currentContext);
                },
                CompleteGizmoSurfaceDrag))
        {
            return false;
        }

        return true;
    }

    private void UpdateGizmoSurfaceDrag(in GizmoContext context)
    {
        if (!TryResolveCurrentPlacementHit(context, out var hit))
        {
            return;
        }

        if (SurfaceDragState.IsMultiSelection)
        {
            UpdateMultiGizmoSurfaceDrag(context, hit);
            return;
        }

        UpdateSingleGizmoSurfaceDrag(context, hit);
    }

    private void UpdateSingleGizmoSurfaceDrag(in GizmoContext context, ObjectSurfaceHit hit)
    {
        var result = ResolveSingleSurfaceDragResult(context, hit);
        var transform = ResolveSingleSurfaceDragTransform(result);
        bool attachmentChanged = _surfaceAttachmentService.HasSurfaceDragAttachmentChange(
            SurfaceDragState.CurrentSingleSnapshot,
            hit,
            context.BoundsSnapshots);

        if (!HasSurfaceDragTransformChanged(
                transform.Position,
                SurfaceDragState.LastResolvedPosition,
                transform.RotationDegrees,
                SurfaceDragState.LastResolvedRotationDegrees)
            && !attachmentChanged)
        {
            return;
        }

        _ = TryApplyAndRecordSingleSurfaceDragTransform(context, transform, hit);
    }

    private void UpdateMultiGizmoSurfaceDrag(in GizmoContext context, ObjectSurfaceHit hit)
    {
        if (!TryResolveMultiGizmoSurfaceDragResult(context, hit, out var result))
        {
            return;
        }

        var resolvedGroupRotationDegrees = ResolveSurfaceDragSelectionRotationDegrees(result);

        if (!HasSurfaceDragTransformChanged(
                result.PivotPosition,
                SurfaceDragState.LastResolvedPosition,
                resolvedGroupRotationDegrees,
                SurfaceDragState.LastResolvedRotationDegrees))
        {
            return;
        }

        _ = TryApplySurfaceDragSelectionResult(result);
    }

    private bool TryResolveMultiGizmoSurfaceDragResult(
        in GizmoContext context,
        ObjectSurfaceHit hit,
        out GizmoSurfaceDragSelectionResult result)
        => GizmoSurfaceDragSolver.TryResolveSelection(
            hit,
            SurfaceDragState.StartRotationQuaternion,
            SurfaceDragState.SelectionEntries,
            SurfaceAlignToNormal,
            SurfaceDragState.RotationSteps,
            context.CameraRight,
            out result);

    private void HandleGizmoSurfaceDragKeyboardShortcuts(in GizmoContext context)
    {
        if (!SurfaceDragState.Matches(context.PrimarySnapshot.Id))
        {
            return;
        }

        var yawSteps = SurfaceDragInputSuppressionLease?.ConsumePressedCount(SeVirtualKey.R) ?? 0;
        var pitchSteps = SurfaceDragInputSuppressionLease?.ConsumePressedCount(SeVirtualKey.T) ?? 0;
        if (SurfaceDragState.IsSingleSelection && SurfaceAlignToNormal)
        {
            pitchSteps = 0;
        }

        if (yawSteps == 0 && pitchSteps == 0)
        {
            return;
        }

        if (SurfaceDragState.IsMultiSelection)
        {
            HandleMultiGizmoSurfaceDragKeyboardShortcuts(context, yawSteps, pitchSteps);
            return;
        }

        HandleSingleGizmoSurfaceDragKeyboardShortcuts(context, yawSteps, pitchSteps);
    }

    private void HandleMultiGizmoSurfaceDragKeyboardShortcuts(in GizmoContext context, int yawSteps, int pitchSteps)
    {
        SurfaceDragState.AddRotationSteps(yawSteps, pitchSteps);
        if (!TryResolveCurrentPlacementHit(context, out var hit)
            || !TryResolveMultiGizmoSurfaceDragResult(context, hit, out var result))
        {
            return;
        }

        _ = TryApplySurfaceDragSelectionResult(result);
    }

    private void HandleSingleGizmoSurfaceDragKeyboardShortcuts(in GizmoContext context, int yawSteps, int pitchSteps)
    {
        SurfaceDragState.AddRotationSteps(yawSteps, pitchSteps);

        if (TryResolveCurrentPlacementHit(context, out var hit))
        {
            _ = TryApplySurfaceDragSingleResult(context, ResolveSingleSurfaceDragResult(context, hit), hit);
            return;
        }
        else if (SurfaceAlignToNormal)
        {
            return;
        }

        var rotationQuaternion = GizmoSurfaceDragSolver.ResolveRotationSteps(
            SurfaceDragState.StartRotationQuaternion,
            SurfaceDragState.RotationSteps,
            surfaceNormal: null,
            context.CameraRight);

        var transform = SurfaceDragState.StartSnapshot.Transform with
        {
            Position = SurfaceDragState.LastResolvedPosition,
            RotationDegrees = ObjectTransformMath.ToRotationDegrees(rotationQuaternion, SurfaceDragState.LastResolvedRotationDegrees),
        };

        _ = TryApplyAndRecordSingleSurfaceDragTransform(context, transform, hit: null);
    }

    private GizmoSurfaceDragSingleResult ResolveSingleSurfaceDragResult(in GizmoContext context, ObjectSurfaceHit hit)
        => GizmoSurfaceDragSolver.ResolveSingle(
            hit,
            SurfaceDragState.StartSnapshot,
            SurfaceDragState.StartRotationQuaternion,
            SurfaceDragState.RotationSteps,
            context.CameraRight,
            SurfaceDragState.PrimaryEntry,
            _surfacePlacementService.ShouldUseNativePlacementOrigin(SurfaceDragState.StartSnapshot, hit),
            SurfaceAlignToNormal);

    private bool TryApplySurfaceDragSingleResult(
        in GizmoContext context,
        in GizmoSurfaceDragSingleResult result,
        ObjectSurfaceHit hit)
        => TryApplyAndRecordSingleSurfaceDragTransform(context, ResolveSingleSurfaceDragTransform(result), hit);

    private ObjectTransform ResolveSingleSurfaceDragTransform(in GizmoSurfaceDragSingleResult result)
        => result.Transform with
        {
            RotationDegrees = ResolveSingleSurfaceDragRotationDegrees(result),
        };

    private bool TryApplyAndRecordSingleSurfaceDragTransform(
        in GizmoContext context,
        ObjectTransform transform,
        ObjectSurfaceHit? hit)
    {
        if (!TryApplySurfaceDragSingleTransform(context, transform, hit, out var appliedSnapshot))
        {
            return false;
        }

        SurfaceDragState.RecordSingleApply(appliedSnapshot.Transform.Position, appliedSnapshot.Transform.RotationDegrees, appliedSnapshot);
        return true;
    }

    private bool TryApplySurfaceDragSingleTransform(
        in GizmoContext context,
        ObjectTransform transform,
        ObjectSurfaceHit? hit,
        out ObjectSnapshot appliedSnapshot)
    {
        var entry = SurfaceDragState.PrimaryEntry;
        var rotation = ObjectTransformMath.CreateRotationQuaternion(transform.RotationDegrees);
        var pivotOffset = entry.ResolvePivotOffset(rotation);
        var pivotPosition = transform.Position - pivotOffset;
        var snapPolicy = ResolveSurfaceDragSnapPolicy(pivotPosition);
        var snappedPivotPosition = snapPolicy.SnapPosition(pivotPosition);
        var appliedTransform = transform with { Position = snappedPivotPosition + pivotOffset };
        ObjectSnapshot baseSnapshot = hit.HasValue
            ? SurfaceDragState.StartSnapshot
            : SurfaceDragState.CurrentSingleSnapshot;
        ObjectSnapshot nextSnapshot = baseSnapshot with { Transform = appliedTransform };
        nextSnapshot = _surfaceAttachmentService.ApplySurfaceDragAttachment(nextSnapshot, hit, context.BoundsSnapshots);

        if (!_mutationService.TryUpdate(nextSnapshot, out appliedSnapshot))
        {
            return false;
        }

        return true;
    }

    private bool TryApplySurfaceDragSelectionResult(in GizmoSurfaceDragSelectionResult result)
    {
        var snappedPivotPosition = ResolveSurfaceDragSnapPolicy(result.PivotPosition).SnapPosition(result.PivotPosition);
        var groupDelta = GizmoSelectionTransformUtility.ResolveRotationDelta(SurfaceDragState.StartRotationQuaternion, result.GroupRotation);
        var resolvedGroupRotationDegrees = ResolveSurfaceDragSelectionRotationDegrees(result);
        var selectionEntries = SurfaceDragState.SelectionEntries;
        var snapshots = new ObjectSnapshot[selectionEntries.Count];
        for (var index = 0; index < selectionEntries.Count; ++index)
        {
            var entry = selectionEntries[index];
            snapshots[index] = entry.Snapshot with
            {
                Transform = GizmoSelectionTransformUtility.ApplyRigidRotation(
                    entry,
                    snappedPivotPosition,
                    groupDelta,
                    SurfaceDragState.ResolveReferenceRotationDegrees(entry.Snapshot.Id)),
            };
        }

        if (!_mutationService.TryUpdateMany(snapshots, out var appliedSnapshots))
        {
            return false;
        }

        SurfaceDragState.RecordSelectionApply(result.PivotPosition, resolvedGroupRotationDegrees, appliedSnapshots);
        return true;
    }

    private Vector3 ResolveSingleSurfaceDragRotationDegrees(in GizmoSurfaceDragSingleResult result)
        => ObjectTransformMath.ToRotationDegrees(result.RotationQuaternion, SurfaceDragState.LastResolvedRotationDegrees);

    private Vector3 ResolveSurfaceDragSelectionRotationDegrees(in GizmoSurfaceDragSelectionResult result)
        => ObjectTransformMath.ToRotationDegrees(result.GroupRotation, SurfaceDragState.LastResolvedRotationDegrees);

    private bool TryResolveCurrentPlacementHit(in GizmoContext context, out ObjectSurfaceHit hit)
    {
        hit = new ObjectSurfaceHit(Vector3.Zero, Vector3.Zero);
        if (!TryBuildCurrentMouseRay(context.ViewportPos, context.ViewportSize, out var rayOrigin, out var rayDirection))
        {
            return false;
        }

        bool hasNativeHit = _surfacePlacementService.TryResolvePlacementHit(
                                context.PrimarySnapshot,
                                context.BoundsSnapshot,
                                rayOrigin,
                                rayDirection,
                                out ObjectSurfaceHit nativeHit)
                            || _placementResolver.TryResolveFromRay(rayOrigin, rayDirection, out nativeHit);
        if (SurfaceDragState.ObjectTargetsEnabled
         && TryResolveObjectPlacementHit(context, rayOrigin, rayDirection, hasNativeHit ? nativeHit.Distance : float.PositiveInfinity, out hit))
        {
            return true;
        }

        hit = nativeHit;
        return hasNativeHit;
    }

    private bool TryResolveObjectPlacementHit(
        in GizmoContext context,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out ObjectSurfaceHit hit)
    {
        if (SurfaceDragState.ObjectTargetShape == SurfaceObjectTargetShape.Geometry)
        {
            return _surfaceTargetService.TryRaycastGeometryTargets(SurfaceDragState.SurfaceTargets, rayOrigin, rayDirection, maxDistance, out hit);
        }

        return GizmoSurfaceObjectRaycastUtility.TryRaycastObjectBounds(
            rayOrigin,
            rayDirection,
            context.BoundsSnapshots,
            SurfaceDragState.SelectionEntries,
            out hit,
            maxDistance);
    }

    private ObjectSurfaceTargetSnapshot CaptureSurfaceTargets(
        in GizmoContext context,
        bool objectTargetsEnabled,
        SurfaceObjectTargetShape objectTargetShape)
    {
        if (!objectTargetsEnabled || objectTargetShape != SurfaceObjectTargetShape.Geometry)
        {
            return ObjectSurfaceTargetSnapshot.Empty;
        }

        var excludedObjectIds = new HashSet<Guid>(context.SelectedSnapshots.Count);
        foreach (var snapshot in context.SelectedSnapshots)
        {
            excludedObjectIds.Add(snapshot.Id);
        }

        return _surfaceTargetService.CaptureTargets(excludedObjectIds);
    }
}

