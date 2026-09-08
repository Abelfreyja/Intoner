using Intoner.Objects.Models;

namespace Intoner.Objects.Utils;

internal static class TemporarySourceUtility
{
    public static string NormalizeKey(string? sourceKey)
        => TextUtility.TrimOrEmpty(sourceKey);

    public static string ResolveName(string? existingName, string requestedName, string fallback)
    {
        string normalizedName = TextUtility.TrimOrEmpty(requestedName);
        return normalizedName.Length > 0
            ? normalizedName
            : TextUtility.TrimOrFallback(existingName, fallback);
    }

    public static List<ObjectSnapshot> CreateRuntimeObjects(
        string sourceKey,
        IEnumerable<ObjectSnapshot> objects)
        => OrderObjects(objects.Select(snapshot => RemapObject(sourceKey, snapshot)));

    public static Dictionary<Guid, ObjectSnapshot> CreateObjectMap(IEnumerable<ObjectSnapshot> objects)
        => objects.ToDictionary(static snapshot => snapshot.Id);

    public static bool TryFindObject(
        IReadOnlyList<ObjectSnapshot> objects,
        Guid objectId,
        out ObjectSnapshot snapshot)
    {
        foreach (ObjectSnapshot candidate in objects)
        {
            if (candidate.Id == objectId)
            {
                snapshot = candidate;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    public static List<ObjectSnapshot> OrderObjects(IEnumerable<ObjectSnapshot> objects)
        => objects.OrderBy(static snapshot => snapshot.CreatedAtUtc).ToList();

    private static ObjectSnapshot RemapObject(string sourceKey, ObjectSnapshot snapshot)
        => snapshot with
        {
            Id = ObjectIdentityUtility.CreateTemporaryObjectId(sourceKey, snapshot.Id),
            LayoutId = null,
            CollectionId = snapshot.CollectionId.Length == 0
                ? string.Empty
                : ObjectIdentityUtility.CreateTemporaryCollectionId(sourceKey, snapshot.CollectionId),
        };
}
