using Intoner.Objects.Utils;
using System.Globalization;

namespace Intoner.Objects.Resources;

/// <summary> object owned in-memory resource path </summary>
internal readonly record struct ObjectMemoryResourcePath
{
    private readonly string? _path;

    internal ObjectMemoryResourcePath(string path, string gamePath)
    {
        _path = path;
        GamePath = gamePath;
    }

    public string Path => _path ?? string.Empty;
    public string GamePath { get; }
}

internal static class ObjectMemoryResourcePathUtility
{
    private const string Prefix = "summit://";
    private const char Separator = '/';

    public static bool IsMemoryResourcePath(string path)
        => TextUtility.TrimOrEmpty(path).StartsWith(Prefix, StringComparison.Ordinal);

    public static string Create(long resourceId, string gamePath)
        => resourceId > 0
            ? Create($"p{resourceId.ToString(CultureInfo.InvariantCulture)}", gamePath)
            : string.Empty;

    public static string Create(string resourceId, string gamePath)
    {
        string normalizedResourceId = TextUtility.TrimOrEmpty(resourceId);
        string normalizedGamePath = ObjectResolvedPath.FromGamePath(gamePath).Path;

        if (normalizedResourceId.Length == 0 || normalizedGamePath.Length == 0
         || normalizedResourceId.Contains(Separator) || normalizedResourceId.Contains('\\')
         || !ObjectResourcePathEncoding.TryGetNativePathByteCount(normalizedResourceId, out int resourceIdByteCount)
         || !ObjectResourcePathEncoding.TryGetNativePathByteCount(normalizedGamePath, out int gamePathByteCount)
         || Prefix.Length + resourceIdByteCount + 1 + gamePathByteCount >= ObjectResourcePathEncoding.MaxNativePathByteCount)
        {
            return string.Empty;
        }

        return $"{Prefix}{normalizedResourceId}{Separator}{normalizedGamePath}";
    }

    public static string Normalize(string path)
        => TryParse(path, out ObjectMemoryResourcePath memoryPath)
            ? memoryPath.Path
            : string.Empty;

    public static bool TryParse(string path, out ObjectMemoryResourcePath memoryPath)
    {
        memoryPath = default;

        string normalizedPath = TextUtility.TrimOrEmpty(path);

        if (!normalizedPath.StartsWith(Prefix, StringComparison.Ordinal)
         || !ObjectResourcePathEncoding.TryGetNativePathByteCount(path, out _))
        {
            return false;
        }

        int separatorIndex = normalizedPath.IndexOf(Separator, Prefix.Length);
        if (separatorIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> resourceId = normalizedPath.AsSpan(Prefix.Length, separatorIndex - Prefix.Length).Trim();
        string gamePath = ObjectResolvedPath.FromGamePath(normalizedPath[(separatorIndex + 1)..]).Path;

        if (resourceId.Length == 0 || resourceId.Contains('\\') || gamePath.Length == 0)
        {
            return false;
        }

        string canonicalPath = resourceId.Length == separatorIndex - Prefix.Length
            && normalizedPath.AsSpan(separatorIndex + 1).SequenceEqual(gamePath)
                ? normalizedPath
                : $"{Prefix}{resourceId}{Separator}{gamePath}";
        memoryPath = new ObjectMemoryResourcePath(canonicalPath, gamePath);
        return true;
    }

    public static string GetGamePathOrSelf(string path)
        => TryParse(ObjectScopedResourcePathUtility.Strip(path), out ObjectMemoryResourcePath memoryPath)
            ? memoryPath.GamePath
            : path;
}
