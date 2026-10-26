using Dalamud.Bindings.ImGui;
using Intoner.Displays;
using Intoner.Objects.Api;
using Intoner.Objects.Models;
using Intoner.Objects.UI.Components;
using Intoner.Objects.UI.Services;
using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.UI;

internal sealed class SceneTransformEditor
{
    private readonly IHistoryCoordinator     _historyCoordinator;
    private readonly IObjectClipboardService _objectClipboard;
    private readonly Gizmo                   _gizmo;
    private SceneItemSnapshot? _scaleStart;
    private SceneScaleEdit? _scaleEdit;

    private Vector3 _scaleInput;
    private bool _scaleAnchored;
    private bool _scaleUniform;
    private bool _scaleEdited;
    private int _scaleAxis;

    private bool InspectorUniformScaling
        => _scaleStart is { } start && _gizmo.Scaling.UsesInspectorUniform(start, _scaleAxis);

    public SceneTransformEditor(
        IHistoryCoordinator historyCoordinator,
        IObjectClipboardService objectClipboard,
        Gizmo gizmo)
    {
        _historyCoordinator = historyCoordinator;
        _objectClipboard    = objectClipboard;
        _gizmo              = gizmo;
    }

    private bool DrawPositionClipboardRow(string id, ref Vector3 value)
        => DrawTransformClipboardRow(id, "Position", ObjectTransformPart.Position, ref value, 0.05f, -10000f, 10000f, "%.3f");

    private bool DrawRotationClipboardRow(string id, ref Vector3 value)
        => DrawTransformClipboardRow(id, "Rotation", ObjectTransformPart.Rotation, ref value, 0.5f, -360f, 360f, "%.1f");

    internal bool DrawScaleClipboardRow(string id, ref Vector3 value, Action<int>? onActivated = null)
        => DrawTransformClipboardRow(id, "Scale", ObjectTransformPart.Scale, ref value, 0.01f, 0.01f, 100f, "%.3f", onActivated);

    internal SceneTransform DrawInspectorRows(SceneItemSnapshot snapshot, bool editGroupPivot = false)
    {
        if (!ImGui.IsAnyItemActive() || _scaleStart?.Id != snapshot.Id)
        {
            EndInspectorScaleEdit();
        }

        bool display = snapshot is DisplaySnapshot;
        string target = snapshot switch
        {
            SceneGroupSnapshot when editGroupPivot => "Group Pivot",
            SceneGroupSnapshot => "Group",
            DisplaySnapshot => "Display",
            _ => "Object",
        };
        string positionId = display ? "displayPosition" : "commonPosition";
        string rotationId = display ? "displayRotation" : "commonRotation";
        string positionEditId = display ? "DisplayPosition" : "CommonPosition";
        string rotationEditId = display ? "DisplayRotation" : "CommonRotation";

        if (editGroupPivot)
        {
            positionId = "pivotPosition";
            rotationId = "pivotRotation";
            positionEditId = "PivotPosition";
            rotationEditId = "PivotRotation";
        }

        SceneTransform transform = snapshot.Transform;
        Vector3 position = snapshot.Transform.Position;

        if (DrawPositionClipboardRow(positionId, ref position))
        {
            transform = ApplyInspectorPositionEdit(positionEditId, $"Move {target}",
                snapshot, position, editGroupPivot);
        }

        Vector3 rotation = snapshot.Transform.RotationDegrees;

        if (DrawRotationClipboardRow(rotationId, ref rotation))
        {
            transform = ApplyInspectorRotationEdit(rotationEditId, $"Rotate {target}",
                snapshot, rotation, editGroupPivot);
        }

        if (editGroupPivot)
        {
            return transform;
        }

        Vector3 scaleInput = _scaleEdit is null ? snapshot.Transform.Scale : _scaleInput;

        if (snapshot is SceneGroupSnapshot)
        {
            float scale = scaleInput.X;
            bool changed = DrawUniformScaleClipboardRow("commonScale", ref scale, axis => BeginInspectorScaleEdit(snapshot, axis));

            if (ShouldApplyInspectorScaleEdit(changed))
            {
                transform = ApplyInspectorScaleEdit("CommonScale", "Scale Group", snapshot, new Vector3(scale));
            }
        }
        else if (display || snapshot is ObjectSnapshot { Model: BgObjectModel or FurnitureModel or VfxModel })
        {
            bool changed = DrawScaleClipboardRow(display ? "displayScale" : "commonScale", ref scaleInput,
                axis => BeginInspectorScaleEdit(snapshot, axis));

            if (ShouldApplyInspectorScaleEdit(changed))
            {
                transform = ApplyInspectorScaleEdit(display ? "DisplayScale" : "CommonScale", $"Scale {target}",
                    snapshot, scaleInput);
            }
        }

        if (!ImGui.IsItemActive())
        {
            EndInspectorScaleEdit();
        }

        return transform;
    }

    private bool DrawUniformScaleClipboardRow(string id, ref float value, Action<int> onActivated)
    {
        float next = value;
        bool pasted = false;
        bool changed = EditorPropertyTable.DragFloat(id, "Scale", ref next, 0.01f, SceneTransformMath.MinimumScale, 100f, "%.3f x",
            TransformClipboardControls.ResolveWidth(), () =>
        {
            if (TransformClipboardControls.Draw(_objectClipboard, id, ObjectTransformPart.Scale, new Vector3(next), out Vector3 scale,
                    uniformScale: true))
            {
                next = Math.Clamp(scale.X, SceneTransformMath.MinimumScale, 100f);
                onActivated(-1);
                pasted = true;
            }
        }, borderColor: ThemeColors.AccentPrimary, onActivated: onActivated);

        if (changed || pasted)
        {
            value = next;
        }

        return changed || pasted;
    }

    private bool DrawTransformClipboardRow(
        string id,
        string title,
        ObjectTransformPart part,
        ref Vector3 value,
        float speed,
        float min,
        float max,
        string format,
        Action<int>? onActivated = null)
    {
        Vector3 rowValue = value;
        bool pasted = false;
        float actionWidth = TransformClipboardControls.ResolveWidth();
        bool changed = EditorPropertyTable.DragFloat3(
            id,
            title,
            ref rowValue,
            speed,
            min,
            max,
            format,
            actionWidth,
            () =>
            {
                if (!TransformClipboardControls.Draw(_objectClipboard, id, part, rowValue, out Vector3 pastedValue))
                {
                    return;
                }

                rowValue = Vector3.Clamp(pastedValue, new Vector3(min), new Vector3(max));
                onActivated?.Invoke(-1);
                pasted = true;
            }, onActivated);

        if (changed || pasted)
        {
            value = rowValue;
        }

        return changed || pasted;
    }

    private Vector3 ApplyPositionSnap(Vector3 position, in SceneSnapBasis basis)
        => GizmoInputUtility.IsTransformSnapActive(_gizmo.Settings.TransformSnapSettings.PositionEnabled)
            ? SceneTransformSnapUtility.SnapPosition(position, _gizmo.Settings.TransformSnapSettings.PositionStep, basis)
            : position;

    private Vector3 ApplyRotationSnap(Vector3 rotationDegrees)
        => GizmoInputUtility.IsTransformSnapActive(_gizmo.Settings.TransformSnapSettings.RotationEnabled)
            ? SceneTransformSnapUtility.SnapRotationDegrees(rotationDegrees, _gizmo.Settings.TransformSnapSettings.RotationStepDegrees)
            : rotationDegrees;

    private Vector3 ApplyScaleSnap(Vector3 scale)
        => GizmoInputUtility.IsTransformSnapActive(_gizmo.Settings.TransformSnapSettings.ScaleEnabled)
            ? SceneTransformSnapUtility.SnapScale(scale, _gizmo.Settings.TransformSnapSettings.ScaleStep)
            : Vector3.Max(scale, new Vector3(SceneTransformMath.MinimumScale));

    private void ApplyInspectorTransformEdit(string editId, string title, SceneHistoryKind kind, SceneItemSnapshot snapshot, SceneTransform nextTransform, bool editGroupPivot = false)
    {
        if (Equals(snapshot.Transform, nextTransform))
        {
            return;
        }

        if (editGroupPivot && snapshot is SceneGroupSnapshot group)
        {
            _historyCoordinator.ApplyInspectorGroupPivotEdit(editId, title, group, nextTransform.Position, nextTransform.RotationDegrees);
            return;
        }

        _historyCoordinator.ApplyInspectorSnapshotEdit(
            editId,
            kind,
            title,
            snapshot,
            snapshot with { Transform = nextTransform });
    }

    private SceneTransform ApplyInspectorPositionEdit(string editId, string title, SceneItemSnapshot snapshot, Vector3 position, bool editGroupPivot = false)
    {
        var nextTransform = snapshot.Transform with { Position = ApplyPositionSnap(position, GizmoSnapBasisUtility.World) };
        ApplyInspectorTransformEdit(editId, title, SceneHistoryKind.Move, snapshot, nextTransform, editGroupPivot);
        return nextTransform;
    }

    private SceneTransform ApplyInspectorRotationEdit(string editId, string title, SceneItemSnapshot snapshot, Vector3 rotationDegrees, bool editGroupPivot = false)
    {
        var nextTransform = snapshot.Transform with { RotationDegrees = ApplyRotationSnap(rotationDegrees) };
        ApplyInspectorTransformEdit(editId, title, SceneHistoryKind.Transform, snapshot, nextTransform, editGroupPivot);
        return nextTransform;
    }

    private bool ShouldApplyInspectorScaleEdit(bool changed)
    {
        _scaleEdited |= changed;
        return changed || _scaleEdited && ImGui.IsItemActive() && _scaleEdit is not null
            && (_scaleAnchored != _gizmo.Scaling.Anchored || _scaleUniform != InspectorUniformScaling);
    }

    private SceneTransform ApplyInspectorScaleEdit(string editId, string title, SceneItemSnapshot snapshot, Vector3 scale)
    {
        if (_scaleStart is not { } start || _scaleEdit is not { } edit)
        {
            return snapshot.Transform;
        }

        _scaleInput = scale;
        _scaleAnchored = _gizmo.Scaling.Anchored;
        _scaleUniform = InspectorUniformScaling;
        Vector3 nextScale;

        if (_scaleAxis >= 0)
        {
            Vector3 startScale = start.Transform.Scale;
            _scaleInput = SceneTransformMath.WithAxisScale(startScale, _scaleAxis, scale[_scaleAxis], _scaleUniform);
            nextScale = SceneTransformMath.WithAxisScale(startScale, _scaleAxis, ApplyScaleSnap(_scaleInput)[_scaleAxis], _scaleUniform);
        }
        else
        {
            nextScale = ApplyScaleSnap(scale);
        }

        if (!_gizmo.Scaling.TryApplyEdit(edit, start.Transform, nextScale, _scaleUniform, out SceneTransform nextTransform))
        {
            return snapshot.Transform;
        }

        ApplyInspectorTransformEdit(editId, title, SceneHistoryKind.Transform, snapshot, nextTransform);
        return nextTransform;
    }

    private void BeginInspectorScaleEdit(SceneItemSnapshot snapshot, int axis)
    {
        _scaleStart = snapshot;
        _scaleInput = snapshot.Transform.Scale;
        _scaleAnchored = _gizmo.Scaling.Anchored;
        _scaleAxis = axis;
        _scaleEdited = false;
        _scaleUniform = InspectorUniformScaling;
        _gizmo.Scaling.TryCreateInspectorEdit(snapshot, axis, _gizmo.GetBoundsLookup(), out _scaleEdit);
    }

    private void EndInspectorScaleEdit()
    {
        _scaleStart = null;
        _scaleEdit = null;
        _scaleEdited = false;
    }
}
