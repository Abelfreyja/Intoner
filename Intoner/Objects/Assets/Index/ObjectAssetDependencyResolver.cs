using Intoner.Objects.Resources;
using System.Collections.Immutable;

namespace Intoner.Objects.Assets;

internal sealed class ObjectAssetDependencyResolver(
    IObjectAssetGameData gameData,
    ObjectAssetSharedGroupCache sharedGroupCache)
{
    private readonly IObjectAssetGameData _gameData = gameData;
    private readonly ObjectAssetSharedGroupCache _sharedGroupCache = sharedGroupCache;

    public IReadOnlyList<string> GetCollectionPathDependencies(
        string requestedPath,
        ObjectResolvedPath effectivePath,
        CatalogAssetState state,
        Lock stateLock)
    {
        HashSet<string> dependencyPaths = new(StringComparer.OrdinalIgnoreCase);
        string normalizedRequestedPath = ObjectPathRules.NormalizeGamePath(requestedPath);
        foreach (string dependencyPath in CollectRequestedPathDependencies(
            normalizedRequestedPath,
            effectivePath,
            state,
            stateLock))
        {
            string normalizedDependencyPath = ObjectPathRules.NormalizeGamePath(dependencyPath);
            if (normalizedDependencyPath.Length > 0)
            {
                dependencyPaths.Add(normalizedDependencyPath);
            }
        }

        return dependencyPaths.Count == 0
            ? []
            : dependencyPaths
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
    }

    private IEnumerable<string> CollectRequestedPathDependencies(
        string requestedPath,
        ObjectResolvedPath effectivePath,
        CatalogAssetState state,
        Lock stateLock)
        => ClassifyScopePath(requestedPath) switch
        {
            RedirectScopePathKind.Model => CollectModelDependencies(requestedPath, effectivePath),
            RedirectScopePathKind.SharedGroup => CollectSharedGroupDependencies(requestedPath, effectivePath, state, stateLock),
            RedirectScopePathKind.Vfx => CollectVfxDependencies(effectivePath),
            RedirectScopePathKind.Material => CollectMaterialDependencies(effectivePath),
            _ => [],
        };

    private IEnumerable<string> CollectModelDependencies(string requestedModelPath, ObjectResolvedPath effectivePath)
    {
        foreach (string materialPath in effectivePath.Kind switch
        {
            ObjectResolvedPathKind.GamePath => ObjectMaterialPathUtility.CollectGameModelMaterialPaths(_gameData, effectivePath.Path),
            ObjectResolvedPathKind.LocalFile => ObjectMaterialPathUtility.CollectLocalModelMaterialPaths(requestedModelPath, effectivePath.Path),
            _ => [],
        })
        {
            yield return materialPath;
        }

        string animationModelPath = effectivePath.Kind == ObjectResolvedPathKind.GamePath
            ? effectivePath.Path
            : requestedModelPath;
        foreach (string animationResourcePath in ObjectPathRules.CollectBgModelAnimationResourcePaths(animationModelPath))
        {
            yield return animationResourcePath;
        }
    }

    private IEnumerable<string> CollectSharedGroupDependencies(
        string requestedPath,
        ObjectResolvedPath effectivePath,
        CatalogAssetState state,
        Lock stateLock)
    {
        if (effectivePath.Kind == ObjectResolvedPathKind.LocalFile)
        {
            return SharedGroupAssetResolver
                .AnalyzeLocalSharedGroupDependencies(_gameData, requestedPath, effectivePath.Path)
                .DependencyPaths;
        }

        if (effectivePath.Kind != ObjectResolvedPathKind.GamePath
         || !_sharedGroupCache.TryGetOrAnalyzeThreadSafe(state, stateLock, effectivePath.Path, out SharedGroupAssetInfo? sharedGroupAssets))
        {
            return [];
        }

        return sharedGroupAssets.CollectionDependencyPaths;
    }

    private IEnumerable<string> CollectVfxDependencies(ObjectResolvedPath effectivePath)
    {
        return effectivePath.Kind switch
        {
            ObjectResolvedPathKind.GamePath => VfxAssetAnalyzer.CollectAvfxDependencyPaths(_gameData, effectivePath.Path),
            ObjectResolvedPathKind.LocalFile => VfxAssetAnalyzer.CollectLocalAvfxDependencyPaths(effectivePath.Path),
            _ => [],
        };
    }

    private IEnumerable<string> CollectMaterialDependencies(ObjectResolvedPath effectivePath)
    {
        return effectivePath.Kind switch
        {
            ObjectResolvedPathKind.GamePath => ObjectMaterialPathUtility.CollectMaterialDependencyPaths(_gameData, effectivePath.Path),
            ObjectResolvedPathKind.LocalFile => ObjectMaterialPathUtility.CollectLocalMaterialDependencyPaths(effectivePath.Path),
            _ => [],
        };
    }

    private static RedirectScopePathKind ClassifyScopePath(string path)
    {
        if (ObjectPathRules.IsModelPath(path))
        {
            return RedirectScopePathKind.Model;
        }

        if (ObjectPathRules.IsSharedGroupPath(path))
        {
            return RedirectScopePathKind.SharedGroup;
        }

        if (ObjectPathRules.IsVfxPath(path))
        {
            return RedirectScopePathKind.Vfx;
        }

        if (ObjectPathRules.IsMaterialPath(path))
        {
            return RedirectScopePathKind.Material;
        }

        return RedirectScopePathKind.Unknown;
    }

    private enum RedirectScopePathKind
    {
        Unknown,
        Model,
        SharedGroup,
        Vfx,
        Material,
    }
}
