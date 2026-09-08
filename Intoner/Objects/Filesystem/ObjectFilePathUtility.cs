namespace Intoner.Objects.Filesystem;

internal static class ObjectFilePathUtility
{
    public static string NormalizeFullPath(string path)
        => Path.GetFullPath(path);

    public static bool PathsMatch(string left, string right)
        => string.Equals(NormalizeFullPath(left), NormalizeFullPath(right), StringComparison.OrdinalIgnoreCase);

    public static bool IsPathWithin(string path, string directory)
    {
        string relativePath = Path.GetRelativePath(NormalizeFullPath(directory), NormalizeFullPath(path));
        return !Path.IsPathRooted(relativePath)
            && !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }
}

