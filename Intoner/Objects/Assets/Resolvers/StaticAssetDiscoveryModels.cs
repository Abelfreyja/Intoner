using Intoner.Objects.Assets.Cache;

namespace Intoner.Objects.Assets;

internal sealed record StaticAssetDiscoveryReuseInput(
    IReadOnlyList<string>? CollisionPaths,
    IReadOnlyList<BgObjectAsset>? GameDataBgObjects,
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
                : null,
            loadedSections.Contains(ObjectAssetCacheSectionKind.StaticResolvedVfx)
                ? snapshot.StaticResolvedVfxEntries
                : null);
}

internal sealed record StaticAssetDiscoverySnapshot(
    string GameVersion,
    IReadOnlyList<string> StaticCollisionPaths,
    IReadOnlyDictionary<string, BgObjectAsset> StaticGameDataBgObjects,
    IReadOnlyDictionary<string, ResolvedVfxPath> StaticResolvedVfxPaths,
    ObjectAssetCacheSectionSet CompleteSections = ObjectAssetCacheSectionSet.AllStatic)
{
    public static StaticAssetDiscoverySnapshot FromCache(string gameVersion, ObjectAssetCacheSnapshot snapshot)
        => new(
            gameVersion,
            snapshot.StaticCollisionPaths,
            snapshot.StaticGameDataBgObjects
                .ToDictionary(static asset => asset.ModelPath, static asset => asset, StringComparer.OrdinalIgnoreCase),
            snapshot.StaticResolvedVfxEntries
                .ToDictionary(static asset => asset.Path, static asset => asset, StringComparer.OrdinalIgnoreCase));

}

internal sealed record ResolvedVfxDiscoverySnapshot(
    IReadOnlyList<ResolvedVfxPath> ResolvedVfxPaths,
    bool IsComplete = true);
