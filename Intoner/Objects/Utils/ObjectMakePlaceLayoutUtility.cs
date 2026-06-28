using Intoner.Objects.Api;

namespace Intoner.Objects.Utils;

internal readonly record struct ObjectMakePlaceFurnitureNode(ObjectMakePlaceFurnitureDocument Furniture, int ParentIndex)
{
    public bool HasParent
        => ParentIndex >= 0;
}

internal static class ObjectMakePlaceLayoutUtility
{
    public static IReadOnlyList<ObjectMakePlaceFurnitureNode> FlattenFurnitureTree(IEnumerable<ObjectMakePlaceFurnitureDocument?> furniture)
    {
        List<ObjectMakePlaceFurnitureNode> flattened = [];
        AppendFlattenedFurniture(flattened, furniture, -1);
        return flattened;
    }

    public static int CountFurnitureTree(IEnumerable<ObjectMakePlaceFurnitureDocument?> furniture)
    {
        var count = 0;
        foreach (ObjectMakePlaceFurnitureDocument? entry in furniture)
        {
            if (entry is null)
            {
                continue;
            }

            count++;
            count += CountFurnitureTree(entry.Attachments ?? []);
        }

        return count;
    }

    private static void AppendFlattenedFurniture(
        List<ObjectMakePlaceFurnitureNode> flattened,
        IEnumerable<ObjectMakePlaceFurnitureDocument?> furniture,
        int parentIndex)
    {
        foreach (ObjectMakePlaceFurnitureDocument? entry in furniture)
        {
            if (entry is null)
            {
                continue;
            }

            int nodeIndex = flattened.Count;
            flattened.Add(new ObjectMakePlaceFurnitureNode(entry, parentIndex));
            AppendFlattenedFurniture(flattened, entry.Attachments ?? [], nodeIndex);
        }
    }
}

