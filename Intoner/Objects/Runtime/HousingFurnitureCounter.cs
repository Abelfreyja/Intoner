using Intoner.Objects.Models;

namespace Intoner.Objects.Runtime;

internal static class HousingFurnitureCounter
{
    public static int Count(IEnumerable<ObjectSnapshot> snapshots)
        => CountCore(snapshots, null, out _);

    public static int CountAndContains(IEnumerable<ObjectSnapshot> snapshots, Guid furnitureId, out bool containsFurniture)
        => CountCore(snapshots, furnitureId, out containsFurniture);

    private static int CountCore(IEnumerable<ObjectSnapshot> snapshots, Guid? furnitureId, out bool containsFurniture)
    {
        containsFurniture = false;
        HashSet<Guid> furnitureIds = [];
        foreach (ObjectSnapshot snapshot in snapshots)
        {
            if (snapshot.Kind != ObjectKind.Furniture)
            {
                continue;
            }

            if (furnitureId.HasValue && snapshot.Id == furnitureId.Value)
            {
                containsFurniture = true;
            }

            furnitureIds.Add(snapshot.Id);
        }

        return furnitureIds.Count;
    }
}

