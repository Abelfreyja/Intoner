using FFXIVClientStructs.FFXIV.Client.System.Input;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed partial class Gizmo
{
    private void BeginGizmoSurfaceDrag(in GizmoContext context)
    {
        _host.PrepareHistoryMutation();
        ResetGizmoSurfaceDrag();
        _surfaceEdit = _sceneItemService.BeginEdit(context.SelectedSnapshots);
        if (_surfaceEdit is null)
        {
            return;
        }

        bool itemTargetsEnabled = Settings.SurfaceItemTargetsEnabled;
        SceneSurfaceTargetShape targetShape = Settings.SurfaceTargetShape;
        HashSet<Guid> selectedItemIds = context.SelectedSnapshots
            .Select(static snapshot => snapshot.Id)
            .ToHashSet();
        SceneSurfaceTargetSnapshot surfaceTargets = CaptureSurfaceTargets(selectedItemIds, itemTargetsEnabled, targetShape);
        State.BeginSurfaceDrag(
            context.SelectedSnapshots,
            context.BoundsLookup,
            context.PrimarySnapshot,
            context.PivotPosition,
            selectedItemIds,
            surfaceTargets,
            itemTargetsEnabled,
            targetShape);
        SurfaceDragKeyboardInputLease = _keyboardInput.BeginSuppression(GizmoConstants.SurfaceDragSuppressedKeys);
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

    private void UpdateSingleGizmoSurfaceDrag(in GizmoContext context, SceneSurfaceHit hit)
    {
        var result = ResolveSingleSurfaceDragResult(context, hit);
        var transform = ResolveSingleSurfaceDragTransform(result);
        _ = TryApplyAndRecordSingleSurfaceDragTransform(context, transform, hit);
    }

    private void UpdateMultiGizmoSurfaceDrag(in GizmoContext context, SceneSurfaceHit hit)
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

        _ = TryApplySurfaceDragSelectionResult(result, hit);
    }

    private bool TryResolveMultiGizmoSurfaceDragResult(
        in GizmoContext context,
        SceneSurfaceHit hit,
        out GizmoSurfaceDragSelectionResult result)
        => GizmoSurfaceDragSolver.TryResolveSelection(
            hit,
            SurfaceDragState.StartRotationQuaternion,
            SurfaceDragState.SelectionEntries,
            context.ManipulationOptions.SurfaceAlignmentAxis,
            context.ManipulationOptions.ForceSurfaceAlignment,
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

        var yawSteps = SurfaceDragKeyboardInputLease?.ConsumePressedCount(SeVirtualKey.R) ?? 0;
        var pitchSteps = SurfaceDragKeyboardInputLease?.ConsumePressedCount(SeVirtualKey.T) ?? 0;
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

        _ = TryApplySurfaceDragSelectionResult(result, hit);
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
            RotationDegrees = SceneTransformMath.ToRotationDegrees(rotationQuaternion, SurfaceDragState.LastResolvedRotationDegrees),
        };

        _ = TryApplyAndRecordSingleSurfaceDragTransform(context, transform, hit: null);
    }

    private GizmoSurfaceDragSingleResult ResolveSingleSurfaceDragResult(in GizmoContext context, SceneSurfaceHit hit)
        => GizmoSurfaceDragSolver.ResolveSingle(
            hit,
            SurfaceDragState.StartSnapshot,
            SurfaceDragState.StartRotationQuaternion,
            SurfaceDragState.RotationSteps,
            context.CameraRight,
            SurfaceDragState.PrimaryEntry,
            context.ManipulationOptions.SurfaceAlignmentAxis,
            context.Manipulation.UsesSurfaceOrigin(SurfaceDragState.StartSnapshot, hit),
            context.ManipulationOptions.ForceSurfaceAlignment,
            SurfaceAlignToNormal);

    private bool TryApplySurfaceDragSingleResult(
        in GizmoContext context,
        in GizmoSurfaceDragSingleResult result,
        SceneSurfaceHit hit)
        => TryApplyAndRecordSingleSurfaceDragTransform(context, ResolveSingleSurfaceDragTransform(result), hit);

    private SceneTransform ResolveSingleSurfaceDragTransform(in GizmoSurfaceDragSingleResult result)
        => result.Transform with
        {
            RotationDegrees = ResolveSingleSurfaceDragRotationDegrees(result),
        };

    private bool TryApplyAndRecordSingleSurfaceDragTransform(
        in GizmoContext context,
        SceneTransform transform,
        SceneSurfaceHit? hit)
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
        SceneTransform transform,
        SceneSurfaceHit? hit,
        out SceneItemSnapshot appliedSnapshot)
    {
        var entry = SurfaceDragState.PrimaryEntry;
        var rotation = SceneTransformMath.CreateRotationQuaternion(transform.RotationDegrees);
        var pivotOffset = entry.ResolvePivotOffset(rotation);
        var pivotPosition = transform.Position - pivotOffset;
        var snapPolicy = ResolveSurfaceDragSnapPolicy(pivotPosition);
        var snappedPivotPosition = snapPolicy.SnapPosition(pivotPosition);
        var appliedTransform = transform with { Position = snappedPivotPosition + pivotOffset };
        SceneItemSnapshot baseSnapshot = hit.HasValue
            ? SurfaceDragState.StartSnapshot
            : SurfaceDragState.CurrentSingleSnapshot;
        SceneItemSnapshot nextSnapshot = context.Manipulation.ApplySurfaceState(
            baseSnapshot with { Transform = appliedTransform },
            hit);

        if (Equals(nextSnapshot, SurfaceDragState.CurrentSingleSnapshot)
            || _surfaceEdit is null
            || !_surfaceEdit.Update([nextSnapshot], out IReadOnlyList<SceneItemSnapshot> appliedSnapshots).IsApplied())
        {
            appliedSnapshot = null!;
            return false;
        }

        appliedSnapshot = appliedSnapshots[0];
        return true;
    }

    private bool TryApplySurfaceDragSelectionResult(
        in GizmoSurfaceDragSelectionResult result,
        SceneSurfaceHit hit)
    {
        var snappedPivotPosition = ResolveSurfaceDragSnapPolicy(result.PivotPosition).SnapPosition(result.PivotPosition);
        var groupDelta = GizmoSelectionTransformUtility.ResolveRotationDelta(SurfaceDragState.StartRotationQuaternion, result.GroupRotation);
        var resolvedGroupRotationDegrees = ResolveSurfaceDragSelectionRotationDegrees(result);
        var selectionEntries = SurfaceDragState.SelectionEntries;
        var snapshots = new SceneItemSnapshot[selectionEntries.Count];
        for (var index = 0; index < selectionEntries.Count; ++index)
        {
            var entry = selectionEntries[index];
            if (!TryGetManipulation(entry.Snapshot, out SceneItemManipulationPolicy? manipulation, out _))
            {
                return false;
            }

            SceneItemSnapshot transformed = entry.Snapshot with
            {
                Transform = GizmoSelectionTransformUtility.ApplyRigidRotation(
                    entry,
                    snappedPivotPosition,
                    groupDelta,
                    SurfaceDragState.ResolveReferenceRotationDegrees(entry.Snapshot.Id)),
            };
            snapshots[index] = manipulation.ApplySurfaceState(transformed, hit);
        }

        if (_surfaceEdit is null || !_surfaceEdit.Update(snapshots, out IReadOnlyList<SceneItemSnapshot> appliedSnapshots).IsApplied())
        {
            return false;
        }

        SurfaceDragState.RecordSelectionApply(result.PivotPosition, resolvedGroupRotationDegrees, appliedSnapshots);
        return true;
    }

    private Vector3 ResolveSingleSurfaceDragRotationDegrees(in GizmoSurfaceDragSingleResult result)
        => SceneTransformMath.ToRotationDegrees(result.RotationQuaternion, SurfaceDragState.LastResolvedRotationDegrees);

    private Vector3 ResolveSurfaceDragSelectionRotationDegrees(in GizmoSurfaceDragSelectionResult result)
        => SceneTransformMath.ToRotationDegrees(result.GroupRotation, SurfaceDragState.LastResolvedRotationDegrees);

    private bool TryResolveCurrentPlacementHit(in GizmoContext context, out SceneSurfaceHit hit)
    {
        hit = SceneSurfaceHit.Empty;
        if (!TryBuildCurrentMouseRay(context.ViewportPos, context.ViewportSize, out var rayOrigin, out var rayDirection))
        {
            return false;
        }

        bool hasWorldHit = context.Manipulation.TryResolveSurfaceHit(
            context.PrimarySnapshot,
            context.BoundsSnapshot,
            rayOrigin,
            rayDirection,
            out SceneSurfaceHit worldHit);
        if (!hasWorldHit
            && _surfaceService.TryResolveWorldHit(rayOrigin, rayDirection, out worldHit))
        {
            hasWorldHit = true;
        }

        if (SurfaceDragState.ItemTargetsEnabled
            && TryResolveItemTargetHit(
                context,
                rayOrigin,
                rayDirection,
                hasWorldHit ? worldHit.Distance : float.PositiveInfinity,
                out hit))
        {
            return true;
        }

        hit = worldHit;
        return hasWorldHit;
    }

    private bool TryResolveItemTargetHit(
        in GizmoContext context,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        float maxDistance,
        out SceneSurfaceHit hit)
    {
        if (SurfaceDragState.TargetShape == SceneSurfaceTargetShape.Geometry)
        {
            return _surfaceService.TryRaycastGeometryTargets(
                SurfaceDragState.SurfaceTargets,
                rayOrigin,
                rayDirection,
                maxDistance,
                out hit);
        }

        return SceneBoundsRaycaster.TryRaycastNearest(
            context.BoundsSnapshots,
            SurfaceDragState.SelectedItemIds,
            rayOrigin,
            rayDirection,
            out hit,
            maxDistance);
    }

    private SceneSurfaceTargetSnapshot CaptureSurfaceTargets(
        IReadOnlySet<Guid> selectedItemIds,
        bool itemTargetsEnabled,
        SceneSurfaceTargetShape targetShape)
    {
        if (!itemTargetsEnabled || targetShape != SceneSurfaceTargetShape.Geometry)
        {
            return SceneSurfaceTargetSnapshot.Empty;
        }

        return _surfaceService.CaptureGeometryTargets(selectedItemIds);
    }
}

