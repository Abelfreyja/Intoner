namespace Intoner.Objects.Assets.Cache;

internal static class ObjectAssetCacheSaveBuilder
{
    public static CaptureData Capture(CatalogAssetState state, ObjectAssetCacheSectionSet sections)
        => new(
            string.IsNullOrWhiteSpace(state.GameVersion) ? null : state.GameVersion,
            string.IsNullOrWhiteSpace(state.SqpackIndexFingerprint) ? null : state.SqpackIndexFingerprint,
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
                ? state.BgModels.Values.Select(static asset => asset.CaptureForSave()).ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.BgModels)
                ? state.GameDataBgObjects.Keys.ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.StandaloneVfx)
                ? state.VfxAssets.Values.Select(static asset => asset.CaptureForSave()).ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.StandaloneVfx)
                ? state.StaticResolvedVfxPaths.Keys.ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.TimelineReferencedVfx)
                ? state.RuntimeTimelineReferencedVfx.ToArray()
                : [],
            sections.Contains(ObjectAssetCacheSectionKind.TimelineReferencedVfx)
                ? state.StaticTimelineReferencedVfx.Keys.ToArray()
                : []);

    public static ObjectAssetCacheSaveRequest BuildRequest(CaptureData capture)
    {
        HashSet<string> staticBgModelPaths = new(capture.StaticBgModelPaths, StringComparer.OrdinalIgnoreCase);
        HashSet<string> staticResolvedVfxPaths = new(capture.StaticResolvedVfxPaths, StringComparer.OrdinalIgnoreCase);
        HashSet<string> staticTimelinePaths = new(capture.StaticTimelineReferencedPaths, StringComparer.OrdinalIgnoreCase);

        return new ObjectAssetCacheSaveRequest(
            capture.GameVersion,
            capture.SqpackIndexFingerprint,
            capture.Sections,
            capture.StaticCollisionPaths,
            capture.StaticGameDataBgObjects
                .Select(ObjectAssetCacheProjection.ToCacheStaticBgObject)
                .ToArray(),
            capture.StaticResolvedVfxEntries
                .Select(ObjectAssetCacheProjection.ToCacheResolvedVfxEntry)
                .ToArray(),
            capture.BgModels
                .Where(static asset => asset.IsRuntimeObserved)
                .Where(asset => !staticBgModelPaths.Contains(asset.CacheModel.Path))
                .Select(static asset => asset.CacheModel)
                .ToArray(),
            capture.VfxAssets
                .Where(static asset => asset.SeenFromRuntime)
                .Where(asset => !staticResolvedVfxPaths.Contains(asset.Path))
                .Where(static asset => asset.SupportClass == VfxStandaloneSupportClass.SupportedStandalone)
                .Select(static asset => new ObjectAssetCacheStandaloneVfx(asset.Path, asset.Evidence, asset.Analysis))
                .ToArray(),
            capture.RuntimeTimelineReferencedVfx
                .Where(static pair => pair.Value.HasEvidence)
                .Where(pair => !staticTimelinePaths.Contains(pair.Key))
                .Select(static pair => new ObjectAssetCacheTimelineReferencedVfxEntry(pair.Key, pair.Value.NormalizedEvidence, pair.Value.Context))
                .ToArray());
    }

    internal sealed record CaptureData(
        string? GameVersion,
        string? SqpackIndexFingerprint,
        ObjectAssetCacheSectionSet Sections,
        IReadOnlyList<string> StaticCollisionPaths,
        IReadOnlyList<GameDataBgObjectAsset> StaticGameDataBgObjects,
        IReadOnlyList<ResolvedVfxPath> StaticResolvedVfxEntries,
        IReadOnlyList<ObservedBgModelState.CacheSaveCapture> BgModels,
        IReadOnlyList<string> StaticBgModelPaths,
        IReadOnlyList<RuntimeVfxAssetState.CacheSaveCapture> VfxAssets,
        IReadOnlyList<string> StaticResolvedVfxPaths,
        IReadOnlyList<KeyValuePair<string, VfxTimelineReferenceInfo>> RuntimeTimelineReferencedVfx,
        IReadOnlyList<string> StaticTimelineReferencedPaths);
}
