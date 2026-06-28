namespace Intoner.Objects.Assets;

internal static class ObjectPathCollectionUtility
{
    public static bool AddGamePath(ICollection<string> paths, ISet<string> seenPaths, string path)
    {
        if (!ObjectPathRules.TryNormalizeGamePath(path, out string normalizedPath)
         || !seenPaths.Add(normalizedPath))
        {
            return false;
        }

        paths.Add(normalizedPath);
        return true;
    }

    public static IReadOnlyList<string> MergeGamePaths(params IEnumerable<string>[] pathGroups)
    {
        List<string> paths = [];
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (IEnumerable<string> pathGroup in pathGroups)
        {
            foreach (string path in pathGroup)
            {
                _ = AddGamePath(paths, seenPaths, path);
            }
        }

        return paths;
    }

    public static HashSet<string> CreateSupportedObjectResourceSet(IEnumerable<string> paths)
    {
        HashSet<string> normalizedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (ObjectPathRules.TryNormalizeSupportedObjectResourcePath(path, out string normalizedPath))
            {
                normalizedPaths.Add(normalizedPath);
            }
        }

        return normalizedPaths;
    }
}

