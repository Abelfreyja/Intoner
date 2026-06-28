using Intoner.Objects.Models;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class GizmoSessionSnapshotUtility
{
    public static bool TryGetHistorySnapshots(
        IReadOnlyList<GizmoSelectionEntry> selectionEntries,
        ObjectSnapshot? appliedSnapshot,
        IReadOnlyList<ObjectSnapshot> appliedSnapshots,
        out ObjectSnapshot[] beforeSnapshots,
        out ObjectSnapshot[] afterSnapshots)
    {
        beforeSnapshots = [];
        afterSnapshots = [];
        if (selectionEntries.Count == 0)
        {
            return false;
        }

        if (appliedSnapshot is not null)
        {
            for (var index = 0; index < selectionEntries.Count; ++index)
            {
                var snapshot = selectionEntries[index].Snapshot;
                if (snapshot.Id != appliedSnapshot.Id)
                {
                    continue;
                }

                beforeSnapshots = [snapshot];
                afterSnapshots = [appliedSnapshot];
                return true;
            }

            return false;
        }

        if (appliedSnapshots.Count == 0)
        {
            return false;
        }

        var matchedSnapshotCount = 0;
        for (var index = 0; index < selectionEntries.Count; ++index)
        {
            if (TryResolveAppliedSnapshot(selectionEntries[index].Snapshot.Id, appliedSnapshots, out _))
            {
                ++matchedSnapshotCount;
            }
        }

        if (matchedSnapshotCount == 0)
        {
            return false;
        }

        beforeSnapshots = new ObjectSnapshot[matchedSnapshotCount];
        afterSnapshots = new ObjectSnapshot[matchedSnapshotCount];
        var matchedIndex = 0;
        for (var index = 0; index < selectionEntries.Count; ++index)
        {
            var snapshot = selectionEntries[index].Snapshot;
            if (!TryResolveAppliedSnapshot(snapshot.Id, appliedSnapshots, out var appliedListSnapshot))
            {
                continue;
            }

            beforeSnapshots[matchedIndex] = snapshot;
            afterSnapshots[matchedIndex] = appliedListSnapshot;
            ++matchedIndex;
        }

        return true;
    }

    public static Vector3 ResolveReferenceRotationDegrees(
        Guid objectId,
        IReadOnlyList<GizmoSelectionEntry> selectionEntries,
        ObjectSnapshot? appliedSnapshot,
        IReadOnlyList<ObjectSnapshot> appliedSnapshots,
        ObjectSnapshot startSnapshot)
    {
        if (TryResolveAppliedSnapshot(objectId, appliedSnapshot, appliedSnapshots, out var resolvedAppliedSnapshot))
        {
            return resolvedAppliedSnapshot.Transform.RotationDegrees;
        }

        for (var index = 0; index < selectionEntries.Count; ++index)
        {
            var entry = selectionEntries[index];
            if (entry.Snapshot.Id == objectId)
            {
                return entry.Snapshot.Transform.RotationDegrees;
            }
        }

        return startSnapshot.Transform.RotationDegrees;
    }

    public static bool TryResolveAppliedSnapshot(
        Guid objectId,
        ObjectSnapshot? appliedSnapshot,
        IReadOnlyList<ObjectSnapshot> appliedSnapshots,
        out ObjectSnapshot snapshot)
    {
        if (appliedSnapshot is not null && appliedSnapshot.Id == objectId)
        {
            snapshot = appliedSnapshot;
            return true;
        }

        return TryResolveAppliedSnapshot(objectId, appliedSnapshots, out snapshot);
    }

    private static bool TryResolveAppliedSnapshot(Guid objectId, IReadOnlyList<ObjectSnapshot> appliedSnapshots, out ObjectSnapshot snapshot)
    {
        for (var index = 0; index < appliedSnapshots.Count; ++index)
        {
            var appliedListSnapshot = appliedSnapshots[index];
            if (appliedListSnapshot.Id == objectId)
            {
                snapshot = appliedListSnapshot;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }
}

