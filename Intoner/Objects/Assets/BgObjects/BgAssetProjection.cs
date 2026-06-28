namespace Intoner.Objects.Assets;

internal static class BgAssetProjection
{
    public static ObservedBgAsset[] BuildObservedSnapshot(
        IReadOnlyCollection<ObservedBgModelState> assets,
        PathKnowledgeBase knowledgeBase,
        IComparer<ObservedBgAsset> comparer)
    {
        ObservedBgAsset[] snapshot = new ObservedBgAsset[assets.Count];
        var index = 0;
        foreach (ObservedBgModelState asset in assets)
        {
            snapshot[index++] = asset.ToAsset(knowledgeBase);
        }

        Array.Sort(snapshot, comparer);
        return snapshot;
    }

    public static GameDataBgObjectAsset[] BuildGameDataSnapshot(
        IReadOnlyCollection<GameDataBgObjectAsset> assets,
        IComparer<GameDataBgObjectAsset> comparer)
    {
        GameDataBgObjectAsset[] snapshot = [.. assets];
        Array.Sort(snapshot, comparer);
        return snapshot;
    }
}

