using Intoner.Objects.Utils;

namespace Intoner.Objects.Assets;

internal sealed partial class ObjectAssetIndex
{
    private static ObservedBgAsset[] BuildObservedBgSnapshot(CatalogAssetState state)
        => BgAssetProjection.BuildObservedSnapshot(
            state.BgModels.Values,
            state.KnowledgeBase,
            ObservedBgAssetComparer);

    private static GameDataBgObjectAsset[] BuildGameDataBgSnapshot(CatalogAssetState state)
        => BgAssetProjection.BuildGameDataSnapshot(
            state.GameDataBgObjects.Values,
            GameDataBgObjectAssetComparer);

    private static bool TryAddBgModel(
        CatalogAssetState state,
        string path,
        string source,
        in ObjectTerritoryMetadata territoryMetadata)
    {
        string normalizedPath = ObjectPathRules.NormalizeGamePath(path);
        if (!ObjectPathRules.IsCatalogModelPath(normalizedPath))
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

    private static bool RemoveGameDataBgDuplicates(CatalogAssetState state)
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

    private ObjectTerritoryMetadata GetCurrentTerritoryMetadata()
        => ObjectTerritoryMetadataUtility.BuildForTerritoryId(_clientState.TerritoryType, _dataManager);
}
