namespace Intoner.Objects.Filesystem;

internal static class ObjectFilePathUtility
{
    public static string NormalizeFullPath(string path)
        => Path.GetFullPath(path);

    public static bool PathsMatch(string left, string right)
        => string.Equals(NormalizeFullPath(left), NormalizeFullPath(right), StringComparison.OrdinalIgnoreCase);
}

