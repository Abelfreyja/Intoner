using Intoner.Objects.Assets;
using Intoner.Objects.Utils;
using System.Globalization;

namespace Intoner.Objects.Resources;

/// <summary> object owned in-memory resource path </summary>
internal readonly record struct ObjectMemoryResourcePath(string ResourceId, string GamePath)
{
    public string Path
        => ObjectMemoryResourcePathUtility.Create(ResourceId, GamePath);
}

internal static class ObjectMemoryResourcePathUtility
{
    private const string Prefix = "summit://";
    private const char Separator = '/';

    public static bool IsMemoryResourcePath(string path)
        => ObjectStringUtility.TrimOrEmpty(path).StartsWith(Prefix, StringComparison.Ordinal);

    public static string Create(long resourceId, string gamePath)
        => Create(resourceId.ToString(CultureInfo.InvariantCulture), gamePath);

    public static string Create(string resourceId, string gamePath)
    {
        string normalizedResourceId = ObjectStringUtility.TrimOrEmpty(resourceId);
        string normalizedGamePath = GameAssetPathRules.NormalizeGamePath(gamePath);
        return normalizedResourceId.Length > 0 && normalizedGamePath.Length > 0
            ? $"{Prefix}{normalizedResourceId}{Separator}{normalizedGamePath}"
            : string.Empty;
    }

    public static string Normalize(string path)
        => TryParse(path, out ObjectMemoryResourcePath memoryPath)
            ? memoryPath.Path
            : string.Empty;

    public static bool TryParse(string path, out ObjectMemoryResourcePath memoryPath)
    {
        memoryPath = default;

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

        string resourceId = normalizedPath[Prefix.Length..separatorIndex];
        string gamePath = GameAssetPathRules.NormalizeGamePath(normalizedPath[(separatorIndex + 1)..]);
        if (resourceId.Length == 0 || gamePath.Length == 0)
        {
            return false;
        }

        memoryPath = new ObjectMemoryResourcePath(resourceId, gamePath);
        return true;
    }

    public static string GetGamePathOrSelf(string path)
        => TryParse(ObjectScopedResourcePathUtility.Strip(path), out ObjectMemoryResourcePath memoryPath)
            ? memoryPath.GamePath
            : path;
}


