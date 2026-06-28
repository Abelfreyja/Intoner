using Intoner.Objects.Assets.Cache;

namespace Intoner.Objects.Assets;

internal sealed partial class ObjectAssetIndex
{
    private static bool AddKnowledgePath(
        CatalogAssetState state,
        string path,
        AssetPathSource source,
        AssetPathContract contract,
        IEnumerable<string> searchTerms,
        KnownVfxFamily vfxFamily = KnownVfxFamily.None)
    {
        if (!state.KnowledgeBase.AddPath(path, source, contract, searchTerms, vfxFamily))
        {
            return false;
        }

        if (ObjectPathRules.IsCatalogModelPath(path))
        {
            state.ObservedBgSnapshotDirty = true;
        }

        if (ObjectPathRules.IsVfxPath(path))
        {
            state.StandaloneVfxSnapshotDirty = true;
        }

        return true;
    }

    private static void MarkObservedBgChanged(CatalogAssetState state)
    {
        MarkBgObjectChanged(state, observedAssetsChanged: true);
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.BgModels);
    }

    private static void MarkGameDataBgChanged(CatalogAssetState state)
    {
        MarkBgObjectChanged(state, observedAssetsChanged: false);
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.StaticBgObjects);
    }

    private static void MarkStandaloneVfxChanged(CatalogAssetState state)
    {
        state.StandaloneVfxSnapshotDirty = true;
        state.StandaloneVfxSectionVersion++;
        MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.StandaloneVfx);
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

    private static void MarkCacheSectionsDirty(CatalogAssetState state, ObjectAssetCacheSectionSet sections)
    {
        if (sections == ObjectAssetCacheSectionSet.None)
        {
            return;
        }

        state.DirtyCacheSections |= sections;
        state.CacheRevision++;
    }

    private static ObservationApplyResult Combine(ObservationApplyResult current, ObservationApplyResult next)
        => current == ObservationApplyResult.ProjectionChanged || next == ObservationApplyResult.ProjectionChanged
            ? ObservationApplyResult.ProjectionChanged
            : current == ObservationApplyResult.MetadataChanged || next == ObservationApplyResult.MetadataChanged
                ? ObservationApplyResult.MetadataChanged
                : ObservationApplyResult.None;

    private enum ObservationApplyResult
    {
        None = 0,
        MetadataChanged = 1,
        ProjectionChanged = 2,
    }

    private sealed class CatalogAssetState
    {
        public string GameVersion { get; set; } = string.Empty;
        public string SqpackIndexFingerprint { get; set; } = string.Empty;
        public PathKnowledgeBase KnowledgeBase { get; } = new();
        public HashSet<string> StaticCollisionPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SharedGroupAssetInfo> SharedGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, GameDataBgObjectAsset> GameDataBgObjects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ResolvedVfxPath> StaticResolvedVfxPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ObservedBgModelState> BgModels { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RuntimeVfxAssetState> VfxAssets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VfxTimelineReferenceInfo> StaticTimelineReferencedVfx { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VfxTimelineReferenceInfo> RuntimeTimelineReferencedVfx { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ProcessedTimelinePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ObservedBgAsset[]? ObservedBgSnapshot { get; set; }
        public GameDataBgObjectAsset[]? GameDataBgSnapshot { get; set; }
        public RuntimeVfxAsset[]? StandaloneVfxSnapshot { get; set; }
        public bool ObservedBgSnapshotDirty { get; set; } = true;
        public bool GameDataBgSnapshotDirty { get; set; } = true;
        public bool StandaloneVfxSnapshotDirty { get; set; } = true;
        public long BgObjectSectionVersion { get; set; }
        public long StandaloneVfxSectionVersion { get; set; }
        public ObjectAssetCacheSectionSet DirtyCacheSections { get; set; }
        public long CacheRevision { get; set; }
    }
}

