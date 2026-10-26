using Intoner.Objects.Resources;
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

    public AssetDependencyPaths GetCollectionPathDependencies(
        string requestedPath,
        ObjectResolvedPath effectivePath,
        CancellationToken cancellationToken = default)
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
                GameAssetFileKind.Mdl => CollectModelDependencies(normalizedRequestedPath, effectivePath, cancellationToken),
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

    private AssetDependencyPaths CollectModelDependencies(string requestedPath, ObjectResolvedPath effectivePath, CancellationToken cancellationToken)
    {
        AssetDependencyPaths materials = effectivePath.Kind switch
        {
            ObjectResolvedPathKind.GamePath => ObjectMaterialPathUtility.CollectGameModelMaterialPaths(_gameData, effectivePath.Path, cancellationToken),
            ObjectResolvedPathKind.LocalFile => ObjectMaterialPathUtility.CollectLocalModelMaterialPaths(_gameData, requestedPath, effectivePath.Path, cancellationToken),
            _ => AssetDependencyPaths.Incomplete,
        };
        string modelPath = effectivePath.Kind == ObjectResolvedPathKind.GamePath ? effectivePath.Path : requestedPath;
        return new(GameAssetPathCollectionUtility.MergeGamePaths(materials.Paths, ObjectAssetPathRules.CollectBgModelAnimationResourcePaths(modelPath)),
            materials.IsComplete);
    }
}
