using Intoner.Objects.Models;

namespace Intoner.Objects.Utils;

internal static class ObjectTemporaryLayoutUtility
{
    public static ObjectSnapshot RemapSnapshot(string sourceKey, ObjectSnapshot snapshot)
        => snapshot with
        {
            Id = ObjectIdentityUtility.CreateTemporaryObjectId(sourceKey, snapshot.Id),
            LayoutId = null,
            CollectionId = snapshot.CollectionId.Length == 0
                ? string.Empty
                : ObjectIdentityUtility.CreateTemporaryCollectionId(sourceKey, snapshot.CollectionId),
        };

    public static Dictionary<Guid, ObjectSnapshot> CreateObjectMap(IReadOnlyList<ObjectSnapshot>? objects)
    {
        Dictionary<Guid, ObjectSnapshot> result = [];
        if (objects is null)
        {
            return result;
        }

        foreach (ObjectSnapshot entry in objects)
        {
            result[entry.Id] = entry;
        }

        return result;
    }

    public static bool TryFindObject(IReadOnlyList<ObjectSnapshot>? objects, Guid objectId, out ObjectSnapshot snapshot)
    {
        if (objects is not null)
        {
            foreach (ObjectSnapshot entry in objects)
            {
                if (entry.Id == objectId)
                {
                    snapshot = entry;
                    return true;
                }
            }
        }

        snapshot = default!;
        return false;
    }

    public static List<ObjectSnapshot> ReplaceObject(IReadOnlyList<ObjectSnapshot>? objects, ObjectSnapshot snapshot)
    {
        List<ObjectSnapshot> nextObjects = [];
        if (objects is not null)
        {
            foreach (ObjectSnapshot entry in objects)
            {
                if (entry.Id != snapshot.Id)
                {
                    nextObjects.Add(entry);
                }
            }
        }

        nextObjects.Add(snapshot);
        return OrderObjects(nextObjects);
    }

    public static List<ObjectSnapshot> RemoveObject(IReadOnlyList<ObjectSnapshot>? objects, Guid objectId)
    {
        List<ObjectSnapshot> nextObjects = [];
        if (objects is not null)
        {
            foreach (ObjectSnapshot entry in objects)
            {
                if (entry.Id != objectId)
                {
                    nextObjects.Add(entry);
                }
            }
        }

        return OrderObjects(nextObjects);
    }

    public static List<ObjectSnapshot> OrderObjects(IEnumerable<ObjectSnapshot> objects)
        => objects
            .OrderBy(static snapshot => snapshot.CreatedAtUtc)
            .ToList();
}

