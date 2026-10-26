using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using static Intoner.Objects.Assets.ObjectAssetStateChange;

namespace Intoner.Objects.Assets;

internal static class ObjectAssetBgModelCatalog
{
    public static ObservationApplyResult ObservePath(
        CatalogAssetState state,
        string path,
        IReadOnlyList<string> searchTerms,
        string catalogSource,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;

        if (!ObjectAssetPathRules.IsCatalogModelPath(normalizedPath))
        {
            return ObservationApplyResult.None;
        }

        bool changed = false;

        if (!state.BgModels.TryGetValue(normalizedPath, out ObservedBgModelState? asset))
        {
            asset = new ObservedBgModelState(normalizedPath);
            state.BgModels[normalizedPath] = asset;
            changed = true;
        }

        changed |= asset.AddSource(catalogSource);
        changed |= asset.AddTerritoryMetadata(territoryMetadata);
        changed |= asset.AddSearchTerms(searchTerms);
        changed |= asset.AddSearchTerms(territoryMetadata.SearchTerms);

        if (!changed)
        {
            return ObservationApplyResult.None;
        }

        MarkObservedBgChanged(state);
        return ObservationApplyResult.ProjectionChanged;
    }
}
