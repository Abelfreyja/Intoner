using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using Intoner.Scene;
using Intoner.Services.Input;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Intoner.Objects.UI;

/// <summary> shared scaling options and edit creation for gizmo handles and inspector fields </summary>
internal sealed class GizmoScaling(
    GizmoSettings settings,
    IShortcutService shortcuts,
    ISceneItemService sceneItems,
    Func<SceneItemSnapshot, bool> supportsScale)
{
    public bool Anchored
        => settings.AnchoredScaling != shortcuts.IsHeld(EditorShortcuts.InvertScaleAnchor);

    public bool Uniform
        => settings.UniformScaling != shortcuts.IsHeld(EditorShortcuts.InvertUniformScaling);

    public bool AnchorUnavailable { get; private set; }

    public static bool RequiresUniform(SceneItemSnapshot snapshot, int selectionCount = 1)
        => selectionCount > 1 || snapshot is SceneGroupSnapshot;

    public bool UsesUniform(SceneItemSnapshot snapshot, int selectionCount = 1)
        => Uniform || RequiresUniform(snapshot, selectionCount);

    public bool UsesInspectorUniform(SceneItemSnapshot snapshot, int axis)
        => snapshot is SceneGroupSnapshot || axis >= 0 && Uniform;

    public bool CheckAnchor(SceneScaleEdit edit, bool uniform, bool anchored)
    {
        AnchorUnavailable = anchored && !edit.CanAnchor(uniform);
        return !AnchorUnavailable;
    }

    public bool TryCreateEdit(IReadOnlyList<SceneItemSnapshot> targets, SceneItemBoundsLookup bounds,
        Vector3 pivot, bool uniform, Vector3 direction, [NotNullWhen(true)] out SceneScaleEdit? edit)
    {
        bool created = SceneScaleEdit.TryCreate(targets, bounds, pivot, direction, supportsScale, out edit);
        AnchorUnavailable = false;

        if (created)
        {
            _ = CheckAnchor(edit!, uniform, Anchored);
        }

        return created;
    }

    public bool TryCreateInspectorEdit(SceneItemSnapshot snapshot, int axis, SceneItemBoundsLookup bounds,
        [NotNullWhen(true)] out SceneScaleEdit? edit)
    {
        IReadOnlyList<SceneItemSnapshot> targets = snapshot is SceneGroupSnapshot group
            ? sceneItems.GetPlacedItems().Where(item => item is ObjectSnapshot member && member.GroupId == group.Id)
                .Append(snapshot).ToArray()
            : [snapshot];
        Vector3 direction = axis >= 0
            ? Vector3.Transform(GizmoAxisUtility.ToUnitVector(GizmoAxisUtility.FromIndex(axis)),
                SceneTransformMath.CreateRotationQuaternion(snapshot.Transform.RotationDegrees))
            : Vector3.UnitY;
        return TryCreateEdit(targets, bounds, snapshot.Transform.Position, UsesInspectorUniform(snapshot, axis), direction, out edit);
    }

    public bool TryApplyEdit(SceneScaleEdit edit, SceneTransform start, Vector3 scale, bool uniform,
        out SceneTransform transform)
    {
        bool anchored = Anchored;

        if (!CheckAnchor(edit, uniform, anchored))
        {
            transform = start;
            return false;
        }

        return edit.TryApplyScale(start, scale, uniform, anchored, out transform);
    }
}
