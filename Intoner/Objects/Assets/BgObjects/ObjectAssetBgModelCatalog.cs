using Intoner.Objects.Utils;
using static Intoner.Objects.Assets.ObjectAssetStateChange;

namespace Intoner.Objects.Assets;

internal static class ObjectAssetBgModelCatalog
{
    public static ObservationApplyResult ObservePath(
        CatalogAssetState state,
        string path,
        AssetPathSource source,
        AssetPathContract contract,
        IReadOnlyList<string> searchTerms,
        string catalogSource,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        _ = AddKnowledgePath(state, path, source, contract, searchTerms);
        return TryAddBgModel(state, path, catalogSource, territoryMetadata)
            ? ObservationApplyResult.ProjectionChanged
            : ObservationApplyResult.None;
    }

    public static bool TryAddBgModel(
        CatalogAssetState state,
        string path,
        string source,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        if (!ObjectAssetPathRules.IsCatalogModelPath(normalizedPath))
        {
            return false;
        }

        if (state.GameDataBgObjects.ContainsKey(normalizedPath))
        {
            return false;
        }

        if (!state.BgModels.TryGetValue(normalizedPath, out ObservedBgModelState? asset))
        {
            asset = new ObservedBgModelState(normalizedPath);
            state.BgModels[normalizedPath] = asset;
            _ = asset.AddSource(source);
            _ = asset.AddTerritoryMetadata(territoryMetadata);
            MarkObservedBgChanged(state);
            return true;
        }

        bool changed = asset.AddSource(source);
        changed |= asset.AddTerritoryMetadata(territoryMetadata);
        if (!changed)
        {
            return false;
        }

        MarkObservedBgChanged(state);
        return true;
    }

    public static bool RemoveGameDataDuplicates(CatalogAssetState state)
    {
        if (state.GameDataBgObjects.Count == 0 || state.BgModels.Count == 0)
        {
            return false;
        }

        bool removedAny = false;
        foreach (string modelPath in state.GameDataBgObjects.Keys)
        {
            removedAny |= state.BgModels.Remove(modelPath);
        }

        if (removedAny)
        {
            MarkObservedBgChanged(state);
        }

        return removedAny;
    }
}
