namespace Intoner.Objects.Assets.Cache;

internal static class ObjectAssetCacheSaveBuilder
{
    public static ObjectAssetCacheSaveRequest Capture(CatalogAssetState state, ObjectAssetCacheSectionSet sections)
        => new(
            string.IsNullOrWhiteSpace(state.GameVersion) ? null : state.GameVersion,
            string.IsNullOrWhiteSpace(state.SqpackIndexFingerprint) ? null : state.SqpackIndexFingerprint,
            state.Language,
            sections,
            sections.Contains(ObjectAssetCacheSectionKind.StaticCollisionPaths)
                ? state.StaticCollisionPaths.ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.StaticBgObjects)
                ? state.GameDataBgObjects.Values.ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.StaticResolvedVfx)
                ? state.StaticResolvedVfxPaths.Values.ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.BgModels)
                ? state.BgModels.Values
                    .Where(static asset => asset.IsRuntimeObserved)
                    .Select(static asset => asset.ToCacheModel())
                    .ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.RuntimeVfx)
                ? state.VfxAssets.Values
                    .Where(static asset => asset.SeenFromRuntime)
                    .Select(static asset => new ObjectAssetCacheRuntimeVfx(asset.Path, asset.Evidence, asset.Analysis))
                    .ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.TimelineReferencedVfx)
                ? state.RuntimeTimelineReferencedVfx
                    .Where(static pair => pair.Value.HasEvidence)
                    .Where(pair => !state.StaticTimelineReferencedVfx.TryGetValue(pair.Key, out VfxTimelineReferenceInfo reference)
                        || !pair.Value.IsCoveredBy(reference))
                    .Select(static pair => new ObjectAssetCacheTimelineReferencedVfxEntry(pair.Key, pair.Value.NormalizedEvidence, pair.Value.Context))
                    .ToArray()
                : []);
}
