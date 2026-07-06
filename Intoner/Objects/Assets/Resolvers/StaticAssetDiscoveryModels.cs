using Intoner.Objects.Assets.Cache;

namespace Intoner.Objects.Assets;

internal sealed record StaticAssetDiscoveryReuseInput(
    IReadOnlyList<string>? CollisionPaths,
    IReadOnlyList<GameDataBgObjectAsset>? GameDataBgObjects,
    IReadOnlyList<ResolvedVfxPath>? ResolvedVfxPaths)
{
    public static StaticAssetDiscoveryReuseInput FromCache(
        ObjectAssetCacheSnapshot snapshot,
        ObjectAssetCacheSectionSet loadedSections)
        => new(
            loadedSections.Contains(ObjectAssetCacheSectionKind.StaticCollisionPaths)
                ? snapshot.StaticCollisionPaths
                : null,
            loadedSections.Contains(ObjectAssetCacheSectionKind.StaticBgObjects)
                ? snapshot.StaticGameDataBgObjects
                    .Select(ObjectAssetCacheProjection.ToGameDataBgObjectAsset)
                    .ToArray()
                : null,
            loadedSections.Contains(ObjectAssetCacheSectionKind.StaticResolvedVfx)
                ? snapshot.StaticResolvedVfxEntries
                    .Select(ObjectAssetCacheProjection.ToResolvedVfxPath)
                    .ToArray()
                : null);
}

internal sealed record StaticAssetDiscoverySnapshot(
    string GameVersion,
    IReadOnlyList<string> StaticCollisionPaths,
    IReadOnlyDictionary<string, GameDataBgObjectAsset> StaticGameDataBgObjects,
    IReadOnlyDictionary<string, ResolvedVfxPath> StaticResolvedVfxPaths)
{
    public static StaticAssetDiscoverySnapshot FromCache(string gameVersion, ObjectAssetCacheSnapshot snapshot)
        => new(
            gameVersion,
            snapshot.StaticCollisionPaths,
            snapshot.StaticGameDataBgObjects
                .Select(ObjectAssetCacheProjection.ToGameDataBgObjectAsset)
                .ToDictionary(static asset => asset.ModelPath, static asset => asset, StringComparer.OrdinalIgnoreCase),
            snapshot.StaticResolvedVfxEntries
                .Select(ObjectAssetCacheProjection.ToResolvedVfxPath)
                .ToDictionary(static asset => asset.Path, static asset => asset, StringComparer.OrdinalIgnoreCase));

    public PathKnowledgeBase BuildKnowledgeBase()
    {
        PathKnowledgeBase knowledgeBase = new();

        foreach (string collisionPath in StaticCollisionPaths)
        {
            _ = knowledgeBase.AddPath(
                collisionPath,
                AssetPathSource.SqpackCollision,
                AssetPathContract.SqpackNamedLeak,
                ["sqpack collision"]);
        }

        foreach (GameDataBgObjectAsset gameDataBgObjectAsset in StaticGameDataBgObjects.Values)
        {
            _ = knowledgeBase.AddPath(
                gameDataBgObjectAsset.ModelPath,
                AssetPathSource.GameData,
                AssetPathContract.None,
                gameDataBgObjectAsset.SearchTerms);
        }

        foreach (ResolvedVfxPath resolvedVfxPath in StaticResolvedVfxPaths.Values)
        {
            _ = knowledgeBase.AddPath(
                resolvedVfxPath.Path,
                resolvedVfxPath.Sources,
                resolvedVfxPath.Contracts,
                resolvedVfxPath.SearchTerms,
                resolvedVfxPath.Family);
        }

        return knowledgeBase;
    }
}

