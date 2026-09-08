namespace Intoner.Objects.Utils;

internal static class ObjectCollectionKeyUtility
{
    public static string NormalizeCollectionId(string? collectionId)
        => TextUtility.TrimOrEmpty(collectionId).ToLowerInvariant();

    public static string NormalizeModDirectory(string? modDirectory)
        => TextUtility.TrimOrEmpty(modDirectory).ToLowerInvariant();
}

