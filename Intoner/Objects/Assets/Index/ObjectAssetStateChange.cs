using Intoner.Objects.Assets.Cache;

namespace Intoner.Objects.Assets;

internal static class ObjectAssetStateChange
{
    public static bool AddKnowledgePath(
        CatalogAssetState state,
        string path,
        AssetPathSource source,
        AssetPathContract contract,
        IEnumerable<string> searchTerms,
        KnownVfxFamily vfxFamily = KnownVfxFamily.None)
    {
        string normalizedPath = GameAssetPathRules.NormalizeGamePath(path);
        if (!state.KnowledgeBase.AddPath(normalizedPath, source, contract, searchTerms, vfxFamily))
        {
            return false;
        }

        if (ObjectAssetPathRules.IsCatalogModelPath(normalizedPath))
        {
            state.ObservedBgSnapshotDirty = true;
        }

        if (GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            state.StandaloneVfxSnapshotDirty = true;
        }

        return true;
    }

    public static void MarkObservedBgChanged(CatalogAssetState state)
    {
        MarkBgObjectChanged(state, observedAssetsChanged: true);
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.BgModels);
    }

    public static void MarkGameDataBgChanged(CatalogAssetState state)
    {
        MarkBgObjectChanged(state, observedAssetsChanged: false);
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.StaticBgObjects);
    }

    public static void MarkStandaloneVfxChanged(CatalogAssetState state)
    {
        state.StandaloneVfxSnapshotDirty = true;
        state.StandaloneVfxSectionVersion++;
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.StandaloneVfx);
    }

    public static void MarkCacheSectionsDirty(CatalogAssetState state, ObjectAssetCacheSectionSet sections)
    {
        if (sections == ObjectAssetCacheSectionSet.None)
        {
            return;
        }

        state.DirtyCacheSections |= sections;
        state.CacheRevision++;
    }

    public static ObservationApplyResult Combine(ObservationApplyResult current, ObservationApplyResult next)
    {
        if (current == ObservationApplyResult.ProjectionChanged
         || next == ObservationApplyResult.ProjectionChanged)
        {
            return ObservationApplyResult.ProjectionChanged;
        }

        if (current == ObservationApplyResult.MetadataChanged
         || next == ObservationApplyResult.MetadataChanged)
        {
            return ObservationApplyResult.MetadataChanged;
        }

        return ObservationApplyResult.None;
    }

    private static void MarkBgObjectChanged(CatalogAssetState state, bool observedAssetsChanged)
    {
        if (observedAssetsChanged)
        {
            state.ObservedBgSnapshotDirty = true;
        }
        else
        {
            state.GameDataBgSnapshotDirty = true;
        }

        state.BgObjectSectionVersion++;
    }
}
