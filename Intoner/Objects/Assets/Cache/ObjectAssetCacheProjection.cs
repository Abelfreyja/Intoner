namespace Intoner.Objects.Assets.Cache;

internal static class ObjectAssetCacheProjection
{
    public static ObjectAssetCacheStaticBgObject ToCacheStaticBgObject(GameDataBgObjectAsset asset)
        => new(
            asset.ModelPath,
            asset.Source,
            asset.RowId,
            asset.SourcePath,
            asset.TerritoryIds,
            asset.TerritoryNames,
            asset.SearchTerms);

    public static GameDataBgObjectAsset ToGameDataBgObjectAsset(ObjectAssetCacheStaticBgObject asset)
        => new(
            asset.ModelPath,
            asset.Source,
            asset.RowId,
            asset.SourcePath,
            asset.TerritoryIds,
            asset.TerritoryNames,
            asset.SearchTerms);

    public static ObjectAssetCacheResolvedVfxEntry ToCacheResolvedVfxEntry(ResolvedVfxPath asset)
        => new(
            asset.Path,
            asset.Family,
            asset.Evidence,
            asset.Sources,
            asset.Contracts,
            asset.SearchTerms,
            asset.Analysis);

    public static ResolvedVfxPath ToResolvedVfxPath(ObjectAssetCacheResolvedVfxEntry asset)
        => new(
            asset.Path,
            asset.Family,
            asset.Evidence,
            asset.Sources,
            asset.Contracts,
            asset.SearchTerms,
            asset.Analysis);
}
