using Intoner.Objects.Models;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class GizmoSelectionTransformUtility
{
    public static GizmoSelectionEntry[] CreateSelectionEntries(IReadOnlyList<ObjectSnapshot> selectedSnapshots, Vector3 pivotPosition)
    {
        var entries = new GizmoSelectionEntry[selectedSnapshots.Count];
        for (var index = 0; index < selectedSnapshots.Count; ++index)
        {
            entries[index] = CreateSelectionEntry(selectedSnapshots[index], boundsSnapshot: null, pivotPosition);
        }

        return entries;
    }

    public static GizmoSelectionEntry[] CreateSelectionEntries(
        IReadOnlyList<ObjectSnapshot> selectedSnapshots,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        Vector3 pivotPosition)
    {
        var entries = new GizmoSelectionEntry[selectedSnapshots.Count];
        for (var index = 0; index < selectedSnapshots.Count; ++index)
        {
            var snapshot = selectedSnapshots[index];
            var boundsSnapshot = ObjectSelectionTransformMath.FindBoundsSnapshot(boundsSnapshots, snapshot.Id);
            entries[index] = CreateSelectionEntry(snapshot, boundsSnapshot, pivotPosition);
        }

        return entries;
    }

    public static Quaternion ResolveRotationDelta(Quaternion startRotation, Quaternion targetRotation)
    {
        var normalizedStart = ObjectTransformMath.NormalizeQuaternion(startRotation);
        var normalizedTarget = ObjectTransformMath.NormalizeQuaternion(targetRotation);
        return ObjectTransformMath.NormalizeQuaternion(normalizedTarget * Quaternion.Inverse(normalizedStart));
    }

    public static ObjectTransform ApplyRigidRotation(
        in GizmoSelectionEntry entry,
        Vector3 pivotPosition,
        Quaternion rotationDelta,
        Vector3 referenceRotationDegrees)
    {
        var normalizedRotationDelta = ObjectTransformMath.NormalizeQuaternion(rotationDelta);
        var nextRotation = ObjectTransformMath.NormalizeQuaternion(normalizedRotationDelta * entry.StartRotationQuaternion);
        return entry.Snapshot.Transform with
        {
            Position = pivotPosition + Vector3.Transform(entry.PivotOffset, normalizedRotationDelta),
            RotationDegrees = ObjectTransformMath.ToRotationDegrees(nextRotation, referenceRotationDegrees),
        };
    }

    private static GizmoSelectionEntry CreateSelectionEntry(
        ObjectSnapshot snapshot,
        ObjectBoundsSnapshot? boundsSnapshot,
        Vector3 pivotPosition)
    {
        var pivotOffset = snapshot.Transform.Position - pivotPosition;
        var startRotationQuaternion = ObjectTransformMath.CreateRotationQuaternion(snapshot.Transform.RotationDegrees);
        if (boundsSnapshot?.LocalBounds is not { } localBounds)
        {
            return new GizmoSelectionEntry(snapshot, pivotOffset, startRotationQuaternion);
        }

        var inverseRotation = Quaternion.Inverse(startRotationQuaternion);
        var localBoundsCenter = new Vector3(localBounds.Transform.M41, localBounds.Transform.M42, localBounds.Transform.M43);
        var boundsCenterLocalOffset = Vector3.Transform(localBoundsCenter - snapshot.Transform.Position, inverseRotation);
        var boundsHalfExtents = new Vector3(localBounds.HalfExtents.X, localBounds.HalfExtents.Y, localBounds.HalfExtents.Z);
        return new GizmoSelectionEntry(snapshot, pivotOffset, startRotationQuaternion, true, boundsCenterLocalOffset, boundsHalfExtents);
    }
}

