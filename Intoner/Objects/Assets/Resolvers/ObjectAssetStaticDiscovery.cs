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
        SqpackIndexSnapshot? indexSnapshot = null;
        if (reuseInput?.CollisionPaths is null || reuseInput.ResolvedVfxPaths is null)
        {
            indexSnapshot = _sqpackIndexStore.Load(cancellationToken);
        }

        IReadOnlyList<string> resolvedCollisionPaths = BuildStaticCollisionPaths(
            reuseInput?.CollisionPaths
            ?? indexSnapshot!.NamedPaths
                .Select(static path => path.Path)
                .ToArray(),
            cancellationToken);

        IReadOnlyDictionary<string, GameDataBgObjectAsset> resolvedGameDataBgObjectAssets = BuildStaticGameDataBgObjects(
            reuseInput?.GameDataBgObjects ?? _gameDataLayoutAssetResolver.Resolve(cancellationToken).BgObjects,
            cancellationToken);

        IReadOnlyDictionary<string, ResolvedVfxPath> resolvedVfxAssets = BuildStaticResolvedVfxPaths(
            reuseInput?.ResolvedVfxPaths ?? [],
            cancellationToken);
        if (reuseInput?.ResolvedVfxPaths is null)
        {
            resolvedVfxAssets = ResolveDiscoveredVfxPaths(indexSnapshot!, cancellationToken);
        }

        string resolvedGameVersion = !string.IsNullOrWhiteSpace(gameVersion)
            ? gameVersion
            : indexSnapshot?.GameVersion ?? string.Empty;

        int analyzedVfxCount = resolvedVfxAssets.Values.Count(static asset => asset.Analysis is not null);

        _logger.LogInformation(
            "built static asset discovery snapshot with {CollisionPathCount} sqpack named paths, {GameDataBgObjectCount} game data bgobject models, {ResolvedVfxCount} resolved vfx paths, and {AnalyzedVfxCount} analyzed vfx paths",
            resolvedCollisionPaths.Count,
            resolvedGameDataBgObjectAssets.Count,
            resolvedVfxAssets.Count,
            analyzedVfxCount);

        return new StaticAssetDiscoverySnapshot(
            resolvedGameVersion,
            resolvedCollisionPaths,
            resolvedGameDataBgObjectAssets,
            resolvedVfxAssets);
    }

    private IReadOnlyDictionary<string, ResolvedVfxPath> ResolveDiscoveredVfxPaths(
        SqpackIndexSnapshot indexSnapshot,
        CancellationToken cancellationToken)
    {
        List<ResolvedVfxPath> staticResolvedVfxPaths = [];
        staticResolvedVfxPaths.AddRange(_gameDataVfxResolver.Resolve(indexSnapshot, cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
        RootExlDatasetIndex? rootExlDatasetIndex = _rootExlResolver.Load(cancellationToken);
        if (rootExlDatasetIndex is not null)
        {
            staticResolvedVfxPaths.AddRange(_rootExlVfxFamilyResolver.Resolve(rootExlDatasetIndex, indexSnapshot, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        staticResolvedVfxPaths.AddRange(_nativeVfxFamilyResolver.Resolve(indexSnapshot, cancellationToken));
        return BuildStaticResolvedVfxPaths(staticResolvedVfxPaths, cancellationToken);
    }

    private static IReadOnlyList<string> BuildStaticCollisionPaths(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        HashSet<string> collisionPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
            if (ObjectPathRules.IsRelevantSqpackCatalogSeedPath(normalizedPath))
            {
                _ = collisionPaths.Add(normalizedPath);
            }
        }

        return collisionPaths
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, GameDataBgObjectAsset> BuildStaticGameDataBgObjects(
        IReadOnlyList<GameDataBgObjectAsset> assets,
        CancellationToken cancellationToken)
    {
        Dictionary<string, GameDataBgObjectAsset> gameDataAssets = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameDataBgObjectAsset asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = ObjectPathRules.NormalizeGamePath(asset.ModelPath);
            if (string.IsNullOrWhiteSpace(normalizedPath))
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
             || !ObjectPathRules.IsVfxPath(resolvedVfxPath.Path)
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

