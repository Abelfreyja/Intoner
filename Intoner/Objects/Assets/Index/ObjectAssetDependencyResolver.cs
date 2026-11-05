using Intoner.Objects.Resources;
using Lumina.Data.Files;
using System.Collections.Immutable;

namespace Intoner.Objects.Assets;

internal readonly record struct AssetDependencyPaths(IReadOnlyList<string> Paths, bool IsComplete = true)
{
    public static AssetDependencyPaths Empty { get; } = new([]);
    public static AssetDependencyPaths Incomplete { get; } = new([], false);
}

internal sealed class ObjectAssetDependencyResolver(IObjectAssetGameData gameData)
{
    private readonly IObjectAssetGameData _gameData = gameData;

    public bool? HasSound(string rootPath, ObjectCollectionResolveData? collection, CancellationToken cancellationToken)
    {
        string root = ObjectResolvedPath.FromGamePath(rootPath).Path;

        if (GameAssetPathRules.ClassifyNormalizedFilePath(root) is not (GameAssetFileKind.Sgb or GameAssetFileKind.Avfx))
        {
            return false;
        }

        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase) { root };
        Queue<string> pending = new();
        pending.Enqueue(root);
        bool complete = true;
        while (pending.TryDequeue(out string? path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectResolvedPath resolved = collection is not null && collection.TryResolvePath(path, out ObjectResolvedPath replacement)
                ? replacement : ObjectResolvedPath.FromGamePath(path);
            AssetDependencyPaths dependencies = GetCollectionPathDependencies(path, resolved, cancellationToken);
            complete &= dependencies.IsComplete;
            foreach (string dependency in dependencies.Paths)
            {
                GameAssetFileKind kind = GameAssetPathRules.ClassifyNormalizedFilePath(dependency);

                if (kind == GameAssetFileKind.Scd)
                {
                    return true;
                }

                if (kind is GameAssetFileKind.Sgb or GameAssetFileKind.Avfx && visited.Add(dependency))
                {
                    pending.Enqueue(dependency);
                }
            }
        }

        return complete ? false : null;
    }

    public AssetDependencyPaths GetCollectionPathDependencies(
        string requestedPath,
        ObjectResolvedPath effectivePath,
        CancellationToken cancellationToken = default,
        IReadOnlySet<ushort>? materialVariants = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!ObjectResourcePathUtility.TryNormalizeSupportedGamePath(requestedPath, out string normalizedRequestedPath))
        {
            return AssetDependencyPaths.Empty;
        }

        AssetDependencyPaths collected;
        try
        {
            collected = GameAssetPathRules.ClassifyNormalizedFilePath(normalizedRequestedPath) switch
            {
                GameAssetFileKind.Mdl => CollectModelDependencies(normalizedRequestedPath, effectivePath, cancellationToken, materialVariants),
                GameAssetFileKind.Sgb => SharedGroupAssetResolver.AnalyzeSharedGroupDependencies(_gameData, normalizedRequestedPath, effectivePath, cancellationToken),
                GameAssetFileKind.Avfx => effectivePath.Kind switch
                {
                    ObjectResolvedPathKind.GamePath => VfxAssetAnalyzer.CollectAvfxDependencyPaths(_gameData, effectivePath.Path, cancellationToken),
                    ObjectResolvedPathKind.LocalFile => VfxAssetAnalyzer.CollectLocalAvfxDependencyPaths(effectivePath.Path, cancellationToken),
                    _ => AssetDependencyPaths.Incomplete,
                },
                GameAssetFileKind.Mtrl => effectivePath.Kind switch
                {
                    ObjectResolvedPathKind.GamePath => ObjectMaterialPathUtility.CollectMaterialDependencyPaths(_gameData, effectivePath.Path, cancellationToken),
                    ObjectResolvedPathKind.LocalFile => ObjectMaterialPathUtility.CollectLocalMaterialDependencyPaths(effectivePath.Path, cancellationToken),
                    _ => AssetDependencyPaths.Incomplete,
                },
                _ => AssetDependencyPaths.Empty,
            };
        }
        catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AssetDependencyPaths.Incomplete;
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in collected.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ObjectResourcePathUtility.TryNormalizeSupportedGamePath(path, out string normalizedPath))
            {
                paths.Add(normalizedPath);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(paths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToImmutableArray(), collected.IsComplete);
    }

    public string GetWallpaperModelPath(string requestedPath, ObjectResolvedPath effectivePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SgbFile? file = effectivePath.Kind switch
        {
            ObjectResolvedPathKind.LocalFile => _gameData.GetFileFromDisk<SgbFile>(effectivePath.Path, requestedPath),
            ObjectResolvedPathKind.GamePath => _gameData.GetFile<SgbFile>(effectivePath.Path),
            _ => null,
        };
        cancellationToken.ThrowIfCancellationRequested();
        return file is not null ? HousingMaterialUtility.GetWallpaperModelPath(file) : string.Empty;
    }

    private AssetDependencyPaths CollectModelDependencies(string requestedPath, ObjectResolvedPath effectivePath, CancellationToken cancellationToken, IReadOnlySet<ushort>? materialVariants)
    {
        AssetDependencyPaths materials = effectivePath.Kind switch
        {
            ObjectResolvedPathKind.GamePath => ObjectMaterialPathUtility.CollectGameModelMaterialPaths(_gameData, effectivePath.Path, cancellationToken, materialVariants),
            ObjectResolvedPathKind.LocalFile => ObjectMaterialPathUtility.CollectLocalModelMaterialPaths(_gameData, requestedPath, effectivePath.Path, cancellationToken, materialVariants),
            _ => AssetDependencyPaths.Incomplete,
        };
        string modelPath = effectivePath.Kind == ObjectResolvedPathKind.GamePath ? effectivePath.Path : requestedPath;
        return new(GameAssetPathCollectionUtility.MergeGamePaths(materials.Paths, ObjectAssetPathRules.CollectBgModelAnimationResourcePaths(modelPath)),
            materials.IsComplete);
    }
}
