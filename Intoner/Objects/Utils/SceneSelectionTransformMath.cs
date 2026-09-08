using Intoner.Scene;
using System.Numerics;

namespace Intoner.Objects.Utils;

/// <summary> indexes physical manipulation bounds by scene item id </summary>
internal sealed class SceneItemBoundsLookup
{
    private readonly Dictionary<Guid, SceneItemBoundsSnapshot> _manipulationBounds;

    public SceneItemBoundsLookup(IReadOnlyList<SceneItemBoundsSnapshot> boundsSnapshots)
    {
        _manipulationBounds = new Dictionary<Guid, SceneItemBoundsSnapshot>(boundsSnapshots.Count);
        foreach (SceneItemBoundsSnapshot boundsSnapshot in boundsSnapshots)
        {
            if (boundsSnapshot.IsManipulationBounds)
            {
                _ = _manipulationBounds.TryAdd(boundsSnapshot.Id, boundsSnapshot);
            }
        }
    }

    public SceneItemBoundsSnapshot? FindManipulationBounds(Guid itemId)
        => _manipulationBounds.GetValueOrDefault(itemId);
}

internal static class SceneSelectionTransformMath
{
    /// <summary> finds the physical bounds used to manipulate one scene item </summary>
    /// <param name="snapshot">the scene item snapshot</param>
    /// <param name="boundsLookup">the indexed manipulation bounds</param>
    /// <returns>the matching bounds snapshot, or null when the item has no physical manipulation bounds</returns>
    public static SceneItemBoundsSnapshot? FindManipulationBoundsSnapshot(
        SceneItemSnapshot snapshot,
        SceneItemBoundsLookup boundsLookup)
        => boundsLookup.FindManipulationBounds(snapshot.Id);

    /// <summary> resolves a gizmo axis length from a domain preference, manipulation bounds, or scale </summary>
    /// <param name="snapshot">the scene item snapshot</param>
    /// <param name="boundsLookup">the indexed manipulation bounds</param>
    /// <returns>a stable axis length for drawing and drag math</returns>
    public static float ResolveItemGizmoAxisLength(
        SceneItemSnapshot snapshot,
        SceneItemBoundsLookup boundsLookup,
        float? preferredAxisLength = null)
    {
        if (preferredAxisLength.HasValue
            && preferredAxisLength.Value > 0f
            && float.IsFinite(preferredAxisLength.Value))
        {
            return Math.Clamp(preferredAxisLength.Value, 0.25f, 4f);
        }

        var boundsSnapshot = FindManipulationBoundsSnapshot(snapshot, boundsLookup);
        if (TryResolveBoundsAxisLength(boundsSnapshot, out var boundsAxisLength))
        {
            return boundsAxisLength;
        }

        var maxScale = MathF.Max(snapshot.Transform.Scale.X, MathF.Max(snapshot.Transform.Scale.Y, snapshot.Transform.Scale.Z));
        return Math.Clamp(maxScale, 0.25f, 1.5f);
    }

    /// <summary> resolves the shared pivot for a selection from world bounds or average position </summary>
    /// <param name="selectedItems">the selected scene item snapshots</param>
    /// <param name="boundsLookup">the indexed manipulation bounds</param>
    /// <returns>the shared selection pivot position</returns>
    public static Vector3 ResolveSelectionPivotPosition(
        IReadOnlyList<SceneItemSnapshot> selectedItems,
        SceneItemBoundsLookup boundsLookup)
    {
        if (selectedItems.Count == 0)
        {
            return Vector3.Zero;
        }

        if (TryResolveSelectionWorldBounds(selectedItems, boundsLookup, out var min, out var max))
        {
            return (min + max) * 0.5f;
        }

        var position = Vector3.Zero;
        foreach (var snapshot in selectedItems)
        {
            position += snapshot.Transform.Position;
        }

        return position / selectedItems.Count;
    }

    /// <summary> resolves a shared gizmo axis length for the current selection </summary>
    /// <param name="selectedItems">the selected scene item snapshots</param>
    /// <param name="boundsLookup">the indexed manipulation bounds</param>
    /// <returns>a stable shared axis length for the selection</returns>
    public static float ResolveSelectionGizmoAxisLength(
        IReadOnlyList<SceneItemSnapshot> selectedItems,
        SceneItemBoundsLookup boundsLookup)
    {
        if (selectedItems.Count == 1)
        {
            return ResolveItemGizmoAxisLength(selectedItems[0], boundsLookup);
        }

        if (TryResolveSelectionWorldBounds(selectedItems, boundsLookup, out var min, out var max))
        {
            var extents = max - min;
            var maxExtent = MathF.Max(extents.X, MathF.Max(extents.Y, extents.Z));
            if (maxExtent > 0.01f)
            {
                return Math.Clamp(maxExtent * 0.5f, 0.25f, 4f);
            }
        }

        var axisLength = 0.25f;
        foreach (var snapshot in selectedItems)
        {
            axisLength = MathF.Max(axisLength, ResolveItemGizmoAxisLength(snapshot, boundsLookup));
        }

        return Math.Clamp(axisLength, 0.25f, 4f);
    }

    /// <summary> resolves one world space bounds box for the whole selection </summary>
    /// <param name="selectedItems">the selected scene item snapshots</param>
    /// <param name="boundsLookup">the indexed manipulation bounds</param>
    /// <param name="min">the resolved world min point</param>
    /// <param name="max">the resolved world max point</param>
    /// <returns>true when any bounds or positions were resolved</returns>
    public static bool TryResolveSelectionWorldBounds(
        IReadOnlyList<SceneItemSnapshot> selectedItems,
        SceneItemBoundsLookup boundsLookup,
        out Vector3 min,
        out Vector3 max)
    {
        min = default;
        max = default;

        var hasBounds = false;
        foreach (var snapshot in selectedItems)
        {
            var boundsSnapshot = FindManipulationBoundsSnapshot(snapshot, boundsLookup);
            if (boundsSnapshot is not null)
            {
                if (!hasBounds)
                {
                    min = boundsSnapshot.Min;
                    max = boundsSnapshot.Max;
                    hasBounds = true;
                }
                else
                {
                    min = Vector3.Min(min, boundsSnapshot.Min);
                    max = Vector3.Max(max, boundsSnapshot.Max);
                }
            }
            else
            {
                var position = snapshot.Transform.Position;
                if (!hasBounds)
                {
                    min = position;
                    max = position;
                    hasBounds = true;
                }
                else
                {
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }
            }
        }

        return hasBounds;
    }

    /// <summary> resolves one stable yaw and pitch axis pair for surface-drag replay </summary>
    /// <param name="baseRotation">the base grouped rotation before steps are replayed</param>
    /// <param name="surfaceNormal">the optional current hit surface normal</param>
    /// <param name="cameraRight">the optional current camera right vector for tangent fallback</param>
    /// <param name="yawAxis">the resolved yaw axis</param>
    /// <param name="pitchAxis">the resolved pitch axis</param>
    /// <returns>true when both axes resolve to usable directions</returns>
    public static bool TryResolveSurfaceDragRotationAxes(
        Quaternion baseRotation,
        Vector3? surfaceNormal,
        Vector3? cameraRight,
        out Vector3 yawAxis,
        out Vector3 pitchAxis)
    {
        var normalizedBaseRotation = SceneTransformMath.NormalizeQuaternion(baseRotation);
        if (!NumericsUtility.TryNormalize(ResolveSurfaceDragYawAxis(normalizedBaseRotation, surfaceNormal), out yawAxis)
            || !NumericsUtility.TryNormalize(ResolveSurfaceDragPitchAxis(normalizedBaseRotation, surfaceNormal, cameraRight), out pitchAxis))
        {
            yawAxis = default;
            pitchAxis = default;
            return false;
        }

        return true;
    }

    /// <summary> applies one stepped surface-drag yaw or pitch rotation on a stable axis pair </summary>
    /// <param name="rotation">the current grouped rotation</param>
    /// <param name="yawStep">whether the step is yaw instead of pitch</param>
    /// <param name="stepCount">the signed step count to apply</param>
    /// <param name="stepDegrees">the degrees per step</param>
    /// <param name="yawAxis">the stable yaw axis for this replay</param>
    /// <param name="pitchAxis">the stable pitch axis for this replay</param>
    /// <returns>the stepped group rotation</returns>
    public static Quaternion ApplySurfaceDragRotationStep(
        Quaternion rotation,
        bool yawStep,
        int stepCount,
        float stepDegrees,
        Vector3 yawAxis,
        Vector3 pitchAxis)
    {
        var currentRotation = SceneTransformMath.NormalizeQuaternion(rotation);
        if (stepCount == 0)
        {
            return currentRotation;
        }

        var rotationAxis = yawStep ? yawAxis : pitchAxis;
        if (!NumericsUtility.TryNormalize(rotationAxis, out var normalizedAxis))
        {
            return currentRotation;
        }

        var radians = stepCount * (stepDegrees * (MathF.PI / 180f));
        var stepDelta = Quaternion.CreateFromAxisAngle(normalizedAxis, radians);
        return SceneTransformMath.NormalizeQuaternion(stepDelta * currentRotation);
    }

    /// <summary> resolves a stable tangent axis on the current surface </summary>
    /// <param name="rotation">the current group rotation</param>
    /// <param name="surfaceNormal">the current surface normal</param>
    /// <param name="cameraRight">the optional camera right fallback</param>
    /// <param name="tangentAxis">the resolved tangent axis</param>
    /// <returns>true when a valid tangent axis was resolved</returns>
    public static bool TryResolveSurfaceTangentAxis(Quaternion rotation, Vector3 surfaceNormal, Vector3? cameraRight, out Vector3 tangentAxis)
    {
        tangentAxis = default;
        if (!NumericsUtility.TryNormalize(surfaceNormal, out surfaceNormal))
        {
            return false;
        }

        if (!TryProjectDirectionOntoPlane(Vector3.Transform(Vector3.UnitX, rotation), surfaceNormal, out tangentAxis)
            && cameraRight is { } projectedCameraRight)
        {
            TryProjectDirectionOntoPlane(projectedCameraRight, surfaceNormal, out tangentAxis);
        }

        if (!NumericsUtility.HasLength(tangentAxis))
        {
            var fallbackAxis = MathF.Abs(Vector3.Dot(surfaceNormal, Vector3.UnitY)) < 0.95f
                ? Vector3.UnitY
                : Vector3.UnitX;
            tangentAxis = Vector3.Cross(surfaceNormal, fallbackAxis);
        }

        if (!NumericsUtility.TryNormalize(tangentAxis, out tangentAxis))
        {
            return false;
        }

        return true;
    }

    /// <summary> projects one direction onto a plane and normalizes the result when possible </summary>
    /// <param name="direction">the source direction to flatten</param>
    /// <param name="normal">the plane normal to remove from the direction</param>
    /// <param name="projectedDirection">the normalized projected direction</param>
    /// <returns>true when the projection keeps a usable direction</returns>
    public static bool TryProjectDirectionOntoPlane(Vector3 direction, Vector3 normal, out Vector3 projectedDirection)
    {
        projectedDirection = ProjectDirectionOntoPlane(direction, normal);
        if (!NumericsUtility.TryNormalize(projectedDirection, out projectedDirection))
        {
            return false;
        }
        return true;
    }

    private static bool TryResolveBoundsAxisLength(SceneItemBoundsSnapshot? boundsSnapshot, out float axisLength)
    {
        axisLength = 0f;

        if (boundsSnapshot?.OrientedBounds is { } localBounds)
        {
            var maxHalfExtent = MathF.Max(localBounds.HalfExtents.X, MathF.Max(localBounds.HalfExtents.Y, localBounds.HalfExtents.Z));
            if (maxHalfExtent > 0.01f)
            {
                axisLength = Math.Clamp(maxHalfExtent * 1.25f, 0.25f, 4f);
                return true;
            }
        }

        if (boundsSnapshot is null)
        {
            return false;
        }

        var extents = boundsSnapshot.Max - boundsSnapshot.Min;
        var maxExtent = MathF.Max(extents.X, MathF.Max(extents.Y, extents.Z));
        if (maxExtent <= 0.01f)
        {
            return false;
        }

        axisLength = Math.Clamp(maxExtent * 0.5f, 0.25f, 4f);
        return true;
    }

    private static Vector3 ResolveSurfaceDragYawAxis(Quaternion groupRotation, Vector3? surfaceNormal)
    {
        if (surfaceNormal is { } normal && NumericsUtility.TryNormalize(normal, out var normalizedNormal))
        {
            return normalizedNormal;
        }

        var upAxis = Vector3.Transform(Vector3.UnitY, groupRotation);
        return !NumericsUtility.TryNormalize(upAxis, out var normalizedUpAxis)
            ? Vector3.UnitY
            : normalizedUpAxis;
    }

    private static Vector3 ResolveSurfaceDragPitchAxis(Quaternion groupRotation, Vector3? surfaceNormal, Vector3? cameraRight)
    {
        if (surfaceNormal is { } normal
            && NumericsUtility.HasLength(normal)
            && TryResolveSurfaceTangentAxis(groupRotation, normal, cameraRight, out var tangentAxis))
        {
            return tangentAxis;
        }

        var rightAxis = Vector3.Transform(Vector3.UnitX, groupRotation);
        return !NumericsUtility.TryNormalize(rightAxis, out var normalizedRightAxis)
            ? Vector3.UnitX
            : normalizedRightAxis;
    }

    /// <summary> removes the component of one direction along a plane normal </summary>
    /// <param name="direction">the source direction to flatten</param>
    /// <param name="normal">the plane normal to remove from the direction</param>
    /// <returns>the direction projected onto the plane</returns>
    public static Vector3 ProjectDirectionOntoPlane(Vector3 direction, Vector3 normal)
        => direction - (Vector3.Dot(direction, normal) * normal);
}

