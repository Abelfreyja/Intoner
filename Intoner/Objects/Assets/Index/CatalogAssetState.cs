using Intoner.Objects.Assets.Cache;

namespace Intoner.Objects.Assets;

internal sealed class CatalogAssetState
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
