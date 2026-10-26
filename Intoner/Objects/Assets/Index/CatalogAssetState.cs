using Dalamud.Game;
using Intoner.Objects.Assets.Cache;

namespace Intoner.Objects.Assets;

internal sealed class CatalogAssetState
{
    public string GameVersion { get; set; } = string.Empty;
    public string SqpackIndexFingerprint { get; set; } = string.Empty;
    public ClientLanguage Language { get; set; }
    public PathKnowledgeBase KnowledgeBase { get; } = new();
    public HashSet<string> StaticCollisionPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SharedGroupAssetInfo> SharedGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, BgObjectAsset> GameDataBgObjects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ResolvedVfxPath> StaticResolvedVfxPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ObservedBgModelState> BgModels { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RuntimeVfxAssetState> VfxAssets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VfxTimelineReferenceInfo> StaticTimelineReferencedVfx { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VfxTimelineReferenceInfo> RuntimeTimelineReferencedVfx { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AssetPathSource> ProcessedTimelinePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public VfxTimelineReferenceCache TimelineReferences { get; } = new();
    public BgObjectAsset[]? BgObjectSnapshot { get; set; }
    public RuntimeVfxAsset[]? StandaloneVfxSnapshot { get; set; }
    public bool BgObjectSnapshotDirty { get; set; } = true;
    public bool StandaloneVfxSnapshotDirty { get; set; } = true;
    public long BgObjectSectionVersion { get; set; }
    public long StandaloneVfxSectionVersion { get; set; }
    public ObjectAssetCacheSectionSet DirtyCacheSections { get; set; }
    public long CacheRevision { get; set; }
}
