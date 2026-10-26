using Intoner.Objects.Resources;
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
        string normalizedPath = ObjectResolvedPath.FromGamePath(path).Path;
        if (!state.KnowledgeBase.AddPath(normalizedPath, source, contract, searchTerms, vfxFamily))
        {
            return false;
        }

        if (GameAssetPathRules.IsFileKind(normalizedPath, GameAssetFileKind.Avfx))
        {
            state.StandaloneVfxSnapshotDirty = true;
        }

        return true;
    }

    public static void MarkObservedBgChanged(CatalogAssetState state)
    {
        MarkBgObjectChanged(state);
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.BgModels);
    }

    public static void MarkGameDataBgChanged(CatalogAssetState state)
    {
        MarkBgObjectChanged(state);
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.StaticBgObjects);
    }

    public static void MarkStandaloneVfxChanged(CatalogAssetState state)
    {
        state.StandaloneVfxSnapshotDirty = true;
        state.StandaloneVfxSectionVersion++;
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.RuntimeVfx);
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

    private static void MarkBgObjectChanged(CatalogAssetState state)
    {
        state.BgObjectSnapshotDirty = true;
        state.BgObjectSectionVersion++;
    }
}
