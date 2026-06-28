using Intoner.Objects.Models;
using Intoner.Objects.Runtime;
using Intoner.Objects.Utils;
using System.Numerics;

namespace Intoner.Objects.UI;

internal static class GizmoSurfaceObjectRaycastUtility
{
    public static bool TryRaycastObjectBounds(
        Vector3 rayOrigin,
        Vector3 rayDirection,
        IReadOnlyList<ObjectBoundsSnapshot> boundsSnapshots,
        IReadOnlyList<GizmoSelectionEntry> excludedEntries,
        out ObjectSurfaceHit hit,
        float maxDistance)
    {
        return ObjectPlacementBoundsRaycaster.TryRaycastNearest(
            boundsSnapshots,
            id => IsExcluded(id, excludedEntries),
            rayOrigin,
            rayDirection,
            out hit,
            maxDistance);
    }

    private static bool IsExcluded(Guid id, IReadOnlyList<GizmoSelectionEntry> excludedEntries)
    {
        foreach (GizmoSelectionEntry entry in excludedEntries)
        {
            if (entry.Snapshot.Id == id)
            {
                return true;
            }
        }

        return false;
    }
}

