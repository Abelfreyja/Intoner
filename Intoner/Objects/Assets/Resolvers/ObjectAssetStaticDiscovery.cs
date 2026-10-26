using Intoner.Objects.Assets.Cache;
using Intoner.Objects.Resources;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Assets;

internal sealed class ObjectAssetStaticDiscovery
{
    private readonly ILogger<ObjectAssetStaticDiscovery> _logger;
    private readonly IObjectAssetGameData _gameData;
    private readonly SqpackIndexStore _sqpackIndexStore;
    private readonly GameDataLayoutAssetResolver _gameDataLayoutAssetResolver;
    private readonly GameDataVfxResolver _gameDataVfxResolver;
    private readonly RootExlResolver _rootExlResolver;
    private readonly RootExlVfxFamilyResolver _rootExlVfxFamilyResolver;
    private readonly NativeVfxFamilyResolver _nativeVfxFamilyResolver;

    public ObjectAssetStaticDiscovery(
        ILogger<ObjectAssetStaticDiscovery> logger,
        IObjectAssetGameData gameData,
        SqpackIndexStore sqpackIndexStore,
        GameDataLayoutAssetResolver gameDataLayoutAssetResolver,
        GameDataVfxResolver gameDataVfxResolver,
        RootExlResolver rootExlResolver,
        RootExlVfxFamilyResolver rootExlVfxFamilyResolver,
        NativeVfxFamilyResolver nativeVfxFamilyResolver)
    {
        _logger = logger;
        _gameData = gameData;
        _sqpackIndexStore = sqpackIndexStore;
        _gameDataLayoutAssetResolver = gameDataLayoutAssetResolver;
        _gameDataVfxResolver = gameDataVfxResolver;
        _rootExlResolver = rootExlResolver;
        _rootExlVfxFamilyResolver = rootExlVfxFamilyResolver;
        _nativeVfxFamilyResolver = nativeVfxFamilyResolver;
    }

    public StaticAssetDiscoverySnapshot Discover(
        string gameVersion,
        StaticAssetDiscoveryReuseInput? reuseInput = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqpackIndexSnapshot? indexSnapshot = null;
        if (reuseInput?.CollisionPaths is null || reuseInput.ResolvedVfxPaths is null)
        {
            indexSnapshot = _sqpackIndexStore.Load(cancellationToken);
        }

        IReadOnlyList<string> resolvedCollisionPaths = BuildStaticCollisionPaths(
            reuseInput?.CollisionPaths
            ?? indexSnapshot!.NamedPaths.Select(static path => path.Path),
            cancellationToken);

        GameDataLayoutAssetResolver.Snapshot? layoutSnapshot = reuseInput?.GameDataBgObjects is null || reuseInput.ResolvedVfxPaths is null
            ? _gameDataLayoutAssetResolver.Resolve(cancellationToken)
            : null;
        ObjectAssetCacheSectionSet completeSections = ObjectAssetCacheSectionSet.AllStatic;
        IReadOnlyDictionary<string, BgObjectAsset> resolvedBgObjectAssets = BuildStaticGameDataBgObjects(
            reuseInput?.GameDataBgObjects ?? layoutSnapshot!.BgObjects,
            cancellationToken);

        if (reuseInput?.GameDataBgObjects is null && !layoutSnapshot!.BgObjectsComplete)
        {
            completeSections &= ~ObjectAssetCacheSectionSet.StaticBgObjects;
        }

        IReadOnlyDictionary<string, ResolvedVfxPath> resolvedVfxAssets = BuildStaticResolvedVfxPaths(
            reuseInput?.ResolvedVfxPaths ?? [],
            cancellationToken);
        if (reuseInput?.ResolvedVfxPaths is null)
        {
            resolvedVfxAssets = ResolveDiscoveredVfxPaths(indexSnapshot!, layoutSnapshot!, out bool isComplete, cancellationToken);

            if (!isComplete)
            {
                completeSections &= ~ObjectAssetCacheSectionSet.StaticResolvedVfx;
            }
        }

        string resolvedGameVersion = !string.IsNullOrWhiteSpace(gameVersion)
            ? gameVersion
            : indexSnapshot?.GameVersion ?? string.Empty;

        cancellationToken.ThrowIfCancellationRequested();
        int analyzedVfxCount = resolvedVfxAssets.Values.Count(static asset => asset.Analysis is not null);

        _logger.LogInformation(
            "built static asset discovery snapshot with {CollisionPathCount} sqpack named paths, {GameDataBgObjectCount} game data bgobject models, {ResolvedVfxCount} resolved vfx paths, and {AnalyzedVfxCount} analyzed vfx paths",
            resolvedCollisionPaths.Count,
            resolvedBgObjectAssets.Count,
            resolvedVfxAssets.Count,
            analyzedVfxCount);

        return new StaticAssetDiscoverySnapshot(
            resolvedGameVersion,
            resolvedCollisionPaths,
            resolvedBgObjectAssets,
            resolvedVfxAssets,
            completeSections);
    }

    private IReadOnlyDictionary<string, ResolvedVfxPath> ResolveDiscoveredVfxPaths(
        SqpackIndexSnapshot indexSnapshot,
        GameDataLayoutAssetResolver.Snapshot layoutSnapshot,
        out bool isComplete,
        CancellationToken cancellationToken)
    {
        ResolvedVfxDiscoverySnapshot gameDataVfxSnapshot = _gameDataVfxResolver.Resolve(indexSnapshot, layoutSnapshot, cancellationToken);
        isComplete = gameDataVfxSnapshot.IsComplete;
        List<ResolvedVfxPath> staticResolvedVfxPaths = [.. gameDataVfxSnapshot.ResolvedVfxPaths];

        cancellationToken.ThrowIfCancellationRequested();
        RootExlDatasetIndex? rootExlDatasetIndex = _rootExlResolver.Load(cancellationToken);
        if (rootExlDatasetIndex is not null)
        {
            ResolvedVfxDiscoverySnapshot rootExlVfxSnapshot = _rootExlVfxFamilyResolver.Resolve(rootExlDatasetIndex, indexSnapshot, cancellationToken);
            isComplete &= rootExlVfxSnapshot.IsComplete;
            staticResolvedVfxPaths.AddRange(rootExlVfxSnapshot.ResolvedVfxPaths);
        }
        else
        {
            isComplete = false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ResolvedVfxDiscoverySnapshot nativeVfxSnapshot = _nativeVfxFamilyResolver.Resolve(indexSnapshot, cancellationToken);
        isComplete &= nativeVfxSnapshot.IsComplete;
        staticResolvedVfxPaths.AddRange(nativeVfxSnapshot.ResolvedVfxPaths);
        return BuildStaticResolvedVfxPaths(staticResolvedVfxPaths, cancellationToken);
    }

    private static IReadOnlyList<string> BuildStaticCollisionPaths(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        HashSet<string> collisionPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;
            if (ObjectAssetPathRules.IsRelevantSqpackCatalogSeedPath(normalizedPath))
            {
                _ = collisionPaths.Add(normalizedPath);
            }
        }

        return collisionPaths
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, BgObjectAsset> BuildStaticGameDataBgObjects(
        IReadOnlyList<BgObjectAsset> assets,
        CancellationToken cancellationToken)
    {
        Dictionary<string, BgObjectAsset> gameDataAssets = new(StringComparer.OrdinalIgnoreCase);
        foreach (BgObjectAsset asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = ObjectResolvedPath.FromGamePath(asset.ModelPath).Path;
            if (!ObjectAssetPathRules.IsCatalogModelPath(normalizedPath))
            {
                continue;
            }

            gameDataAssets[normalizedPath] = string.Equals(normalizedPath, asset.ModelPath, StringComparison.OrdinalIgnoreCase)
                ? asset
                : asset with { ModelPath = normalizedPath };
        }

        return gameDataAssets;
    }

    private IReadOnlyDictionary<string, ResolvedVfxPath> BuildStaticResolvedVfxPaths(
        IReadOnlyList<ResolvedVfxPath> assets,
        CancellationToken cancellationToken)
    {
        Dictionary<string, ResolvedVfxPathAccumulator> resolvedVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (ResolvedVfxPath asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                resolvedVfxPaths,
                _gameData,
                asset);
        }

        foreach (ResolvedVfxPathAccumulator resolvedVfxPath in resolvedVfxPaths.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolvedVfxPath.Analysis is not null
             || !GameAssetPathRules.IsFileKind(resolvedVfxPath.Path, GameAssetFileKind.Avfx)
             || !_gameData.FileExists(resolvedVfxPath.Path))
            {
                continue;
            }

            if (VfxAssetAnalyzer.TryAnalyzeAvfx(_gameData, resolvedVfxPath.Path, out VfxAnalysis analysis))
            {
                resolvedVfxPath.Merge(
                    KnownVfxFamily.None,
                    RuntimeVfxEvidence.None,
                    AssetPathSource.None,
                    AssetPathContract.None,
                    [],
                    analysis);
            }
        }

        return ResolvedVfxPathAccumulator.BuildSnapshot(resolvedVfxPaths.Values)
            .ToDictionary(static asset => asset.Path, static asset => asset, StringComparer.OrdinalIgnoreCase);
    }
}
