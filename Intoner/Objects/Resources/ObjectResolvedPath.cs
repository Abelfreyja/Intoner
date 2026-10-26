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
    {
        if (System.IO.Path.IsPathFullyQualified(TextUtility.TrimOrEmpty(path)))
        {
            return new(ObjectResolvedPathKind.GamePath, string.Empty);
        }

        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        return new(ObjectResolvedPathKind.GamePath,
            ObjectScopedResourcePathUtility.IsObjectScopedPath(normalizedPath)
             || ObjectScopedResourcePathUtility.IsForeignScopedPath(normalizedPath)
             || ObjectMemoryResourcePathUtility.IsMemoryResourcePath(normalizedPath)
             || ObjectLocalFilePathUtility.IsLocalFilePath(normalizedPath)
             || !ObjectResourcePathEncoding.TryGetGamePathByteCount(normalizedPath, out _)
                ? string.Empty : normalizedPath);
    }

    public static ObjectResolvedPath FromLocalFile(string path)
        => new(ObjectResolvedPathKind.LocalFile,
            ObjectLocalFilePathUtility.IsLocalFilePath(path)
                ? ObjectScopedResourcePathUtility.Create(0, path)
                : string.Empty);

    public static ObjectResolvedPath FromMemory(string path)
        => new(ObjectResolvedPathKind.Memory, ObjectMemoryResourcePathUtility.Normalize(path));

    public static bool TryCreate(string path, out ObjectResolvedPath resolvedPath)
    {
        resolvedPath = default;
        string normalizedPath = ObjectScopedResourcePathUtility.Create(0, path);

        if (normalizedPath.Length == 0)
        {
            return false;
        }

        ObjectResolvedPathKind kind = ObjectResolvedPathKind.GamePath;

        if (ObjectMemoryResourcePathUtility.IsMemoryResourcePath(normalizedPath))
        {
            kind = ObjectResolvedPathKind.Memory;
        }
        else if (ObjectLocalFilePathUtility.IsLocalFilePath(normalizedPath))
        {
            kind = ObjectResolvedPathKind.LocalFile;
        }

        resolvedPath = new ObjectResolvedPath(kind, normalizedPath);
        return true;
    }
}

/// <summary> one requested object resource redirect rule </summary>
internal readonly record struct ObjectPathRedirection
{
    public ObjectPathRedirection(string requestedPath, ObjectResolvedPath resolvedPath)
    {
        RequestedPath = ObjectResolvedPath.FromGamePath(requestedPath).Path;
        ResolvedPath = resolvedPath;
    }

    public string RequestedPath { get; } = string.Empty;
    public ObjectResolvedPath ResolvedPath { get; } = default;
}

/// <summary> object resource helpers for tracked paths, existence checks, and redirect validation </summary>
internal static class ObjectResourcePathUtility
{
    /// <summary> recognizes set private path prefixes even when the identity is malformed </summary>
    public static bool IsOwnedResourcePath(string path)
        => ObjectScopedResourcePathUtility.IsObjectScopedPath(path)
            || ObjectMemoryResourcePathUtility.IsMemoryResourcePath(path);

    /// <summary> admits game paths before checking their supported object resource kind </summary>
    public static bool TryNormalizeSupportedGamePath(string path, out string normalizedPath)
    {
        normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (ObjectAssetPathRules.IsSupportedResourcePath(normalizedPath))
        {
            return true;
        }

        normalizedPath = string.Empty;
        return false;
    }

    public static string NormalizeTrackedPath(string path)
        => ObjectScopedResourcePathUtility.Create(0, ObjectScopedResourcePathUtility.Strip(path));

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
        string normalizedRequestedPath = ObjectResolvedPath.FromGamePath(requestedPath).Path;
        return normalizedRequestedPath.Length > 0
            && !string.IsNullOrEmpty(resolvedPath.Path)
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
