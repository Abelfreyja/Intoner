namespace Intoner.Objects.Utils;

internal static class ObjectCollectionKeyUtility
{
    public static string NormalizeCollectionId(string? collectionId)
        => ObjectStringUtility.TrimOrEmpty(collectionId).ToLowerInvariant();

    public static string NormalizeModDirectory(string? modDirectory)
        => ObjectStringUtility.TrimOrEmpty(modDirectory).ToLowerInvariant();
}

