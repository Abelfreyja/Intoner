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
        => new(ObjectResolvedPathKind.GamePath, GameAssetPathRules.NormalizeGamePath(path));

    public static ObjectResolvedPath FromLocalFile(string path)
        => new(ObjectResolvedPathKind.LocalFile, ObjectLocalFilePathUtility.NormalizeLocalFilePath(path));

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

        if (ObjectLocalFilePathUtility.IsLocalFilePath(path))
        {
            if (!ObjectLocalFilePathUtility.TryNormalizeLocalFilePath(path, out string normalizedLocalFilePath))
            {
                resolvedPath = default;
                return false;
            }

            resolvedPath = new ObjectResolvedPath(ObjectResolvedPathKind.LocalFile, normalizedLocalFilePath);
            return true;
        }

        if (!GameAssetPathRules.TryNormalizeGamePath(path, out string normalizedGamePath))
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
        RequestedPath = GameAssetPathRules.NormalizeGamePath(requestedPath);
        ResolvedPath = resolvedPath;
    }

    public string RequestedPath { get; } = string.Empty;
    public ObjectResolvedPath ResolvedPath { get; } = default;
}

/// <summary> object resource helpers for tracked paths, existence checks, and redirect validation </summary>
internal static class ObjectResourcePathUtility
{
    public static string NormalizeTrackedPath(string path)
    {
        string unscopedPath = ObjectScopedResourcePathUtility.Strip(path);
        if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(unscopedPath))
        {
            return ObjectMemoryResourcePathUtility.Normalize(unscopedPath);
        }

        return ObjectLocalFilePathUtility.IsLocalFilePath(unscopedPath)
            ? ObjectLocalFilePathUtility.NormalizeLocalFilePath(unscopedPath)
            : GameAssetPathRules.NormalizeGamePath(unscopedPath);
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
        return GameAssetPathRules.TryNormalizeGamePath(requestedPath, out string normalizedRequestedPath)
            && resolvedPath.Path.Length > 0
            && HasCompatibleResourceKind(normalizedRequestedPath, resolvedPath);
    }

    private static bool HasCompatibleResourceKind(string requestedPath, ObjectResolvedPath resolvedPath)
    {
        ObjectResourcePathKind requestedKind = ObjectAssetPathRules.ClassifyResourcePath(requestedPath);
        if (requestedKind == ObjectResourcePathKind.Unknown)
        {
            return false;
        }

        if (resolvedPath.Kind == ObjectResolvedPathKind.LocalFile)
        {
            return true;
        }

        return requestedKind == ObjectAssetPathRules.ClassifyResourcePath(resolvedPath.ResourceGamePath);
    }
}


