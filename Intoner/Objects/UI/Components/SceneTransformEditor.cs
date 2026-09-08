using Intoner.Objects.Api;
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

    private static SceneSnapBasis WorldTransformSnapBasis
        => GizmoSnapBasisUtility.World;

    public SceneTransformEditor(
        IHistoryCoordinator historyCoordinator,
        IObjectClipboardService objectClipboard,
        Gizmo gizmo)
    {
        _historyCoordinator = historyCoordinator;
        _objectClipboard    = objectClipboard;
        _gizmo              = gizmo;
    }

    internal bool DrawPositionClipboardRow(string id, ref Vector3 value)
        => DrawTransformClipboardRow(id, "Position", ObjectTransformPart.Position, ref value, 0.05f, -10000f, 10000f, "%.3f");

    internal bool DrawRotationClipboardRow(string id, ref Vector3 value)
        => DrawTransformClipboardRow(id, "Rotation", ObjectTransformPart.Rotation, ref value, 0.5f, -360f, 360f, "%.1f");

    internal bool DrawScaleClipboardRow(string id, ref Vector3 value)
        => DrawTransformClipboardRow(id, "Scale", ObjectTransformPart.Scale, ref value, 0.01f, 0.01f, 100f, "%.3f");

    private bool DrawTransformClipboardRow(
        string id,
        string title,
        ObjectTransformPart part,
        ref Vector3 value,
        float speed,
        float min,
        float max,
        string format)
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

                rowValue = ClampVector3(pastedValue, min, max);
                pasted = true;
            });

        if (changed || pasted)
        {
            value = rowValue;
        }

        return changed || pasted;
    }

    private static Vector3 ClampVector3(Vector3 value, float min, float max)
        => new(
            Math.Clamp(value.X, min, max),
            Math.Clamp(value.Y, min, max),
            Math.Clamp(value.Z, min, max));

    private bool IsPositionSnapActive()
        => ResolveTransformSnapActive(_gizmo.Settings.TransformSnapSettings.PositionEnabled);

    private bool IsRotationSnapActive()
        => ResolveTransformSnapActive(_gizmo.Settings.TransformSnapSettings.RotationEnabled);

    private bool IsScaleSnapActive()
        => ResolveTransformSnapActive(_gizmo.Settings.TransformSnapSettings.ScaleEnabled);

    private static bool ResolveTransformSnapActive(bool alwaysEnabled)
        => alwaysEnabled
            ? !GizmoInputUtility.IsPrecisionSnapModifierActive()
            : GizmoInputUtility.IsPrecisionSnapModifierActive();

    private Vector3 ApplyPositionSnap(Vector3 position, in SceneSnapBasis basis)
        => IsPositionSnapActive()
            ? SceneTransformSnapUtility.SnapPosition(position, _gizmo.Settings.TransformSnapSettings.PositionStep, basis)
            : position;

    private Vector3 ApplyRotationSnap(Vector3 rotationDegrees)
        => IsRotationSnapActive()
            ? SceneTransformSnapUtility.SnapRotationDegrees(rotationDegrees, _gizmo.Settings.TransformSnapSettings.RotationStepDegrees)
            : rotationDegrees;

    private Vector3 ApplyScaleSnap(Vector3 scale)
        => IsScaleSnapActive()
            ? SceneTransformSnapUtility.SnapScale(scale, _gizmo.Settings.TransformSnapSettings.ScaleStep)
            : scale;

    private void ApplyInspectorTransformEdit(string editId, string title, SceneHistoryKind kind, SceneItemSnapshot snapshot, SceneTransform nextTransform)
    {
        if (Equals(snapshot.Transform, nextTransform))
        {
            return;
        }

        _historyCoordinator.ApplyInspectorSnapshotEdit(
            editId,
            kind,
            title,
            snapshot,
            snapshot with { Transform = nextTransform });
    }

    internal SceneTransform ApplyInspectorPositionEdit(string editId, string title, SceneHistoryKind kind, SceneItemSnapshot snapshot, Vector3 position)
    {
        var nextTransform = snapshot.Transform with { Position = ApplyPositionSnap(position, WorldTransformSnapBasis) };
        ApplyInspectorTransformEdit(editId, title, kind, snapshot, nextTransform);
        return nextTransform;
    }

    internal SceneTransform ApplyInspectorRotationEdit(string editId, string title, SceneHistoryKind kind, SceneItemSnapshot snapshot, Vector3 rotationDegrees)
    {
        var nextTransform = snapshot.Transform with { RotationDegrees = ApplyRotationSnap(rotationDegrees) };
        ApplyInspectorTransformEdit(editId, title, kind, snapshot, nextTransform);
        return nextTransform;
    }

    internal SceneTransform ApplyInspectorScaleEdit(string editId, string title, SceneHistoryKind kind, SceneItemSnapshot snapshot, Vector3 scale)
    {
        var nextTransform = snapshot.Transform with { Scale = ApplyScaleSnap(scale) };
        ApplyInspectorTransformEdit(editId, title, kind, snapshot, nextTransform);
        return nextTransform;
    }
}
