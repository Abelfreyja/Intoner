using Dalamud.Plugin.Services;
using Intoner.Objects.Assets;
using Intoner.Objects.Utils;

namespace Intoner.Objects.Resources;

/// <summary> supported resolved object path kinds </summary>
internal enum ObjectResolvedPathKind
{
    GamePath,
    LocalFile,
    Memory,
}

/// <summary> one resolved object resource path </summary>
internal readonly record struct ObjectResolvedPath
{
    private ObjectResolvedPath(ObjectResolvedPathKind kind, string path)
    {
        Kind = kind;
        Path = path;
    }

    public ObjectResolvedPathKind Kind { get; } = ObjectResolvedPathKind.GamePath;
    public string Path { get; } = string.Empty;

    public bool IsLocalFile
        => Kind == ObjectResolvedPathKind.LocalFile;

    public bool IsMemory
        => Kind == ObjectResolvedPathKind.Memory;

    public string ResourceGamePath
        => Kind == ObjectResolvedPathKind.Memory
            ? ObjectMemoryResourcePathUtility.GetGamePathOrSelf(Path)
            : Path;

    public static ObjectResolvedPath FromGamePath(string path)
        => new(ObjectResolvedPathKind.GamePath, ObjectResourcePathUtility.NormalizeGamePath(path));

    public static ObjectResolvedPath FromLocalFile(string path)
        => new(ObjectResolvedPathKind.LocalFile, ObjectResourcePathUtility.NormalizeLocalFilePath(path));

    public static ObjectResolvedPath FromMemory(string path)
        => new(ObjectResolvedPathKind.Memory, ObjectMemoryResourcePathUtility.Normalize(path));

    public static bool TryCreate(string path, out ObjectResolvedPath resolvedPath)
    {
        if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(path))
        {
            if (!ObjectMemoryResourcePathUtility.TryParse(path, out ObjectMemoryResourcePath memoryPath))
            {
                resolvedPath = default;
                return false;
            }

            resolvedPath = new ObjectResolvedPath(ObjectResolvedPathKind.Memory, memoryPath.Path);
            return true;
        }

        if (ObjectResourcePathUtility.IsLocalFilePath(path))
        {
            if (!ObjectResourcePathUtility.TryNormalizeLocalFilePath(path, out string normalizedLocalFilePath))
            {
                resolvedPath = default;
                return false;
            }

            resolvedPath = new ObjectResolvedPath(ObjectResolvedPathKind.LocalFile, normalizedLocalFilePath);
            return true;
        }

        if (!ObjectResourcePathUtility.TryNormalizeGamePath(path, out string normalizedGamePath))
        {
            resolvedPath = default;
            return false;
        }

        resolvedPath = new ObjectResolvedPath(ObjectResolvedPathKind.GamePath, normalizedGamePath);
        return true;
    }
}

/// <summary> one requested object resource redirect rule </summary>
internal readonly record struct ObjectPathRedirection
{
    public ObjectPathRedirection(string requestedPath, ObjectResolvedPath resolvedPath)
    {
        RequestedPath = ObjectResourcePathUtility.NormalizeGamePath(requestedPath);
        ResolvedPath = resolvedPath;
    }

    public string RequestedPath { get; } = string.Empty;
    public ObjectResolvedPath ResolvedPath { get; } = default;
}

/// <summary> object resource path helpers for game paths and local files </summary>
internal static class ObjectResourcePathUtility
{
    public static bool IsLocalFilePath(string path)
        => ObjectLocalFilePathUtility.IsLocalFilePath(path);

    public static string NormalizeGamePath(string path)
        => ObjectPathRules.NormalizeGamePath(path);

    public static bool TryNormalizeGamePath(string path, out string normalizedPath)
        => ObjectPathRules.TryNormalizeGamePath(path, out normalizedPath);

    public static string NormalizeLocalFilePath(string path)
        => ObjectLocalFilePathUtility.NormalizeLocalFilePath(path);

    public static bool TryNormalizeLocalFilePath(string path, out string normalizedPath)
        => ObjectLocalFilePathUtility.TryNormalizeLocalFilePath(path, out normalizedPath);

    public static string NormalizeTrackedPath(string path)
    {
        string unscopedPath = ObjectScopedResourcePathUtility.Strip(path);
        if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(unscopedPath))
        {
            return ObjectMemoryResourcePathUtility.Normalize(unscopedPath);
        }

        return IsLocalFilePath(unscopedPath)
            ? NormalizeLocalFilePath(unscopedPath)
            : NormalizeGamePath(unscopedPath);
    }

    public static bool TryNormalizeTrackedPath(string path, out string normalizedPath)
    {
        normalizedPath = NormalizeTrackedPath(path);
        return normalizedPath.Length > 0;
    }

    public static bool Exists(IDataManager gameData, ObjectResolvedPath resolvedPath)
        => resolvedPath.Kind switch
        {
            ObjectResolvedPathKind.GamePath => gameData.FileExists(resolvedPath.Path),
            ObjectResolvedPathKind.LocalFile => ObjectLocalFilePathUtility.FileExists(resolvedPath.Path),
            ObjectResolvedPathKind.Memory => ObjectMemoryResourcePathUtility.TryParse(resolvedPath.Path, out _),
            _ => false,
        };

    public static bool Exists(IDataManager gameData, ObjectResolvedRootPath resolvedPath)
        => resolvedPath.IsReady && Exists(gameData, resolvedPath.ToResolvedPath());

    public static bool IsSupportedRedirection(string requestedPath, ObjectResolvedPath resolvedPath)
    {
        return TryNormalizeGamePath(requestedPath, out string normalizedRequestedPath)
            && resolvedPath.Path.Length > 0
            && HasMatchingResourceKind(normalizedRequestedPath, resolvedPath);
    }

    public static bool HasExtension(string path, string extension)
        => !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool HasMatchingResourceKind(string requestedPath, ObjectResolvedPath resolvedPath)
    {
        ObjectResourcePathKind requestedKind = ObjectPathRules.ClassifyObjectResourcePath(requestedPath);
        return requestedKind != ObjectResourcePathKind.Unknown
            && requestedKind == ObjectPathRules.ClassifyObjectResourcePath(resolvedPath.ResourceGamePath);
    }
}


