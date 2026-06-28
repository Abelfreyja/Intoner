using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using System.Globalization;

namespace Intoner.Objects.Resources;

/// <summary> object collection scope encoded into native resource handle paths </summary>
internal readonly record struct ObjectScopedResourcePath(long ResourceScopeId, string Path);

internal static class ObjectScopedResourcePathUtility
{
    private const string Prefix = "intoner://";
    private const string UriMarker = "://";
    private const char Separator = '/';

    public static bool TryParse(string path, out ObjectScopedResourcePath scopedPath)
    {
        scopedPath = default;

        string normalizedPath = ObjectStringUtility.TrimOrEmpty(path);
        if (!normalizedPath.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int separatorIndex = normalizedPath.IndexOf(Separator, Prefix.Length);
        if (separatorIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> scopeIdSpan = normalizedPath.AsSpan(Prefix.Length, separatorIndex - Prefix.Length);
        if (!long.TryParse(scopeIdSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long resourceScopeId)
         || resourceScopeId <= 0)
        {
            return false;
        }

        string scopedActualPath = NormalizeActualPath(normalizedPath[(separatorIndex + 1)..]);
        if (scopedActualPath.Length == 0)
        {
            return false;
        }

        scopedPath = new ObjectScopedResourcePath(resourceScopeId, scopedActualPath);
        return true;
    }

    public static bool IsForeignScopedPath(string path)
    {
        string normalizedPath = ObjectStringUtility.TrimOrEmpty(path);
        return normalizedPath.Length > 0
            && !normalizedPath.StartsWith(Prefix, StringComparison.Ordinal)
            && (normalizedPath[0] == '|'
                || normalizedPath.Contains(UriMarker, StringComparison.Ordinal));
    }

    public static bool IsObjectScopedPath(string path)
        => ObjectStringUtility.TrimOrEmpty(path).StartsWith(Prefix, StringComparison.Ordinal);

    public static string Create(long resourceScopeId, string path)
    {
        string normalizedPath = NormalizeActualPath(path);
        return resourceScopeId > 0 && normalizedPath.Length > 0
            ? $"{Prefix}{resourceScopeId.ToString(CultureInfo.InvariantCulture)}{Separator}{normalizedPath}"
            : normalizedPath;
    }

    public static string Strip(string path)
        => TryParse(path, out ObjectScopedResourcePath scopedPath)
            ? scopedPath.Path
            : path;

    private static string NormalizeActualPath(string path)
    {
        if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(path))
        {
            return ObjectMemoryResourcePathUtility.Normalize(path);
        }

        return ObjectLocalFilePathUtility.IsLocalFilePath(path)
            ? ObjectLocalFilePathUtility.NormalizeLocalFilePath(path)
            : ObjectPathRules.NormalizeGamePath(path);
    }
}


