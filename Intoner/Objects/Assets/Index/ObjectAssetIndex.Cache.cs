using Intoner.Objects.Assets.Cache;
using Microsoft.Extensions.Logging;

namespace Intoner.Objects.Assets;

internal sealed partial class ObjectAssetIndex
{
    private void TrySaveCacheImmediately(CatalogAssetState state, string successMessage)
    {
        if (state.DirtyCacheSections == ObjectAssetCacheSectionSet.None)
        {
            return;
        }

        ObjectAssetCacheSectionSet sectionsToSave = state.DirtyCacheSections;
        long capturedRevision = state.CacheRevision;
        ObjectAssetCacheSaveCapture capture = CaptureCacheSaveState(state, sectionsToSave);
        ObjectAssetCacheSaveRequest saveRequest = BuildCacheSaveRequest(capture);

        try
        {
            _cacheService.Save(saveRequest);
            if (state.CacheRevision == capturedRevision)
            {
                state.DirtyCacheSections &= ~sectionsToSave;
            }

            _logger.LogInformation(successMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to save rebuilt object asset cache immediately");
        }
    }

    private void LoadCachedRuntimeState(CatalogAssetState state, ObjectAssetCacheSnapshot snapshot)
    {
        foreach (ObjectAssetCacheBgModel bgModel in snapshot.BgModels)
        {
            if (!ObjectPathRules.IsCatalogModelPath(bgModel.Path))
            {
                continue;
            }

            ObservedBgModelState asset = ObservedBgModelState.FromCache(bgModel);
            state.BgModels[asset.Path] = asset;
            _ = AddKnowledgePath(state, asset.Path, AssetPathSource.Persisted, AssetPathContract.PersistedCache, asset.Sources);
        }

        foreach (ObjectAssetCacheTimelineReferencedVfxEntry timelineReferencedVfx in snapshot.TimelineReferencedVfxEntries)
        {
            string normalizedPath = ObjectPathRules.NormalizeGamePath(timelineReferencedVfx.Path);
            if (!ObjectPathRules.IsVfxPath(normalizedPath))
            {
                continue;
            }

            _ = MergeTimelineVfxReference(
                state,
                normalizedPath,
                new VfxTimelineReferenceInfo(timelineReferencedVfx.Evidence, timelineReferencedVfx.Context),
                AssetPathSource.Persisted,
                AssetPathContract.PersistedCache,
                ["persisted"],
                runtimeObserved: true);
        }

        foreach (ObjectAssetCacheStandaloneVfx vfxAsset in snapshot.StandaloneVfxAssets)
        {
            string normalizedPath = ObjectPathRules.NormalizeGamePath(vfxAsset.Path);
            if (!ObjectPathRules.IsVfxPath(normalizedPath))
            {
                continue;
            }

            _ = ObserveStandaloneVfxPath(
                state,
                normalizedPath,
                AssetPathSource.Persisted,
                AssetPathContract.PersistedCache,
                ["persisted"],
                vfxAsset.Evidence,
                runtimeObserved: true);
        }
    }

    private void ScheduleCacheSave()
    {
        lock (_stateLock)
        {
            _cacheSaveQueued = true;
            if (_cacheSaveTask is { IsCompleted: false })
            {
                return;
            }

            _cacheSaveTask = Task.Run(SaveCacheLoop);
        }
    }

    private void SaveCacheLoop()
    {
        while (Volatile.Read(ref _disposeRequested) == 0)
        {
            ObjectAssetCacheSaveWork? saveWork;
            lock (_stateLock)
            {
                saveWork = TryCaptureCacheSaveWorkLocked();
                if (saveWork is null)
                {
                    _cacheSaveQueued = false;
                    _cacheSaveTask = null;
                    return;
                }

                _cacheSaveQueued = false;
            }

            bool writeSucceeded = SaveCacheWork(saveWork, "failed to save object asset cache");

            lock (_stateLock)
            {
                ApplyCacheSaveResultLocked(saveWork, writeSucceeded);

                if (!_cacheSaveQueued && saveWork.State.DirtyCacheSections == ObjectAssetCacheSectionSet.None)
                {
                    _cacheSaveTask = null;
                    return;
                }
            }
        }

        lock (_stateLock)
        {
            _cacheSaveTask = null;
        }
    }

    private void FlushCache()
    {
        Task? cacheSaveTask;
        lock (_stateLock)
        {
            cacheSaveTask = _cacheSaveTask;
        }

        try
        {
            cacheSaveTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to flush object asset cache");
        }

        ObjectAssetCacheSaveWork? saveWork;
        lock (_stateLock)
        {
            saveWork = TryCaptureCacheSaveWorkLocked();
            _cacheSaveQueued = false;
            _cacheSaveTask = null;
        }

        if (saveWork is null)
        {
            return;
        }

        bool writeSucceeded = SaveCacheWork(saveWork, "failed to flush object asset cache");
        lock (_stateLock)
        {
            ApplyCacheSaveResultLocked(saveWork, writeSucceeded);
        }
    }

    private ObjectAssetCacheSaveWork? TryCaptureCacheSaveWorkLocked()
    {
        if (!_warmupState.TryGetValue(out CatalogAssetState? state)
         || state.DirtyCacheSections == ObjectAssetCacheSectionSet.None)
        {
            return null;
        }

        ObjectAssetCacheSectionSet sectionsToSave = state.DirtyCacheSections;
        long capturedRevision = state.CacheRevision;
        ObjectAssetCacheSaveCapture capture = CaptureCacheSaveState(state, sectionsToSave);
        return new ObjectAssetCacheSaveWork(state, sectionsToSave, capturedRevision, capture);
    }

    private bool SaveCacheWork(ObjectAssetCacheSaveWork saveWork, string failureMessage)
    {
        try
        {
            _cacheService.Save(BuildCacheSaveRequest(saveWork.Capture));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureMessage);
            return false;
        }
    }

    private static void ApplyCacheSaveResultLocked(ObjectAssetCacheSaveWork saveWork, bool writeSucceeded)
    {
        if (writeSucceeded && saveWork.State.CacheRevision == saveWork.CapturedRevision)
        {
            saveWork.State.DirtyCacheSections &= ~saveWork.Sections;
        }
    }

    private static ObjectAssetCacheSaveCapture CaptureCacheSaveState(CatalogAssetState state, ObjectAssetCacheSectionSet sections)
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

    private static ObjectAssetCacheSaveRequest BuildCacheSaveRequest(ObjectAssetCacheSaveCapture capture)
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
                .Select(static asset => new ObjectAssetCacheStaticBgObject(
                    asset.ModelPath,
                    asset.Source,
                    asset.RowId,
                    asset.SourcePath,
                    asset.TerritoryIds,
                    asset.TerritoryNames,
                    asset.SearchTerms))
                .ToArray(),
            capture.StaticResolvedVfxEntries
                .Select(static asset => new ObjectAssetCacheResolvedVfxEntry(
                    asset.Path,
                    asset.Family,
                    asset.Evidence,
                    asset.Sources,
                    asset.Contracts,
                    asset.SearchTerms,
                    asset.Analysis))
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
                .Select(static asset => new ObjectAssetCacheStandaloneVfx(asset.Path, asset.Evidence))
                .ToArray(),
            capture.RuntimeTimelineReferencedVfx
                .Where(static pair => pair.Value.HasEvidence)
                .Where(pair => !staticTimelinePaths.Contains(pair.Key))
                .Select(static pair => new ObjectAssetCacheTimelineReferencedVfxEntry(pair.Key, pair.Value.NormalizedEvidence, pair.Value.Context))
                .ToArray());
    }

    private static bool RemoveStaticTimelineReferencedDuplicates(CatalogAssetState state)
    {
        if (state.StaticTimelineReferencedVfx.Count == 0 || state.RuntimeTimelineReferencedVfx.Count == 0)
        {
            return false;
        }

        bool removedAny = false;
        foreach (string path in state.StaticTimelineReferencedVfx.Keys)
        {
            removedAny |= state.RuntimeTimelineReferencedVfx.Remove(path);
        }

        if (removedAny)
        {
            MarkCacheSectionsDirty(state, ObjectAssetCacheSectionSet.TimelineReferencedVfx);
        }

        return removedAny;
    }

    private sealed record ObjectAssetCacheSaveCapture(
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

    private sealed record ObjectAssetCacheSaveWork(
        CatalogAssetState State,
        ObjectAssetCacheSectionSet Sections,
        long CapturedRevision,
        ObjectAssetCacheSaveCapture Capture);
}

