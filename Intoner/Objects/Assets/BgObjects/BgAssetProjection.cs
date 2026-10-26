using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal static class BgAssetProjection
{
    public static BgObjectAsset[] BuildSnapshot(CatalogAssetState state, IComparer<BgObjectAsset> comparer)
    {
        List<BgObjectAsset> assets = new(state.GameDataBgObjects.Count + state.BgModels.Count);
        foreach (BgObjectAsset asset in state.GameDataBgObjects.Values)
        {
            BgObjectAsset projected = asset;

            if (state.BgModels.TryGetValue(asset.ModelPath, out ObservedBgModelState? overlay))
            {
                BgObjectAsset observed = overlay.ToAsset();
                ObjectTerritoryMetadataSet territories = new();
                _ = territories.Add(asset.TerritoryIds, asset.TerritoryNames);
                _ = territories.Add(observed.TerritoryIds, observed.TerritoryNames);
                projected = asset with
                {
                    TerritoryIds = territories.BuildStableIds(),
                    TerritoryNames = territories.BuildStableNames(),
                    SearchTerms = SearchTermUtility.MergeTerms(asset.SearchTerms, observed.SearchTerms),
                };
            }

            assets.Add(projected);
        }

        foreach (ObservedBgModelState observed in state.BgModels.Values.Where(observed => !state.GameDataBgObjects.ContainsKey(observed.Path)))
        {
            assets.Add(observed.ToAsset());
        }

        assets.Sort(comparer);
        return assets.ToArray();
    }
}
