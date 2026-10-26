using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Intoner.Objects.Models;
using Intoner.Objects.Resources;
using Intoner.Objects.Utils;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Intoner.Objects.Assets;

internal static class TerritoryLayoutAssetResolver
{
    internal sealed class AssetInfo(
        IReadOnlyList<string> bgObjectModelPaths,
        IReadOnlyList<ResolvedVfxPath> resolvedVfxPaths,
        AssetReferenceSources sources,
        bool bgObjectsComplete = true,
        bool vfxComplete = true)
    {
        public IReadOnlyList<string> BgObjectModelPaths { get; } = bgObjectModelPaths;
        public IReadOnlyList<ResolvedVfxPath> ResolvedVfxPaths { get; } = resolvedVfxPaths;
        public AssetReferenceSources Sources { get; } = sources;
        public bool BgObjectsComplete { get; } = bgObjectsComplete;
        public bool VfxComplete { get; } = vfxComplete;
        public bool IsComplete => BgObjectsComplete && VfxComplete;
        public IReadOnlyDictionary<string, IReadOnlyList<BgColliderDefinition>> ColliderDefinitions { get; init; } = new Dictionary<string, IReadOnlyList<BgColliderDefinition>>(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly uint LvbSectionMagic = MakeSectionMagic('S', 'C', 'N', '1');
    private static readonly uint LgbSectionMagic = MakeSectionMagic('L', 'G', 'P', '1');

    [StructLayout(LayoutKind.Explicit, Size = 0x5C)]
    private struct FileLayerGroupInstanceVfx
    {
        [FieldOffset(0x00)] public FileLayerGroupInstance Base;
        [FieldOffset(0x30)] public int OffsetPath;
    }

    public static AssetInfo AnalyzeTerritoryLayout(IObjectAssetGameData gameData, string territoryLayoutPath, CancellationToken cancellationToken = default)
        => AnalyzeTerritoryLayout(gameData, new ObjectAssetSharedGroupCache(gameData),
            new Dictionary<string, SharedGroupAssetInfo>(StringComparer.OrdinalIgnoreCase), territoryLayoutPath, cancellationToken);

    internal static AssetInfo AnalyzeTerritoryLayout(
        IObjectAssetGameData gameData,
        ObjectAssetSharedGroupCache sharedGroupCache,
        Dictionary<string, SharedGroupAssetInfo> sharedGroups,
        string territoryLayoutPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedPath = ObjectResolvedPath.FromGamePath(territoryLayoutPath).Path;

        if (!normalizedPath.EndsWith(".lvb", StringComparison.OrdinalIgnoreCase))
        {
            return new AssetInfo([], [], AssetReferenceSources.Empty, bgObjectsComplete: false, vfxComplete: false);
        }

        ObjectAssetFileData file = ObjectAssetFileUtility.ReadGameFile(gameData, normalizedPath, cancellationToken);
        return file.Data is null
            ? new AssetInfo([], [], AssetReferenceSources.Empty, bgObjectsComplete: file.IsComplete, vfxComplete: file.IsComplete)
            : new TerritoryLayoutAssetCollector(gameData, sharedGroupCache, sharedGroups, cancellationToken).Collect(normalizedPath, file.Data);
    }

    private static uint MakeSectionMagic(char a, char b, char c, char d)
        => (uint)(byte)a
         | ((uint)(byte)b << 8)
         | ((uint)(byte)c << 16)
         | ((uint)(byte)d << 24);

    private sealed class TerritoryLayoutAssetCollector(
        IObjectAssetGameData gameData,
        ObjectAssetSharedGroupCache sharedGroupCache,
        Dictionary<string, SharedGroupAssetInfo> sharedGroups,
        CancellationToken cancellationToken)
    {
        private readonly IObjectAssetGameData _gameData = gameData;
        private readonly CancellationToken _cancellationToken = cancellationToken;
        private readonly VfxTimelineReferenceCache _timelineReferenceCache = new();
        private readonly List<string> _bgObjectModelPaths = [];
        private readonly BgColliderDefinitionSet _colliderDefinitions = new();
        private readonly AssetReferenceSources.Builder _sources = new();
        private readonly ObjectAssetSharedGroupCache _sharedGroupCache = sharedGroupCache;
        private readonly Dictionary<string, SharedGroupAssetInfo> _sharedGroups = sharedGroups;
        private readonly Dictionary<string, HashSet<string>> _sharedGroupSources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ResolvedVfxPathAccumulator> _resolvedVfxPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenBgObjectModels = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenLayoutPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _bgObjectsComplete = true;
        private bool _vfxComplete = true;

        public AssetInfo Collect(string territoryLayoutPath, byte[] fileData)
        {
            CollectLvb(territoryLayoutPath, fileData);
            _cancellationToken.ThrowIfCancellationRequested();
            return new AssetInfo(
                _bgObjectModelPaths,
                ResolvedVfxPathAccumulator.BuildSnapshot(_resolvedVfxPaths.Values),
                _sources.Build(),
                _bgObjectsComplete,
                _vfxComplete) { ColliderDefinitions = _colliderDefinitions.Build() };
        }

        private void CollectLvb(string territoryLayoutPath, byte[] fileData)
        {
            if (_seenLayoutPaths.Add(territoryLayoutPath))
            {
                CollectSections(territoryLayoutPath, fileData, LvbSectionMagic);
            }
        }

        private void CollectSections(string sourcePath, byte[] fileData, uint sectionMagic)
        {
            LayoutFileReader file = new(fileData, sourcePath);
            file.VisitSections((reader, magic) =>
            {
                if (magic != sectionMagic)
                {
                    return;
                }

                if (magic == LvbSectionMagic)
                {
                    FileSceneHeader sceneHeader = reader.Read<FileSceneHeader>(0);
                    reader.VisitSceneInstances(CollectInstance, _cancellationToken);
                    CollectReferencedLayerGroups(reader, sceneHeader);
                    try
                    {
                        CollectSceneTimelines(reader, sceneHeader);
                    }
                    catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        _vfxComplete = false;
                    }
                }
                else
                {
                    reader.VisitLayerGroup(0, CollectInstance, _cancellationToken);
                }
            }, _cancellationToken);
        }

        private void CollectReferencedLayerGroups(LayoutFileReader reader, in FileSceneHeader sceneHeader)
        {
            int arrayOffset = reader.ArrayOffset<int>(
                0, sceneHeader.OffsetLayerGroupResources, sceneHeader.NumLayerGroupResources);
            for (int i = 0; i < sceneHeader.NumLayerGroupResources; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int pathOffset = reader.Read<int>(arrayOffset + (long)i * sizeof(int));
                string referencedLayoutPath = ObjectResolvedPath.FromGamePath(
                    reader.ReadString(reader.RelativeOffset(arrayOffset, pathOffset, 1))).Path;

                if (string.IsNullOrWhiteSpace(referencedLayoutPath)
                 || !referencedLayoutPath.EndsWith(".lgb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CollectLgb(referencedLayoutPath);
            }
        }

        private void CollectLgb(string layoutGroupPath)
        {
            if (_seenLayoutPaths.Contains(layoutGroupPath))
            {
                return;
            }

            try
            {
                ObjectAssetFileData file = ObjectAssetFileUtility.ReadGameFile(_gameData, layoutGroupPath, _cancellationToken);
                if (file.Data is null)
                {
                    if (file.IsComplete)
                    {
                        _seenLayoutPaths.Add(layoutGroupPath);
                    }
                    else
                    {
                        _bgObjectsComplete = false;
                        _vfxComplete = false;
                    }

                    return;
                }

                _seenLayoutPaths.Add(layoutGroupPath);
                CollectSections(layoutGroupPath, file.Data, LgbSectionMagic);
            }
            catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _bgObjectsComplete = false;
                _vfxComplete = false;
            }
        }

        private void CollectInstance(LayoutFileReader reader, int instanceOffset, FileLayerGroupInstance instance)
        {
            switch (instance.Type)
            {
                case InstanceType.BgPart:
                    CollectBgPart(reader, instanceOffset);
                    break;
                case InstanceType.Vfx:
                    CollectVfx(reader, instanceOffset);
                    break;
                case InstanceType.SharedGroup:
                case InstanceType.HelperObject:
                    CollectSharedGroup(reader, instanceOffset);
                    break;
            }
        }

        private void CollectBgPart(LayoutFileReader reader, int instanceOffset)
        {
            FileLayerGroupInstanceBgPart bgPart = reader.Read<FileLayerGroupInstanceBgPart>(instanceOffset);
            string modelPath = ObjectResolvedPath.FromGamePath(reader.ReadOptionalString(instanceOffset, bgPart.OffsetPathMdl)).Path;

            if (!ObjectAssetPathRules.IsCatalogModelPath(modelPath)
             || !_gameData.FileExists(modelPath))
            {
                return;
            }

            _sources.Add(modelPath, reader.FilePath);
            try
            {
                _colliderDefinitions.Add(BgColliderDefinitionReader.Read(reader, instanceOffset, modelPath));
            }
            catch (InvalidDataException)
            {
                _bgObjectsComplete = false;
            }

            if (_seenBgObjectModels.Add(modelPath))
            {
                _bgObjectModelPaths.Add(modelPath);
            }
        }

        private void CollectVfx(LayoutFileReader reader, int instanceOffset)
        {
            FileLayerGroupInstanceVfx vfx = reader.Read<FileLayerGroupInstanceVfx>(instanceOffset);
            _ = VfxResolvedPathUtility.TryMergeResolvedPath(
                _resolvedVfxPaths,
                _gameData,
                reader.ReadOptionalString(instanceOffset, vfx.OffsetPath),
                sqpackIndexSnapshot: null,
                KnownVfxFamily.None,
                RuntimeVfxEvidence.LayoutInstance,
                AssetPathSource.GameData,
                AssetPathContract.ParsedFileReference,
                SearchTermUtility.BuildStableTerms("layout vfx", reader.FilePath, reader.ReadOptionalString(instanceOffset, vfx.Base.OffsetName)));
        }

        private void CollectSharedGroup(LayoutFileReader reader, int instanceOffset)
        {
            FileLayerGroupInstanceSharedGroup sharedGroup = reader.Read<FileLayerGroupInstanceSharedGroup>(instanceOffset);
            string sharedGroupPath = ObjectResolvedPath.FromGamePath(reader.ReadOptionalString(instanceOffset, sharedGroup.OffsetPath)).Path;

            if (!ObjectAssetPathRules.IsCatalogSharedGroupPath(sharedGroupPath))
            {
                return;
            }

            if (_sharedGroupCache.GetOrAnalyze(_sharedGroups, sharedGroupPath, _cancellationToken) is not { } sharedGroupAssets)
            {
                _bgObjectsComplete = false;
                _vfxComplete = false;
                return;
            }

            _bgObjectsComplete &= sharedGroupAssets.IsComplete;
            _vfxComplete &= sharedGroupAssets.IsComplete;

            if (sharedGroupAssets.IsComplete)
            {
                if (!_sharedGroupSources.TryGetValue(sharedGroupPath, out HashSet<string>? sourcePaths))
                {
                    sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _sharedGroupSources.Add(sharedGroupPath, sourcePaths);
                }

                if (!sourcePaths.Add(reader.FilePath))
                {
                    return;
                }
            }

            foreach (IReadOnlyList<BgColliderDefinition> definitions in sharedGroupAssets.ColliderDefinitions.Values)
            {
                _colliderDefinitions.AddRange(definitions);
            }

            foreach (string modelPath in sharedGroupAssets.BgObjectModelPaths)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _sources.Add(modelPath, reader.FilePath);
                _sources.Add(modelPath, sharedGroupAssets.Sources[modelPath]);

                if (_seenBgObjectModels.Add(modelPath))
                {
                    _bgObjectModelPaths.Add(modelPath);
                }
            }

            VfxResolvedPathUtility.MergeSharedGroupPaths(_resolvedVfxPaths, _gameData, sharedGroupAssets, [reader.FilePath]);
        }

        private void CollectSceneTimelines(LayoutFileReader reader, in FileSceneHeader sceneHeader)
        {
            if (sceneHeader.OffsetTimelines == 0)
            {
                return;
            }

            int listOffset = reader.RelativeOffset(0, sceneHeader.OffsetTimelines, Unsafe.SizeOf<FileSceneTimelineList>());
            FileSceneTimelineList timelines = reader.Read<FileSceneTimelineList>(listOffset);
            int arrayOffset = reader.ArrayOffset<FileSceneTimeline>(listOffset, timelines.OffsetEntries, timelines.NumEntries);
            for (int i = 0; i < timelines.NumEntries; i++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int timelineOffset = arrayOffset + i * Unsafe.SizeOf<FileSceneTimeline>();
                FileSceneTimeline timeline = reader.Read<FileSceneTimeline>(timelineOffset);
                try
                {
                    CollectSceneTimeline(reader, timelineOffset, timeline);
                }
                catch (Exception ex) when (ObjectAssetFileUtility.IsReadFailure(ex))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    _vfxComplete = false;
                }
            }
        }

        private void CollectSceneTimeline(LayoutFileReader reader, int timelineOffset, in FileSceneTimeline timeline)
        {
            if (GameDataAssetPathUtility.TryBuildActionTimelinePath(
                    reader.ReadOptionalString(timelineOffset, timeline.OffsetActionTimelineKey), out string actionTimelinePath))
            {
                CollectTimelineReferences(
                    actionTimelinePath,
                    SearchTermUtility.BuildStableTerms("layout timeline", reader.FilePath, actionTimelinePath));
            }

            if (timeline.OffsetTmlb == 0)
            {
                return;
            }

            ReadOnlySpan<byte> tmlbData = reader.ReadEmbeddedTimeline(timelineOffset, timeline.OffsetTmlb);

            if (!TmbVfxReferenceReader.TryCollectReferences(_gameData, tmlbData, out TmbVfxReferenceCollection collection, _cancellationToken))
            {
                _vfxComplete = false;
                return;
            }

            MergeTimelineReferences(collection, SearchTermUtility.BuildStableTerms("layout timeline", reader.FilePath, "embedded tmlb"));
        }

        private void CollectTimelineReferences(string timelinePath, IReadOnlyList<string> searchTerms)
        {
            TmbVfxReferenceCollection collection = _timelineReferenceCache.Get(_gameData, timelinePath, _cancellationToken);
            MergeTimelineReferences(collection, SearchTermUtility.MergeTerms(searchTerms, [timelinePath]));
        }

        private void MergeTimelineReferences(TmbVfxReferenceCollection collection, IReadOnlyList<string> searchTerms)
            => _vfxComplete &= VfxResolvedPathUtility.MergeTimelinePaths(
                _resolvedVfxPaths,
                _gameData,
                collection,
                RuntimeVfxEvidence.LayoutTimeline,
                searchTerms,
                cancellationToken: _cancellationToken);
    }
}
